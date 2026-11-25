using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using NinePointRotationCalibration.Halcon;
using NinePointRotationCalibration.Station.Devices;
using NinePointRotationCalibration.Station.Models;
using NinePointRotationCalibration.Station.Services.Calibration;
using NinePointRotationCalibration.Station.Services.Camera;
using NinePointRotationCalibration.Station.Services.Communication;
using NinePointRotationCalibration.Station.Services.Inspection;
using NinePointRotationCalibration.Station.Services.Persistence;
using NinePointRotationCalibration.Station.Services.Teaching;

namespace NinePointRotationCalibration.Station.Services
{
    /// <summary>组合 TCP、相机、标定、示教和检测服务的工作站运行时。</summary>
    public sealed class StationRuntime : IDisposable
    {
        private const int SeenMessageLimit = 4096;
        private readonly object _stateSync = new object();
        private readonly object _jobSaveSync = new object();
        private readonly object _postProcessingSync = new object();
        private readonly object _seenMessagesSync = new object();
        private readonly object _statusPushSync = new object();
        private Task _postProcessingTail = Task.CompletedTask;
        private readonly string _optionsPath;
        private readonly AtomicJsonStore _jsonStore;
        private readonly ProtocolJsonCodec _logCodec = new ProtocolJsonCodec();
        private readonly NinePointRotationCalibrationSdk _sdk;
        private readonly TcpVisionServer _tcpServer;
        private readonly CameraAcquisitionService _camera;
        private readonly CalibrationCoordinator _calibration;
        private readonly TeachingService _teaching;
        private readonly BoardInspectionService _inspection;
        private readonly SemaphoreSlim _inspectionGate;
        private readonly SemaphoreSlim _workflowGate;
        private readonly ConcurrentDictionary<string, byte> _seenMessages;
        private readonly ConcurrentQueue<string> _seenMessageOrder = new ConcurrentQueue<string>();
        private readonly ConcurrentDictionary<string, string> _calibrationRequestConnections;
        private StationOptions _options;
        private CalibrationJob _leftCalibrationJob;
        private CalibrationJob _rightCalibrationJob;
        private CalibrationJob _positioningJob;
        private CalibrationResult _leftCalibrationResult;
        private CalibrationResult _rightCalibrationResult;
        private DetectionProfile _detectionProfile;
        private TeachingData _teachingData;
        private string _calibrationCompletionConnectionId;
        private string _lastStatusPushConnectionId;
        private SystemStatusSnapshot _lastStatusPushSnapshot;
        private bool _hasLastStatusPush;
        private bool _deferTeachingStatusPush;
        private bool _started;
        private int _disposeRequested;
        private volatile bool _disposed;

        private struct SystemStatusSnapshot : IEquatable<SystemStatusSnapshot>
        {
            public bool Ready;
            public bool CameraEnabled;
            public bool CameraConnected;
            public bool CalibrationEnabled;
            public bool CalibrationReady;
            public bool InspectionEnabled;

            public bool Equals(SystemStatusSnapshot other)
            {
                return Ready == other.Ready &&
                       CameraEnabled == other.CameraEnabled &&
                       CameraConnected == other.CameraConnected &&
                       CalibrationEnabled == other.CalibrationEnabled &&
                       CalibrationReady == other.CalibrationReady &&
                       InspectionEnabled == other.InspectionEnabled;
            }
        }

        internal static StationRuntime CreateForSelfTest(
            string optionsPath,
            StationOptions options,
            ICameraDevice cameraDevice)
        {
            return new StationRuntime(optionsPath, options, cameraDevice, null);
        }

        internal static StationRuntime CreateForSelfTest(
            string optionsPath,
            StationOptions options,
            ICameraDevice cameraDevice,
            ITemplateMatchingEngine templateEngine)
        {
            return new StationRuntime(optionsPath, options, cameraDevice, templateEngine);
        }

        private StationRuntime(string optionsPath, StationOptions options)
            : this(optionsPath, options, null, null)
        {
        }

        private StationRuntime(
            string optionsPath,
            StationOptions options,
            ICameraDevice cameraDevice,
            ITemplateMatchingEngine templateEngine)
        {
            LocalDebugLog.EnsureInitialized();
            _optionsPath = optionsPath;
            _options = options ?? throw new ArgumentNullException(nameof(options));
            bool migrated = _options.NormalizeAfterLoad();
            _jsonStore = new AtomicJsonStore();
            _sdk = templateEngine == null
                ? new NinePointRotationCalibrationSdk()
                : new NinePointRotationCalibrationSdk(templateEngine);
            _tcpServer = new TcpVisionServer(_options);
            _camera = new CameraAcquisitionService(
                cameraDevice ?? CreateCameraDevice(_options),
                _options.PreviewIntervalMilliseconds);
            _inspection = new BoardInspectionService(_sdk);
            _inspectionGate = new SemaphoreSlim(1, 1);
            _workflowGate = new SemaphoreSlim(1, 1);
            _seenMessages = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
            _calibrationRequestConnections = new ConcurrentDictionary<string, string>(
                StringComparer.OrdinalIgnoreCase);

            _leftCalibrationJob = LoadJobOrDefault(_options.CalibrationJobPath, "Calibration-Left");
            _rightCalibrationJob = LoadJobOrDefault(_options.RightCalibrationJobPath, "Calibration-Right");
            _positioningJob = LoadJobOrDefault(_options.PositioningJobPath, "PCB positioning");
            _detectionProfile = LoadJsonOrDefault(_options.DetectionProfilePath, () => new DetectionProfile());
            if (MigrateLegacyDetectionRoi(_positioningJob, _detectionProfile))
            {
                LocalDebugLog.Write("INFO", "startup", "Migrated legacy DetectionProfile.Roi into positioning job: " + _options.PositioningJobPath);
                SaveJob(_positioningJob, _options.PositioningJobPath);
            }
            _teachingData = LoadJsonOrDefault(_options.TeachingPath, () => new TeachingData());
            _leftCalibrationResult = _sdk.Calculate(_leftCalibrationJob);
            _rightCalibrationResult = _sdk.Calculate(_rightCalibrationJob);

            LocalDebugLog.Write(
                "INFO",
                "startup",
                "StationRuntime created. OptionsPath='" + _optionsPath + "', Migrated=" + migrated + ", " +
                StationLogFormatter.FormatOptions(_options));
            LocalDebugLog.Write("INFO", "startup", StationLogFormatter.FormatJob("CalibrationJob-Left(R1)", _leftCalibrationJob));
            LocalDebugLog.Write("INFO", "startup", StationLogFormatter.FormatJob("CalibrationJob-Right(R2)", _rightCalibrationJob));
            LocalDebugLog.Write("INFO", "startup", StationLogFormatter.FormatJob("PositioningJob", _positioningJob));
            LocalDebugLog.Write("INFO", "startup", "DetectionProfile: " + StationLogFormatter.FormatDetectionProfile(_detectionProfile));
            LocalDebugLog.Write("INFO", "startup", "TeachingData: " + StationLogFormatter.FormatTeachingData(_teachingData));
            LocalDebugLog.Write("INFO", "startup", "InitialLeftCalibrationResult(R1): " + StationLogFormatter.FormatCalibrationResult(_leftCalibrationResult));
            LocalDebugLog.Write("INFO", "startup", "InitialRightCalibrationResult(R2): " + StationLogFormatter.FormatCalibrationResult(_rightCalibrationResult));

            _calibration = new CalibrationCoordinator(
                _sdk,
                SendCalibrationMessageAsync,
                _camera.CaptureAsync,
                SaveCalibrationJobAsync,
                _options,
                _leftCalibrationJob,
                _rightCalibrationJob);
            _teaching = new TeachingService(
                _sdk,
                _camera.CaptureAsync,
                GetPositioningJobSnapshot,
                SaveTeachingAsync,
                _teachingData);
            WireEvents();
        }

        public event EventHandler<LogEventArgs> Log;
        public event EventHandler<RuntimeStateEventArgs> StateChanged;
        public event EventHandler<FrameEventArgs> FrameReady;
        public event EventHandler<CalibrationPointEventArgs> CalibrationPointChanged;
        public event EventHandler<CalibrationResultEventArgs> CalibrationCompleted;
        public event EventHandler<TeachingEventArgs> TeachingChanged;
        public event EventHandler<InspectionEventArgs> InspectionCompleted;

        /// <summary>从工作站数据目录加载配置并创建默认运行时。</summary>
        public static StationRuntime CreateDefault()
        {
            LocalDebugLog.EnsureInitialized();
            string dataDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data");
            string optionsPath = Path.Combine(dataDirectory, "station.json");
            StationOptions options = new StationOptions();
            AtomicJsonStore store = new AtomicJsonStore();
            if (File.Exists(optionsPath))
            {
                StationOptions loaded;
                string error;
                if (store.TryLoad(optionsPath, out loaded, out error) && loaded != null)
                {
                    options = loaded;
                    if (options.NormalizeAfterLoad()) store.Save(optionsPath, options);
                }
                else
                {
                    LocalDebugLog.Write("WARN", "startup", "Failed to load station.json at '" + optionsPath + "', using defaults. Error=" + error);
                }
            }
            else
            {
                Directory.CreateDirectory(dataDirectory);
                store.Save(optionsPath, options);
                LocalDebugLog.Write("INFO", "startup", "Created default station.json at '" + optionsPath + "'.");
            }
            return new StationRuntime(optionsPath, options);
        }

        public bool IsStarted
        {
            get { lock (_stateSync) return _started; }
        }

        public bool IsTcpListening { get { return _tcpServer.IsRunning; } }
        public bool IsHostConnected { get { return _tcpServer.IsClientConnected; } }
        public bool IsCameraConnected { get { return _camera.IsConnected; } }
        public string CameraProviderName { get { return _camera.ProviderName; } }
        public string ConnectedCameraDeviceId { get { return _camera.ConnectedDeviceId; } }
        public bool IsCameraEnabled { get { lock (_stateSync) return _options.CameraEnabled; } }
        public bool IsCalibrationEnabled { get { lock (_stateSync) return _options.CalibrationEnabled; } }
        public bool IsInspectionEnabled { get { lock (_stateSync) return _options.InspectionEnabled; } }
        public bool IsArchiveImagesEnabled { get { lock (_stateSync) return _options.ArchiveImages; } }
        public bool IsLiveAcquisitionEnabled { get { lock (_stateSync) return _options.LiveAcquisitionEnabled; } }
        public bool IsCameraPreviewRunning { get { return _camera.IsPreviewRunning; } }
        public double LivePreviewFps { get { return _camera.LiveFps; } }
        public long LivePreviewFrameCount { get { return _camera.LiveFrameCount; } }
        public long LivePreviewDroppedFrameCount { get { return _camera.LiveDroppedFrameCount; } }
        public TeachingSide ActiveCalibrationNozzle
        {
            get { return _calibration.ActiveNozzle; }
            set { _calibration.ActiveNozzle = value; }
        }

        public bool IsLeftCalibrationValid
        {
            get { lock (_stateSync) return IsCalibrationValid(_leftCalibrationResult); }
        }

        public bool IsRightCalibrationValid
        {
            get { lock (_stateSync) return IsCalibrationValid(_rightCalibrationResult); }
        }

        public bool IsCalibrationReady
        {
            get
            {
                lock (_stateSync)
                {
                    return IsCalibrationValid(_leftCalibrationResult) &&
                           IsCalibrationValid(_rightCalibrationResult) &&
                           IsTeachingValid(_teaching.GetSnapshot());
                }
            }
        }

        /// <summary>注册实时预览 UI 渲染背压检测委托，避免高帧率预览在界面端堆积。</summary>
        public void SetPreviewConsumerBusyCallback(Func<bool> callback)
        {
            _camera.PreviewConsumerBusy = callback;
        }

        public StationOptions GetOptionsSnapshot()
        {
            lock (_stateSync) return _options.DeepClone();
        }

        public CalibrationJob GetCalibrationJobSnapshot()
        {
            return GetCalibrationJobSnapshot(ActiveCalibrationNozzle);
        }

        public CalibrationJob GetCalibrationJobSnapshot(TeachingSide nozzle)
        {
            lock (_stateSync)
            {
                CalibrationJob job = nozzle == TeachingSide.Right ? _rightCalibrationJob : _leftCalibrationJob;
                return job.DeepClone();
            }
        }

        public CalibrationJob GetPositioningJobSnapshot()
        {
            lock (_stateSync) return _positioningJob.DeepClone();
        }

        public CalibrationResult GetCalibrationResultSnapshot()
        {
            return GetCalibrationResultSnapshot(ActiveCalibrationNozzle);
        }

        public CalibrationResult GetCalibrationResultSnapshot(TeachingSide nozzle)
        {
            lock (_stateSync)
            {
                CalibrationResult result = nozzle == TeachingSide.Right ? _rightCalibrationResult : _leftCalibrationResult;
                return result == null ? null : result.DeepClone();
            }
        }

        public DetectionProfile GetDetectionProfileSnapshot()
        {
            lock (_stateSync) return CloneDetectionProfile(_detectionProfile);
        }

        public TeachingData GetTeachingSnapshot()
        {
            return _teaching.GetSnapshot();
        }

        public IReadOnlyList<CalibrationPointRecord> GetCalibrationRecordsSnapshot()
        {
            return _calibration.GetRecordsSnapshot(ActiveCalibrationNozzle);
        }

        public IReadOnlyList<CalibrationPointRecord> GetCalibrationRecordsSnapshot(TeachingSide nozzle)
        {
            return _calibration.GetRecordsSnapshot(nozzle);
        }

        public Task StartAsync()
        {
            return StartAsync(CancellationToken.None);
        }

        /// <summary>启动 TCP 服务，并按配置尝试连接相机。</summary>
        public async Task StartAsync(CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();
            lock (_stateSync)
            {
                if (_started) return;
                _started = true;
            }

            try
            {
                LocalDebugLog.Write("INFO", "startup", "Starting TCP server on " + _options.TcpBindAddress + ":" + _options.TcpPort + "...");
                await _tcpServer.StartAsync(cancellationToken).ConfigureAwait(false);
                LocalDebugLog.Write("INFO", "startup", "TCP server started on " + _tcpServer.LocalEndPoint);
                OnStateChanged("tcp", true, "Listening on " + _tcpServer.LocalEndPoint);
            }
            catch (Exception exception)
            {
                LocalDebugLog.WriteException("ERROR", "startup", "TCP server failed to start", exception);
                lock (_stateSync) _started = false;
                throw;
            }

            if (!IsCameraEnabled)
            {
                OnLog("INFO", "Camera auto-connect skipped because camera use is disabled.");
                OnStateChanged("camera", false, "Disabled");
                return;
            }

            try
            {
                await ConnectConfiguredCameraAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                OnLog("INFO", "Camera auto-connect was cancelled.");
                throw;
            }
            catch (Exception exception)
            {
                LocalDebugLog.WriteException("WARN", "camera.autoconnect", "Camera did not auto-connect", exception);
                bool noDevice = IsNoCameraFoundMessage(exception.Message);
                OnLog("WARN", noDevice ? "启动时未检测到在线相机，当前为未连接状态。" : "相机自动连接失败：" + exception.Message);
                OnStateChanged("camera", false, noDevice ? "未检测到在线相机" : exception.Message);
            }
        }

        /// <summary>停止预览、取消自动标定并关闭 TCP 服务。</summary>
        public async Task StopAsync()
        {
            bool shouldStop;
            lock (_stateSync)
            {
                shouldStop = _started;
                _started = false;
            }
            if (!shouldStop && !_tcpServer.IsRunning) return;

            LocalDebugLog.Write("INFO", "shutdown", "StationRuntime.StopAsync begin.");
            await _calibration.CancelCalibrationAsync("Station stopping.").ConfigureAwait(false);
            await _camera.StopPreviewAsync().ConfigureAwait(false);
            await _tcpServer.StopAsync().ConfigureAwait(false);
            OnStateChanged("tcp", false, "Stopped");
            LocalDebugLog.Write("INFO", "shutdown", "StationRuntime.StopAsync completed.");
        }

        /// <summary>按指定监听地址与端口启动或热重启 TCP 服务端（不中断相机采集）。</summary>
        public async Task StartTcpServerAsync(string bindAddress, int port, CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();
            if (port < 1 || port > 65535)
                throw new ArgumentOutOfRangeException(nameof(port), "TCP 监听端口必须在 1 到 65535 之间。");

            string normalizedAddress = string.IsNullOrWhiteSpace(bindAddress) ? "0.0.0.0" : bindAddress.Trim();
            bool needRestart = _tcpServer.IsRunning &&
                (!string.Equals(_tcpServer.BindAddress, normalizedAddress, StringComparison.OrdinalIgnoreCase) ||
                 _tcpServer.Port != port);

            if (needRestart)
            {
                await _tcpServer.StopAsync(cancellationToken).ConfigureAwait(false);
            }

            if (!_tcpServer.IsRunning)
            {
                _tcpServer.UpdateEndpoint(normalizedAddress, port);
                await _tcpServer.StartAsync(cancellationToken).ConfigureAwait(false);
            }

            StationOptions snapshot;
            lock (_stateSync)
            {
                _started = true;
                _options.TcpBindAddress = normalizedAddress;
                _options.TcpPort = port;
                snapshot = _options.DeepClone();
            }
            _jsonStore.Save(_optionsPath, snapshot);
            LocalDebugLog.Write("INFO", "tcp", "TCP server listening on " + _tcpServer.LocalEndPoint);
            OnStateChanged("tcp", true, "Listening on " + _tcpServer.LocalEndPoint);
        }

