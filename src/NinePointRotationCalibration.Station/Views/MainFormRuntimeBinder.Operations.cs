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
using NinePointRotationCalibration.WinForms.Controls;
using NinePointRotationCalibration.Station.Models;
using NinePointRotationCalibration.Station.Services;
using NinePointRotationCalibration.Station.Services.Inspection;

namespace NinePointRotationCalibration.Station.Views
{
    /// <summary>界面用户操作处理：相机连接与高级参数、模板编辑/测试、左右示教、检测及路径浏览。</summary>
    public sealed partial class MainFormRuntimeBinder
    {
        private async Task SwitchCameraProviderAsync(CancellationToken cancellationToken)
        {
            string provider = GetSelectedCameraProvider();
            _view.CameraDeviceInput.Items.Clear();
            _view.CameraDeviceInput.SelectedItem = null;
            RenderCameraFeatures(null);
            ResetCameraResolution();
            await _runtime.SwitchCameraProviderAsync(provider, cancellationToken).ConfigureAwait(true);
            UpdateCameraLiveUiStatus();
            RefreshUiFeatureAvailability(false);
        }

        private async Task StartTcpServerFromViewAsync(CancellationToken cancellationToken)
        {
            string bindAddress = _view.TcpBindAddressInput.Text.Trim();
            int port = Decimal.ToInt32(_view.TcpPortInput.Value);
            await _runtime.StartTcpServerAsync(bindAddress, port, cancellationToken).ConfigureAwait(true);
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
            if (string.IsNullOrWhiteSpace(deviceId))
                throw new InvalidOperationException("未检测到在线相机，请检查相机电源及网线/USB连接。");
            StationLog.Write("INFO", "camera.switch", "Connecting selected camera " + deviceId + ".");
            LocalDebugLog.Write("INFO", "camera.switch", "Connecting selected camera " + deviceId + ".");
            await _runtime.ConnectCameraAsync(deviceId, cancellationToken).ConfigureAwait(true);
            try
            {
                await ReadDeviceParametersAsync(cancellationToken).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                LocalDebugLog.WriteException("WARN", "camera.params", "Read parameters after connect failed", exception);
                _view.AppendLog("相机已连接，但读取部分设备参数失败：" + exception.Message, false);
            }
            if (_runtime.IsCameraPreviewRunning)
                ResumeLivePreview();
            UpdateCameraLiveUiStatus();
            StationLog.Write("INFO", "camera.switch", "Live preview resumed for " + deviceId + ".");
            LocalDebugLog.Write("INFO", "camera.switch", "Live preview resumed for " + deviceId + ".");
        }

        private void ToggleArchiveImages()
        {
            bool enabled = _view.ArchiveImagesInput != null && _view.ArchiveImagesInput.Checked;
            _runtime.SetArchiveImages(enabled);
            _view.SetOperationStatus(enabled ? "已开启保存生产原图" : "已关闭保存生产原图", false, false);
            _view.AppendLog(enabled ? "已开启保存生产原图。" : "已关闭保存生产原图。", false);
        }

        private async Task ToggleLiveAcquisitionAsync(CancellationToken cancellationToken)
        {
            bool enabled = _view.LiveAcquisitionInput == null || _view.LiveAcquisitionInput.Checked;
            LocalDebugLog.Write("INFO", "ui.camera.live", "ToggleLiveAcquisitionAsync: enabled=" + enabled);
            await _runtime.SetLiveAcquisitionEnabledAsync(enabled, cancellationToken).ConfigureAwait(true);
            if (enabled && _runtime.IsCameraPreviewRunning)
            {
                ResumeLivePreview();
            }
            UpdateCameraLiveUiStatus();
            _view.SetOperationStatus(enabled ? "已开启兼容相机实时采集" : "已关闭相机实时采集", false, false);
            _view.AppendLog(enabled ? "已开启兼容相机实时采集。" : "已关闭相机实时采集。", false);
        }

        private async Task ToggleLiveStreamAsync(CancellationToken cancellationToken)
        {
            if (_runtime.IsCameraPreviewRunning)
            {
                LocalDebugLog.Write("INFO", "ui.camera.live", "Stopping live preview stream from UI.");
                await _runtime.StopLivePreviewAsync().ConfigureAwait(true);
                UpdateCameraLiveUiStatus();
                _view.SetOperationStatus("实时采集已停止", false, false);
                _view.AppendLog("相机实时采集已停止。", false);
            }
            else
            {
                if (!_runtime.IsCameraConnected)
                    throw new InvalidOperationException("相机未连接，请先连接真实相机后再开启实时预览。");
                LocalDebugLog.Write("INFO", "ui.camera.live", "Starting live preview stream from UI.");
                await _runtime.StartLivePreviewAsync(cancellationToken).ConfigureAwait(true);
                ResumeLivePreview();
                UpdateCameraLiveUiStatus();
                _view.SetOperationStatus("实时采集已启动", false, false);
                _view.AppendLog("相机实时采集已启动。", false);
            }
        }

