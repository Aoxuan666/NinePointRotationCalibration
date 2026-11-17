using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using NinePointRotationCalibration.Halcon;
using NinePointRotationCalibration.Station.Controls;
using NinePointRotationCalibration.Station.Models;
using NinePointRotationCalibration.Station.Services;
using NinePointRotationCalibration.Station.Services.Inspection;

namespace NinePointRotationCalibration.Station.Views
{
    /// <summary>
    /// 连接设计器友好的 MainForm 窗体与 StationRuntime 运行时协调器。
    /// MainForm 本身不持有业务服务依赖，从而保证 WinForms 设计器可正常实例化。
    /// </summary>
    public sealed partial class MainFormRuntimeBinder : IDisposable
    {
        // LivePreview 接收实时相机预览帧；LockedFrame 锁定拍照、导入或检测帧以便标定/调试
        private enum DisplayMode
        {
            LivePreview = 0,
            LockedFrame = 1
        }

        private readonly MainForm _view;
        private readonly StationRuntime _runtime;
        private readonly CancellationTokenSource _lifetime;
        private readonly object _displaySync = new object();
        private readonly object _binaryPreviewSync = new object();
        private readonly BoardBlobAnalyzer _binaryAnalyzer = new BoardBlobAnalyzer();
        private Bitmap _lockedDisplayImage;
        private CancellationTokenSource _binaryPreviewCancellation;
        private List<DetectionRoi> _lastDetectionRegions = new List<DetectionRoi>();
        private List<ViewportPointOverlay> _lastInspectionPoints = new List<ViewportPointOverlay>();
        private List<ViewportRoiOverlay> _lastInspectionRois = new List<ViewportRoiOverlay>();
        private List<ViewportContourOverlay> _lastInspectionContours = new List<ViewportContourOverlay>();
        private long _lastInspectionOverlayGeneration = -1;
        private DisplayMode _displayMode;
        private long _displayGeneration;
        private long _inspectionFrameGeneration = -1;
        private bool _disposed;
        private bool _cameraFeatureResizeHooked;
        private CameraParameterSet _cameraParameterSnapshot;

        public MainFormRuntimeBinder(MainForm view, StationRuntime runtime)
        {
            StationLog.EnsureInitialized();
            _view = view ?? throw new ArgumentNullException(nameof(view));
            _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
            _lifetime = new CancellationTokenSource();

            WireEvents();
            LoadInitialState();
        }

        private void WireEvents()
        {
            _view.Shown += View_Shown;
            _view.FormClosed += View_FormClosed;
            _view.SnapshotRequested += View_SnapshotRequested;
            _view.CalibrationPointRequested += View_CalibrationPointRequested;
            _view.UiActionRequested += View_UiActionRequested;
            _view.FeatureAvailabilityChanged += View_FeatureAvailabilityChanged;
            _view.WorkTabs.SelectedIndexChanged += WorkTabs_SelectedIndexChanged;

            _runtime.Log += Runtime_Log;
            _runtime.StateChanged += Runtime_StateChanged;
            _runtime.FrameReady += Runtime_FrameReady;
            _runtime.CalibrationPointChanged += Runtime_CalibrationPointChanged;
            _runtime.CalibrationCompleted += Runtime_CalibrationCompleted;
            _runtime.TeachingChanged += Runtime_TeachingChanged;
            _runtime.InspectionCompleted += Runtime_InspectionCompleted;
        }

