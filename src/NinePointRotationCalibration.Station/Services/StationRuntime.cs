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
        private CalibrationJob _calibrationJob;
        private CalibrationJob _positioningJob;
        private CalibrationResult _calibrationResult;
        private DetectionProfile _detectionProfile;
        private TeachingData _teachingData;
        private string _calibrationCompletionConnectionId;
        private bool _started;
        private bool _disposed;

        private StationRuntime(string optionsPath, StationOptions options)
            : this(optionsPath, options, null)
        {
        }

        private StationRuntime(string optionsPath, StationOptions options, ICameraDevice cameraDevice)
        {
            LocalDebugLog.EnsureInitialized();
            _optionsPath = optionsPath;
            _options = options ?? throw new ArgumentNullException(nameof(options));
            bool migrated = _options.NormalizeAfterLoad();
            _jsonStore = new AtomicJsonStore();
            _sdk = new NinePointRotationCalibrationSdk();
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

            _calibrationJob = LoadJobOrDefault(_options.CalibrationJobPath, "Calibration");
            _positioningJob = LoadJobOrDefault(_options.PositioningJobPath, "PCB positioning");
            _detectionProfile = LoadJsonOrDefault(_options.DetectionProfilePath, () => new DetectionProfile());
            if (MigrateLegacyDetectionRoi(_positioningJob, _detectionProfile))
            {
                LocalDebugLog.Write("INFO", "startup", "Migrated legacy DetectionProfile.Roi into positioning job: " + _options.PositioningJobPath);
                SaveJob(_positioningJob, _options.PositioningJobPath);
            }
            _teachingData = LoadJsonOrDefault(_options.TeachingPath, () => new TeachingData());
            _calibrationResult = _sdk.Calculate(_calibrationJob);

            LocalDebugLog.Write(
                "INFO",
                "startup",
                "StationRuntime created. OptionsPath='" + _optionsPath + "', Migrated=" + migrated + ", " +
                StationLogFormatter.FormatOptions(_options));
            LocalDebugLog.Write("INFO", "startup", StationLogFormatter.FormatJob("CalibrationJob", _calibrationJob));
            LocalDebugLog.Write("INFO", "startup", StationLogFormatter.FormatJob("PositioningJob", _positioningJob));
            LocalDebugLog.Write("INFO", "startup", "DetectionProfile: " + StationLogFormatter.FormatDetectionProfile(_detectionProfile));
            LocalDebugLog.Write("INFO", "startup", "TeachingData: " + StationLogFormatter.FormatTeachingData(_teachingData));
            LocalDebugLog.Write("INFO", "startup", "InitialCalibrationResult: " + StationLogFormatter.FormatCalibrationResult(_calibrationResult));

            _calibration = new CalibrationCoordinator(
                _sdk,
                SendCalibrationMessageAsync,
                _camera.CaptureAsync,
                SaveCalibrationJobAsync,
                _options,
                _calibrationJob);
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

        public bool IsHostConnected { get { return _tcpServer.IsClientConnected; } }
        public bool IsCameraConnected { get { return _camera.IsConnected; } }
        public bool IsCameraEnabled { get { lock (_stateSync) return _options.CameraEnabled; } }
        public bool IsCalibrationEnabled { get { lock (_stateSync) return _options.CalibrationEnabled; } }
        public bool IsArchiveImagesEnabled { get { lock (_stateSync) return _options.ArchiveImages; } }
        public bool IsLiveAcquisitionEnabled { get { lock (_stateSync) return _options.LiveAcquisitionEnabled; } }
        public bool IsCameraPreviewRunning { get { return _camera.IsPreviewRunning; } }
        public double LivePreviewFps { get { return _camera.LiveFps; } }
        public long LivePreviewFrameCount { get { return _camera.LiveFrameCount; } }
        public long LivePreviewDroppedFrameCount { get { return _camera.LiveDroppedFrameCount; } }

        /// <summary>注册实时预览 UI 渲染背压检测委托，避免高帧率预览在界面端堆积。</summary>
        public void SetPreviewConsumerBusyCallback(Func<bool> callback)
        {
            _camera.PreviewConsumerBusy = callback;
        }
        public bool IsMockCamera
        {
            get
            {
                string provider;
                lock (_stateSync) provider = _options.CameraProvider;
                return string.Equals(provider, "Mock", StringComparison.OrdinalIgnoreCase) ||
                       (_camera.ProviderName ?? string.Empty).StartsWith("Mock", StringComparison.OrdinalIgnoreCase);
            }
        }

        public StationOptions GetOptionsSnapshot()
        {
            lock (_stateSync) return _options.DeepClone();
        }

        public CalibrationJob GetCalibrationJobSnapshot()
        {
            lock (_stateSync) return _calibrationJob.DeepClone();
        }

        public CalibrationJob GetPositioningJobSnapshot()
        {
            lock (_stateSync) return _positioningJob.DeepClone();
        }

        public CalibrationResult GetCalibrationResultSnapshot()
        {
            lock (_stateSync) return _calibrationResult == null ? null : _calibrationResult.DeepClone();
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
            return _calibration.GetRecordsSnapshot();
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
                OnLog("WARN", "Camera did not auto-connect: " + exception.Message);
                OnStateChanged("camera", false, exception.Message);
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
            if (!shouldStop) return;

            LocalDebugLog.Write("INFO", "shutdown", "StationRuntime.StopAsync begin.");
            _calibration.CancelAutomatic();
            await _camera.StopPreviewAsync().ConfigureAwait(false);
            await _tcpServer.StopAsync().ConfigureAwait(false);
            OnStateChanged("tcp", false, "Stopped");
            LocalDebugLog.Write("INFO", "shutdown", "StationRuntime.StopAsync completed.");
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
            // 仅真实相机且开启“兼容相机实时采集”选项时自动开启实时预览流；模拟相机启动时不自动推送默认测试图
            bool autoStartPreview = options.LiveAcquisitionEnabled && !IsMockCamera;
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

        /// <summary>主动开启相机实时采集流；若尚未连接相机则尝试按配置自动连接。</summary>
        public async Task StartLivePreviewAsync(CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            EnsureCameraEnabled();
            LocalDebugLog.Write("INFO", "camera.live", "StartLivePreviewAsync requested (connected=" + _camera.IsConnected + ", mock=" + IsMockCamera + ").");
            if (!_camera.IsConnected)
            {
                await ConnectConfiguredCameraAsync(cancellationToken).ConfigureAwait(false);
            }

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

        /// <summary>执行一个标定点的运动、采图、定位和保存流程。</summary>
        public Task<CalibrationPointRecord> CaptureCalibrationPointAsync(
            int pointNumber,
            CancellationToken cancellationToken)
        {
            EnsureCameraEnabled();
            EnsureCalibrationEnabled();
            return RunExclusiveAsync(
                "calibration point " + pointNumber,
                async () =>
                {
                    CalibrationPointRecord record = await _calibration.CapturePointAsync(pointNumber, cancellationToken).ConfigureAwait(false);
                    if (record != null)
                    {
                        LocalDebugLog.Write(
                            record.Status == CalibrationPointStatus.Completed ? "INFO" : "WARN",
                            "calibration.point",
                            string.Format(
                                CultureInfo.InvariantCulture,
                                "Point={0}, Status={1}, Machine=({2:0.###},{3:0.###},{4:0.###}), Image=({5:0.###},{6:0.###},{7:0.###}), Msg='{8}'",
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

        /// <summary>按计划依次执行全部标定点并计算结果。</summary>
        public Task<CalibrationResult> RunAutomaticCalibrationAsync(CancellationToken cancellationToken)
        {
            EnsureCameraEnabled();
            EnsureCalibrationEnabled();
            return RunExclusiveAsync(
                "automatic calibration",
                async () =>
                {
                    CalibrationResult result = await _calibration.RunAutomaticAsync(cancellationToken).ConfigureAwait(false);
                    LocalDebugLog.Write("INFO", "calibration.auto", "Automatic calibration result: " + StationLogFormatter.FormatCalibrationResult(result));
                    return result;
                },
                cancellationToken);
        }

        public void CancelAutomaticCalibration()
        {
            LocalDebugLog.Write("INFO", "calibration.auto", "Automatic calibration cancellation requested.");
            _calibration.CancelAutomatic();
        }

        public Task<CalibrationResult> CalculateCalibrationAsync(CancellationToken cancellationToken)
        {
            EnsureCalibrationEnabled();
            return RunExclusiveAsync(
                "calibration calculation",
                async () =>
                {
                    CalibrationResult result = await _calibration.CalculateAsync(cancellationToken).ConfigureAwait(false);
                    LocalDebugLog.Write(
                        result != null && result.Success ? "INFO" : "WARN",
                        "calibration.solve",
                        StationLogFormatter.FormatCalibrationResult(result));
                    return result;
                },
                cancellationToken);
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

        /// <summary>在独占工作流下打开标定模板编辑器；更新模板后会自动清空旧标定采样。</summary>
        public TemplateSetupResult EditCalibrationTemplate(Bitmap referenceImage, IWin32Window owner)
        {
            ThrowIfDisposed();
            EnsureCalibrationEnabled();
            EnterSynchronousWorkflow("calibration template editing");
            try
            {
                CalibrationJob current;
                lock (_stateSync) current = _calibrationJob.DeepClone();
                TemplateSetupResult result = _sdk.OpenTemplateDialog(referenceImage, current.Template, owner);
                if (!result.Accepted || result.Template == null)
                {
                    LocalDebugLog.Write("INFO", "template.cal", "Calibration template editing cancelled or unaccepted: code=" + result.ErrorCode + ", msg=" + result.Message);
                    return result;
                }

                current.Template = result.Template.DeepClone();
                current = _sdk.ClearSamples(current);
                lock (_stateSync)
                {
                    _calibrationJob = current.DeepClone();
                    _calibrationResult = _sdk.Calculate(_calibrationJob);
                }
                _calibration.SetJob(current);
                SaveCalibrationJobAsync(current).GetAwaiter().GetResult();
                PublishCalibrationRecords();
                LocalDebugLog.Write("INFO", "template.cal", "Calibration template saved: " + StationLogFormatter.FormatJob("CalibrationJob", current));
                OnLog("INFO", "Calibration template updated; previous samples were cleared.");
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
                lock (_stateSync) current = _positioningJob.DeepClone();
                TemplateSetupResult result = _sdk.OpenTemplateDialog(referenceImage, current.Template, owner, true);
                if (!result.Accepted || result.Template == null)
                {
                    LocalDebugLog.Write("INFO", "template.pos", "Positioning template editing cancelled or unaccepted: code=" + result.ErrorCode + ", msg=" + result.Message);
                    return result;
                }

                current.Template = result.Template.DeepClone();
                lock (_stateSync)
                {
                    _positioningJob = current.DeepClone();
                }
                SaveJob(current, _options.PositioningJobPath);
                LocalDebugLog.Write("INFO", "template.pos", "Positioning template saved to '" + _options.PositioningJobPath + "': " + StationLogFormatter.FormatJob("PositioningJob", current));
                OnLog("INFO", "PCB positioning template updated.");
                return result;
            }
            finally
            {
                _workflowGate.Release();
            }
        }

        /// <summary>从文件加载标定 Job 并立即重新计算标定结果。</summary>
        public JobPersistenceResult LoadCalibrationJob(string path)
        {
            ThrowIfDisposed();
            EnsureCalibrationEnabled();
            EnterSynchronousWorkflow("calibration job loading");
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
                    _calibrationJob = job.DeepClone();
                    _calibrationResult = result == null ? null : result.DeepClone();
                    _options.CalibrationJobPath = path;
                }
                _calibration.SetJob(job);
                PersistOptionsSnapshot();
                PublishCalibrationRecords();
                OnCalibrationCalculationCompleted(this, new CalibrationResultEventArgs(result));
                LocalDebugLog.Write("INFO", "job.load", StationLogFormatter.FormatJob("CalibrationJob", job) + " | " + StationLogFormatter.FormatCalibrationResult(result));
                OnLog("INFO", "Calibration job loaded: " + path);
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
                bool migrated = MigrateLegacyDetectionRoi(loadedJob, _detectionProfile);
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
                string calibrationPath = ResolveDirectoryJobPath(normalized, "calibration.nprcal", "calib", null);
                string positioningPath = ResolveDirectoryJobPath(normalized, "positioning.nprcal", "pos", calibrationPath);
                string teachingPath = Path.Combine(normalized, "teaching.json");
                string detectionPath = Path.Combine(normalized, "detection-profile.json");
                string archiveDirectory = Path.Combine(normalized, "images");

                CalibrationJob currentCalibration;
                CalibrationJob currentPositioning;
                DetectionProfile currentProfile;
                lock (_stateSync)
                {
                    currentCalibration = _calibrationJob == null ? new CalibrationJob { Name = "Calibration" } : _calibrationJob.DeepClone();
                    currentPositioning = _positioningJob == null ? new CalibrationJob { Name = "PCB positioning" } : _positioningJob.DeepClone();
                    currentProfile = CloneDetectionProfile(_detectionProfile);
                }
                TeachingData currentTeaching = _teaching.GetSnapshot();

                int loadedFileCount = 0;
                int createdFileCount = 0;

                CalibrationJob loadedCalibration = currentCalibration;
                if (File.Exists(calibrationPath))
                {
                    JobPersistenceResult res = _sdk.TryLoadJob(calibrationPath);
                    if (res.Success && res.Job != null)
                    {
                        loadedCalibration = res.Job.DeepClone();
                        loadedFileCount++;
                    }
                }
                else
                {
                    SaveJob(loadedCalibration, calibrationPath);
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

                CalibrationResult calibrationResult = _sdk.Calculate(loadedCalibration);
                StationOptions updatedOptions;
                lock (_stateSync)
                {
                    _options.ConfigDirectory = normalized;
                    _options.CalibrationJobPath = calibrationPath;
                    _options.PositioningJobPath = positioningPath;
                    _options.TeachingPath = teachingPath;
                    _options.DetectionProfilePath = detectionPath;
                    _options.ImageArchiveDirectory = archiveDirectory;

                    _calibrationJob = loadedCalibration.DeepClone();
                    _calibrationResult = calibrationResult == null ? null : calibrationResult.DeepClone();
                    _positioningJob = loadedPositioning.DeepClone();
                    _detectionProfile = CloneDetectionProfile(loadedProfile);
                    _teachingData = loadedTeaching;
                    updatedOptions = _options.DeepClone();
                }

                _calibration.UpdateOptions(updatedOptions);
                _calibration.SetJob(loadedCalibration);
                _teaching.SetData(loadedTeaching);
                _jsonStore.Save(_optionsPath, updatedOptions);

                PublishCalibrationRecords();
                OnCalibrationCalculationCompleted(this, new CalibrationResultEventArgs(calibrationResult));
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
                LocalDebugLog.Write("INFO", "config.import", StationLogFormatter.FormatJob("CalibrationJob", loadedCalibration));
                LocalDebugLog.Write("INFO", "config.import", StationLogFormatter.FormatJob("PositioningJob", loadedPositioning));
                LocalDebugLog.Write("INFO", "config.import", "Profile: " + StationLogFormatter.FormatDetectionProfile(loadedProfile));
                LocalDebugLog.Write("INFO", "config.import", "Teaching: " + StationLogFormatter.FormatTeachingData(loadedTeaching));
                OnLog("INFO", summary);
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
            if (image == null) throw new ArgumentNullException(nameof(image));
            EnsureCalibrationEnabled();
            return RunExclusiveAsync(
                "calibration template test",
                () => Task.Run(() =>
                {
                    CalibrationJob job = GetCalibrationJobSnapshot();
                    TemplateMatchResult match = _sdk.Locate(image, job, true);
                    LocalDebugLog.Write(
                        match != null && match.Success ? "INFO" : "WARN",
                        "template.test.cal",
                        match == null
                            ? "Locate returned null."
                            : string.Format(
                                CultureInfo.InvariantCulture,
                                "ok={0}, rc=({1:0.###},{2:0.###}), ang={3:0.###}deg, score={4:0.###}, ct={5:0.0}ms, msg='{6}'",
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
            if (_disposed || image == null) return null;

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
                return _sdk.LocateAll(image, job, true);
            }
            catch (Exception exception)
            {
                LocalDebugLog.WriteException("WARN", "preview.locate", "LocatePositioningForPreview failed", exception);
                return null;
            }
        }

        /// <summary>执行在线检测并发布检测结果。</summary>
        public Task<InspectionResult> RunInspectionAsync(
            int expectedCount,
            double currentR,
            CancellationToken cancellationToken)
        {
            EnsureCameraEnabled();
            EnsureCalibrationEnabled();
            return RunExclusiveAsync(
                "inspection",
                () => ExecuteInspectionAsync(expectedCount, currentR, null, cancellationToken),
                cancellationToken);
        }

        /// <summary>使用指定图像执行离线检测。</summary>
        public Task<InspectionResult> RunOfflineInspectionAsync(
            Bitmap image,
            int expectedCount,
            double currentR,
            CancellationToken cancellationToken)
        {
            return RunOfflineInspectionAsync(image, expectedCount, currentR, cancellationToken, null);
        }

        internal Task<InspectionResult> RunOfflineInspectionAsync(Bitmap image, int expectedCount,
            double currentR, CancellationToken cancellationToken, LockedFrameGrayCache frameCache)
        {
            if (image == null) throw new ArgumentNullException(nameof(image));
            return RunExclusiveAsync(
                "offline inspection",
                () => ExecuteOfflineInspectionAsync(image, expectedCount, currentR, cancellationToken, frameCache),
                cancellationToken);
        }

        /// <summary>切换相机或标定功能开关并持久化；关闭相机时立即断开，开启且已启动时自动重连。</summary>
        public async Task ApplyEnablementAsync(
            bool cameraEnabled,
            bool calibrationEnabled,
            CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            if (!cameraEnabled || !calibrationEnabled) _calibration.CancelAutomatic();

            await _workflowGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                StationOptions snapshot;
                bool shouldConnect;
                lock (_stateSync)
                {
                    _options.CameraEnabled = cameraEnabled;
                    _options.CalibrationEnabled = calibrationEnabled;
                    _options.ConfigurationVersion = StationOptions.CurrentConfigurationVersion;
                    snapshot = _options.DeepClone();
                    shouldConnect = cameraEnabled && _started && !_camera.IsConnected;
                }

                if (!cameraEnabled)
                {
                    await _camera.DisconnectAsync().ConfigureAwait(false);
                    OnStateChanged("camera", false, "Disabled");
                }

                _jsonStore.Save(_optionsPath, snapshot);
                _calibration.UpdateOptions(snapshot);

                if (shouldConnect)
                {
                    try
                    {
                        await ConnectConfiguredCameraAsync(cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception exception)
                    {
                        OnStateChanged("camera", false, exception.Message);
                        OnLog("WARN", "Camera did not auto-connect after being enabled: " + exception.Message);
                        throw;
                    }
                }

                OnStateChanged(
                    "calibration",
                    calibrationEnabled,
                    calibrationEnabled ? "Enabled" : "Disabled");
                OnLog(
                    "INFO",
                    "Station enablement updated: camera=" + cameraEnabled +
                    ", calibration=" + calibrationEnabled + ".");
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

        /// <summary>校验并保存工作站配置（相机/标定开关变更须通过 ApplyEnablementAsync 执行）。</summary>
        public void UpdateOptions(StationOptions options)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            StationOptions copy = options.DeepClone();
            ValidateOptions(copy);
            copy.ConfigurationVersion = StationOptions.CurrentConfigurationVersion;
            lock (_stateSync)
            {
                if (copy.CameraEnabled != _options.CameraEnabled ||
                    copy.CalibrationEnabled != _options.CalibrationEnabled)
                {
                    throw new InvalidOperationException(
                        "Use ApplyEnablementAsync to change camera or calibration enablement.");
                }
                _options = copy;
            }
            _jsonStore.Save(_optionsPath, copy);
            _calibration.UpdateOptions(copy);
            _camera.SetPreviewInterval(copy.PreviewIntervalMilliseconds);
            LocalDebugLog.Write("INFO", "config.options", "Options saved: " + StationLogFormatter.FormatOptions(copy));
            OnLog("INFO", "Station settings saved. TCP or camera provider changes apply after restart.");
        }

        private async Task ConnectConfiguredCameraAsync(CancellationToken cancellationToken)
        {
            IReadOnlyList<CameraDescriptor> devices = await _camera.EnumerateAsync(cancellationToken).ConfigureAwait(false);
            string deviceSummary = devices == null || devices.Count == 0
                ? "<none>"
                : string.Join("; ", devices.Select(d => d == null ? "<null>" : d.Id + "(" + d.DisplayName + ")"));
            LocalDebugLog.Write("INFO", "camera.autoconnect", "Enumerated devices for provider '" + _camera.ProviderName + "': [" + deviceSummary + "]");
            if (devices == null || devices.Count == 0)
                throw new InvalidOperationException("No camera device was found.");

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

        private void ForgetSeenMessages()
        {
            string ignored;
            while (_seenMessageOrder.TryDequeue(out ignored)) { }
            _seenMessages.Clear();
        }

        private void OnTcpConnected(object sender, TcpConnectionEventArgs e)
        {
            ForgetSeenMessages();
            LocalDebugLog.Write("INFO", "tcp.conn", "Connected: connId=" + e.ConnectionId + ", remote=" + e.RemoteEndPoint);
            OnStateChanged("host", true, e.RemoteEndPoint);
            OnLog("INFO", "Upper machine connected: " + e.RemoteEndPoint);
        }

        private void OnTcpDisconnected(object sender, TcpDisconnectedEventArgs e)
        {
            string activeConnectionId = _tcpServer.ActiveConnectionId;
            if (!string.IsNullOrWhiteSpace(activeConnectionId) && !string.Equals(
                activeConnectionId,
                e.ConnectionId,
                StringComparison.Ordinal))
            {
                LocalDebugLog.Write("INFO", "tcp.conn", "Previous connection closed: connId=" + e.ConnectionId + ", activeConnId=" + activeConnectionId + ", reason=" + e.Reason);
                OnLog("INFO", "Previous upper-machine connection closed: " + e.ConnectionId);
                return;
            }

            ForgetSeenMessages();
            LocalDebugLog.Write("WARN", "tcp.conn", "Disconnected: connId=" + e.ConnectionId + ", reason=" + e.Reason);
            OnStateChanged("host", false, e.Reason);
            OnLog("WARN", "Upper machine disconnected: " + e.Reason);
        }

        private async void OnTcpMessageReceived(object sender, ProtocolMessageEventArgs e)
        {
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
                        "DUPLICATE_MESSAGE",
                        "Duplicate messageId.",
                        false)
                        .ConfigureAwait(false);
                    return;
                }

                if (HandleCalibrationProtocolMessage(envelope, e.ConnectionId)) return;
                if (isHeartbeat ||
                    string.Equals(envelope.Type, ProtocolMessageTypes.Hello, StringComparison.OrdinalIgnoreCase))
                {
                    ProtocolEnvelope ack = ProtocolEnvelope.Create(ProtocolMessageTypes.Acknowledge);
                    ack.CorrelationId = envelope.MessageId;
                    ack.Data["role"] = "vision";
                    ack.Data["ready"] = IsCameraEnabled && IsCalibrationEnabled && IsCameraConnected;
                    ack.Data["cameraEnabled"] = IsCameraEnabled;
                    ack.Data["calibrationEnabled"] = IsCalibrationEnabled;
                    ack.Data["protocolVersion"] = "1.0";
                    await _tcpServer.SendAsync(e.ConnectionId, ack).ConfigureAwait(false);
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

                LocalDebugLog.Write("WARN", "tcp.rx", "Unsupported message type: conn=" + e.ConnectionId + ", type=" + envelope.Type);
                await SendErrorAsync(e.ConnectionId, envelope.MessageId, "MESSAGE_TYPE_UNSUPPORTED",
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
                        "INTERNAL_ERROR",
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
            lock (_stateSync)
            {
                cameraEnabled = _options.CameraEnabled;
                calibrationEnabled = _options.CalibrationEnabled;
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
            if (!calibrationEnabled)
            {
                LocalDebugLog.Write("WARN", "tcp.inspect", "Rejected inspection.trigger (CALIBRATION_DISABLED): conn=" + connectionId + ", msgId=" + request.MessageId);
                await SendErrorAsync(
                    connectionId,
                    request.MessageId,
                    ProtocolErrorCodes.CalibrationDisabled,
                    "Production inspection is unavailable because calibration use is disabled.",
                    false).ConfigureAwait(false);
                return;
            }

            if (!await _workflowGate.WaitAsync(0).ConfigureAwait(false))
            {
                LocalDebugLog.Write("WARN", "tcp.inspect", "Rejected inspection.trigger (BUSY): conn=" + connectionId + ", msgId=" + request.MessageId);
                await SendErrorAsync(
                    connectionId,
                    request.MessageId,
                    "BUSY",
                    "Another vision workflow is already running.",
                    true)
                    .ConfigureAwait(false);
                return;
            }

            try
            {
                int defaultExpected;
                lock (_stateSync) defaultExpected = _options.ExpectedBoardCount;
                int expected = ProtocolValue.Integer(request.Data, "expectedCount", defaultExpected);
                double currentR;
                if (!ProtocolValue.TryDouble(request.Data, "currentR", out currentR) ||
                    double.IsNaN(currentR) || double.IsInfinity(currentR))
                {
                    LocalDebugLog.Write("WARN", "tcp.inspect", "Rejected inspection.trigger (PROTOCOL_INVALID currentR): conn=" + connectionId + ", msgId=" + request.MessageId);
                    await SendErrorAsync(
                        connectionId,
                        request.MessageId,
                        "PROTOCOL_INVALID",
                        "inspection.trigger requires a finite currentR value.",
                        false).ConfigureAwait(false);
                    return;
                }
                LocalDebugLog.Write(
                    "INFO",
                    "tcp.inspect",
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "Executing inspection.trigger: conn={0}, msgId={1}, currentR={2:0.###}, expectedCount={3}, archive={4}",
                        connectionId,
                        request.MessageId,
                        currentR,
                        expected,
                        IsArchiveRequested(request)));
                // 视觉结果一经算出立即释放工作流门锁并回传上位机；图像显示、归档和 UI 事件延后到后台线程串行执行，不占用生产 CT
                ProtocolEnvelope response = null;
                InspectionResult completedResult = null;
                try
                {
                    await ExecuteInspectionAsync(
                        expected,
                        currentR,
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
                    _workflowGate.Release();
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
            catch
            {
                throw;
            }
        }

        private async Task<InspectionResult> ExecuteInspectionAsync(
            int expectedCount,
            double currentR,
            ProtocolEnvelope archiveRequest,
            CancellationToken cancellationToken,
            Func<InspectionResult, Task> sendResponse = null)
        {
            await _inspectionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                EnsureCameraEnabled();
                EnsureCalibrationEnabled();
                CalibrationJob positioning;
                CalibrationResult calibration;
                DetectionProfile profile;
                TeachingData teaching;
                lock (_stateSync)
                {
                    positioning = _positioningJob.DeepClone();
                    calibration = _calibrationResult == null ? null : _calibrationResult.DeepClone();
                    profile = CloneDetectionProfile(_detectionProfile);
                    teaching = _teaching.GetSnapshot();
                }

                using (CameraFrame frame = await _camera.CaptureAsync(cancellationToken).ConfigureAwait(false))
                {
                    LocalDebugLog.Write(
                        "INFO",
                        "inspection.capture",
                        string.Format(
                            CultureInfo.InvariantCulture,
                            "Captured online frame: seq={0}, size={1}, expected={2}, currentR={3:0.###}, tcpMode={4}",
                            frame.Sequence,
                            frame.Image == null ? "null" : frame.Image.Width + "x" + frame.Image.Height,
                            expectedCount,
                            currentR,
                            sendResponse != null));
                    InspectionResult result = await Task.Run(() => _inspection.Inspect(
                        frame.Image,
                        positioning,
                        calibration,
                        teaching,
                        profile,
                        expectedCount,
                        currentR), cancellationToken).ConfigureAwait(false);

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
            double currentR,
            CancellationToken cancellationToken,
            LockedFrameGrayCache frameCache)
        {
            await _inspectionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                CalibrationJob positioning;
                CalibrationResult calibration;
                DetectionProfile profile;
                TeachingData teaching;
                bool calibrationEnabled;
                lock (_stateSync)
                {
                    positioning = _positioningJob.DeepClone();
                    calibration = _calibrationResult == null ? null : _calibrationResult.DeepClone();
                    profile = CloneDetectionProfile(_detectionProfile);
                    teaching = _teaching.GetSnapshot();
                    calibrationEnabled = _options.CalibrationEnabled;
                }

                bool calValid = calibration != null && calibration.TranslationSuccess &&
                    calibration.Rotation != null && calibration.Rotation.Success &&
                    calibration.Rotation.IsAvailable && Math.Abs(calibration.Rotation.DirectionMultiplier) >= 0.5;
                bool teachValid = teaching != null &&
                    teaching.Left != null && teaching.Left.IsValid &&
                    teaching.Right != null && teaching.Right.IsValid;
                bool canMapTargets = calibrationEnabled &&
                    calValid &&
                    teachValid &&
                    !double.IsNaN(currentR) && !double.IsInfinity(currentR);

                LocalDebugLog.Write(
                    "INFO",
                    "inspection.offline",
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "Start offline inspection: size={0}x{1}, expected={2}, currentR={3:0.###}, calEnabled={4}, calValid={5}, teachValid={6}, canMapTargets={7}",
                        image.Width,
                        image.Height,
                        expectedCount,
                        currentR,
                        calibrationEnabled,
                        calValid,
                        teachValid,
                        canMapTargets));

                PublishFrame(image, "offline-inspection");
                InspectionResult result = await Task.Run(
                    () => _inspection.InspectCore(image, positioning, calibration, teaching,
                        profile, expectedCount, canMapTargets ? currentR : 0.0, canMapTargets, frameCache),
                    cancellationToken).ConfigureAwait(false);
                if (calibrationEnabled && !canMapTargets && result != null && result.Executed)
                {
                    result.Message = string.IsNullOrWhiteSpace(result.Message)
                        ? "标定或左右示教未完成，当前仅展示视觉定位与瑕疵检测结果。"
                        : result.Message + "（标定或左右示教未完成，当前仅展示视觉定位与瑕疵检测结果）";
                }
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

        private static void FillInspectionResponse(IDictionary<string, object> data, InspectionResult result)
        {
            data["executed"] = result.Executed;
            data["success"] = result.Success;
            data["pass"] = result.Pass;
            data["errorCode"] = result.ErrorCode ?? string.Empty;
            data["message"] = result.Message ?? string.Empty;
            data["expectedCount"] = result.ExpectedCount;
            data["actualCount"] = result.ActualCount;
            data["hasManualMarks"] = result.HasManualMarks;
            data["currentR"] = result.CurrentR;
            data["elapsedMilliseconds"] = result.ElapsedMilliseconds;
            data["matchElapsedMilliseconds"] = result.MatchElapsedMilliseconds;
            data["blobElapsedMilliseconds"] = result.BlobElapsedMilliseconds;

            List<object> items = new List<object>();
            foreach (BoardInspectionItem item in result.Items)
            {
                Dictionary<string, object> itemData = new Dictionary<string, object>();
                itemData["boardIndex"] = item.BoardIndex;
                itemData["row"] = item.ImageRow;
                itemData["column"] = item.ImageColumn;
                itemData["angle"] = item.ImageAngleDegrees;
                itemData["score"] = item.MatchScore;
                itemData["hasManualMark"] = item.HasManualMark;

                List<object> targets = new List<object>();
                foreach (MappedTargetPose target in item.Targets)
                {
                    targets.Add(new Dictionary<string, object>
                    {
                        { "side", target.Side.ToString() },
                        { "x", target.X },
                        { "y", target.Y },
                        { "rOffset", target.ROffset },
                        { "targetR", target.TargetR },
                        { "pickR", target.PickR },
                        { "blindPlaceR", target.BlindPlaceR }
                    });
                }
                itemData["targets"] = targets;
                items.Add(itemData);
            }
            data["items"] = items;
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
            if (envelope == null || !string.Equals(
                envelope.Type,
                ProtocolMessageTypes.MotionArrived,
                StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            // 校验 motion.arrived 是否来自发起 move.request 的同一 TCP 连接，忽略其他并发连接的误报
            string expectedConnectionId;
            if (!string.IsNullOrWhiteSpace(envelope.CorrelationId) &&
                _calibrationRequestConnections.TryGetValue(
                    envelope.CorrelationId,
                    out expectedConnectionId) &&
                !string.Equals(expectedConnectionId, connectionId, StringComparison.Ordinal))
            {
                OnLog(
                    "WARN",
                    "Ignored motion.arrived from a different TCP connection for request " +
                    envelope.CorrelationId + ".");
                return true;
            }

            bool handled = _calibration.HandleProtocolMessage(envelope);
            LocalDebugLog.Write(
                handled ? "INFO" : "WARN",
                "tcp.cal.rx",
                "motion.arrived conn=" + connectionId + ", handled=" + handled + ", payload=" +
                StationLogFormatter.FormatEnvelope(envelope, _logCodec));
            return handled;
        }

        private async Task SendCalibrationMessageAsync(
            ProtocolEnvelope message,
            CancellationToken cancellationToken)
        {
            if (message == null) throw new ArgumentNullException(nameof(message));

            if (string.Equals(message.Type, ProtocolMessageTypes.MoveRequest, StringComparison.OrdinalIgnoreCase))
            {
                EnsureCameraEnabled();
                EnsureCalibrationEnabled();
                string connectionId = _tcpServer.ActiveConnectionId;
                if (string.IsNullOrWhiteSpace(connectionId))
                    throw new InvalidOperationException("No upper-computer client is connected for calibration motion.");

                // 绑定本次 move.request 的 MessageId 与来源 TCP 连接，确保后续结果精确路由回原连接
                if (!_calibrationRequestConnections.TryAdd(message.MessageId, connectionId))
                    throw new InvalidOperationException("Calibration request connection is already registered.");

                lock (_stateSync) _calibrationCompletionConnectionId = connectionId;
                await _tcpServer.SendAsync(connectionId, message, cancellationToken).ConfigureAwait(false);
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
                EventHandler<FrameEventArgs> handler = FrameReady;
                // 事件订阅方会自行克隆所需图像，因此 ownedImage 所有权留在本方法内供后续归档并释放
                if (handler != null) handler(this, new FrameEventArgs(ownedImage, "inspection"));
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

        private Task SaveCalibrationJobAsync(CalibrationJob job)
        {
            lock (_jobSaveSync)
            {
                SaveJob(job, _options.CalibrationJobPath);
                lock (_stateSync) _calibrationJob = job.DeepClone();
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
            if (string.Equals(options.CameraProvider, "Dahua", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(options.CameraProvider, "DH", StringComparison.OrdinalIgnoreCase))
            {
                return new DahuaCameraDevice(options.CameraTimeoutMilliseconds);
            }
            if (string.Equals(options.CameraProvider, "Hikvision", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(options.CameraProvider, "MVS", StringComparison.OrdinalIgnoreCase))
            {
                return new HikvisionCameraDevice();
            }
            return new MockCameraDevice();
        }

        private static void ValidateOptions(StationOptions options)
        {
            if (options.TcpPort < 1 || options.TcpPort > 65535)
                throw new ArgumentOutOfRangeException(nameof(options.TcpPort));
            if (options.TranslationStepX <= 0.0 || options.TranslationStepY <= 0.0 ||
                options.RotationStepDegrees <= 0.0)
                throw new ArgumentOutOfRangeException("Calibration step values must be positive.");
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

        private void PublishCalibrationRecords()
        {
            foreach (CalibrationPointRecord record in _calibration.GetRecordsSnapshot())
                OnCalibrationPointChanged(this, new CalibrationPointEventArgs(record));
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
                PhiRadians = profile.Roi.PhiDegrees * Math.PI / 180.0,
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
            lock (_stateSync) _calibrationResult = e.Result == null ? null : e.Result.DeepClone();
            EventHandler<CalibrationResultEventArgs> handler = CalibrationCompleted;
            if (handler != null) handler(this, e);
        }

        private void OnTeachingChanged(object sender, TeachingEventArgs e)
        {
            lock (_stateSync) _teachingData = _teaching.GetSnapshot();
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
            if (_disposed) throw new ObjectDisposedException(nameof(StationRuntime));
        }

        /// <summary>停止运行并等待在途工作流及后台归档队列收尾后释放所有服务资源。</summary>
        public void Dispose()
        {
            if (_disposed) return;
            LocalDebugLog.Write("INFO", "shutdown", "StationRuntime.Dispose begin.");
            try { StopAsync().GetAwaiter().GetResult(); } catch (Exception ex) { LocalDebugLog.WriteException("WARN", "shutdown", "StopAsync during Dispose", ex); }
            // 等待当前独占工作流和串行后处理队列退出后再销毁信号量与底层设备
            _workflowGate.Wait();
            _workflowGate.Release();
            Task postProcessing;
            lock (_postProcessingSync) postProcessing = _postProcessingTail;
            try { postProcessing.Wait(TimeSpan.FromSeconds(5)); } catch { }
            _disposed = true;
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