        private async Task ResumeMainLivePreviewAsync(CancellationToken cancellationToken)
        {
            if (!_runtime.IsCameraEnabled)
                throw new InvalidOperationException("相机功能已停用，请先启用相机。");
            if (!_runtime.IsCameraConnected)
                throw new InvalidOperationException("相机未连接，无法切换实时预览。");
            LocalDebugLog.Write("INFO", "ui.camera.live", "ResumeMainLivePreviewAsync requested.");
            ResumeLivePreview();
            if (!_runtime.IsCameraPreviewRunning)
            {
                await _runtime.StartLivePreviewAsync(cancellationToken).ConfigureAwait(true);
            }
            UpdateCameraLiveUiStatus();
            _view.SetOperationStatus("主视口已恢复实时预览", false, false);
            _view.AppendLog("主视口已切换至相机实时采集画面。", false);
        }

        private void UpdateCameraLiveUiStatus()
        {
            if (_disposed) return;
            bool running = _runtime.IsCameraPreviewRunning;
            double fps = _runtime.LivePreviewFps;
            long frames = _runtime.LivePreviewFrameCount;
            long dropped = _runtime.LivePreviewDroppedFrameCount;
            string text;
            if (!_runtime.IsCameraEnabled)
            {
                text = "已停用\r\n未采集";
            }
            else if (!_runtime.IsCameraConnected)
            {
                text = "未连接\r\n未采集";
            }
            else if (running)
            {
                text = string.Format(
                    CultureInfo.InvariantCulture,
                    "● {0:0.0} FPS\r\n帧 {1}{2}",
                    fps,
                    frames,
                    dropped > 0 ? " · 跳 " + dropped : string.Empty);
            }
            else if (!_runtime.IsLiveAcquisitionEnabled)
            {
                text = "已连接\r\n实时预览已关闭";
            }
            else
            {
                text = "已连接\r\n待启动实时";
            }
            _view.SetCameraLiveStatus(running, text);
        }

        private async Task RefreshConnectedCameraUiAsync(CancellationToken cancellationToken)
        {
            if (!_runtime.IsCameraEnabled || !_runtime.IsCameraConnected) return;

            StationOptions options = _runtime.GetOptionsSnapshot();
            string activeId = options.CameraSerialNumber;
            if (!string.IsNullOrWhiteSpace(activeId))
            {
                ComboBox input = _view.CameraDeviceInput;
                CameraDescriptor existing = input.Items
                    .OfType<CameraDescriptor>()
                    .FirstOrDefault(item => string.Equals(item.Id, activeId, StringComparison.OrdinalIgnoreCase));
                if (existing == null)
                {
                    existing = new CameraDescriptor { Id = activeId, DisplayName = activeId };
                    input.Items.Add(existing);
                }
                if (!ReferenceEquals(input.SelectedItem, existing))
                    input.SelectedItem = existing;
            }

            try
            {
                await ReadDeviceParametersAsync(cancellationToken).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                StationLog.WriteException("WARN", "camera.refreshFeatures", exception);
                LocalDebugLog.WriteException("WARN", "camera.refreshFeatures", "Failed to refresh camera features", exception);
            }
        }

        private async Task ApplyCameraSettingsAsync(CancellationToken cancellationToken)
        {
            if (!_runtime.IsCameraConnected) throw new InvalidOperationException("请先连接相机。");
            decimal requested = FindControl<NumericUpDown>("nudCameraExposure").Value;
            if (!_readExposure.HasValue || requested != _readExposure.Value)
                await _runtime.SetCameraExposureAsync((double)requested, cancellationToken).ConfigureAwait(true);
            await ReadDeviceParametersAsync(cancellationToken).ConfigureAwait(true);
            UpdateCameraLiveUiStatus();
        }

        private async Task ReadDeviceParametersAsync(CancellationToken token)
        {
            RenderCameraFeatures(null);
            RenderCameraFeatures(await _runtime.GetCameraParametersAsync(token).ConfigureAwait(true));
        }

        private void RenderCameraFeatures(CameraParameterSet parameters)
        {
            _readExposure = null;
            NumericUpDown exposure = FindControl<NumericUpDown>("nudCameraExposure");
            exposure.Value = exposure.Minimum;
            if (parameters != null && parameters.ExposureMicroseconds > 0 &&
                !double.IsNaN(parameters.ExposureMicroseconds) &&
                !double.IsInfinity(parameters.ExposureMicroseconds))
            {
                decimal rawExposure = (decimal)parameters.ExposureMicroseconds;
                decimal clamped = Math.Max(exposure.Minimum, Math.Min(exposure.Maximum, rawExposure));
                exposure.Value = clamped;
                _readExposure = exposure.Value;
            }
            if (!_readExposure.HasValue) exposure.Text = string.Empty;
            FindControl<Label>("lblCameraGain").Text = parameters == null ? "--" :
                double.IsNaN(parameters.Gain) ? "不可读" : parameters.Gain.ToString("0.###", CultureInfo.CurrentCulture);
            string mode = parameters == null ? null : parameters.TriggerMode;
            FindControl<Label>("lblCameraTrigger").Text = mode == "Software" ? "软件触发" :
                mode == "Hardware" ? "硬件触发" : mode == "Continuous" ? "连续采集" :
                parameters == null ? "--" : "不可读";
        }