        private void LoadInitialState()
        {
            StationOptions options = _runtime.GetOptionsSnapshot();
            DetectionProfile profile = _runtime.GetDetectionProfileSnapshot();
            TeachingData teaching = _runtime.GetTeachingSnapshot();

            SetText("txtTcpBindAddress", options.TcpBindAddress);
            SetNumeric("nudTcpPort", options.TcpPort);
            SetNumeric("nudCalibrationStepX", options.TranslationStepX);
            SetNumeric("nudCalibrationStepY", options.TranslationStepY);
            SetNumeric("nudCalibrationStepR", options.RotationStepDegrees);
            SetNumeric("nudMotionTimeout", options.MotionTimeoutMilliseconds);
            SetNumeric("nudExpectedCount", options.ExpectedBoardCount);
            SetNumeric("nudCameraExposure", options.CameraExposureMicroseconds);
            SetNumeric("nudCameraGain", options.CameraGain);
            SetNumeric("nudCameraGamma", options.CameraGamma);
            SetNumeric("nudCameraTimeout", options.CameraTimeoutMilliseconds);
            SetNumeric("nudPreviewInterval", options.PreviewIntervalMilliseconds);
            RenderCameraFeatures(new CameraParameterSet
            {
                Features = options.CameraFeatures == null
                    ? new List<CameraFeatureValue>()
                    : options.CameraFeatures.Select(item => new CameraFeatureValue
                    {
                        Name = item.Key,
                        Value = item.Value,
                        IsWritable = false
                    }).ToList()
            });
            ComboBox triggerMode = FindControl<ComboBox>("cmbCameraTriggerMode");
            triggerMode.SelectedItem = string.Equals(options.CameraTriggerMode, "Hardware", StringComparison.OrdinalIgnoreCase)
                ? "硬件触发"
                : string.Equals(options.CameraTriggerMode, "Continuous", StringComparison.OrdinalIgnoreCase)
                    ? "连续采集"
                    : "软件触发";
            SelectCameraProvider(options.CameraProvider);
            _view.RefreshFeatureAvailability(
                options.CameraEnabled,
                options.CalibrationEnabled,
                options.ArchiveImages,
                false);

            SetPathPair("txtCalibrationTemplatePath", "txtCalibrationFile", options.CalibrationJobPath);
            SetPathPair("txtPositioningTemplatePath", "txtPositioningFile", options.PositioningJobPath);
            SetText("txtTeachingFile", options.TeachingPath);
            SetText("txtDetectionProfile", options.DetectionProfilePath);
            SetText("txtImageArchive", options.ImageArchiveDirectory);

            if (!string.IsNullOrWhiteSpace(options.CameraSerialNumber))
            {
                CameraDescriptor configured = new CameraDescriptor
                {
                    Id = options.CameraSerialNumber,
                    DisplayName = options.CameraSerialNumber
                };
                _view.CameraDeviceInput.Items.Add(configured);
                _view.CameraDeviceInput.SelectedItem = configured;
            }

            ApplyDetectionProfileToView(profile);
            ApplyTeachingToView(teaching == null ? null : teaching.Left);
            ApplyTeachingToView(teaching == null ? null : teaching.Right);

            foreach (CalibrationPointRecord record in _runtime.GetCalibrationRecordsSnapshot())
                ApplyCalibrationRecord(record);

            if (!options.CameraEnabled)
                _view.SetCameraStatus(CameraUiState.Disabled, string.Empty);
            else
                _view.SetCameraStatus(
                    _runtime.IsCameraConnected ? CameraUiState.Connected : CameraUiState.Disconnected,
                    _runtime.IsCameraConnected ? "已就绪" : string.Empty);
            bool tcpStarted = _runtime.IsStarted;
            bool hostConnected = _runtime.IsHostConnected;
            _view.SetTcpStatus(
                tcpStarted || hostConnected,
                hostConnected ? "上位机" : tcpStarted ? "监听中" : "未启动");
            _view.SetDetectionSummary(options.ExpectedBoardCount, 0, false, "等待检测");
            _view.AppendLog("界面与运行时已绑定。", false);
        }

        private async void View_Shown(object sender, EventArgs e)
        {
            if (_runtime.IsStarted) return;
            if (_runtime.IsCameraEnabled)
                _view.SetCameraStatus(CameraUiState.Connecting, "正在自动连接");
            await RunOperationAsync(
                "启动工作站",
                null,
                async token =>
                {
                    await _runtime.StartAsync(token).ConfigureAwait(true);
                    if (!_runtime.IsCameraEnabled)
                        _view.SetCameraStatus(CameraUiState.Disabled, string.Empty);
                    else if (_runtime.IsCameraConnected)
                    {
                        _view.SetCameraStatus(CameraUiState.Connected, "已就绪");
                        await RefreshConnectedCameraUiAsync(token).ConfigureAwait(true);
                    }
                }).ConfigureAwait(true);
        }

