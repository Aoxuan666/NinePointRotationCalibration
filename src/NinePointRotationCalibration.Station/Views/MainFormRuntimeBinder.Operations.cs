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
            StationLog.Write("INFO", "camera.switch", "Connecting selected camera " + deviceId + ".");
            LocalDebugLog.Write("INFO", "camera.switch", "Connecting selected camera " + deviceId + ".");
            await _runtime.ConnectCameraAsync(deviceId, cancellationToken).ConfigureAwait(true);
            await _runtime.SetCameraParametersAsync(ReadCameraParametersFromView(), cancellationToken).ConfigureAwait(true);
            CameraParameterSet connectedParameters = await _runtime.GetCameraParametersAsync(cancellationToken).ConfigureAwait(true);
            RenderCameraFeatures(connectedParameters);
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
            LocalDebugLog.Write("INFO", "ui.camera.live", "ResumeMainLivePreviewAsync requested.");
            ResumeLivePreview();
            if (_runtime.IsCameraEnabled && _runtime.IsLiveAcquisitionEnabled && !_runtime.IsCameraPreviewRunning)
            {
                if (_runtime.IsCameraConnected || _runtime.IsMockCamera)
                {
                    await _runtime.StartLivePreviewAsync(cancellationToken).ConfigureAwait(true);
                }
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
                text = "相机未启用";
            }
            else if (!_runtime.IsLiveAcquisitionEnabled)
            {
                text = "实时采集已关闭";
            }
            else if (running)
            {
                text = string.Format(
                    CultureInfo.InvariantCulture,
                    "采集中 · {0:0.0} FPS · 帧 {1}{2}",
                    fps,
                    frames,
                    dropped > 0 ? " · 跳帧 " + dropped : string.Empty);
            }
            else if (_runtime.IsCameraConnected)
            {
                text = "已连接 · 待启动实时";
            }
            else
            {
                text = "未连接 · 点击「开始实时」采集";
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
                CameraParameterSet connectedParameters = await _runtime
                    .GetCameraParametersAsync(cancellationToken)
                    .ConfigureAwait(true);
                RenderCameraFeatures(connectedParameters);
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
                CameraParameterSet applied = ReadCameraParametersFromView();
                await _runtime.SetCameraParametersAsync(applied, cancellationToken).ConfigureAwait(true);
                RenderCameraFeatures(await _runtime.GetCameraParametersAsync(cancellationToken).ConfigureAwait(true));
            }
            UpdateCameraLiveUiStatus();
        }

        private CameraParameterSet ReadCameraParametersFromView()
        {
            ComboBox triggerMode = FindControl<ComboBox>("cmbCameraTriggerMode");
            string trigger = Convert.ToString(triggerMode.SelectedItem, CultureInfo.InvariantCulture);
            return new CameraParameterSet
            {
                ExposureMicroseconds = ReadNumeric("nudCameraExposure"),
                Gain = ReadNumeric("nudCameraGain"),
                Gamma = ReadNumeric("nudCameraGamma"),
                Features = ReadCameraFeaturesFromView(),
                TriggerMode = string.Equals(trigger, "硬件触发", StringComparison.Ordinal) ? "Hardware" :
                    string.Equals(trigger, "连续采集", StringComparison.Ordinal) ? "Continuous" : "Software"
            };
        }

        private List<CameraFeatureValue> ReadCameraFeaturesFromView()
        {
            FlowLayoutPanel panel = FindControl<FlowLayoutPanel>("flpCameraAdvancedParameters");
            List<CameraFeatureValue> features = new List<CameraFeatureValue>();
            foreach (Control container in panel.Controls)
            {
                Control control = container.Controls.Count > 1 ? container.Controls[1] : container;
                CameraFeatureValue metadata = container.Tag as CameraFeatureValue;
                if (metadata == null) continue;
                CameraFeatureValue feature = metadata.Clone();
                NumericUpDown numeric = control as NumericUpDown;
                CheckBox checkBox = control as CheckBox;
                ComboBox comboBox = control as ComboBox;
                TextBox textBox = control as TextBox;
                if (numeric != null) feature.Value = numeric.Value.ToString(CultureInfo.InvariantCulture);
                else if (checkBox != null) feature.Value = checkBox.Checked ? "true" : "false";
                else if (comboBox != null) feature.Value = Convert.ToString(comboBox.SelectedItem, CultureInfo.InvariantCulture);
                else if (textBox != null) feature.Value = textBox.Text;
                features.Add(feature);
            }
            return features;
        }

        private void RenderCameraFeatures(CameraParameterSet parameters)
        {
            _cameraParameterSnapshot = parameters == null ? null : parameters.Clone();
            FlowLayoutPanel panel = FindControl<FlowLayoutPanel>("flpCameraAdvancedParameters");
            if (!_cameraFeatureResizeHooked)
            {
                _cameraFeatureResizeHooked = true;
                panel.SizeChanged += delegate { ResizeCameraFeatureRows(panel); };
            }

            panel.SuspendLayout();
            try
            {
                UiRenderOptimizer.EnableDoubleBufferingRecursive(panel);
                panel.Controls.Clear();
                int rowWidth = GetCameraFeatureRowWidth(panel);
                Dictionary<string, CameraFeatureValue> supported = (parameters == null || parameters.Features == null)
                    ? new Dictionary<string, CameraFeatureValue>(StringComparer.OrdinalIgnoreCase)
                    : parameters.Features.Where(item => item != null && !string.IsNullOrWhiteSpace(item.Name))
                        .ToDictionary(item => item.Name, item => item, StringComparer.OrdinalIgnoreCase);
                foreach (CameraFeatureValue feature in CreateFixedCameraFeatures(parameters, supported))
                {
                    Control input = CreateFeatureInput(feature);
                    if (input == null) continue;
                    UiRenderOptimizer.EnableDoubleBufferingRecursive(input);
                    input.Tag = feature.Clone();
                    if (input.Controls.Count > 1) input.Controls[1].Tag = feature.Clone();
                    input.Margin = new Padding(0, 2, 0, 2);
                    input.Width = rowWidth;
                    panel.Controls.Add(input);
                }
            }
            finally { panel.ResumeLayout(true); }
        }

        private static int GetCameraFeatureRowWidth(FlowLayoutPanel panel)
        {
            int available = panel.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - panel.Padding.Horizontal - 6;
            return Math.Max(260, available);
        }

        private static void ResizeCameraFeatureRows(FlowLayoutPanel panel)
        {
            if (panel == null || panel.IsDisposed || panel.Controls.Count == 0) return;
            int rowWidth = GetCameraFeatureRowWidth(panel);
            bool anyChanged = false;
            foreach (Control child in panel.Controls)
            {
                if (child.Width != rowWidth)
                {
                    anyChanged = true;
                    break;
                }
            }
            if (!anyChanged) return;

            panel.SuspendLayout();
            try
            {
                foreach (Control child in panel.Controls)
                {
                    if (child.Width != rowWidth) child.Width = rowWidth;
                }
            }
            finally
            {
                panel.ResumeLayout(true);
            }
        }

        // 将大华/海康/模拟相机上报的差异化特征统一映射到固定中文表单行，设备不支持项置为只读
        private static List<CameraFeatureValue> CreateFixedCameraFeatures(CameraParameterSet parameters, Dictionary<string, CameraFeatureValue> supported)
        {
            string[] names =
            {
                "ExposureAuto", "GainAuto", "BalanceWhiteAuto", "Gain", "BalanceRatioRed", "BalanceRatioGreen",
                "BalanceRatioBlue", "AcquisitionFrameRate", "BlackLevel", "OffsetX", "OffsetY", "Width", "Height",
                "PixelFormat", "ReverseX", "ReverseY"
            };
            List<CameraFeatureValue> result = new List<CameraFeatureValue>();
            foreach (string name in names)
            {
                CameraFeatureValue defaults = CreateDefaultFixedFeature(name, parameters);
                CameraFeatureValue raw;
                if (!supported.TryGetValue(name, out raw))
                {
                    string alias = name == "Gain" ? "GainRaw" : name == "BlackLevel" ? "BlackLevelRaw" : null;
                    if (!string.IsNullOrWhiteSpace(alias)) supported.TryGetValue(alias, out raw);
                }

                CameraFeatureValue feature = defaults.Clone();
                if (raw == null)
                {
                    feature.IsWritable = false;
                }
                else
                {
                    bool hasTypedMetadata = raw.HasRange || raw.Minimum < raw.Maximum ||
                        (raw.EnumValues != null && raw.EnumValues.Count > 0) ||
                        raw.Kind != CameraFeatureKind.Number;
                    if (hasTypedMetadata)
                    {
                        feature.Kind = raw.Kind;
                        feature.Minimum = raw.Minimum;
                        feature.Maximum = raw.Maximum;
                        feature.Increment = raw.Increment;
                        feature.HasRange = raw.HasRange;
                        if (raw.EnumValues != null)
                        {
                            foreach (string enumItem in raw.EnumValues)
                            {
                                if (!string.IsNullOrWhiteSpace(enumItem) && !feature.EnumValues.Contains(enumItem))
                                    feature.EnumValues.Add(enumItem);
                            }
                        }
                    }

                    if (!string.IsNullOrWhiteSpace(raw.DisplayName) &&
                        !string.Equals(raw.DisplayName, name, StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(raw.DisplayName, raw.Name, StringComparison.OrdinalIgnoreCase))
                    {
                        feature.DisplayName = raw.DisplayName;
                    }

                    if (!string.IsNullOrWhiteSpace(raw.Unit))
                        feature.Unit = raw.Unit;

                    if (!string.IsNullOrWhiteSpace(raw.Value))
                    {
                        decimal unusedNumeric;
                        bool looksNumericForEnum = feature.Kind == CameraFeatureKind.Enumeration &&
                            (feature.EnumValues == null || feature.EnumValues.Count == 0 ||
                             !feature.EnumValues.Contains(raw.Value)) &&
                            decimal.TryParse(raw.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out unusedNumeric);
                        if (!looksNumericForEnum)
                            feature.Value = raw.Value;
                    }

                    feature.IsWritable = raw.IsWritable;
                }

                result.Add(feature);
            }
            return result;
        }

        private static CameraFeatureValue CreateDefaultFixedFeature(string name, CameraParameterSet parameters)
        {
            CameraFeatureValue feature = new CameraFeatureValue
            {
                Name = name,
                DisplayName = name,
                Kind = CameraFeatureKind.Number,
                Value = "0",
                IsWritable = false
            };
            switch (name)
            {
                case "ExposureAuto": feature.DisplayName = "自动曝光"; feature.Kind = CameraFeatureKind.Enumeration; feature.Value = "Off"; break;
                case "GainAuto": feature.DisplayName = "自动增益"; feature.Kind = CameraFeatureKind.Enumeration; feature.Value = "Off"; break;
                case "BalanceWhiteAuto": feature.DisplayName = "自动白平衡"; feature.Kind = CameraFeatureKind.Enumeration; feature.Value = "Off"; break;
                case "Gain": feature.DisplayName = "增益"; break;
                case "BalanceRatioRed": feature.DisplayName = "白平衡 R"; feature.Value = "1"; break;
                case "BalanceRatioGreen": feature.DisplayName = "白平衡 G"; feature.Value = "1"; break;
                case "BalanceRatioBlue": feature.DisplayName = "白平衡 B"; feature.Value = "1"; break;
                case "AcquisitionFrameRate": feature.DisplayName = "采集帧率"; feature.Unit = "fps"; break;
                case "BlackLevel": feature.DisplayName = "黑电平"; break;
                case "OffsetX": feature.DisplayName = "ROI X 偏移"; feature.Kind = CameraFeatureKind.Integer; break;
                case "OffsetY": feature.DisplayName = "ROI Y 偏移"; feature.Kind = CameraFeatureKind.Integer; break;
                case "Width": feature.DisplayName = "ROI 宽度"; feature.Kind = CameraFeatureKind.Integer; break;
                case "Height": feature.DisplayName = "ROI 高度"; feature.Kind = CameraFeatureKind.Integer; break;
                case "PixelFormat": feature.DisplayName = "像素格式"; feature.Kind = CameraFeatureKind.Enumeration; feature.Value = ""; break;
                case "ReverseX": feature.DisplayName = "水平翻转"; feature.Kind = CameraFeatureKind.Boolean; feature.Value = "false"; break;
                case "ReverseY": feature.DisplayName = "垂直翻转"; feature.Kind = CameraFeatureKind.Boolean; feature.Value = "false"; break;
            }
            return feature;
        }

        private static Control CreateFeatureInput(CameraFeatureValue feature)
        {
            string label = string.IsNullOrWhiteSpace(feature.DisplayName) ? feature.Name : feature.DisplayName;
            if (feature.Kind == CameraFeatureKind.Boolean)
            {
                return new CheckBox
                {
                    AutoSize = false,
                    Height = 26,
                    Padding = new Padding(2, 0, 0, 0),
                    Text = label,
                    Enabled = feature.IsWritable,
                    Checked = string.Equals(feature.Value, "true", StringComparison.OrdinalIgnoreCase)
                };
            }
            if (feature.Kind == CameraFeatureKind.Enumeration)
            {
                ComboBox combo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
                if (feature.EnumValues != null) combo.Items.AddRange(feature.EnumValues.ToArray());
                if (!string.IsNullOrWhiteSpace(feature.Value) && combo.Items.IndexOf(feature.Value) < 0) combo.Items.Add(feature.Value);
                combo.SelectedItem = feature.Value;
                combo.Enabled = feature.IsWritable;
                return WrapFeatureControl(label, combo);
            }
            if (feature.Kind == CameraFeatureKind.String)
                return WrapFeatureControl(label, new TextBox { Text = feature.Value ?? string.Empty, Enabled = feature.IsWritable });

            decimal current;
            decimal.TryParse(feature.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out current);
            decimal minimum = feature.HasRange ? ToDecimal(feature.Minimum, decimal.MinValue) : (feature.Kind == CameraFeatureKind.Integer ? decimal.MinValue : -1000000000M);
            decimal maximum = feature.HasRange ? ToDecimal(feature.Maximum, decimal.MaxValue) : (feature.Kind == CameraFeatureKind.Integer ? decimal.MaxValue : 1000000000M);
            if (minimum >= maximum) { minimum = -1000000000M; maximum = 1000000000M; }
            NumericUpDown numeric = new NumericUpDown
            {
                DecimalPlaces = feature.Kind == CameraFeatureKind.Integer ? 0 : 4,
                Increment = feature.Increment > 0.0 ? ToDecimal(feature.Increment, 0.0001M) : (feature.Kind == CameraFeatureKind.Integer ? 1M : 0.01M),
                Minimum = minimum,
                Maximum = maximum,
                TextAlign = HorizontalAlignment.Right,
                Value = Math.Min(maximum, Math.Max(minimum, current))
            };
            numeric.Enabled = feature.IsWritable;
            return WrapFeatureControl(label, numeric);
        }

        private static Control WrapFeatureControl(string label, Control input)
        {
            TableLayoutPanel row = new TableLayoutPanel();
            row.ColumnCount = 2;
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 118.0F));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100.0F));
            row.RowCount = 1;
            row.RowStyles.Add(new RowStyle(SizeType.Percent, 100.0F));
            row.Height = 28;
            row.Margin = Padding.Empty;

            Label title = new Label
            {
                AutoEllipsis = true,
                AutoSize = false,
                Dock = DockStyle.Fill,
                Margin = new Padding(0, 0, 6, 0),
                Text = label,
                TextAlign = ContentAlignment.MiddleLeft
            };
            input.Dock = DockStyle.Fill;
            input.Margin = new Padding(0, 2, 0, 2);
            row.Controls.Add(title, 0, 0);
            row.Controls.Add(input, 1, 0);
            return row;
        }

        private static decimal ToDecimal(double value, decimal fallback)
        {
            try { return Convert.ToDecimal(value, CultureInfo.InvariantCulture); }
            catch (OverflowException) { return fallback; }
        }

        private Task EditTemplateAsync(bool positioning)
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
                "{0} · {1} · {2} · Rev {3}",
                stateText,
                modeText,
                thresholdText,
                template.Revision);
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

            // 只要当前已加载/锁定图像，或未启用实体相机（含模拟相机），均直接检测当前加载图像，避免回退抓取默认模拟图
            using (Bitmap image = SnapshotAndLockCurrentImage())
            {
                if (image != null)
                {
                    InspectionResult offlineResult = await _runtime
                        .RunOfflineInspectionAsync(image, expectedCount, currentR, cancellationToken)
                        .ConfigureAwait(true);
                    if (offlineResult != null && !offlineResult.Executed)
                    {
                        throw new InvalidOperationException(TranslateInspectionError(offlineResult));
                    }
                    return;
                }
            }

            if (!_runtime.IsCameraEnabled ||
                !_runtime.IsCalibrationEnabled ||
                _runtime.IsMockCamera ||
                !_runtime.IsCameraConnected)
            {
                throw new InvalidOperationException("当前尚未加载图像，请先点击「导入图片」加载检测图像。");
            }

            InspectionResult result = await _runtime
                .RunInspectionAsync(expectedCount, currentR, cancellationToken)
                .ConfigureAwait(true);
            if (result != null && !result.Executed)
            {
                throw new InvalidOperationException(TranslateInspectionError(result));
            }
        }

        private async Task RunCurrentImageInspectionAsync(CancellationToken cancellationToken)
        {
            using (Bitmap image = SnapshotAndLockCurrentImage())
            {
                if (image == null) throw new InvalidOperationException("请先点击「导入图片」加载检测图像。");

                DetectionProfile profile = ReadDetectionProfileFromView();
                _runtime.UpdateDetectionProfile(profile);
                int expectedCount = Decimal.ToInt32(_view.ExpectedBoardCountInput.Value);
                double currentR = ReadNumeric("nudInspectionCurrentR");
                InspectionResult result = await _runtime
                    .RunOfflineInspectionAsync(image, expectedCount, currentR, cancellationToken)
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
                        imported = new Bitmap(source);

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

            StationOptions options = ReadOptionsFromView(current);
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
            options.CameraGain = ReadNumeric("nudCameraGain");
            options.CameraGamma = ReadNumeric("nudCameraGamma");
            options.CameraFeatures = ReadCameraFeaturesFromView()
                .ToDictionary(item => item.Name, item => item.Value ?? string.Empty, StringComparer.OrdinalIgnoreCase);
            string trigger = Convert.ToString(FindControl<ComboBox>("cmbCameraTriggerMode").SelectedItem, CultureInfo.InvariantCulture);
            options.CameraTriggerMode = string.Equals(trigger, "硬件触发", StringComparison.Ordinal) ? "Hardware" :
                string.Equals(trigger, "连续采集", StringComparison.Ordinal) ? "Continuous" : "Software";
            options.CameraTimeoutMilliseconds = Convert.ToInt32(ReadNumeric("nudCameraTimeout"), CultureInfo.InvariantCulture);
            options.PreviewIntervalMilliseconds = Convert.ToInt32(ReadNumeric("nudPreviewInterval"), CultureInfo.InvariantCulture);
            options.LiveAcquisitionEnabled = _view.LiveAcquisitionInput == null || _view.LiveAcquisitionInput.Checked;
            options.ArchiveImages = FindControl<CheckBox>("chkArchiveImages").Checked;
            options.TranslationStepX = ReadNumeric("nudCalibrationStepX");
            options.TranslationStepY = ReadNumeric("nudCalibrationStepY");
            options.RotationStepDegrees = ReadNumeric("nudCalibrationStepR");
            options.MotionTimeoutMilliseconds = Convert.ToInt32(ReadNumeric("nudMotionTimeout"), CultureInfo.InvariantCulture);
            options.ExpectedBoardCount = Decimal.ToInt32(_view.ExpectedBoardCountInput.Value);
            options.ConfigDirectory = ReadTextOrFallback("txtConfigDirectory", options.GetUnifiedConfigDirectory());
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
            SetText("txtConfigDirectory", options.GetUnifiedConfigDirectory());
            SetPathPair("txtCalibrationTemplatePath", "txtCalibrationFile", options.CalibrationJobPath);
            SetPathPair("txtPositioningTemplatePath", "txtPositioningFile", options.PositioningJobPath);
            SetText("txtTeachingFile", options.TeachingPath);
            SetText("txtDetectionProfile", options.DetectionProfilePath);
            SetText("txtImageArchive", options.ImageArchiveDirectory);

            CalibrationJob calibrationJob = _runtime.GetCalibrationJobSnapshot();
            CalibrationJob positioningJob = _runtime.GetPositioningJobSnapshot();
            UpdateTemplateStatusLabel(false, calibrationJob == null ? null : calibrationJob.Template);
            UpdateTemplateStatusLabel(true, positioningJob == null ? null : positioningJob.Template);
            ApplyDetectionProfileToView(_runtime.GetDetectionProfileSnapshot());
            TeachingData teaching = _runtime.GetTeachingSnapshot();
            ApplyTeachingToView(teaching == null ? null : teaching.Left);
            ApplyTeachingToView(teaching == null ? null : teaching.Right);
            foreach (CalibrationPointRecord record in _runtime.GetCalibrationRecordsSnapshot())
            {
                ApplyCalibrationRecord(record);
            }
            CalibrationResult calibrationResult = _runtime.GetCalibrationResultSnapshot();
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
                    return "九点与旋转标定尚未完成";
                case "TEACHING_MISSING":
                    return "左右吸嘴示教数据不完整";
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

                string chosenFolder = Path.GetDirectoryName(dialog.FileName);
                if (!string.IsNullOrWhiteSpace(chosenFolder))
                {
                    _runtime.ImportConfigDirectory(chosenFolder);
                }

                if (File.Exists(dialog.FileName))
                {
                    if (action.Contains("calibration"))
                    {
                        _runtime.LoadCalibrationJob(dialog.FileName);
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
