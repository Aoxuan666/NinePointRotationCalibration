using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
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
    public sealed class StationRuntime : IDisposable
    {
        private const int SeenMessageLimit = 4096;
        private readonly object _stateSync = new object();
        private readonly object _jobSaveSync = new object();
        private readonly string _optionsPath;
        private readonly AtomicJsonStore _jsonStore;
        private readonly NinePointRotationCalibrationSdk _sdk;
        private readonly TcpVisionServer _tcpServer;
        private readonly CameraAcquisitionService _camera;
        private readonly CalibrationCoordinator _calibration;
        private readonly TeachingService _teaching;
        private readonly BoardInspectionService _inspection;
        private readonly SemaphoreSlim _inspectionGate;
        private readonly SemaphoreSlim _workflowGate;
        private readonly ConcurrentDictionary<string, byte> _seenMessages;
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
            _optionsPath = optionsPath;
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _options.NormalizeAfterLoad();
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
                SaveJob(_positioningJob, _options.PositioningJobPath);
            _teachingData = LoadJsonOrDefault(_options.TeachingPath, () => new TeachingData());
            _calibrationResult = _sdk.Calculate(_calibrationJob);

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

        public static StationRuntime CreateDefault()
        {
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
            }
            else
            {
                Directory.CreateDirectory(dataDirectory);
                store.Save(optionsPath, options);
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
                await _tcpServer.StartAsync(cancellationToken).ConfigureAwait(false);
                OnStateChanged("tcp", true, "Listening on " + _tcpServer.LocalEndPoint);
            }
            catch
            {
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
                OnLog("WARN", "Camera did not auto-connect: " + exception.Message);
                OnStateChanged("camera", false, exception.Message);
            }
        }

        public async Task StopAsync()
        {
            bool shouldStop;
            lock (_stateSync)
            {
                shouldStop = _started;
                _started = false;
            }
            if (!shouldStop) return;

            _calibration.CancelAutomatic();
            await _camera.StopPreviewAsync().ConfigureAwait(false);
            await _tcpServer.StopAsync().ConfigureAwait(false);
            OnStateChanged("tcp", false, "Stopped");
        }

        public Task<IReadOnlyList<CameraDescriptor>> EnumerateCamerasAsync(CancellationToken cancellationToken)
        {
            EnsureCameraEnabled();
            return _camera.EnumerateAsync(cancellationToken);
        }

        public async Task ConnectCameraAsync(string deviceId, CancellationToken cancellationToken)
        {
            EnsureCameraEnabled();
            StationOptions options = GetOptionsSnapshot();
            await _camera.StopPreviewAsync().ConfigureAwait(false);
            await _camera.ConnectAsync(deviceId, options.CameraExposureMicroseconds, cancellationToken).ConfigureAwait(false);
            _camera.StartPreview();
            OnStateChanged("camera", true, _camera.ProviderName + " / " + _camera.ConnectedDeviceId);
        }

        public async Task DisconnectCameraAsync()
        {
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
                        PublishFrame(frame.Image, "manual");
                    return true;
                },
                cancellationToken);
        }

        public Task<CalibrationPointRecord> CaptureCalibrationPointAsync(
            int pointNumber,
            CancellationToken cancellationToken)
        {
            EnsureCameraEnabled();
            EnsureCalibrationEnabled();
            return RunExclusiveAsync(
                "calibration point " + pointNumber,
                () => _calibration.CapturePointAsync(pointNumber, cancellationToken),
                cancellationToken);
        }

        public Task<CalibrationResult> RunAutomaticCalibrationAsync(CancellationToken cancellationToken)
        {
            EnsureCameraEnabled();
            EnsureCalibrationEnabled();
            return RunExclusiveAsync(
                "automatic calibration",
                () => _calibration.RunAutomaticAsync(cancellationToken),
                cancellationToken);
        }

        public void CancelAutomaticCalibration()
        {
            _calibration.CancelAutomatic();
        }

        public Task<CalibrationResult> CalculateCalibrationAsync(CancellationToken cancellationToken)
        {
            EnsureCalibrationEnabled();
            return RunExclusiveAsync(
                "calibration calculation",
                () => _calibration.CalculateAsync(cancellationToken),
                cancellationToken);
        }

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
                if (!result.Accepted || result.Template == null) return result;

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
                OnLog("INFO", "Calibration template updated; previous samples were cleared.");
                return result;
            }
            finally
            {
                _workflowGate.Release();
            }
        }

        public TemplateSetupResult EditPositioningTemplate(Bitmap referenceImage, IWin32Window owner)
        {
            ThrowIfDisposed();
            EnterSynchronousWorkflow("positioning template editing");
            try
            {
                CalibrationJob current;
                lock (_stateSync) current = _positioningJob.DeepClone();
                TemplateSetupResult result = _sdk.OpenTemplateDialog(referenceImage, current.Template, owner, true);
                if (!result.Accepted || result.Template == null) return result;

                current.Template = result.Template.DeepClone();
                lock (_stateSync)
                {
                    _positioningJob = current.DeepClone();
                }
                SaveJob(current, _options.PositioningJobPath);
                OnLog("INFO", "PCB positioning template updated.");
                return result;
            }
            finally
            {
                _workflowGate.Release();
            }
        }

        public JobPersistenceResult LoadCalibrationJob(string path)
        {
            ThrowIfDisposed();
            EnsureCalibrationEnabled();
            EnterSynchronousWorkflow("calibration job loading");
            try
            {
                JobPersistenceResult loaded = _sdk.TryLoadJob(path);
                if (!loaded.Success || loaded.Job == null) return loaded;

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
                OnLog("INFO", "Calibration job loaded: " + path);
                return loaded;
            }
            finally
            {
                _workflowGate.Release();
            }
        }

        public JobPersistenceResult LoadPositioningJob(string path)
        {
            ThrowIfDisposed();
            EnterSynchronousWorkflow("positioning job loading");
            try
            {
                JobPersistenceResult loaded = _sdk.TryLoadJob(path);
                if (!loaded.Success || loaded.Job == null) return loaded;

                CalibrationJob loadedJob = loaded.Job.DeepClone();
                bool migrated = MigrateLegacyDetectionRoi(loadedJob, _detectionProfile);
                lock (_stateSync)
                {
                    _positioningJob = loadedJob.DeepClone();
                    _options.PositioningJobPath = path;
                }
                PersistOptionsSnapshot();
                if (migrated) SaveJob(loadedJob, path);
                OnLog("INFO", "Positioning job loaded: " + path);
                return loaded;
            }
            finally
            {
                _workflowGate.Release();
            }
        }

        public Task<TemplateMatchResult> TestCalibrationTemplateAsync(
            Bitmap image,
            CancellationToken cancellationToken)
        {
            if (image == null) throw new ArgumentNullException(nameof(image));
            EnsureCalibrationEnabled();
            return RunExclusiveAsync(
                "calibration template test",
                () => Task.Run(() => _sdk.Locate(image, GetCalibrationJobSnapshot(), true), cancellationToken),
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
                    return _sdk.LocateAll(image, job, includeContour);
                }, cancellationToken),
                cancellationToken);
        }

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

        public Task<InspectionResult> RunOfflineInspectionAsync(
            Bitmap image,
            int expectedCount,
            double currentR,
            CancellationToken cancellationToken)
        {
            if (image == null) throw new ArgumentNullException(nameof(image));
            return RunExclusiveAsync(
                "offline inspection",
                () => ExecuteOfflineInspectionAsync(image, expectedCount, currentR, cancellationToken),
                cancellationToken);
        }

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

        public void UpdateDetectionProfile(DetectionProfile profile)
        {
            if (profile == null) throw new ArgumentNullException(nameof(profile));
            DetectionProfile copy = CloneDetectionProfile(profile);
            lock (_stateSync) _detectionProfile = copy;
            SaveDetectionProfile(copy);
        }

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
            OnLog("INFO", "Station settings saved. TCP or camera provider changes apply after restart.");
        }

        private async Task ConnectConfiguredCameraAsync(CancellationToken cancellationToken)
        {
            IReadOnlyList<CameraDescriptor> devices = await _camera.EnumerateAsync(cancellationToken).ConfigureAwait(false);
            if (devices == null || devices.Count == 0)
                throw new InvalidOperationException("No camera device was found.");

            string configured;
            lock (_stateSync) configured = _options.CameraSerialNumber;
            string deviceId = string.IsNullOrWhiteSpace(configured) ? devices[0].Id : configured;
            await ConnectCameraAsync(deviceId, cancellationToken).ConfigureAwait(false);
        }

        private void WireEvents()
        {
            _tcpServer.Connected += OnTcpConnected;
            _tcpServer.Disconnected += OnTcpDisconnected;
            _tcpServer.MessageReceived += OnTcpMessageReceived;
            _tcpServer.Log += ForwardLog;
            _camera.Log += ForwardLog;
            _camera.PreviewFrame += ForwardFrame;
            _camera.PreviewHealthChanged += OnCameraPreviewHealthChanged;
            _calibration.Log += ForwardLog;
            _calibration.FrameCaptured += ForwardFrame;
            _calibration.PointChanged += OnCalibrationPointChanged;
            _calibration.CalculationCompleted += OnCalibrationCalculationCompleted;
            _teaching.FrameCaptured += ForwardFrame;
            _teaching.TeachingChanged += OnTeachingChanged;
        }

        private void OnTcpConnected(object sender, TcpConnectionEventArgs e)
        {
            _seenMessages.Clear();
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
                OnLog("INFO", "Previous upper-machine connection closed: " + e.ConnectionId);
                return;
            }

            _seenMessages.Clear();
            OnStateChanged("host", false, e.Reason);
            OnLog("WARN", "Upper machine disconnected: " + e.Reason);
        }

        private async void OnTcpMessageReceived(object sender, ProtocolMessageEventArgs e)
        {
            try
            {
                ProtocolEnvelope envelope = e.Message;
                if (!_seenMessages.TryAdd(envelope.MessageId, 0))
                {
                    await SendErrorAsync(
                        e.ConnectionId,
                        envelope.MessageId,
                        "DUPLICATE_MESSAGE",
                        "Duplicate messageId.",
                        false)
                        .ConfigureAwait(false);
                    return;
                }
                if (_seenMessages.Count > SeenMessageLimit) _seenMessages.Clear();

                if (HandleCalibrationProtocolMessage(envelope, e.ConnectionId)) return;
                if (string.Equals(envelope.Type, ProtocolMessageTypes.Heartbeat, StringComparison.OrdinalIgnoreCase) ||
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
                    return;
                }
                if (string.Equals(envelope.Type, ProtocolMessageTypes.InspectionTrigger, StringComparison.OrdinalIgnoreCase))
                {
                    await HandleInspectionTriggerAsync(envelope, e.ConnectionId).ConfigureAwait(false);
                    return;
                }

                await SendErrorAsync(e.ConnectionId, envelope.MessageId, "MESSAGE_TYPE_UNSUPPORTED",
                    "Unsupported message type: " + envelope.Type, false).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
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
                catch (Exception sendException) { OnLog("ERROR", "Unable to send protocol error: " + sendException.Message); }
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
                    await SendErrorAsync(
                        connectionId,
                        request.MessageId,
                        "PROTOCOL_INVALID",
                        "inspection.trigger requires a finite currentR value.",
                        false).ConfigureAwait(false);
                    return;
                }
                InspectionResult result = await ExecuteInspectionAsync(
                    expected,
                    currentR,
                    request,
                    CancellationToken.None).ConfigureAwait(false);

                ProtocolEnvelope response = ProtocolEnvelope.Create(ProtocolMessageTypes.InspectionResult);
                response.CorrelationId = request.MessageId;
                FillInspectionResponse(response.Data, result);
                await _tcpServer.SendAsync(connectionId, response).ConfigureAwait(false);
            }
            finally
            {
                _workflowGate.Release();
            }
        }

        private async Task<InspectionResult> ExecuteInspectionAsync(
            int expectedCount,
            double currentR,
            ProtocolEnvelope archiveRequest,
            CancellationToken cancellationToken)
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
                    PublishFrame(frame.Image, "inspection");
                    if (archiveRequest != null) ArchiveFrameIfRequested(frame.Image, archiveRequest);
                    InspectionResult result = await Task.Run(() => _inspection.Inspect(
                        frame.Image,
                        positioning,
                        calibration,
                        teaching,
                        profile,
                        expectedCount,
                        currentR), cancellationToken).ConfigureAwait(false);
                    OnInspectionCompleted(result);
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
            CancellationToken cancellationToken)
        {
            await _inspectionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                CalibrationJob positioning;
                CalibrationResult calibration;
                DetectionProfile profile;
                TeachingData teaching;
                bool mapTargets;
                lock (_stateSync)
                {
                    positioning = _positioningJob.DeepClone();
                    calibration = _calibrationResult == null ? null : _calibrationResult.DeepClone();
                    profile = CloneDetectionProfile(_detectionProfile);
                    teaching = _teaching.GetSnapshot();
                    mapTargets = _options.CalibrationEnabled;
                }

                PublishFrame(image, "offline-inspection");
                InspectionResult result = await Task.Run(
                    () => mapTargets
                        ? _inspection.Inspect(
                            image,
                            positioning,
                            calibration,
                            teaching,
                            profile,
                            expectedCount,
                            currentR)
                        : _inspection.InspectWithoutMapping(
                            image,
                            positioning,
                            profile,
                            expectedCount),
                    cancellationToken).ConfigureAwait(false);
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
                        { "targetR", target.TargetR }
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

            return _calibration.HandleProtocolMessage(envelope);
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

                if (!_calibrationRequestConnections.TryAdd(message.MessageId, connectionId))
                    throw new InvalidOperationException("Calibration request connection is already registered.");

                lock (_stateSync) _calibrationCompletionConnectionId = connectionId;
                await _tcpServer.SendAsync(connectionId, message, cancellationToken).ConfigureAwait(false);
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

        private void ArchiveFrameIfRequested(Bitmap image, ProtocolEnvelope request)
        {
            bool archive = _options.ArchiveImages;
            object value;
            if (request.Data != null && request.Data.TryGetValue("archiveImage", out value) && value != null)
            {
                bool requested;
                if (bool.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), out requested)) archive = requested;
            }
            if (!archive) return;

            Directory.CreateDirectory(_options.ImageArchiveDirectory);
            string path = Path.Combine(
                _options.ImageArchiveDirectory,
                DateTime.Now.ToString("yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture) + ".png");
            image.Save(path, System.Drawing.Imaging.ImageFormat.Png);
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
            }
            return new CalibrationJob { Name = name };
        }

        private T LoadJsonOrDefault<T>(string path, Func<T> factory) where T : class
        {
            T value;
            string error;
            return File.Exists(path) && _jsonStore.TryLoad(path, out value, out error) && value != null
                ? value
                : factory();
        }

        private static ICameraDevice CreateCameraDevice(StationOptions options)
        {
            if (string.Equals(options.CameraProvider, "Dahua", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(options.CameraProvider, "DH", StringComparison.OrdinalIgnoreCase))
            {
                return new DahuaCameraDevice(options.CameraTimeoutMilliseconds);
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
                throw new InvalidOperationException(
                    "Cannot start " + operationName + " because another vision workflow is running.");
            }

            try
            {
                return await operation().ConfigureAwait(false);
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
                throw new InvalidOperationException(
                    "Cannot start " + operationName + " because another vision workflow is running.");
            }
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

        private void ForwardLog(object sender, LogEventArgs e)
        {
            EventHandler<LogEventArgs> handler = Log;
            if (handler != null) handler(this, e);
        }

        private void OnCameraPreviewHealthChanged(bool available, string detail)
        {
            OnStateChanged("camera", available, detail);
        }

        private void ForwardFrame(object sender, FrameEventArgs e)
        {
            EventHandler<FrameEventArgs> handler = FrameReady;
            if (handler != null) handler(this, e);
        }

        private void PublishFrame(Bitmap image, string source)
        {
            EventHandler<FrameEventArgs> handler = FrameReady;
            if (handler == null || image == null) return;
            using (FrameEventArgs args = new FrameEventArgs((Bitmap)image.Clone(), source))
                handler(this, args);
        }

        private void OnLog(string level, string message)
        {
            EventHandler<LogEventArgs> handler = Log;
            if (handler != null) handler(this, new LogEventArgs(level, message));
        }

        private void OnStateChanged(string component, bool available, string detail)
        {
            EventHandler<RuntimeStateEventArgs> handler = StateChanged;
            if (handler != null) handler(this, new RuntimeStateEventArgs(component, available, detail));
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(StationRuntime));
        }

        public void Dispose()
        {
            if (_disposed) return;
            try { StopAsync().GetAwaiter().GetResult(); } catch { }
            _workflowGate.Wait();
            _workflowGate.Release();
            _disposed = true;
            _inspectionGate.Dispose();
            _workflowGate.Dispose();
            _teaching.Dispose();
            _calibration.Dispose();
            _camera.Dispose();
            _tcpServer.Dispose();
            _sdk.Dispose();
        }
    }
}