        /// <summary>仅停止 TCP 监听（保持相机连接与预览状态不变）。</summary>
        public async Task StopTcpServerAsync()
        {
            if (_disposed) return;
            await _tcpServer.StopAsync().ConfigureAwait(false);
            OnStateChanged("tcp", false, "Stopped");
            LocalDebugLog.Write("INFO", "tcp", "TCP server stopped by user.");
        }

        /// <summary>热切换真实工业相机驱动类型（大华 / 海康），无需重启程序。</summary>
        public async Task SwitchCameraProviderAsync(string provider, CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            string normalized = NormalizeCameraProvider(provider);
            StationOptions nextOptions;
            lock (_stateSync)
            {
                if (string.Equals(_options.CameraProvider, normalized, StringComparison.OrdinalIgnoreCase))
                    return;
                nextOptions = _options.DeepClone();
                nextOptions.CameraProvider = normalized;
                nextOptions.CameraSerialNumber = string.Empty;
            }

            ICameraDevice nextDevice = CreateCameraDevice(nextOptions);
            await _camera.ReplaceDeviceAsync(nextDevice, cancellationToken).ConfigureAwait(false);
            lock (_stateSync)
            {
                _options.CameraProvider = normalized;
                _options.CameraSerialNumber = string.Empty;
                nextOptions = _options.DeepClone();
            }
            _jsonStore.Save(_optionsPath, nextOptions);
            string label = string.Equals(normalized, "Hikvision", StringComparison.OrdinalIgnoreCase)
                ? "海康 (MVS)"
                : "大华 (CameraDH)";
            OnStateChanged("camera", false, "已切换驱动：" + label);
            NotifySystemStatusChangedIfNeeded();
            LocalDebugLog.Write("INFO", "camera.provider", "Switched camera provider to '" + normalized + "'.");
        }

        public async Task<IReadOnlyList<CameraDescriptor>> EnumerateCamerasAsync(CancellationToken cancellationToken)
        {
            EnsureCameraEnabled();
            IReadOnlyList<CameraDescriptor> devices = await _camera.EnumerateAsync(cancellationToken).ConfigureAwait(false);
            string list = devices == null || devices.Count == 0
                ? "<none>"
                : string.Join("; ", devices.Select(d => (d == null ? "<null>" : d.Id + "(" + d.DisplayName + ")")));
            LocalDebugLog.Write("INFO", "camera.enum", "Provider=" + _camera.ProviderName + ", Count=" + (devices == null ? 0 : devices.Count) + ", Devices=[" + list + "]");
            return devices;
        }

        /// <summary>停止旧预览，使用设备当前参数连接相机。</summary>
        public async Task ConnectCameraAsync(string deviceId, CancellationToken cancellationToken)
        {
            EnsureCameraEnabled();
            StationOptions options = GetOptionsSnapshot();
            LocalDebugLog.Write(
                "INFO",
                "camera.connect",
                "Connecting camera deviceId='" + deviceId + "', provider=" + _camera.ProviderName);
            await _camera.StopPreviewAsync().ConfigureAwait(false);
            await _camera.ConnectAsync(deviceId, cancellationToken).ConfigureAwait(false);
            bool autoStartPreview = options.LiveAcquisitionEnabled;
            if (autoStartPreview)
            {
                _camera.StartPreview();
            }
            LocalDebugLog.Write(
                "INFO",
                "camera.connect",
                "Camera connected: " + _camera.ProviderName + " / " + _camera.ConnectedDeviceId +
                ", liveOption=" + options.LiveAcquisitionEnabled + ", previewStarted=" + autoStartPreview);
            OnStateChanged("camera", true, _camera.ProviderName + " / " + _camera.ConnectedDeviceId);
            NotifySystemStatusChangedIfNeeded();
        }

        /// <summary>切换“兼容相机实时采集”开关并持久化；已连接相机时立即启动或停止后台实时流。</summary>
        public async Task SetLiveAcquisitionEnabledAsync(bool enabled, CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            StationOptions snapshot;
            lock (_stateSync)
            {
                _options.LiveAcquisitionEnabled = enabled;
                _options.ConfigurationVersion = StationOptions.CurrentConfigurationVersion;
                snapshot = _options.DeepClone();
            }
            _jsonStore.Save(_optionsPath, snapshot);
            LocalDebugLog.Write("INFO", "camera.live", "LiveAcquisitionEnabled updated to " + enabled + ".");

            if (!enabled)
            {
                await _camera.StopPreviewAsync().ConfigureAwait(false);
                OnLog("INFO", "已关闭相机实时采集。");
                return;
            }

            if (IsCameraEnabled && IsCameraConnected)
            {
                _camera.StartPreview();
                OnLog("INFO", "已开启相机实时采集。");
            }
            else
            {
                OnLog("INFO", "已启用相机实时采集兼容选项（连接相机后自动生效）。");
            }
        }

        /// <summary>主动开启已连接相机的实时采集流（未连接时直接报错，不做隐式连接或模拟）。</summary>
        public async Task StartLivePreviewAsync(CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            EnsureCameraEnabled();
            LocalDebugLog.Write("INFO", "camera.live", "StartLivePreviewAsync requested (connected=" + _camera.IsConnected + ").");
            if (!_camera.IsConnected)
                throw new InvalidOperationException("相机未连接，请先连接相机后再开启实时预览。");

            StationOptions snapshot;
            bool needSave = false;
            lock (_stateSync)
            {
                if (!_options.LiveAcquisitionEnabled)
                {
                    _options.LiveAcquisitionEnabled = true;
                    needSave = true;
                }
                snapshot = _options.DeepClone();
            }
            if (needSave) _jsonStore.Save(_optionsPath, snapshot);

            _camera.StartPreview();
            await Task.CompletedTask.ConfigureAwait(false);
            OnLog("INFO", "相机实时采集已启动：" + _camera.ProviderName + " / " + _camera.ConnectedDeviceId);
        }

        /// <summary>主动停止相机实时采集流（保持相机连接不中断）。</summary>
        public async Task StopLivePreviewAsync()
        {
            if (_disposed) return;
            LocalDebugLog.Write("INFO", "camera.live", "StopLivePreviewAsync requested.");
            await _camera.StopPreviewAsync().ConfigureAwait(false);
            OnLog("INFO", "相机实时采集已停止。");
        }

        public async Task DisconnectCameraAsync()
        {
            LocalDebugLog.Write("INFO", "camera.disconnect", "Disconnecting camera " + _camera.ProviderName + " / " + _camera.ConnectedDeviceId);
            await _camera.DisconnectAsync().ConfigureAwait(false);
            OnStateChanged("camera", false, "Disconnected");
            NotifySystemStatusChangedIfNeeded();
        }

        public async Task SetCameraExposureAsync(double exposureMicroseconds, CancellationToken cancellationToken)
        {
            EnsureCameraEnabled();
            await _camera.SetExposureAsync(exposureMicroseconds, cancellationToken).ConfigureAwait(false);
            StationOptions snapshot;
            lock (_stateSync)
            {
                _options.CameraExposureMicroseconds = exposureMicroseconds;
                snapshot = _options.DeepClone();
            }
            _jsonStore.Save(_optionsPath, snapshot);
            LocalDebugLog.Write("INFO", "camera.params", "Exposure updated and saved: " + exposureMicroseconds.ToString("0.##", CultureInfo.InvariantCulture) + "us.");
        }

        public Task<CameraParameterSet> GetCameraParametersAsync(CancellationToken cancellationToken)
        {
            EnsureCameraEnabled();
            return _camera.GetParametersAsync(cancellationToken);
        }

        /// <summary>向相机下发采集参数，并将最新参数快照保存至配置文件。</summary>
        public async Task SetCameraParametersAsync(CameraParameterSet parameters, CancellationToken cancellationToken)
        {
            EnsureCameraEnabled();
            if (parameters == null) throw new ArgumentNullException(nameof(parameters));
            await _camera.SetParametersAsync(parameters, cancellationToken).ConfigureAwait(false);
            StationOptions snapshot;
            lock (_stateSync)
            {
                _options.CameraExposureMicroseconds = parameters.ExposureMicroseconds;
                _options.CameraGain = parameters.Gain;
                _options.CameraGamma = parameters.Gamma;
                _options.CameraTriggerMode = parameters.TriggerMode;
                _options.CameraFeatures = SerializeCameraFeatures(parameters.Features);
                snapshot = _options.DeepClone();
            }
            _jsonStore.Save(_optionsPath, snapshot);
            LocalDebugLog.Write("INFO", "camera.params", "Parameters applied and saved: " + StationLogFormatter.FormatCameraParameters(parameters));
        }

        private static List<CameraFeatureValue> ParseCameraFeatures(Dictionary<string, string> values)
        {
            List<CameraFeatureValue> result = new List<CameraFeatureValue>();
            if (values == null) return result;
            foreach (KeyValuePair<string, string> pair in values)
                result.Add(new CameraFeatureValue { Name = pair.Key, Value = pair.Value, IsWritable = true });
            return result;
        }

        private static Dictionary<string, string> SerializeCameraFeatures(List<CameraFeatureValue> features)
        {
            Dictionary<string, string> result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (features == null) return result;
            foreach (CameraFeatureValue feature in features)
                if (feature != null && !string.IsNullOrWhiteSpace(feature.Name)) result[feature.Name] = feature.Value ?? string.Empty;
            return result;
        }

        public Task CaptureOnceAsync(CancellationToken cancellationToken)
        {
            EnsureCameraEnabled();
            return RunExclusiveAsync(
                "manual capture",
                async () =>
                {
                    EnsureCameraEnabled();
                    using (CameraFrame frame = await _camera.CaptureAsync(cancellationToken).ConfigureAwait(false))
                    {
                        LocalDebugLog.Write(
                            "INFO",
                            "camera.capture",
                            "Manual capture completed: seq=" + frame.Sequence +
                            ", size=" + (frame.Image == null ? "null" : frame.Image.Width + "x" + frame.Image.Height) +
                            ", capturedAtUtc=" + frame.CapturedAtUtc.ToString("O", CultureInfo.InvariantCulture));
                        PublishFrame(frame.Image, "manual");
                    }
                    return true;
                },
                cancellationToken);
        }

        /// <summary>主动发起指定吸嘴的手动标定会话（发送 calibration.started 并锁存上位机原点）。</summary>
        public Task<string> StartManualCalibrationSessionAsync(
            TeachingSide nozzle,
            CancellationToken cancellationToken)
        {
            EnsureCameraEnabled();
            EnsureCalibrationEnabled();
            return RunExclusiveAsync(
                "manual calibration session (" + ProtocolNozzle.GetAxisR(nozzle) + ")",
                () => _calibration.StartManualSessionAsync(nozzle, cancellationToken),
                cancellationToken);
        }

        /// <summary>执行当前吸嘴一个标定点的运动、采图、定位和会话暂存流程。</summary>
        public Task<CalibrationPointRecord> CaptureCalibrationPointAsync(
            int pointNumber,
            CancellationToken cancellationToken)
        {
            return CaptureCalibrationPointAsync(ActiveCalibrationNozzle, pointNumber, cancellationToken);
        }

        /// <summary>执行指定吸嘴一个标定点的运动、采图、定位和会话暂存流程。</summary>
        public Task<CalibrationPointRecord> CaptureCalibrationPointAsync(
            TeachingSide nozzle,
            int pointNumber,
            CancellationToken cancellationToken)
        {
            EnsureCameraEnabled();
            EnsureCalibrationEnabled();
            return RunExclusiveAsync(
                "calibration point " + ProtocolNozzle.GetAxisR(nozzle) + " P" + pointNumber,
                async () =>
                {
                    CalibrationPointRecord record = await _calibration.CapturePointAsync(nozzle, pointNumber, cancellationToken).ConfigureAwait(false);
                    if (record != null)
                    {
                        LocalDebugLog.Write(
                            record.Status == CalibrationPointStatus.Completed ? "INFO" : "WARN",
                            "calibration.point",
                            string.Format(
                                CultureInfo.InvariantCulture,
                                "Nozzle={0}({1}), Point={2}, Status={3}, Machine=({4:0.###},{5:0.###},{6:0.###}), Image=({7:0.###},{8:0.###},{9:0.###}), Msg='{10}'",
                                ProtocolNozzle.GetNozzleName(nozzle),
                                ProtocolNozzle.GetAxisR(nozzle),
                                record.PointNumber,
                                record.Status,
                                record.MachineX,
                                record.MachineY,
                                record.MachineR,
                                record.ImageRow,
                                record.ImageColumn,
                                record.ImageAngle,
                                record.Message));
                    }
                    return record;
                },
                cancellationToken);
        }

        /// <summary>按计划依次执行当前吸嘴全部 14 点标定并计算结果。</summary>
        public Task<CalibrationResult> RunAutomaticCalibrationAsync(CancellationToken cancellationToken)
        {
            return RunAutomaticCalibrationAsync(ActiveCalibrationNozzle, cancellationToken);
        }

        /// <summary>按计划依次执行指定吸嘴（Left/R1 或 Right/R2）全部 14 点标定并计算结果。</summary>
        public Task<CalibrationResult> RunAutomaticCalibrationAsync(
            TeachingSide nozzle,
            CancellationToken cancellationToken)
        {
            EnsureCameraEnabled();
            EnsureCalibrationEnabled();
            return RunExclusiveAsync(
                "automatic calibration (" + ProtocolNozzle.GetAxisR(nozzle) + ")",
                async () =>
                {
                    CalibrationResult result = await _calibration.RunAutomaticAsync(nozzle, cancellationToken).ConfigureAwait(false);
                    LocalDebugLog.Write("INFO", "calibration.auto", "Automatic calibration result (" + ProtocolNozzle.GetAxisR(nozzle) + "): " + StationLogFormatter.FormatCalibrationResult(result));
                    return result;
                },
                cancellationToken);
        }

        public void CancelAutomaticCalibration()
        {
            LocalDebugLog.Write("INFO", "calibration.auto", "Calibration cancellation requested.");
            _calibration.CancelAutomatic();
        }

        /// <summary>主动取消正在执行的自动/手动标定会话并通知上位机 calibration.completed(CANCELLED)。</summary>
        public Task CancelCalibrationAsync(string reason = null)
        {
            LocalDebugLog.Write("INFO", "calibration.cancel", "Calibration cancel requested: " + (reason ?? "Operator stopped calibration."));
            return _calibration.CancelCalibrationAsync(reason);
        }

        public Task<CalibrationResult> CalculateCalibrationAsync(CancellationToken cancellationToken)
        {
            return CalculateCalibrationAsync(ActiveCalibrationNozzle, cancellationToken);
        }

        public Task<CalibrationResult> CalculateCalibrationAsync(
            TeachingSide nozzle,
            CancellationToken cancellationToken)
        {
            EnsureCalibrationEnabled();
            return RunExclusiveAsync(
                "calibration calculation (" + ProtocolNozzle.GetAxisR(nozzle) + ")",
                async () =>
                {
                    CalibrationResult result = await _calibration.CalculateAsync(nozzle, cancellationToken).ConfigureAwait(false);
                    LocalDebugLog.Write(
                        result != null && result.Success ? "INFO" : "WARN",
                        "calibration.solve",
                        "Nozzle=" + ProtocolNozzle.GetAxisR(nozzle) + " | " + StationLogFormatter.FormatCalibrationResult(result));
                    return result;
                },
                cancellationToken);
        }

        /// <summary>仅记录一侧示教的机械轴坐标（不拍照，供轴遮挡视野时先记录轴位）。</summary>
        public Task<TeachingPose> RecordTeachingMachinePoseAsync(
            TeachingSide side,
            MachinePose machinePose,
            CancellationToken cancellationToken)
        {
            EnsureCalibrationEnabled();
            return RunExclusiveAsync(
                side + " teaching record machine",
                () => _teaching.RecordMachinePoseAsync(side, machinePose, cancellationToken),
                cancellationToken);
        }

        /// <summary>移轴离开视野后触发一次拍照并使用定位模板匹配基准板图像坐标。</summary>
        public Task<TeachingPose> LocateTeachingImageAsync(
            TeachingSide side,
            CancellationToken cancellationToken)
        {
            EnsureCameraEnabled();
            EnsureCalibrationEnabled();
            return RunExclusiveAsync(
                side + " teaching locate image",
                () => _teaching.LocateImagePoseAsync(side, cancellationToken),
                cancellationToken);
        }

        /// <summary>同步更新指定吸嘴标定模板的最低匹配分数（低于此分数标定立即中断报 NG）。</summary>
        public void UpdateCalibrationMinScore(TeachingSide nozzle, double minScore)
        {
            ThrowIfDisposed();
            if (double.IsNaN(minScore) || double.IsInfinity(minScore)) return;
            double clamped = Math.Max(0.0, Math.Min(1.0, minScore));
            CalibrationJob jobToSave = null;
            lock (_stateSync)
            {
                CalibrationJob target = nozzle == TeachingSide.Right ? _rightCalibrationJob : _leftCalibrationJob;
                if (target != null && target.Template != null)
                {
                    if (target.Template.MatchParameters == null)
                        target.Template.MatchParameters = new MatchParameters();
                    if (Math.Abs(target.Template.MatchParameters.MinScore - clamped) > 1e-6)
                    {
                        target.Template.MatchParameters.MinScore = clamped;
                        if (target.Template.HasUsableModel)
                        {
                            jobToSave = target.DeepClone();
                        }
                    }
                }
            }
            _calibration.UpdateTemplateMinScore(nozzle, clamped);
            if (jobToSave != null)
            {
                try
                {
                    SaveCalibrationJobAsync(nozzle, jobToSave).GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    LocalDebugLog.WriteException("WARN", "calibration.minScore", "Failed to persist calibration MinScore", ex);
                }
            }
        }