        private async void View_FeatureAvailabilityChanged(object sender, EventArgs e)
        {
            if (_disposed) return;

            bool cameraEnabled = _view.CameraEnabledInput.Checked;
            bool calibrationEnabled = _view.CalibrationEnabledInput.Checked;
            bool shouldAttemptConnect = cameraEnabled && _runtime.IsStarted && !_runtime.IsCameraConnected;
            bool resumePreview = cameraEnabled && !_runtime.IsCameraEnabled;
            if (shouldAttemptConnect)
                _view.SetCameraStatus(CameraUiState.Connecting, "正在自动连接");

            await RunOperationAsync(
                "应用运行开关",
                null,
                async token =>
                {
                    await _runtime.ApplyEnablementAsync(cameraEnabled, calibrationEnabled, token)
                        .ConfigureAwait(true);
                    if (resumePreview) ResumeLivePreview();
                    if (_runtime.IsCameraEnabled && _runtime.IsCameraConnected)
                        await RefreshConnectedCameraUiAsync(token).ConfigureAwait(true);
                })
                .ConfigureAwait(true);
        }

        private void View_FormClosed(object sender, FormClosedEventArgs e)
        {
            Dispose();
        }

        private void WorkTabs_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (_disposed || _view.WorkTabs.SelectedTab == null ||
                string.Equals(_view.WorkTabs.SelectedTab.Name, "tabDetection", StringComparison.Ordinal))
                return;

            // 切离检测页时退出主画面二值预览，并递增显示代次使在途异步预览结果失效
            try
            {
                CancelBinaryPreview();
                lock (_displaySync) _displayGeneration++;
                CheckBox binaryToggle = FindControl<CheckBox>("chkBinaryPreview");
                if (binaryToggle.Checked) binaryToggle.Checked = false;
            }
            catch (ObjectDisposedException)
            {
            }
            catch (InvalidOperationException)
            {
            }
        }

        private async void View_SnapshotRequested(object sender, EventArgs e)
        {
            await RunOperationAsync(
                "单次拍照",
                _view.FindActionControl("camera.snapshot"),
                token => _runtime.CaptureOnceAsync(token)).ConfigureAwait(true);
        }

        private async void View_CalibrationPointRequested(object sender, CalibrationPointRequestedEventArgs e)
        {
            Control source = FindControl<Button>("btnCalibrationPoint" + e.PointNumber);
            await RunOperationAsync(
                "采集标定点 " + e.PointNumber,
                source,
                async token =>
                {
                    ApplyCalibrationStepOptions();
                    await _runtime.CaptureCalibrationPointAsync(e.PointNumber, token).ConfigureAwait(true);
                }).ConfigureAwait(true);
        }

