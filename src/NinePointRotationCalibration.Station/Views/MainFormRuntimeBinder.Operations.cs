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
    /// <summary>User-triggered operations: camera, templates, teaching, inspection, settings and paths.</summary>
    public sealed partial class MainFormRuntimeBinder
    {
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
            options.ArchiveImages = FindControl<CheckBox>("chkArchiveImages").Checked;
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
    }
}