        /// <summary>采集并定位一侧示教姿态。</summary>
        public Task<TeachingPose> CaptureTeachingAsync(
            TeachingSide side,
            MachinePose machinePose,
            CancellationToken cancellationToken)
        {
            EnsureCameraEnabled();
            EnsureCalibrationEnabled();
            return RunExclusiveAsync(
                side + " teaching",
                () => _teaching.CaptureAsync(side, machinePose, cancellationToken),
                cancellationToken);
        }

        public Task<TeachingPose> ClearTeachingAsync(
            TeachingSide side,
            CancellationToken cancellationToken)
        {
            return RunExclusiveAsync(
                "clear " + side + " teaching",
                () => _teaching.ClearAsync(side, cancellationToken),
                cancellationToken);
        }

        /// <summary>在独占工作流下打开标定模板编辑器；更新当前吸嘴模板并清空其旧采样（另一吸嘴无模板时自动同步）。</summary>
        public TemplateSetupResult EditCalibrationTemplate(Bitmap referenceImage, IWin32Window owner)
        {
            return EditCalibrationTemplate(ActiveCalibrationNozzle, referenceImage, owner);
        }

        /// <summary>在独占工作流下打开指定吸嘴的标定模板编辑器。</summary>
        public TemplateSetupResult EditCalibrationTemplate(TeachingSide nozzle, Bitmap referenceImage, IWin32Window owner)
        {
            ThrowIfDisposed();
            EnsureCalibrationEnabled();
            EnterSynchronousWorkflow("calibration template editing (" + ProtocolNozzle.GetAxisR(nozzle) + ")");
            try
            {
                CalibrationJob current;
                CalibrationJob other;
                TeachingSide otherNozzle = nozzle == TeachingSide.Right ? TeachingSide.Left : TeachingSide.Right;
                lock (_stateSync)
                {
                    current = (nozzle == TeachingSide.Right ? _rightCalibrationJob : _leftCalibrationJob).DeepClone();
                    other = (otherNozzle == TeachingSide.Right ? _rightCalibrationJob : _leftCalibrationJob).DeepClone();
                }
                TemplateSetupResult result = _sdk.OpenTemplateDialog(referenceImage, current.Template, owner);
                if (!result.Accepted || result.Template == null)
                {
                    LocalDebugLog.Write("INFO", "template.cal", "Calibration template editing cancelled or unaccepted: code=" + result.ErrorCode + ", msg=" + result.Message);
                    return result;
                }

                current.Template = result.Template.DeepClone();
                current = _sdk.ClearSamples(current);
                CalibrationResult recalculated = _sdk.Calculate(current);
                SaveCalibrationJobAsync(nozzle, current).GetAwaiter().GetResult();

                bool seededOther = false;
                if (other != null && (other.Template == null || !other.Template.HasUsableModel))
                {
                    other.Template = result.Template.DeepClone();
                    other = _sdk.ClearSamples(other);
                    SaveCalibrationJobAsync(otherNozzle, other).GetAwaiter().GetResult();
                    seededOther = true;
                }

                lock (_stateSync)
                {
                    if (nozzle == TeachingSide.Right)
                    {
                        _rightCalibrationJob = current.DeepClone();
                        _rightCalibrationResult = recalculated == null ? null : recalculated.DeepClone();
                        if (seededOther)
                        {
                            _leftCalibrationJob = other.DeepClone();
                            _leftCalibrationResult = _sdk.Calculate(other);
                        }
                    }
                    else
                    {
                        _leftCalibrationJob = current.DeepClone();
                        _leftCalibrationResult = recalculated == null ? null : recalculated.DeepClone();
                        if (seededOther)
                        {
                            _rightCalibrationJob = other.DeepClone();
                            _rightCalibrationResult = _sdk.Calculate(other);
                        }
                    }
                }
                _calibration.SetJob(nozzle, current);
                if (seededOther) _calibration.SetJob(otherNozzle, other);
                PublishCalibrationRecords(nozzle);
                OnCalibrationCalculationCompleted(this, new CalibrationResultEventArgs(nozzle, recalculated));
                LocalDebugLog.Write("INFO", "template.cal", "Calibration template saved (" + ProtocolNozzle.GetAxisR(nozzle) + "): " + StationLogFormatter.FormatJob("CalibrationJob", current));
                OnLog("INFO", ProtocolNozzle.GetDisplayName(nozzle) + " 标定模板已更新（旧采样已清空）" + (seededOther ? "，并已同步初始化另一吸嘴标定模板。" : "。"));
                return result;
            }
            finally
            {
                _workflowGate.Release();
            }
        }

        /// <summary>在独占工作流下打开 PCB 定位模板编辑器并保存定位模板。</summary>
        public TemplateSetupResult EditPositioningTemplate(Bitmap referenceImage, IWin32Window owner)
        {
            ThrowIfDisposed();
            EnterSynchronousWorkflow("positioning template editing");
            try
            {
                CalibrationJob current;
                string positioningPath;
                lock (_stateSync)
                {
                    current = _positioningJob.DeepClone();
                    positioningPath = _options.PositioningJobPath;
                }
                TemplateSetupResult result = _sdk.OpenTemplateDialog(referenceImage, current.Template, owner, true);
                if (!result.Accepted || result.Template == null)
                {
                    LocalDebugLog.Write("INFO", "template.pos", "Positioning template editing cancelled or unaccepted: code=" + result.ErrorCode + ", msg=" + result.Message);
                    return result;
                }

                current.Template = result.Template.DeepClone();
                SaveJob(current, positioningPath);
                lock (_stateSync)
                {
                    _positioningJob = current.DeepClone();
                }
                LocalDebugLog.Write("INFO", "template.pos", "Positioning template saved to '" + positioningPath + "': " + StationLogFormatter.FormatJob("PositioningJob", current));
                OnLog("INFO", "PCB positioning template updated.");
                return result;
            }
            finally
            {
                _workflowGate.Release();
            }
        }

        /// <summary>从文件加载当前吸嘴标定 Job 并立即重新计算标定结果。</summary>
        public JobPersistenceResult LoadCalibrationJob(string path)
        {
            return LoadCalibrationJob(ActiveCalibrationNozzle, path);
        }

        /// <summary>从文件加载指定吸嘴（Left/R1 或 Right/R2）标定 Job 并立即重新计算标定结果。</summary>
        public JobPersistenceResult LoadCalibrationJob(TeachingSide nozzle, string path)
        {
            ThrowIfDisposed();
            EnsureCalibrationEnabled();
            EnterSynchronousWorkflow("calibration job loading (" + ProtocolNozzle.GetAxisR(nozzle) + ")");
            try
            {
                JobPersistenceResult loaded = _sdk.TryLoadJob(path);
                if (!loaded.Success || loaded.Job == null)
                {
                    LocalDebugLog.Write("WARN", "job.load", "Failed to load calibration job from '" + path + "': " + loaded.Message);
                    return loaded;
                }

                CalibrationJob job = loaded.Job.DeepClone();
                CalibrationResult result = _sdk.Calculate(job);
                lock (_stateSync)
                {
                    if (nozzle == TeachingSide.Right)
                    {
                        _rightCalibrationJob = job.DeepClone();
                        _rightCalibrationResult = result == null ? null : result.DeepClone();
                        _options.RightCalibrationJobPath = path;
                    }
                    else
                    {
                        _leftCalibrationJob = job.DeepClone();
                        _leftCalibrationResult = result == null ? null : result.DeepClone();
                        _options.CalibrationJobPath = path;
                    }
                }
                _calibration.SetJob(nozzle, job);
                PersistOptionsSnapshot();
                PublishCalibrationRecords(nozzle);
                OnCalibrationCalculationCompleted(this, new CalibrationResultEventArgs(nozzle, result));
                LocalDebugLog.Write("INFO", "job.load", StationLogFormatter.FormatJob("CalibrationJob-" + ProtocolNozzle.GetAxisR(nozzle), job) + " | " + StationLogFormatter.FormatCalibrationResult(result));
                OnLog("INFO", ProtocolNozzle.GetDisplayName(nozzle) + " calibration job loaded: " + path);
                return loaded;
            }
            finally
            {
                _workflowGate.Release();
            }
        }

        /// <summary>从文件加载定位 Job，并在必要时兼容迁移旧版检测 ROI 配置。</summary>
        public JobPersistenceResult LoadPositioningJob(string path)
        {
            ThrowIfDisposed();
            EnterSynchronousWorkflow("positioning job loading");
            try
            {
                JobPersistenceResult loaded = _sdk.TryLoadJob(path);
                if (!loaded.Success || loaded.Job == null)
                {
                    LocalDebugLog.Write("WARN", "job.load", "Failed to load positioning job from '" + path + "': " + loaded.Message);
                    return loaded;
                }

                CalibrationJob loadedJob = loaded.Job.DeepClone();
                DetectionProfile profileSnapshot;
                lock (_stateSync) profileSnapshot = CloneDetectionProfile(_detectionProfile);
                bool migrated = MigrateLegacyDetectionRoi(loadedJob, profileSnapshot);
                lock (_stateSync)
                {
                    _positioningJob = loadedJob.DeepClone();
                    _options.PositioningJobPath = path;
                }
                PersistOptionsSnapshot();
                if (migrated) SaveJob(loadedJob, path);
                LocalDebugLog.Write("INFO", "job.load", "Migrated=" + migrated + " | " + StationLogFormatter.FormatJob("PositioningJob", loadedJob));
                OnLog("INFO", "Positioning job loaded: " + path);
                return loaded;
            }
            finally
            {
                _workflowGate.Release();
            }
        }

        /// <summary>将配置与数据文件（标定、定位、示教、检测参数与归档目录）统一绑定到同一文件夹并同步加载。</summary>
        public string ImportConfigDirectory(string directory)
        {
            ThrowIfDisposed();
            if (string.IsNullOrWhiteSpace(directory))
                throw new ArgumentException("配置文件夹路径不能为空。", nameof(directory));

            string normalized = Path.GetFullPath(directory.Trim());
            Directory.CreateDirectory(normalized);

            EnterSynchronousWorkflow("importing configuration directory");
            try
            {
                string leftCalibrationPath = ResolveDirectoryJobPath(normalized, "calibration.nprcal", "calib", null);
                string rightCalibrationPath = Path.Combine(normalized, "calibration-right.nprcal");
                string positioningPath = ResolveDirectoryJobPath(normalized, "positioning.nprcal", "pos", leftCalibrationPath);
                if (string.Equals(positioningPath, rightCalibrationPath, StringComparison.OrdinalIgnoreCase))
                {
                    positioningPath = Path.Combine(normalized, "positioning.nprcal");
                }
                string teachingPath = Path.Combine(normalized, "teaching.json");
                string detectionPath = Path.Combine(normalized, "detection-profile.json");
                string archiveDirectory = Path.Combine(normalized, "images");

                CalibrationJob currentLeftCalibration;
                CalibrationJob currentRightCalibration;
                CalibrationJob currentPositioning;
                DetectionProfile currentProfile;
                lock (_stateSync)
                {
                    currentLeftCalibration = _leftCalibrationJob == null ? new CalibrationJob { Name = "Calibration-Left" } : _leftCalibrationJob.DeepClone();
                    currentRightCalibration = _rightCalibrationJob == null ? new CalibrationJob { Name = "Calibration-Right" } : _rightCalibrationJob.DeepClone();
                    currentPositioning = _positioningJob == null ? new CalibrationJob { Name = "PCB positioning" } : _positioningJob.DeepClone();
                    currentProfile = CloneDetectionProfile(_detectionProfile);
                }
                TeachingData currentTeaching = _teaching.GetSnapshot();

                int loadedFileCount = 0;
                int createdFileCount = 0;

                CalibrationJob loadedLeftCalibration = currentLeftCalibration;
                if (File.Exists(leftCalibrationPath))
                {
                    JobPersistenceResult res = _sdk.TryLoadJob(leftCalibrationPath);
                    if (res.Success && res.Job != null)
                    {
                        loadedLeftCalibration = res.Job.DeepClone();
                        loadedFileCount++;
                    }
                }
                else
                {
                    SaveJob(loadedLeftCalibration, leftCalibrationPath);
                    createdFileCount++;
                }

                CalibrationJob loadedRightCalibration = currentRightCalibration;
                if (File.Exists(rightCalibrationPath))
                {
                    JobPersistenceResult res = _sdk.TryLoadJob(rightCalibrationPath);
                    if (res.Success && res.Job != null)
                    {
                        loadedRightCalibration = res.Job.DeepClone();
                        loadedFileCount++;
                    }
                }
                else
                {
                    SaveJob(loadedRightCalibration, rightCalibrationPath);
                    createdFileCount++;
                }

                DetectionProfile loadedProfile = currentProfile;
                if (File.Exists(detectionPath))
                {
                    DetectionProfile parsed;
                    string error;
                    if (_jsonStore.TryLoad(detectionPath, out parsed, out error) && parsed != null)
                    {
                        loadedProfile = CloneDetectionProfile(parsed);
                        loadedFileCount++;
                    }
                }
                else
                {
                    _jsonStore.Save(detectionPath, loadedProfile);
                    createdFileCount++;
                }

                CalibrationJob loadedPositioning = currentPositioning;
                if (File.Exists(positioningPath))
                {
                    JobPersistenceResult res = _sdk.TryLoadJob(positioningPath);
                    if (res.Success && res.Job != null)
                    {
                        loadedPositioning = res.Job.DeepClone();
                        if (MigrateLegacyDetectionRoi(loadedPositioning, loadedProfile))
                        {
                            SaveJob(loadedPositioning, positioningPath);
                        }
                        loadedFileCount++;
                    }
                }
                else
                {
                    SaveJob(loadedPositioning, positioningPath);
                    createdFileCount++;
                }

                TeachingData loadedTeaching = currentTeaching;
                if (File.Exists(teachingPath))
                {
                    TeachingData parsed;
                    string error;
                    if (_jsonStore.TryLoad(teachingPath, out parsed, out error) && parsed != null)
                    {
                        loadedTeaching = parsed;
                        loadedFileCount++;
                    }
                }
                else
                {
                    _jsonStore.Save(teachingPath, loadedTeaching);
                    createdFileCount++;
                }

                CalibrationResult leftCalibrationResult = _sdk.Calculate(loadedLeftCalibration);
                CalibrationResult rightCalibrationResult = _sdk.Calculate(loadedRightCalibration);
                StationOptions updatedOptions;
                lock (_stateSync)
                {
                    _options.ConfigDirectory = normalized;
                    _options.CalibrationJobPath = leftCalibrationPath;
                    _options.RightCalibrationJobPath = rightCalibrationPath;
                    _options.PositioningJobPath = positioningPath;
                    _options.TeachingPath = teachingPath;
                    _options.DetectionProfilePath = detectionPath;
                    _options.ImageArchiveDirectory = archiveDirectory;

                    _leftCalibrationJob = loadedLeftCalibration.DeepClone();
                    _rightCalibrationJob = loadedRightCalibration.DeepClone();
                    _leftCalibrationResult = leftCalibrationResult == null ? null : leftCalibrationResult.DeepClone();
                    _rightCalibrationResult = rightCalibrationResult == null ? null : rightCalibrationResult.DeepClone();
                    _positioningJob = loadedPositioning.DeepClone();
                    _detectionProfile = CloneDetectionProfile(loadedProfile);
                    _teachingData = loadedTeaching;
                    updatedOptions = _options.DeepClone();
                }

                _calibration.UpdateOptions(updatedOptions);
                _calibration.SetJob(TeachingSide.Left, loadedLeftCalibration);
                _calibration.SetJob(TeachingSide.Right, loadedRightCalibration);
                _teaching.SetData(loadedTeaching);
                _jsonStore.Save(_optionsPath, updatedOptions);

                TeachingSide activeNozzle = ActiveCalibrationNozzle;
                PublishCalibrationRecords(activeNozzle);
                OnCalibrationCalculationCompleted(
                    this,
                    new CalibrationResultEventArgs(
                        activeNozzle,
                        activeNozzle == TeachingSide.Right ? rightCalibrationResult : leftCalibrationResult));
                if (loadedTeaching != null)
                {
                    if (loadedTeaching.Left != null) OnTeachingChanged(this, new TeachingEventArgs(loadedTeaching.Left));
                    if (loadedTeaching.Right != null) OnTeachingChanged(this, new TeachingEventArgs(loadedTeaching.Right));
                }

                string summary = string.Format(
                    CultureInfo.InvariantCulture,
                    "配置文件夹已切换：{0}（已加载 {1} 项，新建补全 {2} 项）",
                    normalized,
                    loadedFileCount,
                    createdFileCount);
                LocalDebugLog.Write("INFO", "config.import", summary + " | " + StationLogFormatter.FormatOptions(updatedOptions));
                LocalDebugLog.Write("INFO", "config.import", StationLogFormatter.FormatJob("CalibrationJob-Left(R1)", loadedLeftCalibration));
                LocalDebugLog.Write("INFO", "config.import", StationLogFormatter.FormatJob("CalibrationJob-Right(R2)", loadedRightCalibration));
                LocalDebugLog.Write("INFO", "config.import", StationLogFormatter.FormatJob("PositioningJob", loadedPositioning));
                LocalDebugLog.Write("INFO", "config.import", "Profile: " + StationLogFormatter.FormatDetectionProfile(loadedProfile));
                LocalDebugLog.Write("INFO", "config.import", "Teaching: " + StationLogFormatter.FormatTeachingData(loadedTeaching));
                OnLog("INFO", summary);
                NotifySystemStatusChangedIfNeeded();
                return summary;
            }
            finally
            {
                _workflowGate.Release();
            }
        }