        private Task EditTemplateAsync(bool positioning)
        {
            bool hasSavedReferenceImage = _runtime.HasPersistedTemplateReferenceImage(positioning);
            Bitmap image = hasSavedReferenceImage
                ? TrySnapshotCurrentImageWithoutLocking()
                : SnapshotAndLockCurrentImage();
            if (image == null && !hasSavedReferenceImage)
            {
                ImportLocalImage();
                image = SnapshotAndLockCurrentImage();
            }
            using (image)
            {
                if (image == null && !hasSavedReferenceImage)
                {
                    const string message = "请先导入一张图像后再创建或编辑模板。";
                    _view.AppendLog(message, true);
                    _view.SetOperationStatus("已取消：未加载图像", false, true);
                    return Task.CompletedTask;
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
                    // ErrorCode.None 是 SDK 约定的用户主动取消标识；其它错误码表示打开或校验失败，需向用户展示原因
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

                UpdateTemplateStatusLabel(positioning, result.Template);
                if (positioning)
                {
                    ApplyDetectionProfileToView(_runtime.GetDetectionProfileSnapshot());
                    lock (_displaySync) ClearInspectionPreviewCache();
                    if (_view.IsDetectionDualViewActive) ScheduleBinaryPreview(false);
                }
                else
                {
                    SyncCalibrationMinScoreToView(_runtime.GetCalibrationJobSnapshot(_runtime.ActiveCalibrationNozzle));
                }
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
            CalibrationJob loadedJob = positioning
                ? _runtime.GetPositioningJobSnapshot()
                : _runtime.GetCalibrationJobSnapshot();
            UpdateTemplateStatusLabel(positioning, loadedJob == null ? null : loadedJob.Template);
            if (positioning)
                ApplyDetectionProfileToView(_runtime.GetDetectionProfileSnapshot());
            else
                SyncCalibrationMinScoreToView(loadedJob);
            _view.AppendLog((positioning ? "定位" : "标定") + "文件已加载：" + path, false);
            return Task.CompletedTask;
        }

        private void UpdateTemplateStatusLabel(bool positioning, TemplateDefinition template)
        {
            string controlName = positioning
                ? "lblStatustxtPositioningTemplatePath"
                : "lblStatustxtCalibrationTemplatePath";
            Label label = _view.Controls.Find(controlName, true).OfType<Label>().FirstOrDefault();
            if (label == null) return;

            if (template == null || (!template.HasUsableModel && template.TemplateRoi == null))
            {
                label.ForeColor = Color.FromArgb(94, 101, 109);
                label.Text = "未加载";
                return;
            }

            string stateText = template.HasUsableModel ? "已就绪" : "待重建模型";
            ShapeModelParameters shape = template.ShapeParameters;
            ShapeFeatureExtractionSettings extraction = shape == null ? null : shape.FeatureExtraction;
            bool isXld = template.ModelType == TemplateModelType.Shape &&
                extraction != null &&
                extraction.Mode == ShapeFeatureExtractionMode.EdgeContours;
            string modeText = template.ModelType == TemplateModelType.Ncc
                ? "NCC"
                : isXld ? "Shape(XLD)" : "Shape";

            string thresholdText;
            if (template.ModelType == TemplateModelType.Ncc)
            {
                thresholdText = "归一化互相关";
            }
            else if (isXld && extraction != null)
            {
                bool auto = extraction.AutoThreshold || (shape != null && shape.AutoThreshold);
                thresholdText = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0} Canny {1:0.#}/{2:0.#}, MinC={3}",
                    auto ? "自适应" : "手动",
                    extraction.EdgeLowThreshold,
                    extraction.EdgeHighThreshold,
                    shape == null ? 0 : shape.MinContrast);
            }
            else
            {
                bool auto = shape != null && shape.AutoThreshold;
                thresholdText = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0} 对比度 {1}/{2}",
                    auto ? "自适应" : "手动",
                    shape == null ? 0 : shape.Contrast,
                    shape == null ? 0 : shape.MinContrast);
            }

            label.ForeColor = template.HasUsableModel
                ? Color.FromArgb(21, 128, 61)
                : Color.FromArgb(180, 83, 9);
            label.Text = string.Format(
                CultureInfo.InvariantCulture,
                "{0} · {1} · {2} · Rev {3}{4}",
                stateText,
                modeText,
                thresholdText,
                template.Revision,
                template.HasReferenceImage ? " · 含底图" : string.Empty);
        }