        private async void View_UiActionRequested(object sender, UiActionRequestedEventArgs e)
        {
            string action = e.ActionKey ?? string.Empty;
            try
            {
                switch (action)
                {
                    case "camera.snapshot":
                        // 拍照动作统一由 SnapshotRequested 事件处理，避免同一次点击触发两次采集
                        return;
                    case "calibration.manual":
                        ApplyCalibrationStepOptions();
                        _view.SetOperationStatus("手动标定：请选择点位", false, false);
                        _view.AppendLog("已进入手动标定，请点击 1-14 点位。", false);
                        return;
                    case "calibration.auto":
                        await RunOperationAsync("自动标定", e.Source, async token =>
                        {
                            ApplyCalibrationStepOptions();
                            await _runtime.RunAutomaticCalibrationAsync(token).ConfigureAwait(true);
                        }).ConfigureAwait(true);
                        return;
                    case "calibration.cancel":
                        _runtime.CancelAutomaticCalibration();
                        _view.SetOperationStatus("已请求取消标定", false, false);
                        _view.AppendLog("已请求取消自动标定。", false);
                        return;
                    case "calibration.calculate":
                        await RunOperationAsync(
                            "计算标定结果",
                            e.Source,
                            token => _runtime.CalculateCalibrationAsync(token)).ConfigureAwait(true);
                        return;
                    case "camera.enumerate":
                        await RunOperationAsync("枚举相机", e.Source, EnumerateCamerasAsync).ConfigureAwait(true);
                        return;
                    case "camera.connect":
                        await RunOperationAsync("连接相机", e.Source, ConnectSelectedCameraAsync).ConfigureAwait(true);
                        return;
                    case "camera.disconnect":
                        await RunOperationAsync(
                            "断开相机",
                            e.Source,
                            token => _runtime.DisconnectCameraAsync()).ConfigureAwait(true);
                        return;
                    case "camera.apply":
                        await RunOperationAsync("应用相机参数", e.Source, ApplyCameraSettingsAsync).ConfigureAwait(true);
                        return;
                    case "camera.archiveImages":
                        ToggleArchiveImages();
                        return;
                    case "camera.test":
                        await RunOperationAsync(
                            "测试拍照",
                            e.Source,
                            token => _runtime.CaptureOnceAsync(token)).ConfigureAwait(true);
                        return;
                    case "image.import":
                        ImportLocalImage();
                        return;
                    case "detection.currentImage":
                        await RunOperationAsync(
                            "检测当前图",
                            e.Source,
                            RunCurrentImageInspectionAsync).ConfigureAwait(true);
                        return;
                    case "detection.debug":
                        OpenBlobDebugWindow();
                        return;
                    case "detection.binaryPreview":
                        if (IsBinaryPreviewEnabled()) ScheduleBinaryPreview(false);
                        else RestoreSourceDisplay();
                        return;
                    case "detection.previewParameters":
                        if (e.Source != null && string.Equals(
                            e.Source.Name,
                            "chkAutoThreshold",
                            StringComparison.OrdinalIgnoreCase))
                        {
                            FindControl<NumericUpDown>("nudBrightThreshold").Enabled =
                                !FindControl<CheckBox>("chkAutoThreshold").Checked;
                        }
                        if (IsBinaryPreviewEnabled()) ScheduleBinaryPreview(true);
                        return;
                    case "tcp.start":
                        await RunOperationAsync(
                            "启动 TCP 监听",
                            e.Source,
                            token => _runtime.StartAsync(token)).ConfigureAwait(true);
                        return;
                    case "tcp.stop":
                        await RunOperationAsync("停止 TCP 监听", e.Source, token => _runtime.StopAsync()).ConfigureAwait(true);
                        return;
                    case "teaching.left.capture":
                        await CaptureTeachingAsync(TeachingSide.Left, e.Source).ConfigureAwait(true);
                        return;
                    case "teaching.right.capture":
                        await CaptureTeachingAsync(TeachingSide.Right, e.Source).ConfigureAwait(true);
                        return;
                    case "teaching.left.clear":
                        await ClearTeachingAsync(TeachingSide.Left, e.Source).ConfigureAwait(true);
                        return;
                    case "teaching.right.clear":
                        await ClearTeachingAsync(TeachingSide.Right, e.Source).ConfigureAwait(true);
                        return;
                    case "template.calibration.browse":
                    case "template.positioning.browse":
                    case "files.calibration.browse":
                    case "files.positioning.browse":
                    case "files.teaching.browse":
                    case "files.detection.browse":
                    case "files.archive.browse":
                        BrowsePath(action);
                        return;
                    case "template.calibration.create":
                        await RunOperationAsync(
                            "创建标定模板",
                            e.Source,
                            token => EditTemplateAsync(false)).ConfigureAwait(true);
                        return;
                    case "template.positioning.create":
                        await RunOperationAsync(
                            "创建定位模板",
                            e.Source,
                            token => EditTemplateAsync(true)).ConfigureAwait(true);
                        return;
                    case "template.calibration.load":
                        await RunOperationAsync(
                            "加载标定文件",
                            e.Source,
                            token => LoadTemplateAsync(false, token)).ConfigureAwait(true);
                        return;
                    case "template.positioning.load":
                        await RunOperationAsync(
                            "加载定位文件",
                            e.Source,
                            token => LoadTemplateAsync(true, token)).ConfigureAwait(true);
                        return;
                    case "template.calibration.test":
                        await RunOperationAsync(
                            "测试标定模板",
                            e.Source,
                            token => TestTemplateAsync(false, token)).ConfigureAwait(true);
                        return;
                    case "template.positioning.test":
                        await RunOperationAsync(
                            "测试定位模板",
                            e.Source,
                            token => TestTemplateAsync(true, token)).ConfigureAwait(true);
                        return;
                    case "detection.save":
                        SaveDetectionProfile();
                        return;
                    case "detection.test":
                        await RunOperationAsync(
                            "手动检测",
                            e.Source,
                            RunInspectionAsync).ConfigureAwait(true);
                        return;
                    case "settings.save":
                        SaveAllSettings();
                        return;
                    default:
                        _view.AppendLog("未识别的界面动作：" + action, true);
                        return;
                }
            }
            catch (Exception exception)
            {
                ReportException(action, exception);
            }
        }