        /// <summary>按指定路径重新加载示教与检测参数文件（若文件存在），并同步更新配置路径。</summary>
        public void ReloadAuxiliaryConfigs(string teachingPath, string detectionProfilePath)
        {
            ThrowIfDisposed();
            EnterSynchronousWorkflow("reloading auxiliary configs");
            try
            {
                TeachingData reloadedTeaching = null;
                DetectionProfile reloadedProfile = null;

                if (!string.IsNullOrWhiteSpace(teachingPath) && File.Exists(teachingPath))
                {
                    TeachingData parsed;
                    string error;
                    if (_jsonStore.TryLoad(teachingPath, out parsed, out error) && parsed != null)
                    {
                        reloadedTeaching = parsed;
                    }
                    else
                    {
                        LocalDebugLog.Write("WARN", "config.reload", "Failed to load teaching file '" + teachingPath + "': " + error);
                    }
                }
                if (!string.IsNullOrWhiteSpace(detectionProfilePath) && File.Exists(detectionProfilePath))
                {
                    DetectionProfile parsed;
                    string error;
                    if (_jsonStore.TryLoad(detectionProfilePath, out parsed, out error) && parsed != null)
                    {
                        reloadedProfile = CloneDetectionProfile(parsed);
                    }
                    else
                    {
                        LocalDebugLog.Write("WARN", "config.reload", "Failed to load detection profile '" + detectionProfilePath + "': " + error);
                    }
                }

                StationOptions updatedOptions;
                lock (_stateSync)
                {
                    if (!string.IsNullOrWhiteSpace(teachingPath)) _options.TeachingPath = teachingPath;
                    if (!string.IsNullOrWhiteSpace(detectionProfilePath)) _options.DetectionProfilePath = detectionProfilePath;
                    if (reloadedProfile != null) _detectionProfile = reloadedProfile;
                    if (reloadedTeaching != null) _teachingData = reloadedTeaching;
                    updatedOptions = _options.DeepClone();
                }

                if (reloadedTeaching != null)
                {
                    _teaching.SetData(reloadedTeaching);
                    if (reloadedTeaching.Left != null) OnTeachingChanged(this, new TeachingEventArgs(reloadedTeaching.Left));
                    if (reloadedTeaching.Right != null) OnTeachingChanged(this, new TeachingEventArgs(reloadedTeaching.Right));
                }

                _jsonStore.Save(_optionsPath, updatedOptions);
                LocalDebugLog.Write(
                    "INFO",
                    "config.reload",
                    "Auxiliary configs reloaded. teaching='" + teachingPath + "' (" + (reloadedTeaching != null) + "), profile='" + detectionProfilePath + "' (" + (reloadedProfile != null) + ")");
            }
            finally
            {
                _workflowGate.Release();
            }
        }

        private static string ResolveDirectoryJobPath(
            string directory,
            string defaultFileName,
            string nameKeyword,
            string excludedPath)
        {
            string preferred = Path.Combine(directory, defaultFileName);
            if (File.Exists(preferred)) return preferred;

            string[] candidates = Directory.Exists(directory)
                ? Directory.GetFiles(directory, "*.nprcal", SearchOption.TopDirectoryOnly)
                : new string[0];
            string matched = candidates.FirstOrDefault(file =>
                !string.Equals(file, excludedPath, StringComparison.OrdinalIgnoreCase) &&
                Path.GetFileNameWithoutExtension(file).IndexOf(nameKeyword, StringComparison.OrdinalIgnoreCase) >= 0);
            if (!string.IsNullOrWhiteSpace(matched)) return matched;

            if (!string.IsNullOrWhiteSpace(excludedPath))
            {
                string remaining = candidates.FirstOrDefault(file =>
                    !string.Equals(file, excludedPath, StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrWhiteSpace(remaining)) return remaining;
            }
            return preferred;
        }

        public Task<TemplateMatchResult> TestCalibrationTemplateAsync(
            Bitmap image,
            CancellationToken cancellationToken)
        {
            return TestCalibrationTemplateAsync(ActiveCalibrationNozzle, image, cancellationToken);
        }

        public Task<TemplateMatchResult> TestCalibrationTemplateAsync(
            TeachingSide nozzle,
            Bitmap image,
            CancellationToken cancellationToken)
        {
            if (image == null) throw new ArgumentNullException(nameof(image));
            EnsureCalibrationEnabled();
            return RunExclusiveAsync(
                "calibration template test (" + ProtocolNozzle.GetAxisR(nozzle) + ")",
                () => Task.Run(() =>
                {
                    CalibrationJob job = GetCalibrationJobSnapshot(nozzle);
                    TemplateMatchResult match = _sdk.Locate(image, job, true);
                    LocalDebugLog.Write(
                        match != null && match.Success ? "INFO" : "WARN",
                        "template.test.cal",
                        match == null
                            ? "Locate returned null."
                            : string.Format(
                                CultureInfo.InvariantCulture,
                                "nozzle={0}, ok={1}, rc=({2:0.###},{3:0.###}), ang={4:0.###}deg, score={5:0.###}, ct={6:0.0}ms, msg='{7}'",
                                ProtocolNozzle.GetAxisR(nozzle),
                                match.Success,
                                match.Row,
                                match.Column,
                                match.AngleDegrees,
                                match.Score,
                                match.ElapsedMilliseconds,
                                match.Message));
                    return match;
                }, cancellationToken),
                cancellationToken);
        }

        public Task<TemplateMatchCollectionResult> TestPositioningTemplateAsync(
            Bitmap image,
            CancellationToken cancellationToken)
        {
            return TestPositioningTemplateAsync(image, true, cancellationToken);
        }

        public Task<TemplateMatchCollectionResult> TestPositioningTemplateAsync(
            Bitmap image,
            bool includeContour,
            CancellationToken cancellationToken)
        {
            return TestPositioningTemplateAsync(image, 0, includeContour, cancellationToken);
        }

        public Task<TemplateMatchCollectionResult> TestPositioningTemplateAsync(
            Bitmap image,
            int maximumMatches,
            bool includeContour,
            CancellationToken cancellationToken)
        {
            if (image == null) throw new ArgumentNullException(nameof(image));
            return RunExclusiveAsync(
                "positioning template test",
                () => Task.Run(() =>
                {
                    CalibrationJob job = GetPositioningJobSnapshot();
                    if (maximumMatches > 0 && job != null && job.Template != null &&
                        job.Template.MatchParameters != null)
                    {
                        job.Template.MatchParameters.NumMatches = maximumMatches;
                    }
                    TemplateMatchCollectionResult result = _sdk.LocateAll(image, job, includeContour);
                    int matchCount = result == null || result.Matches == null ? 0 : result.Matches.Count;
                    string summary = result == null || result.Matches == null
                        ? string.Empty
                        : string.Join("; ", result.Matches.Where(m => m != null).Select((m, idx) =>
                            string.Format(CultureInfo.InvariantCulture, "#{0}({1:0.##},{2:0.##},ang={3:0.##},score={4:0.###})", idx + 1, m.Row, m.Column, m.AngleDegrees, m.Score)));
                    LocalDebugLog.Write(
                        result != null && result.Success ? "INFO" : "WARN",
                        "template.test.pos",
                        string.Format(
                            CultureInfo.InvariantCulture,
                            "ok={0}, max={1}, count={2}, ct={3:0.0}ms, msg='{4}', matches=[{5}]",
                            result != null && result.Success,
                            maximumMatches,
                            matchCount,
                            result == null ? 0.0 : result.ElapsedMilliseconds,
                            result == null ? "<null>" : result.Message,
                            summary));
                    return result;
                }),
                cancellationToken);
        }

        /// <summary>供检测页实时效果图在后台定位检测 ROI 使用，不占用独占工作流门锁，且取消或失败时不向调用方抛异常。</summary>
        public TemplateMatchCollectionResult LocatePositioningForPreview(Bitmap image, int maximumMatches)
        {
            return LocatePositioningForPreview(image, null, maximumMatches);
        }

        /// <summary>复用当前锁定帧灰度缓存定位检测 ROI，避免检测页首次预览重复转换整幅高分辨率图像。</summary>
        internal TemplateMatchCollectionResult LocatePositioningForPreview(Bitmap image, LockedFrameGrayCache frameCache, int maximumMatches)
        {
            if (_disposed || (image == null && frameCache == null)) return null;

            CalibrationJob job = GetPositioningJobSnapshot();
            if (job == null || job.Template == null || !job.Template.HasUsableModel)
            {
                return null;
            }

            if (maximumMatches > 0 && job.Template.MatchParameters != null)
            {
                job.Template.MatchParameters.NumMatches = maximumMatches;
            }

            try
            {
                using (HalconGrayImage gray = frameCache == null ? null : frameCache.Acquire(image))
                {
                    return gray != null
                        ? _sdk.LocateAll(image, gray, job.Template, true)
                        : _sdk.LocateAll(image, job, true);
                }
            }
            catch (Exception exception)
            {
                LocalDebugLog.WriteException("WARN", "preview.locate", "LocatePositioningForPreview failed", exception);
                return null;
            }
        }

        /// <summary>执行在线检测并发布检测结果（单 R 兼容入口）。</summary>
        public Task<InspectionResult> RunInspectionAsync(
            int expectedCount,
            double currentR,
            CancellationToken cancellationToken)
        {
            return RunInspectionAsync(expectedCount, currentR, currentR, cancellationToken);
        }

        /// <summary>执行双 R 轴在线检测并发布检测结果。</summary>
        public Task<InspectionResult> RunInspectionAsync(
            int expectedCount,
            double currentRLeft,
            double currentRRight,
            CancellationToken cancellationToken)
        {
            EnsureCameraEnabled();
            EnsureInspectionEnabled();
            return RunExclusiveAsync(
                "inspection",
                () => ExecuteInspectionAsync(expectedCount, currentRLeft, currentRRight, null, cancellationToken),
                cancellationToken);
        }

        /// <summary>使用指定图像执行离线检测（单 R 兼容入口）。</summary>
        public Task<InspectionResult> RunOfflineInspectionAsync(
            Bitmap image,
            int expectedCount,
            double currentR,
            CancellationToken cancellationToken)
        {
            return RunOfflineInspectionAsync(image, expectedCount, currentR, currentR, cancellationToken, null);
        }

        /// <summary>使用指定图像执行双 R 轴离线检测。</summary>
        public Task<InspectionResult> RunOfflineInspectionAsync(
            Bitmap image,
            int expectedCount,
            double currentRLeft,
            double currentRRight,
            CancellationToken cancellationToken)
        {
            return RunOfflineInspectionAsync(image, expectedCount, currentRLeft, currentRRight, cancellationToken, null);
        }

        internal Task<InspectionResult> RunOfflineInspectionAsync(Bitmap image, int expectedCount,
            double currentR, CancellationToken cancellationToken, LockedFrameGrayCache frameCache)
        {
            return RunOfflineInspectionAsync(image, expectedCount, currentR, currentR, cancellationToken, frameCache);
        }

        internal Task<InspectionResult> RunOfflineInspectionAsync(
            Bitmap image,
            int expectedCount,
            double currentRLeft,
            double currentRRight,
            CancellationToken cancellationToken,
            LockedFrameGrayCache frameCache)
        {
            if (image == null) throw new ArgumentNullException(nameof(image));
            EnsureInspectionEnabled();
            return RunExclusiveAsync(
                "offline inspection",
                () => ExecuteOfflineInspectionAsync(image, expectedCount, currentRLeft, currentRRight, cancellationToken, frameCache),
                cancellationToken);
        }

        /// <summary>切换相机或标定功能开关并持久化；关闭相机时立即断开，开启且已启动时自动重连。</summary>
        public Task ApplyEnablementAsync(
            bool cameraEnabled,
            bool calibrationEnabled,
            CancellationToken cancellationToken)
        {
            return ApplyEnablementAsync(cameraEnabled, calibrationEnabled, IsInspectionEnabled, cancellationToken);
        }

        /// <summary>独立切换相机、标定、检测三大模块开关并持久化；彼此完全解耦互不影响。</summary>
        public async Task ApplyEnablementAsync(
            bool cameraEnabled,
            bool calibrationEnabled,
            bool inspectionEnabled,
            CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            if (!cameraEnabled || !calibrationEnabled)
            {
                await _calibration.CancelCalibrationAsync("Calibration or camera disabled in station settings.")
                    .ConfigureAwait(false);
            }

            await _workflowGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                StationOptions snapshot;
                bool cameraJustEnabled;
                lock (_stateSync)
                {
                    bool previousCameraEnabled = _options.CameraEnabled;
                    _options.CameraEnabled = cameraEnabled;
                    _options.CalibrationEnabled = calibrationEnabled;
                    _options.InspectionEnabled = inspectionEnabled;
                    _options.ConfigurationVersion = StationOptions.CurrentConfigurationVersion;
                    snapshot = _options.DeepClone();
                    // 仅在“启用相机”从关闭切换为开启时尝试自动发现并连接，切换标定/检测开关绝不触发相机重连
                    cameraJustEnabled = !previousCameraEnabled && cameraEnabled && _started && !_camera.IsConnected;
                }

                if (!cameraEnabled)
                {
                    await _camera.DisconnectAsync().ConfigureAwait(false);
                    OnStateChanged("camera", false, "Disabled");
                }

                _jsonStore.Save(_optionsPath, snapshot);
                _calibration.UpdateOptions(snapshot);

                if (cameraJustEnabled)
                {
                    try
                    {
                        await ConnectConfiguredCameraAsync(cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception exception)
                    {
                        bool noDevice = IsNoCameraFoundMessage(exception.Message);
                        OnStateChanged("camera", false, noDevice ? "未检测到在线相机" : exception.Message);
                        OnLog("WARN", noDevice ? "已启用相机，但未检测到在线设备，请连接硬件后点击「连接相机」。" : "启用相机后自动连接失败：" + exception.Message);
                    }
                }

                OnStateChanged(
                    "calibration",
                    calibrationEnabled,
                    calibrationEnabled ? "Enabled" : "Disabled");
                OnStateChanged(
                    "inspection",
                    inspectionEnabled,
                    inspectionEnabled ? "Enabled" : "Disabled");
                NotifySystemStatusChangedIfNeeded();
                OnLog(
                    "INFO",
                    "Station enablement updated: camera=" + cameraEnabled +
                    ", calibration=" + calibrationEnabled +
                    ", inspection=" + inspectionEnabled + ".");
            }
            finally
            {
                _workflowGate.Release();
            }
        }

        public void SetArchiveImages(bool enabled)
        {
            ThrowIfDisposed();
            StationOptions snapshot;
            lock (_stateSync)
            {
                if (_options.ArchiveImages == enabled) return;
                _options.ArchiveImages = enabled;
                _options.ConfigurationVersion = StationOptions.CurrentConfigurationVersion;
                snapshot = _options.DeepClone();
            }
            _jsonStore.Save(_optionsPath, snapshot);
            OnLog("INFO", "Image archiving " + (enabled ? "enabled" : "disabled") + ".");
        }

        public void UpdateDetectionProfile(DetectionProfile profile)
        {
            if (profile == null) throw new ArgumentNullException(nameof(profile));
            DetectionProfile copy = CloneDetectionProfile(profile);
            lock (_stateSync) _detectionProfile = copy;
            SaveDetectionProfile(copy);
            LocalDebugLog.Write("INFO", "config.profile", "DetectionProfile updated: " + StationLogFormatter.FormatDetectionProfile(copy));
        }

        /// <summary>校验并保存工作站配置（相机/标定/检测开关变更须通过 ApplyEnablementAsync 执行）。</summary>
        public void UpdateOptions(StationOptions options)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            StationOptions copy = options.DeepClone();
            ValidateOptions(copy);
            copy.CameraProvider = NormalizeCameraProvider(copy.CameraProvider);
            copy.ConfigurationVersion = StationOptions.CurrentConfigurationVersion;
            lock (_stateSync)
            {
                if (copy.CameraEnabled != _options.CameraEnabled ||
                    copy.CalibrationEnabled != _options.CalibrationEnabled ||
                    copy.InspectionEnabled != _options.InspectionEnabled)
                {
                    throw new InvalidOperationException(
                        "Use ApplyEnablementAsync to change camera, calibration, or inspection enablement.");
                }
                _options = copy;
            }
            _jsonStore.Save(_optionsPath, copy);
            _calibration.UpdateOptions(copy);
            _tcpServer.HeartbeatTimeoutMilliseconds = copy.HeartbeatTimeoutMilliseconds;
            _camera.SetPreviewInterval(copy.PreviewIntervalMilliseconds);
            LocalDebugLog.Write("INFO", "config.options", "Options saved: " + StationLogFormatter.FormatOptions(copy));
            OnLog("INFO", "工作站设置已保存。");
        }