        private async Task TestTemplateAsync(bool positioning, CancellationToken cancellationToken)
        {
            Bitmap image = SnapshotAndLockCurrentImage();
            if (image == null)
            {
                ImportLocalImage();
                image = SnapshotAndLockCurrentImage();
            }
            using (image)
            {
                if (image == null)
                {
                    throw new InvalidOperationException("请先点击「导入图片」加载图像后再测试模板。");
                }

                if (positioning)
                {
                    int maximumMatches = Decimal.ToInt32(_view.ExpectedBoardCountInput.Value) + 1;
                    TemplateMatchCollectionResult result = await _runtime
                        .TestPositioningTemplateAsync(image, maximumMatches, true, cancellationToken)
                        .ConfigureAwait(true);
                    if (result == null || !result.Success)
                        throw new InvalidOperationException(result == null ? "定位模板未返回结果。" : result.Message);

                    List<ViewportPointOverlay> points = new List<ViewportPointOverlay>();
                    List<ViewportContourOverlay> contours = new List<ViewportContourOverlay>();
                    if (result.Matches != null)
                    {
                        foreach (TemplateMatchResult match in result.Matches)
                        {
                            points.Add(new ViewportPointOverlay
                            {
                                ImagePoint = new PointF((float)match.Column, (float)match.Row),
                                Label = string.Empty,
                                Color = Color.LimeGreen
                            });
                            IEnumerable<List<ImagePoint>> segments = match.ContourSegments;
                            if (segments == null || !segments.Any())
                                segments = new[] { match.Contour };
                            foreach (List<ImagePoint> segment in segments)
                            {
                                if (segment == null || segment.Count < 2) continue;
                                contours.Add(new ViewportContourOverlay
                                {
                                    Points = segment.Select(point => new PointF(
                                        (float)point.Column,
                                        (float)point.Row)).ToList(),
                                    Color = Color.LimeGreen,
                                    LineWidth = 2.0F
                                });
                            }
                        }
                    }
                    _view.Viewport.SetPointOverlays(points);
                    _view.Viewport.SetContourOverlays(contours);
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
                            Label = string.Empty,
                            Color = Color.LimeGreen
                        }
                    });
                    List<ViewportContourOverlay> contours = new List<ViewportContourOverlay>();
                    IEnumerable<List<ImagePoint>> segments = result.ContourSegments;
                    if (segments == null || !segments.Any()) segments = new[] { result.Contour };
                    foreach (List<ImagePoint> segment in segments)
                    {
                        if (segment == null || segment.Count < 2) continue;
                        contours.Add(new ViewportContourOverlay
                        {
                            Points = segment.Select(point => new PointF(
                                (float)point.Column,
                                (float)point.Row)).ToList(),
                            Color = Color.LimeGreen,
                            LineWidth = 2.0F
                        });
                    }
                    _view.Viewport.SetContourOverlays(contours);
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

        private async Task RecordTeachingMachinePoseAsync(TeachingSide side, Control source)
        {
            string sideKey = side == TeachingSide.Left ? "left" : "right";
            string sideLabel = side == TeachingSide.Left ? "左侧" : "右侧";
            bool queryHost = _runtime.IsHostConnected;
            MachinePose manualPose = null;
            if (!queryHost)
            {
                manualPose = new MachinePose
                {
                    X = ParseFiniteDouble(FindControl<TextBox>(sideKey + "MachineX").Text, "机械 X"),
                    Y = ParseFiniteDouble(FindControl<TextBox>(sideKey + "MachineY").Text, "机械 Y"),
                    ThetaDegrees = ParseFiniteDouble(FindControl<TextBox>(sideKey + "MachineR").Text, "机械 R")
                };
            }

            await RunOperationAsync(
                sideLabel + (queryHost ? "实时读取并记录轴坐标" : "记录轴坐标"),
                source,
                async token =>
                {
                    TeachingPose saved = queryHost
                        ? await _runtime.RequestAndRecordTeachingMachinePoseFromHostAsync(side, token).ConfigureAwait(true)
                        : await _runtime.RecordTeachingMachinePoseAsync(side, manualPose, token).ConfigureAwait(true);
                    if (saved != null)
                    {
                        ApplyTeachingToView(saved);
                    }
                    if (saved != null && saved.IsValid)
                    {
                        _view.AppendLog(sideLabel + "示教已完成（轴坐标与图像坐标均已保存）。", false);
                    }
                    else
                    {
                        _view.AppendLog(
                            sideLabel + (queryHost ? "实时轴坐标已从上位机读取并同步到页面。" : "手动轴坐标已记录并同步到页面（上位机未连接）。") +
                            "请将轴移出视野后点击「模板匹配定位」。",
                            false);
                    }
                }).ConfigureAwait(true);
        }

        private async Task LocateTeachingImageAsync(TeachingSide side, Control source)
        {
            string sideLabel = side == TeachingSide.Left ? "左侧" : "右侧";
            await RunOperationAsync(
                sideLabel + "模板匹配定位",
                source,
                async token =>
                {
                    TeachingPose saved = await _runtime.LocateTeachingImageAsync(side, token).ConfigureAwait(true);
                    if (saved != null)
                    {
                        ApplyTeachingToView(saved);
                        string summary = string.Format(
                            CultureInfo.InvariantCulture,
                            "{0}模板匹配定位成功：Row {1:0.###}, Col {2:0.###}, 角度 {3:0.###}°, 分数 {4:0.000}。{5}",
                            sideLabel,
                            saved.ImageRow,
                            saved.ImageColumn,
                            saved.ImageAngleDegrees,
                            saved.MatchScore,
                            saved.IsValid ? "示教数据已合并保存。" : "请继续点击「记录轴坐标」以完成示教。");
                        _view.AppendLog(summary, false);
                    }
                }).ConfigureAwait(true);
        }

        private Task ClearTeachingAsync(TeachingSide side, Control source)
        {
            return RunOperationAsync(
                (side == TeachingSide.Left ? "清除左侧" : "清除右侧") + "示教",
                source,
                async token =>
                {
                    TeachingPose cleared = await _runtime.ClearTeachingAsync(side, token).ConfigureAwait(true);
                    if (cleared != null) ApplyTeachingToView(cleared);
                });
        }