        /// <summary>执行界面触发的异步任务，统一管理按钮禁用、操作状态栏及异常日志。</summary>
        private async Task RunOperationAsync(
            string description,
            Control source,
            Func<CancellationToken, Task> operation)
        {
            if (_disposed || operation == null) return;

            CancellationToken token = _lifetime.Token;
            if (source != null) source.Enabled = false;
            _view.RefreshFeatureAvailability(
                _view.CameraEnabledInput.Checked,
                _view.CalibrationEnabledInput.Checked,
                _view.ArchiveImagesInput != null && _view.ArchiveImagesInput.Checked,
                true);
            _view.SetOperationStatus(description + "...", true, false);
            try
            {
                await operation(token).ConfigureAwait(true);
                if (!_disposed)
                {
                    _view.SetOperationStatus(description + "完成", false, false);
                    _view.AppendLog(description + "完成。", false);
                }
            }
            catch (OperationCanceledException)
            {
                if (!_disposed)
                {
                    _view.SetOperationStatus(description + "已取消", false, false);
                    _view.AppendLog(description + "已取消。", false);
                }
            }
            catch (Exception exception)
            {
                if (!_disposed) ReportException(description, exception);
            }
            finally
            {
                if (!_disposed && source != null && !source.IsDisposed) source.Enabled = true;
                if (!_disposed)
                {
                    _view.RefreshFeatureAvailability(
                        _runtime.IsCameraEnabled,
                        _runtime.IsCalibrationEnabled,
                        _runtime.IsArchiveImagesEnabled,
                        false);
                }
            }
        }

        private void ReportException(string operation, Exception exception)
        {
            Exception error = exception is AggregateException
                ? ((AggregateException)exception).Flatten().InnerExceptions.FirstOrDefault() ?? exception
                : exception;
            StationLog.WriteException("ERROR", "UI operation: " + operation, error);
            _view.AppendLog(operation + "失败：" + error.Message, true);
            _view.SetOperationStatus(operation + "失败", false, true);
        }

        private string GetSelectedCameraId()
        {
            CameraDescriptor descriptor = _view.CameraDeviceInput.SelectedItem as CameraDescriptor;
            if (descriptor != null) return descriptor.Id ?? string.Empty;
            return Convert.ToString(_view.CameraDeviceInput.SelectedItem, CultureInfo.InvariantCulture) ?? string.Empty;
        }

        private string GetSelectedCameraProvider()
        {
            string text = Convert.ToString(_view.CameraProviderInput.SelectedItem, CultureInfo.InvariantCulture) ?? string.Empty;
            if (text.IndexOf("大华", StringComparison.OrdinalIgnoreCase) >= 0 ||
                text.IndexOf("Dahua", StringComparison.OrdinalIgnoreCase) >= 0 ||
                text.IndexOf("CameraDH", StringComparison.OrdinalIgnoreCase) >= 0) return "Dahua";
            if (text.IndexOf("海康", StringComparison.OrdinalIgnoreCase) >= 0 ||
                text.IndexOf("Hikvision", StringComparison.OrdinalIgnoreCase) >= 0 ||
                text.IndexOf("MVS", StringComparison.OrdinalIgnoreCase) >= 0) return "Hikvision";
            return "Mock";
        }

