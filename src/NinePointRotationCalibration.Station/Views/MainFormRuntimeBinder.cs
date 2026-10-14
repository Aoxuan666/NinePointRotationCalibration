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
    /// Connects the design-time-safe form to the runtime composition root.
    /// MainForm intentionally has no service dependency so the WinForms designer can instantiate it.
    /// </summary>
    public sealed class MainFormRuntimeBinder : IDisposable
    {
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

        public MainFormRuntimeBinder(MainForm view, StationRuntime runtime)
        {
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
            SetNumeric("nudCameraTimeout", options.CameraTimeoutMilliseconds);
            SelectCameraProvider(options.CameraProvider);
            _view.RefreshFeatureAvailability(options.CameraEnabled, options.CalibrationEnabled, false);

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
                        _view.SetCameraStatus(CameraUiState.Connected, "已就绪");
                }).ConfigureAwait(true);
        }

        private async void View_FeatureAvailabilityChanged(object sender, EventArgs e)
        {
            if (_disposed) return;

            bool cameraEnabled = _view.CameraEnabledInput.Checked;
            bool calibrationEnabled = _view.CalibrationEnabledInput.Checked;
            bool resumePreview = cameraEnabled && !_runtime.IsCameraEnabled;
            if (cameraEnabled && !_runtime.IsCameraConnected)
                _view.SetCameraStatus(CameraUiState.Connecting, "正在自动连接");

            await RunOperationAsync(
                "应用运行开关",
                null,
                async token =>
                {
                    await _runtime.ApplyEnablementAsync(cameraEnabled, calibrationEnabled, token)
                        .ConfigureAwait(true);
                    if (resumePreview) ResumeLivePreview();
                })
                .ConfigureAwait(true);
        }

        private void View_FormClosed(object sender, FormClosedEventArgs e)
        {
            Dispose();
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
                        // SnapshotRequested owns this action to avoid issuing two camera triggers.
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
                        false);
                }
            }
        }

        private async Task EnumerateCamerasAsync(CancellationToken cancellationToken)
        {
            IReadOnlyList<CameraDescriptor> cameras = await _runtime
                .EnumerateCamerasAsync(cancellationToken)
                .ConfigureAwait(true);

            ComboBox input = _view.CameraDeviceInput;
            string previousId = GetSelectedCameraId();
            input.BeginUpdate();
            try
            {
                input.Items.Clear();
                if (cameras != null)
                {
                    foreach (CameraDescriptor camera in cameras) input.Items.Add(camera);
                }

                CameraDescriptor selected = input.Items
                    .OfType<CameraDescriptor>()
                    .FirstOrDefault(item => string.Equals(item.Id, previousId, StringComparison.OrdinalIgnoreCase));
                input.SelectedItem = selected ?? (input.Items.Count > 0 ? input.Items[0] : null);
            }
            finally
            {
                input.EndUpdate();
            }

            _view.AppendLog("发现相机 " + input.Items.Count + " 台。", false);
        }

        private async Task ConnectSelectedCameraAsync(CancellationToken cancellationToken)
        {
            if (_view.CameraDeviceInput.Items.Count == 0)
                await EnumerateCamerasAsync(cancellationToken).ConfigureAwait(true);

            string deviceId = GetSelectedCameraId();
            if (string.IsNullOrWhiteSpace(deviceId)) throw new InvalidOperationException("请选择相机设备。");
            await _runtime.ConnectCameraAsync(deviceId, cancellationToken).ConfigureAwait(true);
            ResumeLivePreview();
        }

        private async Task ApplyCameraSettingsAsync(CancellationToken cancellationToken)
        {
            StationOptions before = _runtime.GetOptionsSnapshot();
            StationOptions updated = ReadOptionsFromView(before);
            _runtime.UpdateOptions(updated);

            bool providerChanged = !string.Equals(before.CameraProvider, updated.CameraProvider, StringComparison.OrdinalIgnoreCase);
            if (providerChanged)
            {
                _view.AppendLog("相机驱动变更已保存，重启工作站后生效。", false);
                return;
            }

            if (_runtime.IsCameraConnected)
            {
                await _runtime
                    .SetCameraExposureAsync(updated.CameraExposureMicroseconds, cancellationToken)
                    .ConfigureAwait(true);
            }
        }

        private Task EditTemplateAsync(bool positioning)
        {
            using (Bitmap image = SnapshotAndLockCurrentImage())
            {
                if (image == null)
                {
                    const string message = "当前没有可用图像。请先拍照或导入一张清晰图像，再创建模板。";
                    _view.AppendLog(message, true);
                    _view.SetOperationStatus("创建模板失败：无当前图像", false, true);
                    throw new InvalidOperationException(message);
                }

                TemplateSetupResult result = positioning
                    ? _runtime.EditPositioningTemplate(image, _view)
                    : _runtime.EditCalibrationTemplate(image, _view);
                if (result == null)
                {
                    throw new InvalidOperationException("模板编辑未返回结果。");
                }

                if (!result.Accepted)
                {
                    // ErrorCode.None is the SDK's explicit user-cancel contract. Any
                    // other code means the editor failed before/while opening and the
                    // actual reason must be visible instead of being reported as cancel.
                    if (result.ErrorCode == CalibrationErrorCode.None)
                    {
                        _view.AppendLog("模板编辑已取消。", false);
                        return Task.CompletedTask;
                    }

                    string detail = string.IsNullOrWhiteSpace(result.Message)
                        ? "未知错误"
                        : result.Message.Trim();
                    throw new InvalidOperationException(string.Format(
                        CultureInfo.InvariantCulture,
                        "模板编辑失败 [{0}]：{1}",
                        result.ErrorCode,
                        detail));
                }

                if (result.Template == null)
                {
                    throw new InvalidOperationException("模板编辑已确认，但没有返回模板数据。");
                }

                if (positioning)
                    ApplyDetectionProfileToView(_runtime.GetDetectionProfileSnapshot());
                _view.AppendLog(positioning ? "定位模板已更新。" : "标定模板已更新。", false);
            }
            return Task.CompletedTask;
        }

        private Task LoadTemplateAsync(bool positioning, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string firstName = positioning ? "txtPositioningTemplatePath" : "txtCalibrationTemplatePath";
            string secondName = positioning ? "txtPositioningFile" : "txtCalibrationFile";
            string path = ReadPath(firstName, secondName, string.Empty);
            if (string.IsNullOrWhiteSpace(path)) throw new InvalidOperationException("请先选择模板文件。");

            JobPersistenceResult result = positioning
                ? _runtime.LoadPositioningJob(path)
                : _runtime.LoadCalibrationJob(path);
            if (!result.Success) throw new InvalidOperationException(result.Message);

            SetPathPair(firstName, secondName, path);
            if (positioning)
                ApplyDetectionProfileToView(_runtime.GetDetectionProfileSnapshot());
            _view.AppendLog((positioning ? "定位" : "标定") + "文件已加载：" + path, false);
            return Task.CompletedTask;
        }

        private async Task TestTemplateAsync(bool positioning, CancellationToken cancellationToken)
        {
            using (Bitmap image = SnapshotAndLockCurrentImage())
            {
                if (image == null) throw new InvalidOperationException("请先拍照，再测试模板。");

                if (positioning)
                {
                    int maximumMatches = Decimal.ToInt32(_view.ExpectedBoardCountInput.Value) + 1;
                    TemplateMatchCollectionResult result = await _runtime
                        .TestPositioningTemplateAsync(image, maximumMatches, false, cancellationToken)
                        .ConfigureAwait(true);
                    if (result == null || !result.Success)
                        throw new InvalidOperationException(result == null ? "定位模板未返回结果。" : result.Message);

                    List<ViewportPointOverlay> points = new List<ViewportPointOverlay>();
                    if (result.Matches != null)
                    {
                        int index = 1;
                        foreach (TemplateMatchResult match in result.Matches)
                        {
                            points.Add(new ViewportPointOverlay
                            {
                                ImagePoint = new PointF((float)match.Column, (float)match.Row),
                                Label = string.Format(
                                    CultureInfo.InvariantCulture,
                                    "匹配 {0}  {1:0.000}  CT {2:0.0} ms",
                                    index,
                                    match.Score,
                                    result.ElapsedMilliseconds),
                                Color = Color.LimeGreen
                            });
                            index++;
                        }
                    }
                    _view.Viewport.SetPointOverlays(points);
                    _view.AppendLog(
                        string.Format(
                            CultureInfo.InvariantCulture,
                            "定位模板匹配到 {0} 个目标，CT {1:0.0} ms。",
                            points.Count,
                            result.ElapsedMilliseconds),
                        false);
                }
                else
                {
                    TemplateMatchResult result = await _runtime
                        .TestCalibrationTemplateAsync(image, cancellationToken)
                        .ConfigureAwait(true);
                    if (result == null || !result.Success)
                        throw new InvalidOperationException(result == null ? "标定模板未返回结果。" : result.Message);

                    _view.Viewport.SetPointOverlays(new[]
                    {
                        new ViewportPointOverlay
                        {
                            ImagePoint = new PointF((float)result.Column, (float)result.Row),
                            Label = string.Format(
                                CultureInfo.InvariantCulture,
                                "标定  {0:0.000}  CT {1:0.0} ms",
                                result.Score,
                                result.ElapsedMilliseconds),
                            Color = Color.LimeGreen
                        }
                    });
                    _view.AppendLog(
                        string.Format(
                            CultureInfo.InvariantCulture,
                            "标定模板匹配成功：Row {0:0.###}，Col {1:0.###}，角度 {2:0.###}，CT {3:0.0} ms。",
                            result.Row,
                            result.Column,
                            result.AngleDegrees,
                            result.ElapsedMilliseconds),
                        false);
                }
            }
        }

        private async Task CaptureTeachingAsync(TeachingSide side, Control source)
        {
            string sideKey = side == TeachingSide.Left ? "left" : "right";
            MachinePose pose = new MachinePose
            {
                X = ParseFiniteDouble(FindControl<TextBox>(sideKey + "MachineX").Text, "机械 X"),
                Y = ParseFiniteDouble(FindControl<TextBox>(sideKey + "MachineY").Text, "机械 Y"),
                ThetaDegrees = ParseFiniteDouble(FindControl<TextBox>(sideKey + "MachineR").Text, "机械 R")
            };

            await RunOperationAsync(
                (side == TeachingSide.Left ? "左侧" : "右侧") + "示教",
                source,
                token => _runtime.CaptureTeachingAsync(side, pose, token)).ConfigureAwait(true);
        }

        private Task ClearTeachingAsync(TeachingSide side, Control source)
        {
            return RunOperationAsync(
                (side == TeachingSide.Left ? "清除左侧" : "清除右侧") + "示教",
                source,
                token => _runtime.ClearTeachingAsync(side, token));
        }

        private async Task RunInspectionAsync(CancellationToken cancellationToken)
        {
            DetectionProfile profile = ReadDetectionProfileFromView();
            _runtime.UpdateDetectionProfile(profile);
            int expectedCount = Decimal.ToInt32(_view.ExpectedBoardCountInput.Value);
            double currentR = ReadNumeric("nudInspectionCurrentR");

            // The detection-page test remains useful with camera/calibration disabled:
            // reuse the locked/imported frame through the same offline workflow as the
            // top-level "检测当前图" command.
            if (!_runtime.IsCameraEnabled || !_runtime.IsCalibrationEnabled)
            {
                using (Bitmap image = SnapshotAndLockCurrentImage())
                {
                    if (image == null) throw new InvalidOperationException("请先导入图片或拍照。");
                    await _runtime
                        .RunOfflineInspectionAsync(image, expectedCount, currentR, cancellationToken)
                        .ConfigureAwait(true);
                }
                return;
            }

            await _runtime
                .RunInspectionAsync(expectedCount, currentR, cancellationToken)
                .ConfigureAwait(true);
        }

        private async Task RunCurrentImageInspectionAsync(CancellationToken cancellationToken)
        {
            using (Bitmap image = SnapshotAndLockCurrentImage())
            {
                if (image == null) throw new InvalidOperationException("请先导入图片或拍照。");

                DetectionProfile profile = ReadDetectionProfileFromView();
                _runtime.UpdateDetectionProfile(profile);
                int expectedCount = Decimal.ToInt32(_view.ExpectedBoardCountInput.Value);
                double currentR = ReadNumeric("nudInspectionCurrentR");
                await _runtime
                    .RunOfflineInspectionAsync(image, expectedCount, currentR, cancellationToken)
                    .ConfigureAwait(true);
            }
        }

        private void OpenBlobDebugWindow()
        {
            using (Bitmap image = SnapshotAndLockCurrentImage())
            {
                if (image == null) throw new InvalidOperationException("请先导入图片或拍照。");

                DetectionProfile profile = ReadDetectionProfileFromView();
                CalibrationJob positioningJob = _runtime.GetPositioningJobSnapshot();
                int maximumMatches = Decimal.ToInt32(_view.ExpectedBoardCountInput.Value) + 1;
                using (BlobDebugForm dialog = new BlobDebugForm(
                    image,
                    positioningJob,
                    profile,
                    maximumMatches))
                {
                    if (dialog.ShowDialog(_view) != DialogResult.OK) return;

                    DetectionProfile applied = dialog.AppliedProfile;
                    ApplyDetectionProfileToView(applied);
                    _runtime.UpdateDetectionProfile(applied);
                    _view.AppendLog("Blob 调试参数已应用并保存。", false);
                    _view.SetOperationStatus("Blob 调试参数已应用", false, false);
                }
            }
        }

        private void ImportLocalImage()
        {
            using (OpenFileDialog dialog = new OpenFileDialog())
            {
                dialog.CheckFileExists = true;
                dialog.Filter = "图像文件 (*.bmp;*.jpg;*.jpeg;*.png;*.tif;*.tiff)|*.bmp;*.jpg;*.jpeg;*.png;*.tif;*.tiff|所有文件 (*.*)|*.*";
                dialog.RestoreDirectory = true;
                dialog.Title = "导入检测图片";
                if (dialog.ShowDialog(_view) != DialogResult.OK) return;

                Bitmap imported = null;
                try
                {
                    using (Image source = Image.FromFile(dialog.FileName))
                        imported = new Bitmap(source);

                    PinOwnedDisplayImage(
                        imported,
                        "本地图   " + Path.GetFileName(dialog.FileName),
                        false);
                    imported = null;
                    _view.AppendLog("已导入本地图像：" + dialog.FileName, false);
                    _view.SetOperationStatus("本地图像已加载", false, false);
                }
                finally
                {
                    if (imported != null) imported.Dispose();
                }
            }
        }

        private void ApplyCalibrationStepOptions()
        {
            StationOptions options = _runtime.GetOptionsSnapshot();
            options.TranslationStepX = ReadNumeric("nudCalibrationStepX");
            options.TranslationStepY = ReadNumeric("nudCalibrationStepY");
            options.RotationStepDegrees = ReadNumeric("nudCalibrationStepR");
            options.MotionTimeoutMilliseconds = Convert.ToInt32(ReadNumeric("nudMotionTimeout"), CultureInfo.InvariantCulture);
            _runtime.UpdateOptions(options);
        }

        private void SaveAllSettings()
        {
            StationOptions options = ReadOptionsFromView(_runtime.GetOptionsSnapshot());
            _runtime.UpdateOptions(options);
            _runtime.UpdateDetectionProfile(ReadDetectionProfileFromView());
            _view.AppendLog("工作站设置与检测参数已保存。", false);
            _view.SetOperationStatus("设置已保存", false, false);
        }

        private StationOptions ReadOptionsFromView(StationOptions options)
        {
            options.CameraEnabled = _view.CameraEnabledInput.Checked;
            options.CalibrationEnabled = _view.CalibrationEnabledInput.Checked;
            options.ConfigurationVersion = StationOptions.CurrentConfigurationVersion;
            options.TcpBindAddress = _view.TcpBindAddressInput.Text.Trim();
            options.TcpPort = Decimal.ToInt32(_view.TcpPortInput.Value);
            options.CameraProvider = GetSelectedCameraProvider();
            options.CameraSerialNumber = GetSelectedCameraId();
            options.CameraExposureMicroseconds = ReadNumeric("nudCameraExposure");
            options.CameraTimeoutMilliseconds = Convert.ToInt32(ReadNumeric("nudCameraTimeout"), CultureInfo.InvariantCulture);
            options.TranslationStepX = ReadNumeric("nudCalibrationStepX");
            options.TranslationStepY = ReadNumeric("nudCalibrationStepY");
            options.RotationStepDegrees = ReadNumeric("nudCalibrationStepR");
            options.MotionTimeoutMilliseconds = Convert.ToInt32(ReadNumeric("nudMotionTimeout"), CultureInfo.InvariantCulture);
            options.ExpectedBoardCount = Decimal.ToInt32(_view.ExpectedBoardCountInput.Value);
            options.CalibrationJobPath = ReadPath("txtCalibrationTemplatePath", "txtCalibrationFile", options.CalibrationJobPath);
            options.PositioningJobPath = ReadPath("txtPositioningTemplatePath", "txtPositioningFile", options.PositioningJobPath);
            options.TeachingPath = ReadTextOrFallback("txtTeachingFile", options.TeachingPath);
            options.DetectionProfilePath = ReadTextOrFallback("txtDetectionProfile", options.DetectionProfilePath);
            options.ImageArchiveDirectory = ReadTextOrFallback("txtImageArchive", options.ImageArchiveDirectory);
            return options;
        }

        private void SaveDetectionProfile()
        {
            DetectionProfile profile = ReadDetectionProfileFromView();
            _runtime.UpdateDetectionProfile(profile);
            StationOptions options = _runtime.GetOptionsSnapshot();
            options.ExpectedBoardCount = Decimal.ToInt32(_view.ExpectedBoardCountInput.Value);
            _runtime.UpdateOptions(options);
            _view.Viewport.ClearOverlays();
            _view.AppendLog("检测参数已保存。", false);
            _view.SetOperationStatus("检测参数已保存", false, false);
        }

        private DetectionProfile ReadDetectionProfileFromView()
        {
            DetectionProfile profile = _runtime.GetDetectionProfileSnapshot() ?? new DetectionProfile();
            profile.AutoThreshold = FindControl<CheckBox>("chkAutoThreshold").Checked;
            profile.DetectDarkForeground = FindControl<CheckBox>("chkDarkForeground").Checked;
            profile.BrightThreshold = Convert.ToInt32(ReadNumeric("nudBrightThreshold"), CultureInfo.InvariantCulture);
            profile.MinimumBlobArea = ReadNumeric("nudMinimumBlobArea");
            profile.MaximumBlobArea = ReadNumeric("nudMaximumBlobArea");
            profile.RectangleFilterEnabled = _view.RectangleFilterInput.Checked;
            profile.RectangleFillThreshold = ReadNumeric("nudRectangularity");
            profile.FillHoles = FindControl<CheckBox>("chkFillHoles").Checked;
            profile.MorphologyRadius = Convert.ToInt32(
                ReadNumeric("nudMorphologyRadius"), CultureInfo.InvariantCulture);
            profile.FollowTemplateTransform = FindControl<CheckBox>("chkRoiAffineWithTemplate").Checked;
            profile.TemplateMatchingEnabled = _view.TemplateMatchingInput.Checked;
            return profile;
        }

        private void ApplyDetectionProfileToView(DetectionProfile profile)
        {
            if (profile == null) profile = new DetectionProfile();
            if (profile.Roi == null) profile.Roi = new DetectionRoi();
            SetNumeric("nudBrightThreshold", profile.BrightThreshold);
            SetNumeric("nudMinimumBlobArea", profile.MinimumBlobArea);
            SetNumeric("nudMaximumBlobArea", profile.MaximumBlobArea);
            SetNumeric("nudRectangularity", profile.RectangleFillThreshold);
            SetNumeric("nudMorphologyRadius", profile.MorphologyRadius);
            FindControl<CheckBox>("chkAutoThreshold").Checked = profile.AutoThreshold;
            FindControl<CheckBox>("chkDarkForeground").Checked = profile.DetectDarkForeground;
            FindControl<NumericUpDown>("nudBrightThreshold").Enabled = !profile.AutoThreshold;
            _view.RectangleFilterInput.Checked = profile.RectangleFilterEnabled;
            FindControl<CheckBox>("chkFillHoles").Checked = profile.FillHoles;
            FindControl<CheckBox>("chkRoiAffineWithTemplate").Checked = profile.FollowTemplateTransform;
            _view.TemplateMatchingInput.Checked = profile.TemplateMatchingEnabled;
        }

        private void BrowsePath(string action)
        {
            if (string.Equals(action, "files.archive.browse", StringComparison.OrdinalIgnoreCase))
            {
                using (FolderBrowserDialog dialog = new FolderBrowserDialog())
                {
                    dialog.Description = "选择图像归档目录";
                    dialog.SelectedPath = FindControl<TextBox>("txtImageArchive").Text;
                    if (dialog.ShowDialog(_view) == DialogResult.OK)
                        SetText("txtImageArchive", dialog.SelectedPath);
                }
                return;
            }

            string primaryName;
            string secondaryName = null;
            string filter;
            if (action.Contains("calibration"))
            {
                primaryName = "txtCalibrationTemplatePath";
                secondaryName = "txtCalibrationFile";
                filter = "标定文件 (*.nprcal)|*.nprcal|所有文件 (*.*)|*.*";
            }
            else if (action.Contains("positioning"))
            {
                primaryName = "txtPositioningTemplatePath";
                secondaryName = "txtPositioningFile";
                filter = "定位文件 (*.nprcal)|*.nprcal|所有文件 (*.*)|*.*";
            }
            else if (action.Contains("teaching"))
            {
                primaryName = "txtTeachingFile";
                filter = "JSON 文件 (*.json)|*.json|所有文件 (*.*)|*.*";
            }
            else
            {
                primaryName = "txtDetectionProfile";
                filter = "JSON 文件 (*.json)|*.json|所有文件 (*.*)|*.*";
            }

            using (OpenFileDialog dialog = new OpenFileDialog())
            {
                string current = FindControl<TextBox>(primaryName).Text;
                dialog.CheckFileExists = false;
                dialog.FileName = string.IsNullOrWhiteSpace(current) ? string.Empty : Path.GetFileName(current);
                string directory = string.IsNullOrWhiteSpace(current) ? string.Empty : Path.GetDirectoryName(current);
                if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory)) dialog.InitialDirectory = directory;
                dialog.Filter = filter;
                dialog.RestoreDirectory = true;
                if (dialog.ShowDialog(_view) != DialogResult.OK) return;

                if (secondaryName == null) SetText(primaryName, dialog.FileName);
                else SetPathPair(primaryName, secondaryName, dialog.FileName);
            }
        }

        private void Runtime_Log(object sender, LogEventArgs e)
        {
            if (e == null) return;
            bool error = string.Equals(e.Level, "ERROR", StringComparison.OrdinalIgnoreCase);
            _view.AppendLog("[" + (e.Level ?? "INFO") + "] " + (e.Message ?? string.Empty), error);
        }

        private void Runtime_StateChanged(object sender, RuntimeStateEventArgs e)
        {
            if (e == null) return;
            if (string.Equals(e.Component, "camera", StringComparison.OrdinalIgnoreCase))
            {
                ApplyCameraState(e.Available, e.Detail);
            }
            else if (string.Equals(e.Component, "host", StringComparison.OrdinalIgnoreCase))
            {
                _view.SetTcpStatus(e.Available || _runtime.IsStarted, e.Available ? "上位机 " + e.Detail : "等待上位机连接");
            }
            else if (string.Equals(e.Component, "tcp", StringComparison.OrdinalIgnoreCase))
            {
                if (e.Available && _runtime.IsHostConnected) return;
                _view.SetTcpStatus(e.Available, e.Detail);
            }
        }

        private void ApplyCameraState(bool connected, string detail)
        {
            string normalized = detail ?? string.Empty;
            if (!_runtime.IsCameraEnabled ||
                string.Equals(normalized, "Disabled", StringComparison.OrdinalIgnoreCase))
            {
                _view.SetCameraStatus(CameraUiState.Disabled, string.Empty);
                return;
            }

            if (connected)
            {
                _view.SetCameraStatus(CameraUiState.Connected, normalized);
                return;
            }

            bool ordinaryDisconnect = string.IsNullOrWhiteSpace(normalized) ||
                                      string.Equals(normalized, "Disconnected", StringComparison.OrdinalIgnoreCase);
            _view.SetCameraStatus(
                ordinaryDisconnect ? CameraUiState.Disconnected : CameraUiState.Error,
                ordinaryDisconnect ? string.Empty : normalized);
        }

        private void Runtime_FrameReady(object sender, FrameEventArgs e)
        {
            if (e == null || e.Image == null) return;
            string source = e.Source ?? "frame";
            if (string.Equals(source, "preview", StringComparison.OrdinalIgnoreCase))
            {
                lock (_displaySync)
                {
                    if (_displayMode != DisplayMode.LivePreview || _disposed) return;
                    // Drop a preview frame before copying it when the viewport is
                    // already waiting on a frame. This keeps camera throughput from
                    // turning into a UI-thread bitmap allocation queue.
                    if (_view.Viewport.TrySetPreviewImage(e.Image))
                    {
                        _view.SetImageInformation(string.Format(
                            CultureInfo.InvariantCulture,
                            "{0} x {1}   {2}   {3:HH:mm:ss.fff}",
                            e.Image.Width,
                            e.Image.Height,
                            source,
                            e.CapturedAtUtc.ToLocalTime()));
                    }
                }
                return;
            }

            Bitmap copy = new Bitmap(e.Image);
            PinOwnedDisplayImage(
                copy,
                source,
                string.Equals(source, "inspection", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(source, "offline-inspection", StringComparison.OrdinalIgnoreCase),
                e.CapturedAtUtc);
        }

        private void Runtime_CalibrationPointChanged(object sender, CalibrationPointEventArgs e)
        {
            if (e == null || e.Record == null) return;
            ApplyCalibrationRecord(e.Record);
            _view.SelectCalibrationPoint(e.Record.PointNumber);
        }

        private void ApplyCalibrationRecord(CalibrationPointRecord record)
        {
            if (record == null) return;
            bool hasSample = record.CapturedAtUtc.HasValue || record.Status == CalibrationPointStatus.Completed;
            _view.UpdateCalibrationSample(
                record.PointNumber,
                hasSample ? (double?)record.MachineX : null,
                hasSample ? (double?)record.MachineY : null,
                hasSample ? (double?)record.MachineR : null,
                hasSample ? (double?)record.ImageRow : null,
                hasSample ? (double?)record.ImageColumn : null,
                hasSample ? (double?)record.ImageAngle : null,
                FormatCalibrationStatus(record.Status));
        }

        private void Runtime_CalibrationCompleted(object sender, CalibrationResultEventArgs e)
        {
            CalibrationResult result = e == null ? null : e.Result;
            if (result == null)
            {
                _view.AppendLog("标定计算未返回结果。", true);
                return;
            }

            _view.AppendLog(
                string.Format(
                    CultureInfo.InvariantCulture,
                    "标定{0}：{1}，RMS {2:0.###} px。",
                    result.Success ? "成功" : "失败",
                    result.Message,
                    result.RmsResidualPixels),
                !result.Success);
            _view.SetOperationStatus(result.Success ? "标定完成" : "标定失败", false, !result.Success);
        }

        private void Runtime_TeachingChanged(object sender, TeachingEventArgs e)
        {
            ApplyTeachingToView(e == null ? null : e.Pose);
        }

        private void ApplyTeachingToView(TeachingPose pose)
        {
            if (pose == null) return;
            _view.UpdateTeaching(
                pose.Side == TeachingSide.Left ? "left" : "right",
                pose.ImageRow,
                pose.ImageColumn,
                pose.ImageAngleDegrees,
                pose.MachineX,
                pose.MachineY,
                pose.MachineR,
                pose.IsValid);
        }

        private void Runtime_InspectionCompleted(object sender, InspectionEventArgs e)
        {
            InspectionResult result = e == null ? null : e.Result;
            if (result == null) return;

            List<object[]> rows = new List<object[]>();
            List<ViewportPointOverlay> points = new List<ViewportPointOverlay>();
            List<ViewportRoiOverlay> regions = new List<ViewportRoiOverlay>();
            List<ViewportContourOverlay> contours = new List<ViewportContourOverlay>();
            foreach (BoardInspectionItem item in result.Items ?? new List<BoardInspectionItem>())
            {
                string sides = string.Join(",", item.Targets.Select(target => target.Side == TeachingSide.Left ? "L" : "R"));
                rows.Add(new object[]
                {
                    item.BoardIndex,
                    item.MatchScore.ToString("0.000", CultureInfo.InvariantCulture),
                    item.ImageColumn.ToString("0.###", CultureInfo.InvariantCulture),
                    item.ImageRow.ToString("0.###", CultureInfo.InvariantCulture),
                    item.ImageAngleDegrees.ToString("0.###", CultureInfo.InvariantCulture),
                    item.HasManualMark ? "有" : "无",
                    sides,
                    item.HasManualMark ? "NG" : "OK"
                });
                foreach (BlobFinding blob in item.Blobs ?? new List<BlobFinding>())
                {
                    if (blob == null || blob.RejectedAsRectangle) continue;
                    if (blob.Contour == null || blob.Contour.Count < 2) continue;
                    contours.Add(new ViewportContourOverlay
                    {
                        Points = blob.Contour.ToList(),
                        Color = Color.LimeGreen,
                        LineWidth = 2.0F
                    });
                }
                if (item.DetectionRegion != null)
                {
                    DetectionRoi region = item.DetectionRegion;
                    regions.Add(new ViewportRoiOverlay
                    {
                        ImageBounds = new RectangleF(
                            (float)(region.ReferenceCenterColumn - region.HalfWidth),
                            (float)(region.ReferenceCenterRow - region.HalfHeight),
                            (float)(region.HalfWidth * 2.0),
                            (float)(region.HalfHeight * 2.0)),
                        AngleDegrees = (float)region.PhiDegrees,
                        Label = "板 " + item.BoardIndex + " 检测区",
                        Color = item.HasManualMark ? Color.OrangeRed : Color.DeepSkyBlue
                    });
                }
            }

            _view.ReplaceDetectionResults(rows);
            lock (_displaySync)
            {
                if (_displayMode == DisplayMode.LockedFrame &&
                    _inspectionFrameGeneration >= 0 &&
                    _inspectionFrameGeneration == _displayGeneration)
                {
                    _lastInspectionPoints = points;
                    _lastInspectionRois = regions;
                    _lastInspectionContours = contours;
                    _lastDetectionRegions = result.Items
                        .Where(item => item != null && item.DetectionRegion != null)
                        .Select(item => CloneDetectionRoi(item.DetectionRegion))
                        .ToList();
                    _lastInspectionOverlayGeneration = _displayGeneration;
                    if (!IsBinaryPreviewEnabled())
                    {
                        _view.Viewport.SetPointOverlays(null);
                        _view.Viewport.SetRoiOverlays(null);
                        _view.Viewport.SetContourOverlays(contours);
                    }
                }
            }
            _view.SetDetectionSummary(result.ExpectedCount, result.ActualCount, result.Success && result.Pass, result.Message);
            _view.SetInspectionHud(
                result.ElapsedMilliseconds,
                result.MatchElapsedMilliseconds,
                result.BlobElapsedMilliseconds,
                result.ImageConversionElapsedMilliseconds,
                result.ThresholdElapsedMilliseconds,
                result.MorphologyElapsedMilliseconds,
                result.HoleFillElapsedMilliseconds,
                result.ComponentElapsedMilliseconds,
                result.ExpectedCount,
                result.ActualCount,
                result.Success && result.Pass,
                result.HasManualMarks);
            if (IsBinaryPreviewEnabled()) ScheduleBinaryPreview(false);
        }

        private bool IsBinaryPreviewEnabled()
        {
            CheckBox toggle = FindControl<CheckBox>("chkBinaryPreview");
            return toggle != null && toggle.Checked;
        }

        private async void ScheduleBinaryPreview(bool debounce)
        {
            if (_disposed || !IsBinaryPreviewEnabled()) return;

            CancellationTokenSource cancellation =
                CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            lock (_binaryPreviewSync)
            {
                if (_binaryPreviewCancellation != null) _binaryPreviewCancellation.Cancel();
                _binaryPreviewCancellation = cancellation;
            }

            Bitmap source = null;
            List<DetectionRoi> regions;
            long generation;
            lock (_displaySync)
            {
                generation = _displayGeneration;
                if (_lockedDisplayImage != null) source = new Bitmap(_lockedDisplayImage);
                regions = _lastInspectionOverlayGeneration == generation
                    ? _lastDetectionRegions.Select(CloneDetectionRoi).ToList()
                    : new List<DetectionRoi>();
            }

            if (source == null)
            {
                lock (_binaryPreviewSync)
                    if (ReferenceEquals(_binaryPreviewCancellation, cancellation))
                        _binaryPreviewCancellation = null;
                cancellation.Dispose();
                _view.SetOperationStatus("二值预览需要已拍摄或已导入的图像", false, true);
                return;
            }

            try
            {
                DetectionProfile profile = ReadDetectionProfileFromView();
                if (profile.TemplateMatchingEnabled && regions.Count == 0)
                {
                    _view.SetOperationStatus("正在定位检测 ROI...", true, false);
                    int maximumMatches = Decimal.ToInt32(_view.ExpectedBoardCountInput.Value) + 1;
                    TemplateMatchCollectionResult matchResult = await _runtime
                        .TestPositioningTemplateAsync(
                            source,
                            maximumMatches,
                            false,
                            cancellation.Token)
                        .ConfigureAwait(true);
                    CalibrationJob job = _runtime.GetPositioningJobSnapshot();
                    if (matchResult != null && matchResult.Success && job != null && job.Template != null)
                    {
                        regions = (matchResult.Matches ?? new List<TemplateMatchResult>())
                            .Where(match => match != null && match.Success)
                            .Select(match => _binaryAnalyzer.ResolveDetectionRoi(job.Template, match, profile))
                            .Where(region => region != null)
                            .ToList();
                    }
                    if (regions.Count == 0)
                        throw new InvalidOperationException("模板定位后没有得到有效检测 ROI。请检查模板和检测 ROI。");

                    lock (_displaySync)
                    {
                        if (generation == _displayGeneration)
                        {
                            _lastDetectionRegions = regions.Select(CloneDetectionRoi).ToList();
                            _lastInspectionOverlayGeneration = generation;
                        }
                    }
                }

                _view.SetOperationStatus("正在更新二值预览...", true, false);
                if (debounce) await Task.Delay(180, cancellation.Token).ConfigureAwait(true);
                BinaryPreviewPayload preview = await Task.Run(
                    () => BuildBinaryPreview(source, regions, profile, cancellation.Token),
                    cancellation.Token).ConfigureAwait(true);
                source.Dispose();
                source = null;

                bool current;
                lock (_displaySync) current = generation == _displayGeneration;
                if (!current || cancellation.IsCancellationRequested || !IsBinaryPreviewEnabled())
                {
                    preview.Dispose();
                    return;
                }

                _view.DisplayOwnedImage(preview.TakeImage());
                _view.Viewport.SetPointOverlays(null);
                _view.Viewport.SetContourOverlays(preview.Contours);
                _view.Viewport.SetRoiOverlays(null);
                _view.SetImageInformation(string.Format(
                    CultureInfo.InvariantCulture,
                    "局部二值   {0}   {1}   形态学 {2}px   ROI 外保留原图",
                    profile.DetectDarkForeground ? "白底黑点" : "黑底白点",
                    profile.AutoThreshold ? "自动阈值" : "阈值 " + profile.BrightThreshold,
                    profile.MorphologyRadius));
                _view.SetOperationStatus("二值预览已更新", false, false);
                preview.Dispose();
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exception)
            {
                if (!_disposed) _view.SetOperationStatus("二值预览失败：" + exception.Message, false, true);
            }
            finally
            {
                if (source != null) source.Dispose();
                lock (_binaryPreviewSync)
                {
                    if (ReferenceEquals(_binaryPreviewCancellation, cancellation))
                        _binaryPreviewCancellation = null;
                }
                cancellation.Dispose();
            }
        }

        private BinaryPreviewPayload BuildBinaryPreview(
            Bitmap source,
            IList<DetectionRoi> regions,
            DetectionProfile profile,
            CancellationToken cancellationToken)
        {
            BinaryPreviewPayload payload = new BinaryPreviewPayload();
            Bitmap combined = new Bitmap(source);
            payload.Image = combined;
            try
            {
                using (Graphics graphics = Graphics.FromImage(combined))
                {
                    graphics.CompositingMode = CompositingMode.SourceCopy;
                    graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
                    graphics.PixelOffsetMode = PixelOffsetMode.Half;

                    IEnumerable<DetectionRoi> targets = regions != null && regions.Count > 0
                        ? regions
                        : new[]
                        {
                            new DetectionRoi
                            {
                                ReferenceCenterRow = source.Height / 2.0,
                                ReferenceCenterColumn = source.Width / 2.0,
                                HalfWidth = source.Width / 2.0,
                                HalfHeight = source.Height / 2.0
                            }
                        };
                    foreach (DetectionRoi roi in targets)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        using (BlobAnalysisResult analysis = _binaryAnalyzer.AnalyzeRoiDetailed(
                            source, roi, profile, true, cancellationToken))
                        {
                            if (analysis.BinaryImage == null || analysis.ImageBounds.Width <= 0 ||
                                analysis.ImageBounds.Height <= 0) continue;
                            GraphicsState state = graphics.Save();
                            try
                            {
                                using (GraphicsPath clip = CreateRoiClipPath(analysis.Roi))
                                {
                                    graphics.SetClip(clip, CombineMode.Replace);
                                    graphics.DrawImage(
                                        analysis.BinaryImage,
                                        analysis.ImageBounds,
                                        0,
                                        0,
                                        analysis.BinaryImage.Width,
                                        analysis.BinaryImage.Height,
                                        GraphicsUnit.Pixel);
                                }
                            }
                            finally
                            {
                                graphics.Restore(state);
                            }
                            foreach (BlobFinding finding in analysis.Findings ?? new List<BlobFinding>())
                            {
                                if (finding == null || finding.RejectedAsRectangle ||
                                    finding.Contour == null || finding.Contour.Count < 2) continue;
                                payload.Contours.Add(new ViewportContourOverlay
                                {
                                    Points = finding.Contour.ToList(),
                                    Color = Color.LimeGreen,
                                    LineWidth = 2.0F
                                });
                            }
                        }
                    }
                }
                return payload;
            }
            catch
            {
                payload.Dispose();
                throw;
            }
        }

        private static GraphicsPath CreateRoiClipPath(DetectionRoi roi)
        {
            GraphicsPath path = new GraphicsPath();
            if (roi == null || roi.HalfWidth <= 0.0 || roi.HalfHeight <= 0.0)
            {
                path.AddRectangle(RectangleF.Empty);
                return path;
            }

            double radians = roi.PhiDegrees * Math.PI / 180.0;
            double cosine = Math.Cos(radians);
            double sine = Math.Sin(radians);
            PointF[] corners = new PointF[4];
            double[] x = { -roi.HalfWidth, roi.HalfWidth, roi.HalfWidth, -roi.HalfWidth };
            double[] y = { -roi.HalfHeight, -roi.HalfHeight, roi.HalfHeight, roi.HalfHeight };
            for (int index = 0; index < corners.Length; index++)
            {
                corners[index] = new PointF(
                    (float)(roi.ReferenceCenterColumn + (x[index] * cosine) - (y[index] * sine)),
                    (float)(roi.ReferenceCenterRow + (x[index] * sine) + (y[index] * cosine)));
            }
            path.AddPolygon(corners);
            return path;
        }

        private void RestoreSourceDisplay()
        {
            CancelBinaryPreview();
            Bitmap source;
            List<ViewportPointOverlay> points;
            List<ViewportRoiOverlay> rois;
            List<ViewportContourOverlay> contours;
            lock (_displaySync)
            {
                source = _lockedDisplayImage == null ? null : new Bitmap(_lockedDisplayImage);
                bool current = _lastInspectionOverlayGeneration == _displayGeneration;
                points = current ? _lastInspectionPoints : new List<ViewportPointOverlay>();
                rois = current ? _lastInspectionRois : new List<ViewportRoiOverlay>();
                contours = current ? _lastInspectionContours : new List<ViewportContourOverlay>();
            }
            if (source == null) return;
            _view.DisplayOwnedImage(source);
            _view.Viewport.SetPointOverlays(null);
            _view.Viewport.SetRoiOverlays(null);
            _view.Viewport.SetContourOverlays(contours);
            _view.SetImageInformation("原图   检测输入");
            _view.SetOperationStatus("已切换为原图", false, false);
        }

        private void CancelBinaryPreview()
        {
            lock (_binaryPreviewSync)
            {
                if (_binaryPreviewCancellation == null) return;
                try { _binaryPreviewCancellation.Cancel(); }
                catch (ObjectDisposedException) { }
            }
        }

        private static DetectionRoi CloneDetectionRoi(DetectionRoi source)
        {
            if (source == null) return null;
            return new DetectionRoi
            {
                ReferenceCenterRow = source.ReferenceCenterRow,
                ReferenceCenterColumn = source.ReferenceCenterColumn,
                PhiDegrees = source.PhiDegrees,
                HalfWidth = source.HalfWidth,
                HalfHeight = source.HalfHeight
            };
        }

        private Bitmap SnapshotAndLockCurrentImage()
        {
            lock (_displaySync)
            {
                if (_lockedDisplayImage != null) return new Bitmap(_lockedDisplayImage);
            }

            Bitmap snapshot = _view.Viewport.Snapshot();
            if (snapshot == null) return null;

            Bitmap lockedCopy = new Bitmap(snapshot);
            PinOwnedDisplayImage(lockedCopy, "检测输入", false);
            return snapshot;
        }

        private void PinOwnedDisplayImage(
            Bitmap image,
            string source,
            bool inspectionFrame,
            DateTime? capturedAtUtc = null)
        {
            if (image == null) return;
            CancelBinaryPreview();
            Bitmap displayCopy = new Bitmap(image);
            Bitmap previous = null;
            lock (_displaySync)
            {
                if (_disposed)
                {
                    image.Dispose();
                    displayCopy.Dispose();
                    return;
                }

                previous = _lockedDisplayImage;
                _lockedDisplayImage = image;
                _displayMode = DisplayMode.LockedFrame;
                _displayGeneration++;
                _inspectionFrameGeneration = inspectionFrame ? _displayGeneration : -1;
                ClearInspectionPreviewCache();
                _view.Viewport.ClearOverlays();
                DisplayImageCore(displayCopy, source, capturedAtUtc ?? DateTime.UtcNow);
            }
            if (previous != null) previous.Dispose();
        }

        private void ResumeLivePreview()
        {
            CancelBinaryPreview();
            CheckBox binaryToggle = FindControl<CheckBox>("chkBinaryPreview");
            if (binaryToggle != null && binaryToggle.Checked) binaryToggle.Checked = false;
            Bitmap previous;
            lock (_displaySync)
            {
                previous = _lockedDisplayImage;
                _lockedDisplayImage = null;
                _displayMode = DisplayMode.LivePreview;
                _displayGeneration++;
                _inspectionFrameGeneration = -1;
                ClearInspectionPreviewCache();
                if (!_disposed) _view.Viewport.ClearOverlays();
            }
            if (previous != null) previous.Dispose();
        }

        private void ClearInspectionPreviewCache()
        {
            _lastDetectionRegions = new List<DetectionRoi>();
            _lastInspectionPoints = new List<ViewportPointOverlay>();
            _lastInspectionRois = new List<ViewportRoiOverlay>();
            _lastInspectionContours = new List<ViewportContourOverlay>();
            _lastInspectionOverlayGeneration = -1;
        }

        private void DisplayImageCore(Bitmap image, string source, DateTime capturedAtUtc)
        {
            _view.DisplayOwnedImage(image);
            _view.SetImageInformation(string.Format(
                CultureInfo.InvariantCulture,
                "{0} x {1}   {2}   {3:HH:mm:ss.fff}",
                image.Width,
                image.Height,
                source ?? "frame",
                capturedAtUtc.ToLocalTime()));
        }

        private void ReportException(string operation, Exception exception)
        {
            Exception error = exception is AggregateException
                ? ((AggregateException)exception).Flatten().InnerExceptions.FirstOrDefault() ?? exception
                : exception;
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
            return text.IndexOf("大华", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   text.IndexOf("Dahua", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   text.IndexOf("CameraDH", StringComparison.OrdinalIgnoreCase) >= 0
                ? "Dahua"
                : "Mock";
        }

        private void SelectCameraProvider(string provider)
        {
            ComboBox input = _view.CameraProviderInput;
            bool dahua = string.Equals(provider, "Dahua", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(provider, "DH", StringComparison.OrdinalIgnoreCase);
            int preferred = dahua ? 0 : Math.Min(2, input.Items.Count - 1);
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

        private sealed class BinaryPreviewPayload : IDisposable
        {
            public BinaryPreviewPayload()
            {
                Contours = new List<ViewportContourOverlay>();
            }

            public Bitmap Image { get; set; }
            public List<ViewportContourOverlay> Contours { get; private set; }

            public Bitmap TakeImage()
            {
                Bitmap image = Image;
                Image = null;
                return image;
            }

            public void Dispose()
            {
                Bitmap image = Image;
                Image = null;
                if (image != null) image.Dispose();
            }
        }

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