        private async Task ConnectConfiguredCameraAsync(CancellationToken cancellationToken)
        {
            IReadOnlyList<CameraDescriptor> devices = await _camera.EnumerateAsync(cancellationToken).ConfigureAwait(false);
            string deviceSummary = devices == null || devices.Count == 0
                ? "<none>"
                : string.Join("; ", devices.Select(d => d == null ? "<null>" : d.Id + "(" + d.DisplayName + ")"));
            LocalDebugLog.Write("INFO", "camera.autoconnect", "Enumerated devices for provider '" + _camera.ProviderName + "': [" + deviceSummary + "]");
            if (devices == null || devices.Count == 0)
                throw new InvalidOperationException("未检测到在线相机设备。");

            string configured;
            lock (_stateSync) configured = _options.CameraSerialNumber;
            CameraDescriptor configuredDevice = string.IsNullOrWhiteSpace(configured)
                ? null
                : devices.FirstOrDefault(device => string.Equals(
                    device.Id,
                    configured,
                    StringComparison.OrdinalIgnoreCase));
            string deviceId = configuredDevice == null ? devices[0].Id : configuredDevice.Id;
            if (!string.IsNullOrWhiteSpace(configured) && configuredDevice == null)
            {
                OnLog("WARN", "Configured camera '" + configured +
                    "' was not found for provider '" + _camera.ProviderName +
                    "'. Connecting to discovered camera '" + deviceId + "' instead.");
            }

            await ConnectCameraAsync(deviceId, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(configured, deviceId, StringComparison.OrdinalIgnoreCase))
            {
                StationOptions snapshot;
                lock (_stateSync)
                {
                    _options.CameraSerialNumber = deviceId;
                    snapshot = _options.DeepClone();
                }
                _jsonStore.Save(_optionsPath, snapshot);
                OnLog("INFO", "Saved active camera selection: " + deviceId);
            }
        }

        private void WireEvents()
        {
            _tcpServer.Connected += OnTcpConnected;
            _tcpServer.Disconnected += OnTcpDisconnected;
            _tcpServer.MessageReceived += OnTcpMessageReceived;
            _tcpServer.Log += (sender, args) => ForwardTaggedLog("tcp", sender, args);
            _camera.Log += (sender, args) => ForwardTaggedLog("camera", sender, args);
            _camera.PreviewFrame += ForwardFrame;
            _camera.PreviewHealthChanged += OnCameraPreviewHealthChanged;
            _camera.PreviewRunningChanged += OnCameraPreviewRunningChanged;
            _calibration.Log += (sender, args) => ForwardTaggedLog("calibration", sender, args);
            _calibration.FrameCaptured += ForwardFrame;
            _calibration.PointChanged += OnCalibrationPointChanged;
            _calibration.CalculationCompleted += OnCalibrationCalculationCompleted;
            _teaching.FrameCaptured += ForwardFrame;
            _teaching.TeachingChanged += OnTeachingChanged;
        }

        /// <summary>
        /// 记录已处理的 messageId 以实现去重。使用定长 FIFO 窗口仅淘汰最早的消息 ID，
        /// 避免整批清空导致防重出现空窗期。
        /// </summary>
        private bool RememberMessage(string messageId)
        {
            lock (_seenMessagesSync)
            {
                if (!_seenMessages.TryAdd(messageId, 0)) return false;
                _seenMessageOrder.Enqueue(messageId);
                string evicted;
                while (_seenMessageOrder.Count > SeenMessageLimit && _seenMessageOrder.TryDequeue(out evicted))
                {
                    byte ignored;
                    _seenMessages.TryRemove(evicted, out ignored);
                }
                return true;
            }
        }

        private void ForgetSeenMessages()
        {
            lock (_seenMessagesSync)
            {
                string ignored;
                while (_seenMessageOrder.TryDequeue(out ignored)) { }
                _seenMessages.Clear();
            }
        }

        private void OnTcpConnected(object sender, TcpConnectionEventArgs e)
        {
            ForgetSeenMessages();
            lock (_statusPushSync)
            {
                _lastStatusPushConnectionId = null;
                _hasLastStatusPush = false;
            }
            LocalDebugLog.Write("INFO", "tcp.conn", "Connected: connId=" + e.ConnectionId + ", remote=" + e.RemoteEndPoint);
            OnStateChanged("host", true, e.RemoteEndPoint);
            OnLog("INFO", "Upper machine connected: " + e.RemoteEndPoint);
        }

        private void OnTcpDisconnected(object sender, TcpDisconnectedEventArgs e)
        {
            ClearCalibrationRequestConnectionsFor(e.ConnectionId);

            string activeConnectionId = _tcpServer.ActiveConnectionId;
            if (!string.IsNullOrWhiteSpace(activeConnectionId) && !string.Equals(
                activeConnectionId,
                e.ConnectionId,
                StringComparison.Ordinal))
            {
                bool clearedReplacedCalibration = false;
                lock (_stateSync)
                {
                    if (string.Equals(_calibrationCompletionConnectionId, e.ConnectionId, StringComparison.Ordinal))
                    {
                        _calibrationCompletionConnectionId = null;
                        clearedReplacedCalibration = true;
                    }
                }
                if (clearedReplacedCalibration)
                {
                    _calibration.CancelPendingMoves("Previous TCP connection replaced/closed: " + e.Reason);
                }
                LocalDebugLog.Write("INFO", "tcp.conn", "Previous connection closed: connId=" + e.ConnectionId + ", activeConnId=" + activeConnectionId + ", reason=" + e.Reason);
                OnLog("INFO", "Previous upper-machine connection closed: " + e.ConnectionId);
                return;
            }

            bool clearedCalibrationConnection = false;
            lock (_stateSync)
            {
                if (string.Equals(_calibrationCompletionConnectionId, e.ConnectionId, StringComparison.Ordinal))
                {
                    _calibrationCompletionConnectionId = null;
                    clearedCalibrationConnection = true;
                }
            }
            lock (_statusPushSync)
            {
                if (string.Equals(_lastStatusPushConnectionId, e.ConnectionId, StringComparison.Ordinal))
                {
                    _lastStatusPushConnectionId = null;
                    _hasLastStatusPush = false;
                }
            }
            if (clearedCalibrationConnection || _calibration.HasActiveSession)
            {
                _calibration.CancelPendingMoves("TCP disconnected: " + e.Reason);
            }

            ForgetSeenMessages();
            LocalDebugLog.Write("WARN", "tcp.conn", "Disconnected: connId=" + e.ConnectionId + ", reason=" + e.Reason);
            OnStateChanged("host", false, e.Reason);
            OnLog("WARN", "Upper machine disconnected: " + e.Reason);
        }

        private async void OnTcpMessageReceived(object sender, ProtocolMessageEventArgs e)
        {
            if (Volatile.Read(ref _disposeRequested) != 0 || _disposed) return;
            try
            {
                ProtocolEnvelope envelope = e.Message;
                bool isHeartbeat = string.Equals(envelope.Type, ProtocolMessageTypes.Heartbeat, StringComparison.OrdinalIgnoreCase);
                LocalDebugLog.Write(
                    isHeartbeat ? "DEBUG" : "INFO",
                    "tcp.rx",
                    "conn=" + e.ConnectionId + " " + StationLogFormatter.FormatEnvelope(envelope, _logCodec));

                if (!RememberMessage(envelope.MessageId))
                {
                    LocalDebugLog.Write("WARN", "tcp.rx", "Duplicate messageId rejected: conn=" + e.ConnectionId + ", msgId=" + envelope.MessageId);
                    await SendErrorAsync(
                        e.ConnectionId,
                        envelope.MessageId,
                        ProtocolErrorCodes.DuplicateMessage,
                        "Duplicate messageId.",
                        false)
                        .ConfigureAwait(false);
                    return;
                }

                if (HandleCalibrationProtocolMessage(envelope, e.ConnectionId)) return;
                if (string.Equals(envelope.Type, ProtocolMessageTypes.CalibrationCancel, StringComparison.OrdinalIgnoreCase))
                {
                    await _calibration.HandleCalibrationCancelAsync(envelope).ConfigureAwait(false);
                    return;
                }
                if (isHeartbeat ||
                    string.Equals(envelope.Type, ProtocolMessageTypes.Hello, StringComparison.OrdinalIgnoreCase))
                {
                    SystemStatusSnapshot statusSnapshot = CaptureSystemStatusSnapshot();
                    ProtocolEnvelope ack = ProtocolEnvelope.Create(ProtocolMessageTypes.Acknowledge);
                    ack.CorrelationId = envelope.MessageId;
                    FillSystemStatusData(ack.Data, statusSnapshot);
                    await _tcpServer.SendAsync(e.ConnectionId, ack).ConfigureAwait(false);
                    RecordSentSystemStatus(e.ConnectionId, statusSnapshot);
                    LocalDebugLog.Write(
                        isHeartbeat ? "DEBUG" : "INFO",
                        "tcp.tx",
                        "conn=" + e.ConnectionId + " " + StationLogFormatter.FormatEnvelope(ack, _logCodec));
                    return;
                }
                if (string.Equals(envelope.Type, ProtocolMessageTypes.InspectionTrigger, StringComparison.OrdinalIgnoreCase))
                {
                    await HandleInspectionTriggerAsync(envelope, e.ConnectionId).ConfigureAwait(false);
                    return;
                }
                if (string.Equals(envelope.Type, ProtocolMessageTypes.TeachingSubmit, StringComparison.OrdinalIgnoreCase))
                {
                    await HandleTeachingSubmitAsync(envelope, e.ConnectionId).ConfigureAwait(false);
                    return;
                }

                LocalDebugLog.Write("WARN", "tcp.rx", "Unsupported message type: conn=" + e.ConnectionId + ", type=" + envelope.Type);
                await SendErrorAsync(e.ConnectionId, envelope.MessageId, ProtocolErrorCodes.MessageTypeUnsupported,
                    "Unsupported message type: " + envelope.Type, false).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                LocalDebugLog.WriteException("ERROR", "tcp.dispatch", "Protocol handler failed for conn=" + e.ConnectionId, exception);
                OnLog("ERROR", "Protocol handler failed: " + exception.Message);
                try
                {
                    await SendErrorAsync(
                        e.ConnectionId,
                        e.Message.MessageId,
                        ProtocolErrorCodes.InternalError,
                        exception.Message,
                        true).ConfigureAwait(false);
                }
                catch (Exception sendException)
                {
                    LocalDebugLog.WriteException("ERROR", "tcp.dispatch", "Unable to send protocol error", sendException);
                    OnLog("ERROR", "Unable to send protocol error: " + sendException.Message);
                }
            }
        }

        private async Task HandleInspectionTriggerAsync(ProtocolEnvelope request, string connectionId)
        {
            bool cameraEnabled;
            bool calibrationEnabled;
            bool inspectionEnabled;
            lock (_stateSync)
            {
                cameraEnabled = _options.CameraEnabled;
                calibrationEnabled = _options.CalibrationEnabled;
                inspectionEnabled = _options.InspectionEnabled;
            }
            if (!inspectionEnabled)
            {
                LocalDebugLog.Write("WARN", "tcp.inspect", "Rejected inspection.trigger (INSPECTION_DISABLED): conn=" + connectionId + ", msgId=" + request.MessageId);
                await SendErrorAsync(
                    connectionId,
                    request.MessageId,
                    ProtocolErrorCodes.InspectionDisabled,
                    "Production inspection is unavailable because inspection is disabled.",
                    false).ConfigureAwait(false);
                return;
            }
            if (!cameraEnabled)
            {
                LocalDebugLog.Write("WARN", "tcp.inspect", "Rejected inspection.trigger (CAMERA_DISABLED): conn=" + connectionId + ", msgId=" + request.MessageId);
                await SendErrorAsync(
                    connectionId,
                    request.MessageId,
                    ProtocolErrorCodes.CameraDisabled,
                    "Production inspection is unavailable because camera use is disabled.",
                    false).ConfigureAwait(false);
                return;
            }
            if (!IsCameraConnected)
            {
                LocalDebugLog.Write("WARN", "tcp.inspect", "Rejected inspection.trigger (CAMERA_NOT_CONNECTED): conn=" + connectionId + ", msgId=" + request.MessageId);
                await SendErrorAsync(
                    connectionId,
                    request.MessageId,
                    ProtocolErrorCodes.CameraNotConnected,
                    "Production inspection is unavailable because the camera is not connected.",
                    true).ConfigureAwait(false);
                return;
            }

            int defaultExpected;
            lock (_stateSync) defaultExpected = _options.ExpectedBoardCount;
            int expected = ProtocolValue.Integer(request.Data, "expectedCount", defaultExpected);

            double currentRLeft;
            double currentRRight;
            string angleError;
            if (!TryParseInspectionTriggerAngles(
                request.Data,
                calibrationEnabled && IsCalibrationReady,
                out currentRLeft,
                out currentRRight,
                out angleError))
            {
                LocalDebugLog.Write("WARN", "tcp.inspect", "Rejected inspection.trigger (PROTOCOL_INVALID): conn=" + connectionId + ", msgId=" + request.MessageId + ", detail=" + angleError);
                await SendErrorAsync(
                    connectionId,
                    request.MessageId,
                    ProtocolErrorCodes.ProtocolInvalid,
                    angleError,
                    false).ConfigureAwait(false);
                return;
            }

            if (!await _workflowGate.WaitAsync(0).ConfigureAwait(false))
            {
                LocalDebugLog.Write("WARN", "tcp.inspect", "Rejected inspection.trigger (BUSY): conn=" + connectionId + ", msgId=" + request.MessageId);
                await SendErrorAsync(
                    connectionId,
                    request.MessageId,
                    ProtocolErrorCodes.Busy,
                    "Another vision workflow is already running.",
                    true)
                    .ConfigureAwait(false);
                return;
            }

            bool workflowGateHeld = true;
            try
            {

                LocalDebugLog.Write(
                    "INFO",
                    "tcp.inspect",
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "Executing inspection.trigger: conn={0}, msgId={1}, currentRLeft={2:0.###}, currentRRight={3:0.###}, expectedCount={4}, archive={5}",
                        connectionId,
                        request.MessageId,
                        currentRLeft,
                        currentRRight,
                        expected,
                        IsArchiveRequested(request)));
                // 视觉结果一经算出立即释放工作流门锁并回传上位机；图像显示、归档和 UI 事件延后到后台线程串行执行，不占用生产 CT
                ProtocolEnvelope response = null;
                InspectionResult completedResult = null;
                try
                {
                    await ExecuteInspectionAsync(
                        expected,
                        currentRLeft,
                        currentRRight,
                        request,
                        CancellationToken.None,
                        result =>
                        {
                            completedResult = result;
                            response = ProtocolEnvelope.Create(ProtocolMessageTypes.InspectionResult);
                            response.CorrelationId = request.MessageId;
                            FillInspectionResponse(response.Data, result);
                            return Task.CompletedTask;
                        }).ConfigureAwait(false);
                }
                finally
                {
                    if (workflowGateHeld)
                    {
                        workflowGateHeld = false;
                        _workflowGate.Release();
                    }
                }

                if (response != null)
                {
                    await _tcpServer.SendAsync(connectionId, response).ConfigureAwait(false);
                    LocalDebugLog.Write(
                        completedResult != null && completedResult.Success && completedResult.Pass ? "INFO" : "WARN",
                        "tcp.tx",
                        "Sent inspection.result conn=" + connectionId + ", corrId=" + response.CorrelationId +
                        ", " + StationLogFormatter.FormatInspectionResult(completedResult));
                }
            }
            finally
            {
                if (workflowGateHeld)
                {
                    workflowGateHeld = false;
                    _workflowGate.Release();
                }
            }
        }