        private void SelectCameraProvider(string provider)
        {
            ComboBox input = _view.CameraProviderInput;
            bool dahua = string.Equals(provider, "Dahua", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(provider, "DH", StringComparison.OrdinalIgnoreCase);
            bool hikvision = string.Equals(provider, "Hikvision", StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(provider, "MVS", StringComparison.OrdinalIgnoreCase);
            int preferred = dahua ? 0 : hikvision ? 1 : Math.Min(2, input.Items.Count - 1);
            if (preferred >= 0) input.SelectedIndex = preferred;
        }

        private T FindControl<T>(string name) where T : Control
        {
            T control = _view.Controls.Find(name, true).OfType<T>().FirstOrDefault();
            if (control == null) throw new InvalidOperationException("界面控件不存在：" + name);
            return control;
        }

        private double ReadNumeric(string name)
        {
            return Decimal.ToDouble(FindControl<NumericUpDown>(name).Value);
        }

        private void SetNumeric(string name, double value)
        {
            NumericUpDown input = FindControl<NumericUpDown>(name);
            decimal converted;
            try { converted = Convert.ToDecimal(value, CultureInfo.InvariantCulture); }
            catch (OverflowException) { converted = value < 0.0 ? input.Minimum : input.Maximum; }
            input.Value = Math.Min(input.Maximum, Math.Max(input.Minimum, converted));
        }

        private void SetText(string name, string value)
        {
            FindControl<TextBox>(name).Text = value ?? string.Empty;
        }

        private void SetPathPair(string firstName, string secondName, string path)
        {
            SetText(firstName, path);
            SetText(secondName, path);
        }

        private string ReadPath(string firstName, string secondName, string fallback)
        {
            string first = FindControl<TextBox>(firstName).Text.Trim();
            string second = FindControl<TextBox>(secondName).Text.Trim();
            return !string.IsNullOrWhiteSpace(first) ? first : !string.IsNullOrWhiteSpace(second) ? second : fallback;
        }

        private string ReadTextOrFallback(string name, string fallback)
        {
            string value = FindControl<TextBox>(name).Text.Trim();
            return string.IsNullOrWhiteSpace(value) ? fallback : value;
        }

        private static double ParseFiniteDouble(string text, string fieldName)
        {
            double value;
            if ((!double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value) &&
                 !double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)) ||
                double.IsNaN(value) || double.IsInfinity(value))
            {
                throw new FormatException(fieldName + "必须是有效数字。");
            }
            return value;
        }

        private static string FormatCalibrationStatus(CalibrationPointStatus status)
        {
            switch (status)
            {
                case CalibrationPointStatus.Moving: return "运动中";
                case CalibrationPointStatus.Capturing: return "拍照中";
                case CalibrationPointStatus.Locating: return "匹配中";
                case CalibrationPointStatus.Completed: return "OK";
                case CalibrationPointStatus.Failed: return "NG";
                default: return "待采集";
            }
        }

        /// <summary>解除界面与运行时事件订阅，取消在途任务并释放锁定帧。</summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _view.Shown -= View_Shown;
            _view.FormClosed -= View_FormClosed;
            _view.SnapshotRequested -= View_SnapshotRequested;
            _view.CalibrationPointRequested -= View_CalibrationPointRequested;
            _view.UiActionRequested -= View_UiActionRequested;
            _view.FeatureAvailabilityChanged -= View_FeatureAvailabilityChanged;
            _view.WorkTabs.SelectedIndexChanged -= WorkTabs_SelectedIndexChanged;

            _runtime.Log -= Runtime_Log;
            _runtime.StateChanged -= Runtime_StateChanged;
            _runtime.FrameReady -= Runtime_FrameReady;
            _runtime.CalibrationPointChanged -= Runtime_CalibrationPointChanged;
            _runtime.CalibrationCompleted -= Runtime_CalibrationCompleted;
            _runtime.TeachingChanged -= Runtime_TeachingChanged;
            _runtime.InspectionCompleted -= Runtime_InspectionCompleted;

            _runtime.CancelAutomaticCalibration();
            CancelBinaryPreview();
            _lifetime.Cancel();
            _lifetime.Dispose();

            Bitmap locked;
            lock (_displaySync)
            {
                locked = _lockedDisplayImage;
                _lockedDisplayImage = null;
                _displayGeneration++;
                _inspectionFrameGeneration = -1;
            }
            if (locked != null) locked.Dispose();
        }
    }
}