        private async Task RunInspectionAsync(CancellationToken cancellationToken)
        {
            if (!_runtime.IsInspectionEnabled)
                throw new InvalidOperationException("检测功能已关闭，请先在「相机」页开启「启用检测」。");

            DetectionProfile profile = ReadDetectionProfileFromView();
            ValidateDetectionProfile(profile);
            _runtime.UpdateDetectionProfile(profile);
            int expectedCount = Decimal.ToInt32(_view.ExpectedBoardCountInput.Value);
            double currentRLeft = ReadNumeric("nudInspectionCurrentR");
            double currentRRight = ReadNumeric("nudInspectionCurrentRRight");

            // 若当前已锁定/导入图像，直接检测该锁定图；若处于实时预览或未锁定状态且相机已连接，则触发真实相机采图检测（不要求先标定）
            LockedFrameGrayCache cache;
            using (Bitmap image = TrySnapshotLockedImage(out cache))
            {
                if (image != null)
                {
                    InspectionResult offlineResult = await _runtime
                        .RunOfflineInspectionAsync(image, expectedCount, currentRLeft, currentRRight, cancellationToken, cache)
                        .ConfigureAwait(true);
                    if (offlineResult != null && !offlineResult.Executed)
                    {
                        throw new InvalidOperationException(TranslateInspectionError(offlineResult));
                    }
                    return;
                }
            }

            if (!_runtime.IsCameraEnabled || !_runtime.IsCameraConnected)
            {
                throw new InvalidOperationException("当前未连接相机且尚未导入图像，请先连接相机或点击「导入图片」。");
            }

            InspectionResult result = await _runtime
                .RunInspectionAsync(expectedCount, currentRLeft, currentRRight, cancellationToken)
                .ConfigureAwait(true);
            if (result != null && !result.Executed)
            {
                throw new InvalidOperationException(TranslateInspectionError(result));
            }
        }

        private async Task RunCurrentImageInspectionAsync(CancellationToken cancellationToken)
        {
            if (!_runtime.IsInspectionEnabled)
                throw new InvalidOperationException("检测功能已关闭，请先在「相机」页开启「启用检测」。");

            LockedFrameGrayCache cache;
            using (Bitmap image = SnapshotForInspection(out cache))
            {
                if (image == null) throw new InvalidOperationException("请先点击「导入图片」加载检测图像。");

                DetectionProfile profile = ReadDetectionProfileFromView();
                ValidateDetectionProfile(profile);
                _runtime.UpdateDetectionProfile(profile);
                int expectedCount = Decimal.ToInt32(_view.ExpectedBoardCountInput.Value);
                double currentRLeft = ReadNumeric("nudInspectionCurrentR");
                double currentRRight = ReadNumeric("nudInspectionCurrentRRight");
                InspectionResult result = await _runtime
                    .RunOfflineInspectionAsync(image, expectedCount, currentRLeft, currentRRight, cancellationToken, cache)
                    .ConfigureAwait(true);
                if (result != null && !result.Executed)
                {
                    throw new InvalidOperationException(TranslateInspectionError(result));
                }
            }
        }