        private async Task HandleTeachingSubmitAsync(ProtocolEnvelope request, string connectionId)
        {
            bool cameraEnabled;
            bool calibrationEnabled;
            int cameraTimeoutMs;
            lock (_stateSync)
            {
                cameraEnabled = _options.CameraEnabled;
                calibrationEnabled = _options.CalibrationEnabled;
                cameraTimeoutMs = _options.CameraTimeoutMilliseconds;
            }
            if (!calibrationEnabled)
            {
                LocalDebugLog.Write("WARN", "tcp.teach", "Rejected teaching.submit (CALIBRATION_DISABLED): conn=" + connectionId + ", msgId=" + request.MessageId);
                await SendErrorAsync(
                    connectionId,
                    request.MessageId,
                    ProtocolErrorCodes.CalibrationDisabled,
                    "Teaching is unavailable because calibration is disabled.",
                    false).ConfigureAwait(false);
                return;
            }
            if (!cameraEnabled)
            {
                LocalDebugLog.Write("WARN", "tcp.teach", "Rejected teaching.submit (CAMERA_DISABLED): conn=" + connectionId + ", msgId=" + request.MessageId);
                await SendErrorAsync(
                    connectionId,
                    request.MessageId,
                    ProtocolErrorCodes.CameraDisabled,
                    "Teaching is unavailable because camera use is disabled.",
                    false).ConfigureAwait(false);
                return;
            }
            if (!IsCameraConnected)
            {
                LocalDebugLog.Write("WARN", "tcp.teach", "Rejected teaching.submit (CAMERA_NOT_CONNECTED): conn=" + connectionId + ", msgId=" + request.MessageId);
                await SendErrorAsync(
                    connectionId,
                    request.MessageId,
                    ProtocolErrorCodes.CameraNotConnected,
                    "Teaching is unavailable because the camera is not connected.",
                    true).ConfigureAwait(false);
                return;
            }

            TeachingSide side;
            MachinePose machinePose;
            string parseError;
            if (!TryParseTeachingSubmit(request.Data, out side, out machinePose, out parseError))
            {
                LocalDebugLog.Write("WARN", "tcp.teach", "Rejected teaching.submit (PROTOCOL_INVALID): conn=" + connectionId + ", msgId=" + request.MessageId + ", detail=" + parseError);
                await SendErrorAsync(
                    connectionId,
                    request.MessageId,
                    ProtocolErrorCodes.ProtocolInvalid,
                    parseError,
                    false).ConfigureAwait(false);
                return;
            }

            if (!await _workflowGate.WaitAsync(0).ConfigureAwait(false))
            {
                LocalDebugLog.Write("WARN", "tcp.teach", "Rejected teaching.submit (BUSY): conn=" + connectionId + ", msgId=" + request.MessageId);
                await SendErrorAsync(
                    connectionId,
                    request.MessageId,
                    ProtocolErrorCodes.Busy,
                    "Another vision workflow is already running.",
                    true).ConfigureAwait(false);
                return;
            }

            bool workflowGateHeld = true;
            bool teachingSucceeded = false;
            bool responseSent = false;
            ProtocolEnvelope response = null;
            try
            {
                LocalDebugLog.Write(
                    "INFO",
                    "tcp.teach",
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "Executing teaching.submit: conn={0}, msgId={1}, nozzle={2}, axisR={3}, machine=({4:0.###},{5:0.###},{6:0.###}deg)",
                        connectionId,
                        request.MessageId,
                        ProtocolNozzle.GetNozzleName(side),
                        ProtocolNozzle.GetAxisR(side),
                        machinePose.X,
                        machinePose.Y,
                        machinePose.ThetaDegrees));

                int teachingCaptureLimitMs = Math.Max(200, Math.Min(cameraTimeoutMs <= 0 ? 2000 : cameraTimeoutMs, 2000));
                lock (_statusPushSync) _deferTeachingStatusPush = true;
                try
                {
                    TeachingPose savedPose = await _teaching
                        .CaptureAsync(side, machinePose, teachingCaptureLimitMs, CancellationToken.None)
                        .ConfigureAwait(false);
                    teachingSucceeded = true;
                    response = BuildTeachingResultEnvelope(
                        request.MessageId,
                        side,
                        true,
                        ProtocolErrorCodes.None,
                        "Teaching saved.",
                        savedPose,
                        machinePose);
                }
                catch (TeachingWorkflowException ex)
                {
                    response = BuildTeachingResultEnvelope(
                        request.MessageId,
                        side,
                        false,
                        ex.ErrorCode,
                        ex.Message,
                        null,
                        machinePose);
                }
                catch (Exception ex)
                {
                    LocalDebugLog.WriteException("ERROR", "tcp.teach", "Unexpected exception during teaching.submit", ex);
                    response = BuildTeachingResultEnvelope(
                        request.MessageId,
                        side,
                        false,
                        ProtocolErrorCodes.InternalError,
                        ex.Message,
                        null,
                        machinePose);
                }
                finally
                {
                    if (workflowGateHeld)
                    {
                        workflowGateHeld = false;
                        _workflowGate.Release();
                    }
                }

                if (response != null)
                {
                    await _tcpServer.SendAsync(connectionId, response).ConfigureAwait(false);
                    responseSent = true;
                    LocalDebugLog.Write(
                        teachingSucceeded ? "INFO" : "WARN",
                        "tcp.tx",
                        "conn=" + connectionId + " " + StationLogFormatter.FormatEnvelope(response, _logCodec));
                }
            }
            finally
            {
                lock (_statusPushSync) _deferTeachingStatusPush = false;
                if (workflowGateHeld)
                {
                    workflowGateHeld = false;
                    _workflowGate.Release();
                }
                if (responseSent && teachingSucceeded)
                {
                    NotifySystemStatusChangedIfNeeded();
                }
            }
        }

        private static bool TryParseTeachingSubmit(
            IDictionary<string, object> data,
            out TeachingSide side,
            out MachinePose machinePose,
            out string errorMessage)
        {
            side = TeachingSide.Left;
            machinePose = null;
            errorMessage = null;

            string rawNozzle = ProtocolValue.String(data, "nozzle", string.Empty).Trim();
            string rawAxis = ProtocolValue.String(data, "axisR", string.Empty).Trim();
            bool hasNozzle = !string.IsNullOrWhiteSpace(rawNozzle);
            bool hasAxis = !string.IsNullOrWhiteSpace(rawAxis);
            if (!hasNozzle && !hasAxis)
            {
                errorMessage = "teaching.submit requires nozzle (Left/Right) or axisR (R1/R2).";
                return false;
            }

            TeachingSide nozzleSide = TeachingSide.Left;
            if (hasNozzle)
            {
                if (string.Equals(rawNozzle, "Left", StringComparison.OrdinalIgnoreCase))
                    nozzleSide = TeachingSide.Left;
                else if (string.Equals(rawNozzle, "Right", StringComparison.OrdinalIgnoreCase))
                    nozzleSide = TeachingSide.Right;
                else
                {
                    errorMessage = "teaching.submit nozzle must be Left or Right.";
                    return false;
                }
            }

            TeachingSide axisSide = TeachingSide.Left;
            if (hasAxis)
            {
                if (string.Equals(rawAxis, "R1", StringComparison.OrdinalIgnoreCase))
                    axisSide = TeachingSide.Left;
                else if (string.Equals(rawAxis, "R2", StringComparison.OrdinalIgnoreCase))
                    axisSide = TeachingSide.Right;
                else
                {
                    errorMessage = "teaching.submit axisR must be R1 or R2.";
                    return false;
                }
            }

            if (hasNozzle && hasAxis && nozzleSide != axisSide)
            {
                errorMessage = "teaching.submit nozzle '" + rawNozzle + "' conflicts with axisR '" + rawAxis + "'.";
                return false;
            }

            side = hasNozzle ? nozzleSide : axisSide;

            double x;
            double y;
            double r;
            if (!ProtocolValue.TryDouble(data, "x", out x) || double.IsNaN(x) || double.IsInfinity(x))
            {
                errorMessage = "teaching.submit requires a finite numeric x coordinate.";
                return false;
            }
            if (!ProtocolValue.TryDouble(data, "y", out y) || double.IsNaN(y) || double.IsInfinity(y))
            {
                errorMessage = "teaching.submit requires a finite numeric y coordinate.";
                return false;
            }
            if (!ProtocolValue.TryDouble(data, "r", out r) || double.IsNaN(r) || double.IsInfinity(r))
            {
                errorMessage = "teaching.submit requires a finite numeric r angle.";
                return false;
            }

            machinePose = new MachinePose(x, y, r);
            return true;
        }

        private static ProtocolEnvelope BuildTeachingResultEnvelope(
            string correlationId,
            TeachingSide side,
            bool success,
            string errorCode,
            string message,
            TeachingPose savedPose,
            MachinePose inputPose)
        {
            ProtocolEnvelope envelope = ProtocolEnvelope.Create(ProtocolMessageTypes.TeachingResult);
            envelope.CorrelationId = correlationId;
            IDictionary<string, object> data = envelope.Data;
            data["nozzle"] = ProtocolNozzle.GetNozzleName(side);
            data["axisR"] = ProtocolNozzle.GetAxisR(side);
            data["success"] = success;
            data["errorCode"] = string.IsNullOrWhiteSpace(errorCode)
                ? ProtocolErrorCodes.None
                : errorCode.Trim().ToUpperInvariant();
            data["message"] = message ?? string.Empty;
            data["machineX"] = ProtocolNumber.Round3(savedPose != null ? savedPose.MachineX : (inputPose != null ? inputPose.X : 0.0));
            data["machineY"] = ProtocolNumber.Round3(savedPose != null ? savedPose.MachineY : (inputPose != null ? inputPose.Y : 0.0));
            data["machineR"] = ProtocolNumber.Round3(savedPose != null ? savedPose.MachineR : (inputPose != null ? inputPose.ThetaDegrees : 0.0));
            data["imageRow"] = ProtocolNumber.Round2(savedPose != null ? savedPose.ImageRow : 0.0);
            data["imageColumn"] = ProtocolNumber.Round2(savedPose != null ? savedPose.ImageColumn : 0.0);
            data["imageAngle"] = ProtocolNumber.Round2(savedPose != null ? savedPose.ImageAngleDegrees : 0.0);
            data["score"] = ProtocolNumber.Round3(savedPose != null ? savedPose.MatchScore : 0.0);
            return envelope;
        }

        private static bool TryParseInspectionTriggerAngles(
            IDictionary<string, object> data,
            bool requireAngles,
            out double currentRLeft,
            out double currentRRight,
            out string errorMessage)
        {
            currentRLeft = 0.0;
            currentRRight = 0.0;
            errorMessage = null;

            bool hasLeft = data != null && data.ContainsKey("currentRLeft");
            bool hasRight = data != null && data.ContainsKey("currentRRight");
            bool hasLegacy = data != null && data.ContainsKey("currentR");

            if (hasLeft ^ hasRight)
            {
                errorMessage = "inspection.trigger requires both currentRLeft and currentRRight when dual-R fields are used.";
                return false;
            }

            if (hasLeft && hasRight)
            {
                if (!ProtocolValue.TryDouble(data, "currentRLeft", out currentRLeft) ||
                    double.IsNaN(currentRLeft) || double.IsInfinity(currentRLeft))
                {
                    errorMessage = "inspection.trigger requires a finite currentRLeft value.";
                    return false;
                }
                if (!ProtocolValue.TryDouble(data, "currentRRight", out currentRRight) ||
                    double.IsNaN(currentRRight) || double.IsInfinity(currentRRight))
                {
                    errorMessage = "inspection.trigger requires a finite currentRRight value.";
                    return false;
                }
                return true;
            }

            if (hasLegacy)
            {
                double legacyR;
                if (!ProtocolValue.TryDouble(data, "currentR", out legacyR) ||
                    double.IsNaN(legacyR) || double.IsInfinity(legacyR))
                {
                    errorMessage = "inspection.trigger requires a finite currentR value.";
                    return false;
                }
                currentRLeft = legacyR;
                currentRRight = legacyR;
                return true;
            }

            if (requireAngles)
            {
                errorMessage = "inspection.trigger requires finite currentRLeft and currentRRight values.";
                return false;
            }

            return true;
        }

        private async Task<InspectionResult> ExecuteInspectionAsync(
            int expectedCount,
            double currentRLeft,
            double currentRRight,
            ProtocolEnvelope archiveRequest,
            CancellationToken cancellationToken,
            Func<InspectionResult, Task> sendResponse = null)
        {
            await _inspectionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                EnsureCameraEnabled();
                EnsureInspectionEnabled();
                if (!_camera.IsConnected)
                    throw new InvalidOperationException("相机未连接，请先连接相机后再执行在线检测。");
                CalibrationJob positioning;
                CalibrationResult leftCalibration;
                CalibrationResult rightCalibration;
                DetectionProfile profile;
                TeachingData teaching;
                bool calibrationEnabled;
                int cameraTimeoutMs;
                lock (_stateSync)
                {
                    positioning = _positioningJob.DeepClone();
                    leftCalibration = _leftCalibrationResult == null ? null : _leftCalibrationResult.DeepClone();
                    rightCalibration = _rightCalibrationResult == null ? null : _rightCalibrationResult.DeepClone();
                    profile = CloneDetectionProfile(_detectionProfile);
                    teaching = _teaching.GetSnapshot();
                    calibrationEnabled = _options.CalibrationEnabled;
                    cameraTimeoutMs = _options.CameraTimeoutMilliseconds;
                }

                bool calValid = IsCalibrationValid(leftCalibration) && IsCalibrationValid(rightCalibration);
                bool teachValid = IsTeachingValid(teaching);
                bool canMapTargets = calibrationEnabled &&
                    calValid &&
                    teachValid &&
                    !double.IsNaN(currentRLeft) && !double.IsInfinity(currentRLeft) &&
                    !double.IsNaN(currentRRight) && !double.IsInfinity(currentRRight);

                CameraFrame frame;
                if (sendResponse != null)
                {
                    // 生产 TCP 检测采图超时收紧至 <= 2000ms，确保整帧端到端响应在 3000ms 上限内
                    int productionCaptureLimitMs = Math.Max(200, Math.Min(cameraTimeoutMs <= 0 ? 2000 : cameraTimeoutMs, 2000));
                    using (CancellationTokenSource captureCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                    {
                        captureCts.CancelAfter(productionCaptureLimitMs);
                        try
                        {
                            frame = await _camera.CaptureAsync(captureCts.Token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                        {
                            InspectionResult timeoutResult = new InspectionResult
                            {
                                Executed = false,
                                Success = false,
                                Pass = false,
                                ErrorCode = "IMAGE_MISSING",
                                Message = "Camera capture timed out (" + productionCaptureLimitMs + " ms).",
                                ExpectedCount = expectedCount,
                                CurrentRLeft = currentRLeft,
                                CurrentRRight = currentRRight,
                                CurrentR = currentRLeft
                            };
                            LocalDebugLog.Write("WARN", "inspection.capture", "Production capture timed out: " + timeoutResult.Message);
                            await sendResponse(timeoutResult).ConfigureAwait(false);
                            return timeoutResult;
                        }
                        catch (Exception captureEx)
                        {
                            InspectionResult failedResult = new InspectionResult
                            {
                                Executed = false,
                                Success = false,
                                Pass = false,
                                ErrorCode = "IMAGE_MISSING",
                                Message = "Camera capture failed: " + captureEx.Message,
                                ExpectedCount = expectedCount,
                                CurrentRLeft = currentRLeft,
                                CurrentRRight = currentRRight,
                                CurrentR = currentRLeft
                            };
                            LocalDebugLog.WriteException("WARN", "inspection.capture", "Production capture failed", captureEx);
                            await sendResponse(failedResult).ConfigureAwait(false);
                            return failedResult;
                        }
                    }
                }
                else
                {
                    frame = await _camera.CaptureAsync(cancellationToken).ConfigureAwait(false);
                }

                using (frame)
                {
                    LocalDebugLog.Write(
                        "INFO",
                        "inspection.capture",
                        string.Format(
                            CultureInfo.InvariantCulture,
                            "Captured online frame: seq={0}, size={1}, expected={2}, currentRLeft={3:0.###}, currentRRight={4:0.###}, calEn={5}, canMap={6}, tcpMode={7}",
                            frame.Sequence,
                            frame.Image == null ? "null" : frame.Image.Width + "x" + frame.Image.Height,
                            expectedCount,
                            currentRLeft,
                            currentRRight,
                            calibrationEnabled,
                            canMapTargets,
                            sendResponse != null));
                    InspectionResult result = await Task.Run(() => _inspection.InspectCore(
                        frame.Image,
                        positioning,
                        leftCalibration,
                        rightCalibration,
                        teaching,
                        profile,
                        expectedCount,
                        currentRLeft,
                        currentRRight,
                        canMapTargets), cancellationToken).ConfigureAwait(false);
                    RefineUnmappedInspectionMessage(result, calibrationEnabled, canMapTargets, false);

                    if (sendResponse == null)
                    {
                        LocalDebugLog.Write(
                            result != null && result.Success && result.Pass ? "INFO" : "WARN",
                            "inspection.local",
                            StationLogFormatter.FormatInspectionResult(result));
                        PublishFrame(frame.Image, "inspection");
                        OnInspectionCompleted(result);
                        return result;
                    }

                    await sendResponse(result).ConfigureAwait(false);
                    bool archive = archiveRequest != null && IsArchiveRequested(archiveRequest);
                    Bitmap postImage = (Bitmap)frame.Image.Clone();
                    DateTime capturedAtUtc = frame.CapturedAtUtc;
                    QueuePostProcessing(() => PublishInspectionFrame(postImage, capturedAtUtc, archive, result));
                    return result;
                }
            }
            finally
            {
                _inspectionGate.Release();
            }
        }

        private async Task<InspectionResult> ExecuteOfflineInspectionAsync(
            Bitmap image,
            int expectedCount,
            double currentRLeft,
            double currentRRight,
            CancellationToken cancellationToken,
            LockedFrameGrayCache frameCache)
        {
            await _inspectionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                CalibrationJob positioning;
                CalibrationResult leftCalibration;
                CalibrationResult rightCalibration;
                DetectionProfile profile;
                TeachingData teaching;
                bool calibrationEnabled;
                lock (_stateSync)
                {
                    positioning = _positioningJob.DeepClone();
                    leftCalibration = _leftCalibrationResult == null ? null : _leftCalibrationResult.DeepClone();
                    rightCalibration = _rightCalibrationResult == null ? null : _rightCalibrationResult.DeepClone();
                    profile = CloneDetectionProfile(_detectionProfile);
                    teaching = _teaching.GetSnapshot();
                    calibrationEnabled = _options.CalibrationEnabled;
                }

                bool calValid = IsCalibrationValid(leftCalibration) && IsCalibrationValid(rightCalibration);
                bool teachValid = IsTeachingValid(teaching);
                bool canMapTargets = calibrationEnabled &&
                    calValid &&
                    teachValid &&
                    !double.IsNaN(currentRLeft) && !double.IsInfinity(currentRLeft) &&
                    !double.IsNaN(currentRRight) && !double.IsInfinity(currentRRight);

                LocalDebugLog.Write(
                    "INFO",
                    "inspection.offline",
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "Start offline inspection: size={0}x{1}, expected={2}, currentRLeft={3:0.###}, currentRRight={4:0.###}, calEnabled={5}, calValid={6}, teachValid={7}, canMapTargets={8}",
                        image.Width,
                        image.Height,
                        expectedCount,
                        currentRLeft,
                        currentRRight,
                        calibrationEnabled,
                        calValid,
                        teachValid,
                        canMapTargets));

                PublishFrame(image, "offline-inspection");
                InspectionResult result = await Task.Run(
                    () => _inspection.InspectCore(
                        image,
                        positioning,
                        leftCalibration,
                        rightCalibration,
                        teaching,
                        profile,
                        expectedCount,
                        currentRLeft,
                        currentRRight,
                        canMapTargets,
                        frameCache),
                    cancellationToken).ConfigureAwait(false);
                RefineUnmappedInspectionMessage(result, calibrationEnabled, canMapTargets, true);
                LocalDebugLog.Write(
                    result != null && result.Success && result.Pass ? "INFO" : "WARN",
                    "inspection.offline",
                    StationLogFormatter.FormatInspectionResult(result));
                OnInspectionCompleted(result);
                return result;
            }
            finally
            {
                _inspectionGate.Release();
            }
        }

        private static void RefineUnmappedInspectionMessage(
            InspectionResult result,
            bool calibrationEnabled,
            bool canMapTargets,
            bool offline)
        {
            if (canMapTargets || result == null || !result.Executed) return;
            if (string.Equals(result.ErrorCode, ProtocolErrorCodes.NoMaterial, StringComparison.OrdinalIgnoreCase)) return;

            if (string.Equals(result.ErrorCode, ProtocolErrorCodes.CoordinateNotMapped, StringComparison.OrdinalIgnoreCase))
            {
                int ngCount = result.Items == null ? 0 : result.Items.Count(item => item != null && item.HasManualMark);
                int okCount = (result.Items == null ? 0 : result.Items.Count) - ngCount;
                result.Message = calibrationEnabled
                    ? string.Format(
                        CultureInfo.InvariantCulture,
                        "检测已完成，但双吸嘴标定或左右示教未就绪，未生成取料坐标（OK {0} / NG {1}）",
                        okCount, ngCount)
                    : string.Format(
                        CultureInfo.InvariantCulture,
                        "检测已完成，但标定未启用，未生成取料坐标（OK {0} / NG {1}）",
                        okCount, ngCount);
                return;
            }

            if (string.Equals(result.ErrorCode, ProtocolErrorCodes.BoardCountMismatch, StringComparison.OrdinalIgnoreCase))
            {
                string detail = calibrationEnabled
                    ? (offline ? "双吸嘴标定或左右示教未完成，当前仅展示视觉定位与瑕疵检测结果" : "双吸嘴标定或左右示教未完成，未生成取料坐标")
                    : "标定未启用，未生成取料坐标";
                result.Message = string.IsNullOrWhiteSpace(result.Message)
                    ? detail
                    : result.Message + "（" + detail + "）";
            }
        }

        private static void FillInspectionResponse(IDictionary<string, object> data, InspectionResult result)
        {
            double currentRLeftOut = ProtocolNumber.Round3(result.CurrentRLeft);
            double currentRRightOut = ProtocolNumber.Round3(result.CurrentRRight);
            double currentROut = ProtocolNumber.Round3(result.CurrentR);

            data["executed"] = result.Executed;
            data["success"] = result.Success;
            data["pass"] = result.Pass;
            data["coordinateMapped"] = result.CoordinateMappingApplied;
            data["errorCode"] = string.IsNullOrWhiteSpace(result.ErrorCode)
                ? ProtocolErrorCodes.None
                : result.ErrorCode.Trim().ToUpperInvariant();
            data["message"] = result.Message ?? string.Empty;
            data["expectedCount"] = result.ExpectedCount;
            data["actualCount"] = result.ActualCount;
            data["hasManualMarks"] = result.HasManualMarks;
            data["currentRLeft"] = currentRLeftOut;
            data["currentRRight"] = currentRRightOut;
            data["currentR"] = currentROut;
            data["elapsedMilliseconds"] = ProtocolNumber.Round1(result.ElapsedMilliseconds);
            data["matchElapsedMilliseconds"] = ProtocolNumber.Round1(result.MatchElapsedMilliseconds);
            data["blobElapsedMilliseconds"] = ProtocolNumber.Round1(result.BlobElapsedMilliseconds);

            List<object> items = new List<object>();
            foreach (BoardInspectionItem item in result.Items)
            {
                Dictionary<string, object> itemData = new Dictionary<string, object>();
                itemData["boardIndex"] = item.BoardIndex;
                itemData["valid"] = item.Valid;
                itemData["row"] = ProtocolNumber.Round2(item.ImageRow);
                itemData["column"] = ProtocolNumber.Round2(item.ImageColumn);
                itemData["angle"] = ProtocolNumber.Round2(item.ImageAngleDegrees);
                itemData["score"] = ProtocolNumber.Round3(item.MatchScore);
                itemData["hasManualMark"] = item.HasManualMark;

                List<object> targets = new List<object>();
                foreach (MappedTargetPose target in item.Targets)
                {
                    double sideCurrentR = target.Side == TeachingSide.Right ? currentRRightOut : currentRLeftOut;
                    double rOffsetOut = ProtocolNumber.Round3(target.ROffset);
                    double targetROut = ProtocolNumber.Round3(sideCurrentR + rOffsetOut);
                    double blindPlaceROut = ProtocolNumber.Round3(target.BlindPlaceR);

                    targets.Add(new Dictionary<string, object>
                    {
                        { "side", ProtocolNozzle.GetNozzleName(target.Side) },
                        { "axis", string.IsNullOrWhiteSpace(target.Axis) ? ProtocolNozzle.GetAxisR(target.Side) : target.Axis },
                        { "x", ProtocolNumber.Round3(target.X) },
                        { "y", ProtocolNumber.Round3(target.Y) },
                        { "rOffset", rOffsetOut },
                        { "targetR", targetROut },
                        { "pickR", targetROut },
                        { "blindPlaceR", blindPlaceROut }
                    });
                }
                itemData["targets"] = targets;
                items.Add(itemData);
            }
            data["items"] = items;
        }

        private SystemStatusSnapshot CaptureSystemStatusSnapshot()
        {
            bool cameraEnabled = IsCameraEnabled;
            bool cameraConnected = IsCameraConnected;
            bool calibrationEnabled = IsCalibrationEnabled;
            bool calibrationReady = IsCalibrationReady;
            bool inspectionEnabled = IsInspectionEnabled;
            return new SystemStatusSnapshot
            {
                Ready = cameraEnabled && cameraConnected && (inspectionEnabled || calibrationEnabled),
                CameraEnabled = cameraEnabled,
                CameraConnected = cameraConnected,
                CalibrationEnabled = calibrationEnabled,
                CalibrationReady = calibrationReady,
                InspectionEnabled = inspectionEnabled
            };
        }

        private static void FillSystemStatusData(IDictionary<string, object> data, SystemStatusSnapshot snapshot)
        {
            data["role"] = "vision";
            data["ready"] = snapshot.Ready;
            data["cameraEnabled"] = snapshot.CameraEnabled;
            data["cameraConnected"] = snapshot.CameraConnected;
            data["calibrationEnabled"] = snapshot.CalibrationEnabled;
            data["calibrationReady"] = snapshot.CalibrationReady;
            data["inspectionEnabled"] = snapshot.InspectionEnabled;
            data["protocolVersion"] = ProtocolEnvelope.CurrentVersion;
        }

        private void RecordSentSystemStatus(string connectionId, SystemStatusSnapshot snapshot)
        {
            if (string.IsNullOrWhiteSpace(connectionId)) return;
            lock (_statusPushSync)
            {
                _lastStatusPushConnectionId = connectionId;
                _lastStatusPushSnapshot = snapshot;
                _hasLastStatusPush = true;
            }
        }

        private void NotifySystemStatusChangedIfNeeded()
        {
            if (Volatile.Read(ref _disposeRequested) != 0 || _disposed) return;
            string connectionId = _tcpServer.ActiveConnectionId;
            if (string.IsNullOrWhiteSpace(connectionId)) return;

            SystemStatusSnapshot snapshot = CaptureSystemStatusSnapshot();
            lock (_statusPushSync)
            {
                if (_hasLastStatusPush &&
                    string.Equals(_lastStatusPushConnectionId, connectionId, StringComparison.Ordinal) &&
                    _lastStatusPushSnapshot.Equals(snapshot))
                {
                    return;
                }
                _lastStatusPushConnectionId = connectionId;
                _lastStatusPushSnapshot = snapshot;
                _hasLastStatusPush = true;
            }

            _ = PushSystemStatusAsync(connectionId, snapshot);
        }

        private async Task PushSystemStatusAsync(string connectionId, SystemStatusSnapshot snapshot)
        {
            try
            {
                ProtocolEnvelope status = ProtocolEnvelope.Create(ProtocolMessageTypes.SystemStatus);
                status.CorrelationId = null;
                FillSystemStatusData(status.Data, snapshot);
                await _tcpServer.SendAsync(connectionId, status).ConfigureAwait(false);
                LocalDebugLog.Write(
                    "INFO",
                    "tcp.tx.status",
                    "Pushed system.status conn=" + connectionId + " " + StationLogFormatter.FormatEnvelope(status, _logCodec));
            }
            catch (Exception exception)
            {
                LocalDebugLog.Write("DEBUG", "tcp.tx.status", "Skipped system.status push for conn=" + connectionId + ": " + exception.Message);
            }
        }

        private async Task SendErrorAsync(
            string connectionId,
            string correlationId,
            string code,
            string message,
            bool retryable)
        {
            ProtocolEnvelope error = ProtocolEnvelope.Create(ProtocolMessageTypes.Error);
            error.CorrelationId = correlationId;
            error.Data["code"] = code;
            error.Data["message"] = message;
            error.Data["retryable"] = retryable;
            await _tcpServer.SendAsync(connectionId, error).ConfigureAwait(false);
            LocalDebugLog.Write(
                "WARN",
                "tcp.tx.err",
                "conn=" + connectionId + " " + StationLogFormatter.FormatEnvelope(error, _logCodec));
        }

        private bool HandleCalibrationProtocolMessage(ProtocolEnvelope envelope, string connectionId)
        {
            if (envelope == null) return false;
            bool isStartAck = string.Equals(envelope.Type, ProtocolMessageTypes.CalibrationStartedAck, StringComparison.OrdinalIgnoreCase);
            bool isArrived = string.Equals(envelope.Type, ProtocolMessageTypes.MotionArrived, StringComparison.OrdinalIgnoreCase);
            bool isFailed = string.Equals(envelope.Type, ProtocolMessageTypes.MotionFailed, StringComparison.OrdinalIgnoreCase);
            if (!isStartAck && !isArrived && !isFailed) return false;

            // 校验应答报文是否来自发起对应请求的同一 TCP 连接，忽略其他并发连接的误报
            string expectedConnectionId;
            if (!string.IsNullOrWhiteSpace(envelope.CorrelationId) &&
                _calibrationRequestConnections.TryGetValue(
                    envelope.CorrelationId,
                    out expectedConnectionId) &&
                !string.Equals(expectedConnectionId, connectionId, StringComparison.Ordinal))
            {
                OnLog(
                    "WARN",
                    "Ignored " + envelope.Type + " from a different TCP connection for request " +
                    envelope.CorrelationId + ".");
                return true;
            }

            if ((isStartAck || isFailed) && !string.IsNullOrWhiteSpace(envelope.CorrelationId))
            {
                string ignored;
                _calibrationRequestConnections.TryRemove(envelope.CorrelationId, out ignored);
            }

            bool handled = _calibration.HandleProtocolMessage(envelope);
            LocalDebugLog.Write(
                handled ? "INFO" : "WARN",
                "tcp.cal.rx",
                envelope.Type + " conn=" + connectionId + ", handled=" + handled + ", payload=" +
                StationLogFormatter.FormatEnvelope(envelope, _logCodec));
            return true;
        }

        private async Task SendCalibrationMessageAsync(
            ProtocolEnvelope message,
            CancellationToken cancellationToken)
        {
            if (message == null) throw new ArgumentNullException(nameof(message));

            if (string.Equals(message.Type, ProtocolMessageTypes.CalibrationStarted, StringComparison.OrdinalIgnoreCase))
            {
                EnsureCameraEnabled();
                EnsureCalibrationEnabled();
                string connectionId = _tcpServer.ActiveConnectionId;
                if (string.IsNullOrWhiteSpace(connectionId))
                    throw new InvalidOperationException("No upper-computer client is connected for calibration session.");

                if (!_calibrationRequestConnections.TryAdd(message.MessageId, connectionId))
                    throw new InvalidOperationException("Calibration start request connection is already registered.");

                lock (_stateSync) _calibrationCompletionConnectionId = connectionId;
                try
                {
                    await _tcpServer.SendAsync(connectionId, message, cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    string ignored;
                    _calibrationRequestConnections.TryRemove(message.MessageId, out ignored);
                    throw;
                }
                LocalDebugLog.Write("INFO", "tcp.cal.tx", "conn=" + connectionId + " " + StationLogFormatter.FormatEnvelope(message, _logCodec));
                return;
            }

            if (string.Equals(message.Type, ProtocolMessageTypes.MoveRequest, StringComparison.OrdinalIgnoreCase))
            {
                EnsureCameraEnabled();
                EnsureCalibrationEnabled();
                string connectionId;
                lock (_stateSync) connectionId = _calibrationCompletionConnectionId;
                if (string.IsNullOrWhiteSpace(connectionId)) connectionId = _tcpServer.ActiveConnectionId;
                if (string.IsNullOrWhiteSpace(connectionId))
                    throw new InvalidOperationException("No upper-computer client is connected for calibration motion.");

                // 绑定本次 move.request 的 MessageId 与来源 TCP 连接，确保后续结果精确路由回原连接
                if (!_calibrationRequestConnections.TryAdd(message.MessageId, connectionId))
                    throw new InvalidOperationException("Calibration request connection is already registered.");

                lock (_stateSync) _calibrationCompletionConnectionId = connectionId;
                try
                {
                    await _tcpServer.SendAsync(connectionId, message, cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    string ignored;
                    _calibrationRequestConnections.TryRemove(message.MessageId, out ignored);
                    throw;
                }
                LocalDebugLog.Write("INFO", "tcp.cal.tx", "conn=" + connectionId + " " + StationLogFormatter.FormatEnvelope(message, _logCodec));
                return;
            }

            if (string.Equals(
                message.Type,
                ProtocolMessageTypes.CalibrationPointResult,
                StringComparison.OrdinalIgnoreCase))
            {
                string connectionId;
                if (string.IsNullOrWhiteSpace(message.CorrelationId) ||
                    !_calibrationRequestConnections.TryGetValue(message.CorrelationId, out connectionId))
                {
                    throw new InvalidOperationException(
                        "Calibration point result has no originating TCP connection.");
                }

                try
                {
                    await _tcpServer.SendAsync(connectionId, message, cancellationToken).ConfigureAwait(false);
                    LocalDebugLog.Write("INFO", "tcp.cal.tx", "conn=" + connectionId + " " + StationLogFormatter.FormatEnvelope(message, _logCodec));
                }
                finally
                {
                    string ignored;
                    _calibrationRequestConnections.TryRemove(message.CorrelationId, out ignored);
                }
                return;
            }

            if (string.Equals(
                message.Type,
                ProtocolMessageTypes.CalibrationCompleted,
                StringComparison.OrdinalIgnoreCase))
            {
                string connectionId;
                lock (_stateSync) connectionId = _calibrationCompletionConnectionId;
                if (string.IsNullOrWhiteSpace(connectionId)) connectionId = _tcpServer.ActiveConnectionId;
                if (string.IsNullOrWhiteSpace(connectionId))
                    throw new InvalidOperationException(
                        "Calibration completion has no originating TCP connection.");

                try
                {
                    await _tcpServer.SendAsync(connectionId, message, cancellationToken).ConfigureAwait(false);
                    LocalDebugLog.Write("INFO", "tcp.cal.tx", "conn=" + connectionId + " " + StationLogFormatter.FormatEnvelope(message, _logCodec));
                }
                finally
                {
                    ClearCalibrationRequestConnectionsFor(connectionId);
                    lock (_stateSync)
                    {
                        if (string.Equals(
                            _calibrationCompletionConnectionId,
                            connectionId,
                            StringComparison.Ordinal))
                        {
                            _calibrationCompletionConnectionId = null;
                        }
                    }
                }
                return;
            }

            throw new InvalidOperationException(
                "Unsupported calibration protocol message type: " + message.Type + ".");
        }

        private void ClearCalibrationRequestConnectionsFor(string connectionId)
        {
            if (string.IsNullOrWhiteSpace(connectionId)) return;
            foreach (KeyValuePair<string, string> pair in _calibrationRequestConnections)
            {
                if (string.Equals(pair.Value, connectionId, StringComparison.Ordinal))
                {
                    string ignored;
                    _calibrationRequestConnections.TryRemove(pair.Key, out ignored);
                }
            }
        }

        private bool IsArchiveRequested(ProtocolEnvelope request)
        {
            bool archive;
            lock (_stateSync) archive = _options.ArchiveImages;
            object value;
            if (request.Data != null && request.Data.TryGetValue("archiveImage", out value) && value != null)
            {
                bool requested;
                if (bool.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), out requested)) archive = requested;
            }
            return archive;
        }

        /// <summary>
        /// 在后台线程严格按触发顺序执行回包后的收尾任务，确保 UI 展示与归档有序且不阻塞 TCP 响应。
        /// </summary>
        private void QueuePostProcessing(Action action)
        {
            lock (_postProcessingSync)
            {
                _postProcessingTail = _postProcessingTail.ContinueWith(
                    previous =>
                    {
                        try
                        {
                            action();
                        }
                        catch (Exception exception)
                        {
                            LocalDebugLog.WriteException("ERROR", "inspection.post", "Inspection post-processing failed", exception);
                            OnLog("ERROR", "Inspection post-processing failed: " + exception.Message);
                        }
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.None,
                    TaskScheduler.Default);
            }
        }

        private void PublishInspectionFrame(
            Bitmap ownedImage,
            DateTime capturedAtUtc,
            bool archive,
            InspectionResult result)
        {
            try
            {
                // 事件订阅方会自行克隆所需图像，因此 ownsImage 置 false 并将所有权留在本方法内供后续归档并释放，
                // 同时通过 ForwardFrame 隔离单订阅方异常避免中断检测完成通知与归档。
                ForwardFrame(this, new FrameEventArgs(ownedImage, "inspection", false));
                OnInspectionCompleted(result);
                if (archive) ArchiveFrame(ownedImage, capturedAtUtc);
            }
            finally
            {
                ownedImage.Dispose();
            }
        }

        private void ArchiveFrame(Bitmap image, DateTime capturedAtUtc)
        {
            string directory;
            lock (_stateSync) directory = _options.ImageArchiveDirectory;
            Directory.CreateDirectory(directory);
            string timestamp = (capturedAtUtc == default(DateTime)
                ? DateTime.UtcNow
                : capturedAtUtc).ToLocalTime().ToString(
                    "yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture);
            string path = Path.Combine(directory, timestamp + ".png");
            int suffix = 1;
            while (File.Exists(path))
            {
                path = Path.Combine(
                    directory,
                    timestamp + "_" + suffix.ToString("00", CultureInfo.InvariantCulture) + ".png");
                suffix++;
            }
            image.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            LocalDebugLog.Write("INFO", "archive", "Saved inspection frame to '" + path + "' (" + image.Width + "x" + image.Height + ").");
        }

        private Task SaveCalibrationJobAsync(TeachingSide nozzle, CalibrationJob job)
        {
            lock (_jobSaveSync)
            {
                string path;
                lock (_stateSync)
                {
                    path = nozzle == TeachingSide.Right ? _options.RightCalibrationJobPath : _options.CalibrationJobPath;
                }
                SaveJob(job, path);
                lock (_stateSync)
                {
                    if (nozzle == TeachingSide.Right) _rightCalibrationJob = job.DeepClone();
                    else _leftCalibrationJob = job.DeepClone();
                }
            }
            return Task.CompletedTask;
        }

        private Task SaveTeachingAsync(TeachingData data)
        {
            _jsonStore.Save(_options.TeachingPath, data);
            lock (_stateSync) _teachingData = data;
            return Task.CompletedTask;
        }

        private void SaveDetectionProfile(DetectionProfile profile)
        {
            _jsonStore.Save(_options.DetectionProfilePath, profile);
        }

        private void SaveJob(CalibrationJob job, string path)
        {
            JobPersistenceResult result = _sdk.TrySaveJob(path, job);
            if (!result.Success) throw new IOException(result.Message);
        }

        private CalibrationJob LoadJobOrDefault(string path, string name)
        {
            if (File.Exists(path))
            {
                JobPersistenceResult loaded = _sdk.TryLoadJob(path);
                if (loaded.Success && loaded.Job != null) return loaded.Job;
                LocalDebugLog.Write("WARN", "job.load", "Failed to load job '" + name + "' at '" + path + "', using empty job. Msg=" + loaded.Message);
            }
            else
            {
                LocalDebugLog.Write("INFO", "job.load", "Job file not found for '" + name + "' at '" + path + "', initialized empty job.");
            }
            return new CalibrationJob { Name = name };
        }

        private T LoadJsonOrDefault<T>(string path, Func<T> factory) where T : class
        {
            if (!File.Exists(path))
            {
                LocalDebugLog.Write("INFO", "config.load", "JSON file not found at '" + path + "', using default " + typeof(T).Name + ".");
                return factory();
            }

            T value;
            string error;
            if (_jsonStore.TryLoad(path, out value, out error) && value != null) return value;
            LocalDebugLog.Write("WARN", "config.load", "Failed to parse JSON '" + path + "' as " + typeof(T).Name + ", using default. Error=" + error);
            return factory();
        }

        private static ICameraDevice CreateCameraDevice(StationOptions options)
        {
            if (string.Equals(options.CameraProvider, "Hikvision", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(options.CameraProvider, "MVS", StringComparison.OrdinalIgnoreCase))
            {
                return new HikvisionCameraDevice();
            }
            return new DahuaCameraDevice(options.CameraTimeoutMilliseconds);
        }

        private static string NormalizeCameraProvider(string provider)
        {
            if (string.Equals(provider, "Hikvision", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(provider, "MVS", StringComparison.OrdinalIgnoreCase))
                return "Hikvision";
            return "Dahua";
        }

        private static bool IsNoCameraFoundMessage(string message)
        {
            if (string.IsNullOrWhiteSpace(message)) return false;
            return message.IndexOf("No camera", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   message.IndexOf("not found", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   message.IndexOf("未检测到在线相机", StringComparison.Ordinal) >= 0 ||
                   message.IndexOf("未发现", StringComparison.Ordinal) >= 0;
        }

        private static bool IsCalibrationValid(CalibrationResult calibration)
        {
            return calibration != null && calibration.TranslationSuccess &&
                   calibration.Rotation != null && calibration.Rotation.Success &&
                   calibration.Rotation.IsAvailable && Math.Abs(calibration.Rotation.DirectionMultiplier) >= 0.5;
        }

        private static bool IsTeachingValid(TeachingData teaching)
        {
            return teaching != null &&
                   teaching.Left != null && teaching.Left.IsValid &&
                   teaching.Right != null && teaching.Right.IsValid;
        }

        private static void ValidateOptions(StationOptions options)
        {
            if (options.TcpPort < 1 || options.TcpPort > 65535)
                throw new ArgumentOutOfRangeException(nameof(options.TcpPort));
            if (double.IsNaN(options.TranslationStepX) || double.IsInfinity(options.TranslationStepX) || options.TranslationStepX <= 0.0 ||
                double.IsNaN(options.TranslationStepY) || double.IsInfinity(options.TranslationStepY) || options.TranslationStepY <= 0.0 ||
                double.IsNaN(options.RotationStepDegrees) || double.IsInfinity(options.RotationStepDegrees) || options.RotationStepDegrees <= 0.0)
                throw new ArgumentOutOfRangeException("Calibration step values must be positive finite numbers.");
            if (options.ExpectedBoardCount <= 0)
                throw new ArgumentOutOfRangeException(nameof(options.ExpectedBoardCount));
        }

        private void EnsureCameraEnabled()
        {
            bool enabled;
            lock (_stateSync) enabled = _options.CameraEnabled;
            if (!enabled)
                throw new InvalidOperationException("Camera use is disabled in station settings.");
        }

        private void EnsureCalibrationEnabled()
        {
            bool enabled;
            lock (_stateSync) enabled = _options.CalibrationEnabled;
            if (!enabled)
                throw new InvalidOperationException("Calibration use is disabled in station settings.");
        }

        private void EnsureInspectionEnabled()
        {
            bool enabled;
            lock (_stateSync) enabled = _options.InspectionEnabled;
            if (!enabled)
                throw new InvalidOperationException("检测功能已在设置中关闭，请先在「相机」页开启「启用检测」。");
        }

        private async Task<T> RunExclusiveAsync<T>(
            string operationName,
            Func<Task<T>> operation,
            CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            if (operation == null) throw new ArgumentNullException(nameof(operation));
            if (!await _workflowGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            {
                LocalDebugLog.Write("WARN", "workflow", "Rejected '" + operationName + "' because another workflow is active.");
                throw new InvalidOperationException(
                    "Cannot start " + operationName + " because another vision workflow is running.");
            }

            Stopwatch watch = Stopwatch.StartNew();
            LocalDebugLog.Write("INFO", "workflow", "Start: " + operationName);
            try
            {
                T result = await operation().ConfigureAwait(false);
                watch.Stop();
                LocalDebugLog.Write("INFO", "workflow", "Completed: " + operationName + " in " + watch.Elapsed.TotalMilliseconds.ToString("0.0", CultureInfo.InvariantCulture) + "ms");
                return result;
            }
            catch (OperationCanceledException)
            {
                watch.Stop();
                LocalDebugLog.Write("WARN", "workflow", "Cancelled: " + operationName + " after " + watch.Elapsed.TotalMilliseconds.ToString("0.0", CultureInfo.InvariantCulture) + "ms");
                throw;
            }
            catch (Exception exception)
            {
                watch.Stop();
                LocalDebugLog.WriteException("ERROR", "workflow", "Failed: " + operationName + " after " + watch.Elapsed.TotalMilliseconds.ToString("0.0", CultureInfo.InvariantCulture) + "ms", exception);
                throw;
            }
            finally
            {
                _workflowGate.Release();
            }
        }

        private void EnterSynchronousWorkflow(string operationName)
        {
            if (!_workflowGate.Wait(0))
            {
                LocalDebugLog.Write("WARN", "workflow", "Rejected synchronous '" + operationName + "' because another workflow is active.");
                throw new InvalidOperationException(
                    "Cannot start " + operationName + " because another vision workflow is running.");
            }
            LocalDebugLog.Write("INFO", "workflow", "Enter synchronous: " + operationName);
        }

        private void PersistOptionsSnapshot()
        {
            StationOptions snapshot;
            lock (_stateSync) snapshot = _options.DeepClone();
            _jsonStore.Save(_optionsPath, snapshot);
        }

        private void PublishCalibrationRecords(TeachingSide nozzle)
        {
            foreach (CalibrationPointRecord record in _calibration.GetRecordsSnapshot(nozzle))
                OnCalibrationPointChanged(this, new CalibrationPointEventArgs(nozzle, record));
        }

        private static DetectionProfile CloneDetectionProfile(DetectionProfile source)
        {
            if (source == null) return new DetectionProfile();
            return new DetectionProfile
            {
                Roi = source.Roi == null ? new DetectionRoi() : new DetectionRoi
                {
                    ReferenceCenterRow = source.Roi.ReferenceCenterRow,
                    ReferenceCenterColumn = source.Roi.ReferenceCenterColumn,
                    PhiDegrees = source.Roi.PhiDegrees,
                    HalfWidth = source.Roi.HalfWidth,
                    HalfHeight = source.Roi.HalfHeight
                },
                AutoThreshold = source.AutoThreshold,
                DetectDarkForeground = source.DetectDarkForeground,
                BrightThreshold = source.BrightThreshold,
                MinimumBlobArea = source.MinimumBlobArea,
                MaximumBlobArea = source.MaximumBlobArea,
                RectangleFilterEnabled = source.RectangleFilterEnabled,
                RectangleFillThreshold = source.RectangleFillThreshold,
                FillHoles = source.FillHoles,
                FillDarkHoles = source.FillDarkHoles,
                MorphologyRadius = source.MorphologyRadius,
                FollowTemplateTransform = source.FollowTemplateTransform,
                TemplateMatchingEnabled = source.TemplateMatchingEnabled
            };
        }

        // 兼容旧版配置文件：若定位模板尚未包含 DetectionRoi，则从 DetectionProfile.Roi 自动迁移
        private static bool MigrateLegacyDetectionRoi(
            CalibrationJob positioningJob,
            DetectionProfile profile)
        {
            if (positioningJob == null || positioningJob.Template == null ||
                positioningJob.Template.DetectionRoi != null || profile == null || profile.Roi == null ||
                profile.Roi.HalfWidth <= 0.0 || profile.Roi.HalfHeight <= 0.0)
            {
                return false;
            }

            positioningJob.Template.DetectionRoi = new RegionDefinition
            {
                Kind = RegionKind.Rectangle2,
                CenterRow = profile.Roi.ReferenceCenterRow,
                CenterColumn = profile.Roi.ReferenceCenterColumn,
                // DetectionRoi.PhiDegrees 在行向下屏幕坐标系中顺时针为正，RegionDefinition.PhiRadians 为 HALCON 逆时针弧度，需取反。
                PhiRadians = -profile.Roi.PhiDegrees * Math.PI / 180.0,
                Length1 = profile.Roi.HalfWidth,
                Length2 = profile.Roi.HalfHeight
            };
            return true;
        }

        private void OnCalibrationPointChanged(object sender, CalibrationPointEventArgs e)
        {
            EventHandler<CalibrationPointEventArgs> handler = CalibrationPointChanged;
            if (handler != null) handler(this, e);
        }

        private void OnCalibrationCalculationCompleted(object sender, CalibrationResultEventArgs e)
        {
            if (e != null)
            {
                lock (_stateSync)
                {
                    // 仅在求解成功或明确加载/重置（无旧结果覆盖风险）时更新有效结果；
                    // 若本次自动/手动标定会话失败（有 sessionId 且 !Success），保留旧标定结果不覆盖！
                    bool isFailedSession = !string.IsNullOrWhiteSpace(e.SessionId) && (e.Result == null || !e.Result.Success);
                    if (!isFailedSession)
                    {
                        if (e.Nozzle == TeachingSide.Right)
                            _rightCalibrationResult = e.Result == null ? null : e.Result.DeepClone();
                        else
                            _leftCalibrationResult = e.Result == null ? null : e.Result.DeepClone();
                    }
                }
                NotifySystemStatusChangedIfNeeded();
            }
            EventHandler<CalibrationResultEventArgs> handler = CalibrationCompleted;
            if (handler != null) handler(this, e);
        }

        private void OnTeachingChanged(object sender, TeachingEventArgs e)
        {
            lock (_stateSync) _teachingData = _teaching.GetSnapshot();
            bool deferPush;
            lock (_statusPushSync) deferPush = _deferTeachingStatusPush;
            if (!deferPush) NotifySystemStatusChangedIfNeeded();
            EventHandler<TeachingEventArgs> handler = TeachingChanged;
            if (handler != null) handler(this, e);
        }

        private void OnInspectionCompleted(InspectionResult result)
        {
            EventHandler<InspectionEventArgs> handler = InspectionCompleted;
            if (handler != null) handler(this, new InspectionEventArgs(result));
        }

        private void ForwardTaggedLog(string category, object sender, LogEventArgs e)
        {
            if (e != null)
            {
                LocalDebugLog.Write(e.Level, category, e.Message);
            }
            ForwardLog(sender, e);
        }

        private void ForwardLog(object sender, LogEventArgs e)
        {
            EventHandler<LogEventArgs> handler = Log;
            if (handler != null) handler(this, e);
        }

        private void OnCameraPreviewHealthChanged(bool available, string detail)
        {
            LocalDebugLog.Write(available ? "INFO" : "WARN", "camera.preview", "PreviewHealth available=" + available + ", detail='" + detail + "'");
            OnStateChanged("camera", available, detail);
            NotifySystemStatusChangedIfNeeded();
        }

        private void OnCameraPreviewRunningChanged(bool running)
        {
            LocalDebugLog.Write("INFO", "camera.live", "PreviewRunningChanged running=" + running);
            OnStateChanged("camera.live", running, running ? "Running" : "Stopped");
        }

        private void ForwardFrame(object sender, FrameEventArgs e)
        {
            EventHandler<FrameEventArgs> handler = FrameReady;
            if (handler == null) return;
            foreach (EventHandler<FrameEventArgs> subscriber in handler.GetInvocationList())
            {
                try
                {
                    subscriber(this, e);
                }
                catch (Exception exception)
                {
                    LocalDebugLog.WriteException("WARN", "runtime.frame", "FrameReady subscriber threw an exception", exception);
                }
            }
        }

        private void PublishFrame(Bitmap image, string source)
        {
            EventHandler<FrameEventArgs> handler = FrameReady;
            if (handler == null || image == null) return;
            using (FrameEventArgs args = new FrameEventArgs((Bitmap)image.Clone(), source))
                ForwardFrame(this, args);
        }

        private void OnLog(string level, string message)
        {
            StationLog.Write(level, "runtime", message);
            LocalDebugLog.Write(level, "runtime", message);
            EventHandler<LogEventArgs> handler = Log;
            if (handler == null) return;
            try
            {
                handler(this, new LogEventArgs(level, message));
            }
            catch
            {
            }
        }

        private void OnStateChanged(string component, bool available, string detail)
        {
            EventHandler<RuntimeStateEventArgs> handler = StateChanged;
            if (handler == null) return;
            foreach (EventHandler<RuntimeStateEventArgs> subscriber in handler.GetInvocationList())
            {
                try
                {
                    subscriber(this, new RuntimeStateEventArgs(component, available, detail));
                }
                catch (Exception exception)
                {
                    LocalDebugLog.WriteException("WARN", "runtime.state", "StateChanged subscriber threw an exception for component='" + component + "'", exception);
                }
            }
        }

        private void ThrowIfDisposed()
        {
            if (Volatile.Read(ref _disposeRequested) != 0 || _disposed)
                throw new ObjectDisposedException(nameof(StationRuntime));
        }

        /// <summary>停止运行并等待在途工作流及后台归档队列收尾后释放所有服务资源。</summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposeRequested, 1) != 0) return;
            LocalDebugLog.Write("INFO", "shutdown", "StationRuntime.Dispose begin.");
            try { StopAsync().GetAwaiter().GetResult(); } catch (Exception ex) { LocalDebugLog.WriteException("WARN", "shutdown", "StopAsync during Dispose", ex); }
            // 等待当前独占工作流和串行后处理队列退出后再销毁信号量与底层设备
            bool gateAcquired = _workflowGate.Wait(TimeSpan.FromSeconds(10));
            _disposed = true;
            if (gateAcquired) _workflowGate.Release();
            Task postProcessing;
            lock (_postProcessingSync) postProcessing = _postProcessingTail;
            try { postProcessing.Wait(TimeSpan.FromSeconds(5)); } catch { }
            _inspectionGate.Dispose();
            _workflowGate.Dispose();
            _teaching.Dispose();
            _calibration.Dispose();
            _camera.Dispose();
            _tcpServer.Dispose();
            _sdk.Dispose();
            LocalDebugLog.Write("INFO", "shutdown", "StationRuntime.Dispose completed.");
            LocalDebugLog.Flush(2000);
        }
    }
}