        // 锁定当前显示图像打开 Blob 调试弹窗，确认后同步参数至界面和运行时配置
        private void OpenBlobDebugWindow()
        {
            Bitmap image = SnapshotAndLockCurrentImage();
            if (image == null)
            {
                ImportLocalImage();
                image = SnapshotAndLockCurrentImage();
            }
            using (image)
            {
                if (image == null) throw new InvalidOperationException("请先点击「导入图片」加载图像。");

                DetectionProfile profile = ReadDetectionProfileFromView();
                CalibrationJob positioningJob = _runtime.GetPositioningJobSnapshot();
                int maximumMatches = Decimal.ToInt32(_view.ExpectedBoardCountInput.Value) + 1;
                using (BlobDebugForm dialog = new BlobDebugForm(
                    image,
                    positioningJob,
                    profile,
                    maximumMatches))
                {
                    LocalDebugLog.Write("INFO", "ui.blobDebug", "Opened BlobDebugForm (image=" + image.Width + "x" + image.Height + ", maxMatches=" + maximumMatches + ").");
                    if (dialog.ShowDialog(_view) != DialogResult.OK)
                    {
                        LocalDebugLog.Write("INFO", "ui.blobDebug", "BlobDebugForm cancelled.");
                        return;
                    }

                    DetectionProfile applied = dialog.AppliedProfile;
                    ApplyDetectionProfileToView(applied);
                    _runtime.UpdateDetectionProfile(applied);
                    LocalDebugLog.Write("INFO", "ui.blobDebug", "BlobDebugForm applied: " + StationLogFormatter.FormatDetectionProfile(applied));
                    _view.AppendLog("Blob 调试参数已应用并保存。", false);
                    _view.SetOperationStatus("Blob 调试参数已应用", false, false);
                    if (_view.IsDetectionDualViewActive) ScheduleBinaryPreview(false);
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
                    {
                        Bitmap sourceBitmap = source as Bitmap;
                        imported = sourceBitmap != null
                            ? ImageViewport.CloneToPArgbFast(sourceBitmap)
                            : new Bitmap(source);
                    }

                    int w = imported.Width;
                    int h = imported.Height;
                    // 交由 PinOwnedDisplayImage 接管位图所有权并锁定显示，imported 置空避免 finally 误释放
                    PinOwnedDisplayImage(
                        imported,
                        "本地图   " + Path.GetFileName(dialog.FileName),
                        false);
                    imported = null;
                    LocalDebugLog.Write("INFO", "ui.image", "Imported local image '" + dialog.FileName + "' (" + w + "x" + h + ").");
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
            _runtime.UpdateCalibrationMinScore(GetSelectedCalibrationNozzle(), ReadNumeric("nudCalibrationMinScore"));
        }

        private void SaveAllSettings()
        {
            string requestedFolder = ReadTextOrFallback("txtConfigDirectory", string.Empty);
            StationOptions current = _runtime.GetOptionsSnapshot();
            if (!string.IsNullOrWhiteSpace(requestedFolder) &&
                !string.Equals(
                    Path.GetFullPath(requestedFolder),
                    Path.GetFullPath(current.GetUnifiedConfigDirectory()),
                    StringComparison.OrdinalIgnoreCase))
            {
                _runtime.ImportConfigDirectory(requestedFolder);
                RefreshAllConfigUiFromRuntime();
                current = _runtime.GetOptionsSnapshot();
            }

            DetectionProfile profile = ReadDetectionProfileFromView();
            ValidateDetectionProfile(profile);
            StationOptions options = ReadOptionsFromView(current);
            _runtime.UpdateOptions(options);
            _runtime.UpdateDetectionProfile(profile);
            _view.AppendLog("工作站设置与检测参数已保存。", false);
            _view.SetOperationStatus("设置已保存", false, false);
        }

        private StationOptions ReadOptionsFromView(StationOptions options)
        {
            options.CameraEnabled = _view.CameraEnabledInput.Checked;
            options.CalibrationEnabled = _view.CalibrationEnabledInput.Checked;
            options.InspectionEnabled = _view.InspectionEnabledInput == null || _view.InspectionEnabledInput.Checked;
            options.ConfigurationVersion = StationOptions.CurrentConfigurationVersion;
            options.TcpBindAddress = _view.TcpBindAddressInput.Text.Trim();
            options.TcpPort = Decimal.ToInt32(_view.TcpPortInput.Value);
            options.CameraProvider = GetSelectedCameraProvider();
            options.CameraSerialNumber = GetSelectedCameraId();
            options.LiveAcquisitionEnabled = _view.LiveAcquisitionInput == null || _view.LiveAcquisitionInput.Checked;
            options.ArchiveImages = FindControl<CheckBox>("chkArchiveImages").Checked;
            options.TranslationStepX = ReadNumeric("nudCalibrationStepX");
            options.TranslationStepY = ReadNumeric("nudCalibrationStepY");
            options.RotationStepDegrees = ReadNumeric("nudCalibrationStepR");
            options.MotionTimeoutMilliseconds = Convert.ToInt32(ReadNumeric("nudMotionTimeout"), CultureInfo.InvariantCulture);
            options.ExpectedBoardCount = Decimal.ToInt32(_view.ExpectedBoardCountInput.Value);
            options.ConfigDirectory = ReadTextOrFallback("txtConfigDirectory", options.GetUnifiedConfigDirectory());
            options.CalibrationJobPath = ReadTextOrFallback("txtCalibrationFile", options.CalibrationJobPath);
            options.RightCalibrationJobPath = ReadTextOrFallback("txtRightCalibrationFile", options.RightCalibrationJobPath);
            options.PositioningJobPath = ReadPath("txtPositioningTemplatePath", "txtPositioningFile", options.PositioningJobPath);
            options.TeachingPath = ReadTextOrFallback("txtTeachingFile", options.TeachingPath);
            options.DetectionProfilePath = ReadTextOrFallback("txtDetectionProfile", options.DetectionProfilePath);
            options.ImageArchiveDirectory = ReadTextOrFallback("txtImageArchive", options.ImageArchiveDirectory);
            return options;
        }

        private void SaveDetectionProfile()
        {
            DetectionProfile profile = ReadDetectionProfileFromView();
            ValidateDetectionProfile(profile);
            _runtime.UpdateDetectionProfile(profile);
            StationOptions options = _runtime.GetOptionsSnapshot();
            options.ExpectedBoardCount = Decimal.ToInt32(_view.ExpectedBoardCountInput.Value);
            _runtime.UpdateOptions(options);
            _view.Viewport.ClearOverlays();
            _view.AppendLog("检测参数已保存。", false);
            _view.SetOperationStatus("检测参数已保存", false, false);
        }

        private static void ValidateDetectionProfile(DetectionProfile profile)
        {
            if (profile == null) throw new InvalidOperationException("检测参数无效。");
            if (profile.MinimumBlobArea > profile.MaximumBlobArea)
                throw new InvalidOperationException("最小面积不能大于最大面积，请调整后再继续。");
        }

        private DetectionProfile ReadDetectionProfileFromView()
        {
            DetectionProfile profile = _runtime.GetDetectionProfileSnapshot() ?? new DetectionProfile();
            bool darkForeground = _view.DetectionPolarityInput != null && _view.DetectionPolarityInput.SelectedIndex == 1;
            bool fillEnabled = _view.HoleFillModeInput == null || _view.HoleFillModeInput.SelectedIndex == 0;
            profile.AutoThreshold = FindControl<CheckBox>("chkAutoThreshold").Checked;
            profile.DetectDarkForeground = darkForeground;
            profile.BrightThreshold = Convert.ToInt32(ReadNumeric("nudBrightThreshold"), CultureInfo.InvariantCulture);
            profile.MinimumBlobArea = ReadNumeric("nudMinimumBlobArea");
            profile.MaximumBlobArea = ReadNumeric("nudMaximumBlobArea");
            profile.RectangleFilterEnabled = _view.RectangleFilterInput.Checked;
            profile.RectangleFillThreshold = ReadNumeric("nudRectangularity");
            profile.FillHoles = fillEnabled;
            profile.FillDarkHoles = fillEnabled;
            profile.MorphologyRadius = Convert.ToInt32(
                ReadNumeric("nudMorphologyRadius"), CultureInfo.InvariantCulture);
            profile.ClosingRadius = Convert.ToInt32(
                ReadNumeric("nudClosingRadius"), CultureInfo.InvariantCulture);
            profile.FollowTemplateTransform = FindControl<CheckBox>("chkRoiAffineWithTemplate").Checked;
            profile.TemplateMatchingEnabled = _view.TemplateMatchingInput.Checked;
            return profile;
        }

        private void ApplyDetectionProfileToView(DetectionProfile profile)
        {
            if (profile == null) profile = new DetectionProfile();
            if (profile.Roi == null) profile.Roi = new DetectionRoi();
            _suppressDetectionEvents = true;
            try
            {
                SetNumeric("nudBrightThreshold", profile.BrightThreshold);
                SetNumeric("nudMinimumBlobArea", profile.MinimumBlobArea);
                SetNumeric("nudMaximumBlobArea", profile.MaximumBlobArea);
                SetNumeric("nudRectangularity", profile.RectangleFillThreshold);
                SetNumeric("nudMorphologyRadius", profile.MorphologyRadius);
                SetNumeric("nudClosingRadius", profile.ClosingRadius);
                FindControl<CheckBox>("chkAutoThreshold").Checked = profile.AutoThreshold;
                FindControl<NumericUpDown>("nudBrightThreshold").Enabled = !profile.AutoThreshold;
                if (_view.DetectionPolarityInput != null && _view.DetectionPolarityInput.Items.Count >= 2)
                {
                    _view.DetectionPolarityInput.SelectedIndex = profile.DetectDarkForeground ? 1 : 0;
                }
                if (_view.HoleFillModeInput != null && _view.HoleFillModeInput.Items.Count >= 2)
                {
                    bool fillEnabled = profile.DetectDarkForeground ? profile.FillDarkHoles : profile.FillHoles;
                    _view.HoleFillModeInput.SelectedIndex = fillEnabled ? 0 : 1;
                }
                _view.RectangleFilterInput.Checked = profile.RectangleFilterEnabled;
                FindControl<NumericUpDown>("nudRectangularity").Enabled = profile.RectangleFilterEnabled;
                CheckBox affineWithTemplate = FindControl<CheckBox>("chkRoiAffineWithTemplate");
                affineWithTemplate.Checked = profile.FollowTemplateTransform;
                affineWithTemplate.Enabled = profile.TemplateMatchingEnabled;
                _view.TemplateMatchingInput.Checked = profile.TemplateMatchingEnabled;
                _view.ExpectedBoardCountInput.Enabled = profile.TemplateMatchingEnabled;
            }
            finally
            {
                _suppressDetectionEvents = false;
            }
        }

        private void ImportConfigFolder()
        {
            using (FolderBrowserDialog dialog = new FolderBrowserDialog())
            {
                dialog.Description = "选择统一配置文件夹（自动加载同目录下的标定、定位、示教与检测参数文件）";
                string current = ReadTextOrFallback("txtConfigDirectory", string.Empty);
                if (!string.IsNullOrWhiteSpace(current) && Directory.Exists(current))
                {
                    dialog.SelectedPath = current;
                }
                if (dialog.ShowDialog(_view) != DialogResult.OK) return;

                string summary = _runtime.ImportConfigDirectory(dialog.SelectedPath);
                RefreshAllConfigUiFromRuntime();
                _view.AppendLog(summary, false);
                _view.SetOperationStatus("配置文件夹已导入", false, false);
            }
        }

        private void RefreshAllConfigUiFromRuntime()
        {
            StationOptions options = _runtime.GetOptionsSnapshot();
            TeachingSide activeNozzle = _runtime.ActiveCalibrationNozzle;
            SetText("txtConfigDirectory", options.GetUnifiedConfigDirectory());
            SetText("txtCalibrationFile", options.CalibrationJobPath);
            SetText("txtRightCalibrationFile", options.RightCalibrationJobPath);
            SetText("txtCalibrationTemplatePath", activeNozzle == TeachingSide.Right ? options.RightCalibrationJobPath : options.CalibrationJobPath);
            SetPathPair("txtPositioningTemplatePath", "txtPositioningFile", options.PositioningJobPath);
            SetText("txtTeachingFile", options.TeachingPath);
            SetText("txtDetectionProfile", options.DetectionProfilePath);
            SetText("txtImageArchive", options.ImageArchiveDirectory);

            CalibrationJob calibrationJob = _runtime.GetCalibrationJobSnapshot(activeNozzle);
            CalibrationJob positioningJob = _runtime.GetPositioningJobSnapshot();
            SyncCalibrationMinScoreToView(calibrationJob);
            UpdateTemplateStatusLabel(false, calibrationJob == null ? null : calibrationJob.Template);
            UpdateTemplateStatusLabel(true, positioningJob == null ? null : positioningJob.Template);
            ApplyDetectionProfileToView(_runtime.GetDetectionProfileSnapshot());
            TeachingData teaching = _runtime.GetTeachingSnapshot();
            ApplyTeachingToView(teaching == null ? null : teaching.Left);
            ApplyTeachingToView(teaching == null ? null : teaching.Right);
            foreach (CalibrationPointRecord record in _runtime.GetCalibrationRecordsSnapshot(activeNozzle))
            {
                ApplyCalibrationRecord(record);
            }
            CalibrationResult calibrationResult = _runtime.GetCalibrationResultSnapshot(activeNozzle);
            _view.SetCalibrationRmsSummary(calibrationResult);
            _view.ApplyCalibrationDiagnostics(calibrationResult);

            lock (_displaySync) ClearInspectionPreviewCache();
            if (_view.IsDetectionDualViewActive) ScheduleBinaryPreview(false);
            UpdateCameraLiveUiStatus();
        }

        private static string TranslateInspectionError(InspectionResult result)
        {
            if (result == null) return "未返回检测结果";
            switch (result.ErrorCode ?? string.Empty)
            {
                case "NO_MATERIAL":
                    return "视野内未检测到 PCB 来料（空料）";
                case "IMAGE_MISSING":
                    return "尚未加载图像，请先点击「导入图片」";
                case "POSITIONING_TEMPLATE_MISSING":
                    return "定位模板尚未配置，请先在「模板」页加载或创建定位模板";
                case "DETECTION_ROI_MISSING":
                case "BOARD_BOUNDS_MISSING":
                    return "定位模板缺少模板 ROI 或检测 ROI，请在模板编辑器中添加区域并创建模型";
                case "POSITIONING_FAILED":
                    return "当前图像未能匹配到定位模板，请检查模板参数或目标数量设置";
                case "CALIBRATION_MISSING":
                    return "左/右吸嘴 9+5 标定尚未全部完成";
                case "TEACHING_MISSING":
                    return "左右吸嘴示教数据不完整";
                case "CURRENT_R_INVALID":
                    return "传入的 R1/R2 角度无效";
                case "COORDINATE_NOT_MAPPED":
                    return "检测已完成，但标定未启用或未就绪，未生成取料坐标";
                case "COORDINATE_MAPPING_FAILED":
                    return "存在单板坐标映射异常，未生成完整取料坐标";
                default:
                    return string.IsNullOrWhiteSpace(result.Message) ? "检测执行失败" : result.Message;
            }
        }

        private void BrowsePath(string action)
        {
            if (string.Equals(action, "files.folder.import", StringComparison.OrdinalIgnoreCase))
            {
                ImportConfigFolder();
                return;
            }
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

            bool isRightCalibration = string.Equals(action, "files.calibrationRight.browse", StringComparison.OrdinalIgnoreCase);
            string primaryName;
            string secondaryName = null;
            string filter;
            if (isRightCalibration)
            {
                primaryName = "txtRightCalibrationFile";
                filter = "右吸嘴标定文件 (*.nprcal)|*.nprcal|所有文件 (*.*)|*.*";
            }
            else if (action.Contains("calibration"))
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

                string chosenFolder = Path.GetDirectoryName(dialog.FileName);
                if (!string.IsNullOrWhiteSpace(chosenFolder))
                {
                    _runtime.ImportConfigDirectory(chosenFolder);
                }

                if (File.Exists(dialog.FileName))
                {
                    if (isRightCalibration)
                    {
                        if (_runtime.IsCalibrationEnabled)
                        {
                            _runtime.LoadCalibrationJob(TeachingSide.Right, dialog.FileName);
                        }
                        else
                        {
                            _view.AppendLog("当前已关闭「启用标定」，仅同步配置目录路径，未加载右吸嘴标定任务。", false);
                        }
                    }
                    else if (action.Contains("calibration"))
                    {
                        if (_runtime.IsCalibrationEnabled)
                        {
                            TeachingSide targetNozzle = string.Equals(action, "files.calibration.browse", StringComparison.OrdinalIgnoreCase)
                                ? TeachingSide.Left
                                : _runtime.ActiveCalibrationNozzle;
                            _runtime.LoadCalibrationJob(targetNozzle, dialog.FileName);
                        }
                        else
                        {
                            _view.AppendLog("当前已关闭「启用标定」，仅同步配置目录路径，未加载标定任务。", false);
                        }
                    }
                    else if (action.Contains("positioning"))
                    {
                        _runtime.LoadPositioningJob(dialog.FileName);
                    }
                    else if (action.Contains("teaching"))
                    {
                        _runtime.ReloadAuxiliaryConfigs(dialog.FileName, null);
                    }
                    else
                    {
                        _runtime.ReloadAuxiliaryConfigs(null, dialog.FileName);
                    }
                }

                RefreshAllConfigUiFromRuntime();
                if (secondaryName == null) SetText(primaryName, dialog.FileName);
                else SetPathPair(primaryName, secondaryName, dialog.FileName);
                _view.AppendLog("已切换至配置目录并加载：" + dialog.FileName, false);
                _view.SetOperationStatus("配置已加载", false, false);
            }
        }
    }
}
