using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;
using NinePointRotationCalibration.Station.Controls;

namespace NinePointRotationCalibration.Station.Views
{
    public enum CameraUiState
    {
        Disabled,
        Disconnected,
        Connecting,
        Connected,
        Error
    }

    public enum TcpUiState
    {
        NotListening,
        Listening,
        Connected
    }

    public sealed class CalibrationPointRequestedEventArgs : EventArgs
    {
        public CalibrationPointRequestedEventArgs(int pointNumber)
        {
            PointNumber = pointNumber;
        }

        public int PointNumber { get; private set; }
    }

    public sealed class UiActionRequestedEventArgs : EventArgs
    {
        public UiActionRequestedEventArgs(string actionKey, Control source)
        {
            ActionKey = actionKey ?? string.Empty;
            Source = source;
        }

        public string ActionKey { get; private set; }
        public Control Source { get; private set; }
    }

    [DesignerCategory("Form")]
    public partial class MainForm : Form
    {
        private readonly Dictionary<int, Button> _pointButtonMap = new Dictionary<int, Button>();
        private readonly Color _idlePointColor = Color.FromArgb(50, 56, 64);
        private readonly Color _activePointColor = Color.FromArgb(17, 122, 101);
        private bool _workflowBusy;
        private bool _suppressFeatureEvents;
        private CameraUiState _cameraUiState = CameraUiState.Disconnected;
        private TcpUiState _tcpUiState = TcpUiState.NotListening;

        public MainForm()
        {
            InitializeComponent();
            InitializeCalibrationRows();
            WireUiEvents();
            toolTip.SetToolTip(
                chkTemplateMatchingEnabled,
                "关闭后跳过模板定位，对当前图像直接做整图二值化/Blob；生产检测建议保持开启。");
            RefreshFeatureAvailability(false);
        }

        /// <summary>
        /// Compatibility overload for composition roots. The UI deliberately does not inspect
        /// or retain the supplied object; runtime binding belongs outside the designer surface.
        /// </summary>
        public MainForm(object compositionRoot) : this()
        {
        }

        public event EventHandler SnapshotRequested;
        public event EventHandler<CalibrationPointRequestedEventArgs> CalibrationPointRequested;
        public event EventHandler<UiActionRequestedEventArgs> UiActionRequested;
        public event EventHandler FeatureAvailabilityChanged;

        [Browsable(false)]
        public ImageViewport Viewport
        {
            get { return imageViewport; }
        }

        [Browsable(false)]
        public DataGridView CalibrationGrid
        {
            get { return dgvCalibration; }
        }

        [Browsable(false)]
        public DataGridView DetectionResultGrid
        {
            get { return dgvDetectionResults; }
        }

        [Browsable(false)]
        public RichTextBox LogView
        {
            get { return rtbLog; }
        }

        [Browsable(false)]
        public TabControl WorkTabs
        {
            get { return tabWork; }
        }

        [Browsable(false)]
        public NumericUpDown ExpectedBoardCountInput
        {
            get { return nudExpectedCount; }
        }

        [Browsable(false)]
        public CheckBox RectangleFilterInput
        {
            get { return chkRectangleFilter; }
        }

        [Browsable(false)]
        public CheckBox TemplateMatchingInput
        {
            get { return chkTemplateMatchingEnabled; }
        }

        [Browsable(false)]
        public ComboBox CameraProviderInput
        {
            get { return cmbCameraProvider; }
        }

        [Browsable(false)]
        public ComboBox CameraDeviceInput
        {
            get { return cmbCameraDevice; }
        }

        [Browsable(false)]
        public CheckBox CameraEnabledInput
        {
            get { return chkCameraEnabled; }
        }

        [Browsable(false)]
        public CheckBox CalibrationEnabledInput
        {
            get { return chkCalibrationEnabled; }
        }

        [Browsable(false)]
        public Button ImportImageButton
        {
            get { return btnImportImage; }
        }

        [Browsable(false)]
        public Button DetectCurrentImageButton
        {
            get { return btnDetectCurrentImage; }
        }

        [Browsable(false)]
        public Label CameraStatusIndicator
        {
            get { return lblCameraIndicator; }
        }

        [Browsable(false)]
        public CameraUiState CameraStatusState
        {
            get { return _cameraUiState; }
        }

        [Browsable(false)]
        public Label TcpStatusIndicator
        {
            get { return lblTcpIndicator; }
        }

        [Browsable(false)]
        public TcpUiState TcpStatusState
        {
            get { return _tcpUiState; }
        }

        [Browsable(false)]
        public TextBox TcpBindAddressInput
        {
            get { return txtTcpBindAddress; }
        }

        [Browsable(false)]
        public NumericUpDown TcpPortInput
        {
            get { return nudTcpPort; }
        }

        public void DisplayImage(Bitmap image)
        {
            imageViewport.SetImage(image);
        }

        public void DisplayOwnedImage(Bitmap image)
        {
            imageViewport.SetImageOwned(image);
        }

        public void SetCameraStatus(bool connected, string detail)
        {
            SetCameraStatus(connected ? CameraUiState.Connected : CameraUiState.Disconnected, detail);
        }

        public void SetCameraStatus(CameraUiState state, string detail)
        {
            RunOnUiThread(delegate
            {
                string stateText;
                Color indicatorColor;
                switch (state)
                {
                    case CameraUiState.Disabled:
                        stateText = "相机已停用";
                        indicatorColor = Color.FromArgb(145, 151, 158);
                        break;
                    case CameraUiState.Connecting:
                        stateText = "相机连接中";
                        indicatorColor = Color.FromArgb(230, 176, 47);
                        break;
                    case CameraUiState.Connected:
                        stateText = "相机已连接";
                        indicatorColor = Color.FromArgb(46, 190, 105);
                        break;
                    case CameraUiState.Error:
                        stateText = "相机异常";
                        indicatorColor = Color.FromArgb(222, 75, 75);
                        break;
                    default:
                        stateText = "相机未连接";
                        indicatorColor = Color.FromArgb(145, 151, 158);
                        break;
                }

                _cameraUiState = state;
                lblCameraIndicator.ForeColor = indicatorColor;
                lblCameraStatus.Text = stateText +
                                       (string.IsNullOrWhiteSpace(detail) ? string.Empty : "  " + detail.Trim());
                toolTip.SetToolTip(lblCameraStatus, lblCameraStatus.Text);
            });
        }

        public void RefreshFeatureAvailability(bool cameraEnabled, bool calibrationEnabled, bool workflowBusy)
        {
            RunOnUiThread(delegate
            {
                _suppressFeatureEvents = true;
                try
                {
                    chkCameraEnabled.Checked = cameraEnabled;
                    chkCalibrationEnabled.Checked = calibrationEnabled;
                }
                finally
                {
                    _suppressFeatureEvents = false;
                }

                ApplyFeatureAvailability(cameraEnabled, calibrationEnabled, workflowBusy);
            });
        }

        public void RefreshFeatureAvailability(bool workflowBusy)
        {
            RunOnUiThread(delegate
            {
                ApplyFeatureAvailability(chkCameraEnabled.Checked, chkCalibrationEnabled.Checked, workflowBusy);
            });
        }

        public void RefreshFeatureAvailability()
        {
            RefreshFeatureAvailability(_workflowBusy);
        }

        public void SetTcpStatus(bool connected, string detail)
        {
            RunOnUiThread(delegate
            {
                string normalized = (detail ?? string.Empty).Trim();
                bool hostConnected = normalized.StartsWith("上位机", StringComparison.OrdinalIgnoreCase);
                string stateText;
                Color indicatorColor;
                if (hostConnected)
                {
                    _tcpUiState = TcpUiState.Connected;
                    stateText = "TCP 已连接";
                    indicatorColor = Color.FromArgb(46, 190, 105);
                }
                else if (connected)
                {
                    _tcpUiState = TcpUiState.Listening;
                    stateText = "TCP 监听中";
                    indicatorColor = Color.FromArgb(230, 176, 47);
                }
                else
                {
                    _tcpUiState = TcpUiState.NotListening;
                    stateText = "TCP 未监听";
                    indicatorColor = Color.FromArgb(145, 151, 158);
                }

                lblTcpIndicator.ForeColor = indicatorColor;
                lblTcpStatus.Text = stateText;
                toolTip.SetToolTip(
                    lblTcpStatus,
                    string.IsNullOrWhiteSpace(normalized) ? stateText : stateText + "  " + normalized);
            });
        }

        public void SetOperationStatus(string text, bool isBusy, bool isError)
        {
            RunOnUiThread(delegate
            {
                lblOperationStatus.Text = string.IsNullOrWhiteSpace(text) ? "就绪" : text;
                lblOperationStatus.BackColor = isError
                    ? Color.FromArgb(153, 55, 55)
                    : isBusy ? Color.FromArgb(157, 105, 25) : Color.FromArgb(43, 92, 68);
            });
        }

        public void SetImageInformation(string text)
        {
            RunOnUiThread(delegate { lblImageInfo.Text = string.IsNullOrWhiteSpace(text) ? "无图像" : text; });
        }

        public void SelectCalibrationPoint(int pointNumber)
        {
            RunOnUiThread(delegate
            {
                foreach (KeyValuePair<int, Button> pair in _pointButtonMap)
                {
                    pair.Value.BackColor = pair.Key == pointNumber ? _activePointColor : _idlePointColor;
                    pair.Value.ForeColor = Color.White;
                }

                foreach (DataGridViewRow row in dgvCalibration.Rows)
                {
                    int rowPoint;
                    if (int.TryParse(Convert.ToString(row.Cells[0].Value, CultureInfo.InvariantCulture), out rowPoint))
                    {
                        row.Selected = rowPoint == pointNumber;
                    }
                }
            });
        }

        public void UpdateCalibrationSample(
            int pointNumber,
            double? machineX,
            double? machineY,
            double? machineR,
            double? imageRow,
            double? imageColumn,
            double? imageAngle,
            string status)
        {
            RunOnUiThread(delegate
            {
                DataGridViewRow row = FindCalibrationRow(pointNumber);
                if (row == null) return;
                row.Cells[2].Value = FormatNumber(machineX);
                row.Cells[3].Value = FormatNumber(machineY);
                row.Cells[4].Value = FormatNumber(machineR);
                row.Cells[5].Value = FormatNumber(imageRow);
                row.Cells[6].Value = FormatNumber(imageColumn);
                row.Cells[7].Value = FormatNumber(imageAngle);
                row.Cells[8].Value = status ?? string.Empty;
                row.DefaultCellStyle.ForeColor = string.Equals(status, "NG", StringComparison.OrdinalIgnoreCase)
                    ? Color.FromArgb(190, 55, 55)
                    : Color.FromArgb(45, 49, 54);
            });
        }

        public void ClearCalibrationSamples()
        {
            RunOnUiThread(delegate
            {
                foreach (DataGridViewRow row in dgvCalibration.Rows)
                {
                    for (int column = 2; column < row.Cells.Count; column++) row.Cells[column].Value = string.Empty;
                    row.DefaultCellStyle.ForeColor = Color.FromArgb(45, 49, 54);
                    row.Selected = false;
                }
                SelectCalibrationPoint(0);
            });
        }

        public void UpdateTeaching(
            string side,
            double imageRow,
            double imageColumn,
            double imageAngle,
            double machineX,
            double machineY,
            double machineR,
            bool valid)
        {
            RunOnUiThread(delegate
            {
                bool left = string.Equals(side, "left", StringComparison.OrdinalIgnoreCase) || side == "左";
                TextBox[] values = left ? leftTeachingValues : rightTeachingValues;
                double[] data = { imageRow, imageColumn, imageAngle, machineX, machineY, machineR };
                for (int index = 0; index < values.Length && index < data.Length; index++)
                {
                    values[index].Text = data[index].ToString("0.###", CultureInfo.InvariantCulture);
                }

                Label statusLabel = left ? lblLeftTeachingStatus : lblRightTeachingStatus;
                statusLabel.Text = valid ? "已示教" : "未示教";
                statusLabel.ForeColor = valid ? Color.FromArgb(17, 122, 101) : Color.FromArgb(138, 68, 68);
            });
        }

        public void SetDetectionSummary(int expected, int actual, bool success, string message)
        {
            RunOnUiThread(delegate
            {
                lblDetectionSummary.Text = string.Format(
                    CultureInfo.InvariantCulture,
                    "目标 {0}  /  实际 {1}  /  {2}{3}",
                    expected,
                    actual,
                    success ? "OK" : "NG",
                    string.IsNullOrWhiteSpace(message) ? string.Empty : "  " + message);
                lblDetectionSummary.ForeColor = success
                    ? Color.FromArgb(17, 122, 101)
                    : Color.FromArgb(178, 55, 55);
            });
        }

        public void SetInspectionHud(
            double totalMilliseconds,
            double matchMilliseconds,
            double blobMilliseconds,
            double imageConversionMilliseconds,
            double thresholdMilliseconds,
            double morphologyMilliseconds,
            double holeFillMilliseconds,
            double componentMilliseconds,
            int expected,
            int actual,
            bool pass,
            bool hasManualMarks)
        {
            RunOnUiThread(delegate
            {
                lblInspectionHud.Text = string.Format(
                    CultureInfo.InvariantCulture,
                    "CT {0:0} ms   定位 {1:0}   检测 {2:0}\r\n结果 {3}   板 {4}/{5}   标记 {6}\r\n图转 {7:0}  阈值 {8:0}  形态 {9:0}  填孔 {10:0}  连通 {11:0}",
                    totalMilliseconds,
                    matchMilliseconds,
                    blobMilliseconds,
                    pass ? "OK" : "NG",
                    actual,
                    expected,
                    hasManualMarks ? "有" : "无",
                    imageConversionMilliseconds,
                    thresholdMilliseconds,
                    morphologyMilliseconds,
                    holeFillMilliseconds,
                    componentMilliseconds);
                lblInspectionHud.ForeColor = pass
                    ? Color.FromArgb(116, 235, 176)
                    : Color.FromArgb(255, 154, 154);
                lblInspectionHud.Visible = true;
                lblInspectionHud.BringToFront();
            });
        }

        public void ReplaceDetectionResults(IEnumerable<object[]> rows)
        {
            RunOnUiThread(delegate
            {
                dgvDetectionResults.Rows.Clear();
                if (rows == null) return;
                foreach (object[] row in rows) dgvDetectionResults.Rows.Add(row);
            });
        }

        public void AppendLog(string message, bool isError)
        {
            if (string.IsNullOrWhiteSpace(message)) return;
            RunOnUiThread(delegate
            {
                if (rtbLog.TextLength > 200000)
                {
                    rtbLog.Select(0, Math.Min(50000, rtbLog.TextLength));
                    rtbLog.SelectedText = string.Empty;
                }

                rtbLog.SelectionStart = rtbLog.TextLength;
                rtbLog.SelectionLength = 0;
                rtbLog.SelectionColor = isError ? Color.FromArgb(210, 79, 79) : Color.FromArgb(205, 211, 218);
                rtbLog.AppendText(DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture));
                rtbLog.AppendText(isError ? "  [ERR] " : "  [INF] ");
                rtbLog.AppendText(message);
                rtbLog.AppendText(Environment.NewLine);
                rtbLog.SelectionStart = rtbLog.TextLength;
                rtbLog.ScrollToCaret();
            });
        }

        public void AppendLog(string message)
        {
            AppendLog(message, false);
        }

        public Control FindActionControl(string actionKey)
        {
            return FindControls(this)
                .FirstOrDefault(control => string.Equals(Convert.ToString(control.Tag), actionKey, StringComparison.OrdinalIgnoreCase));
        }

        public void SetActionEnabled(string actionKey, bool enabled)
        {
            RunOnUiThread(delegate
            {
                Control control = FindActionControl(actionKey);
                if (control != null) control.Enabled = enabled;
            });
        }

        private void InitializeCalibrationRows()
        {
            dgvCalibration.Rows.Clear();
            for (int point = 1; point <= 14; point++)
            {
                string kind = point <= 9 ? "XY" : "R";
                dgvCalibration.Rows.Add(point, kind, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty);
            }

            _pointButtonMap.Clear();
            foreach (Button button in calibrationPointButtons)
            {
                int point;
                if (int.TryParse(Convert.ToString(button.Tag, CultureInfo.InvariantCulture), out point))
                {
                    _pointButtonMap[point] = button;
                }
            }
        }

        private void WireUiEvents()
        {
            btnSnapshot.Click += delegate
            {
                EventHandler handler = SnapshotRequested;
                if (handler != null) handler(this, EventArgs.Empty);
                RaiseUiAction("camera.snapshot", btnSnapshot);
            };
            btnFit.Click += delegate { imageViewport.FitImage(); };
            btnActualPixels.Click += delegate { imageViewport.ShowActualPixels(); };
            btnClearLog.Click += delegate { rtbLog.Clear(); };
            imageViewport.ImagePositionChanged += ImageViewport_ImagePositionChanged;
            imageViewport.ImageAvailabilityChanged += ImageViewport_ImageAvailabilityChanged;
            chkCameraEnabled.CheckedChanged += FeatureToggle_CheckedChanged;
            chkCalibrationEnabled.CheckedChanged += FeatureToggle_CheckedChanged;

            foreach (Button button in calibrationPointButtons)
            {
                button.Click += CalibrationPointButton_Click;
            }

            foreach (Control control in FindControls(this))
            {
                Button button = control as Button;
                string actionKey = button == null ? null : button.Tag as string;
                if (button != null && !string.IsNullOrWhiteSpace(actionKey) && !IsPointButton(button))
                {
                    if (button == btnSnapshot || button == btnFit || button == btnActualPixels || button == btnClearLog) continue;
                    button.Click += CommandButton_Click;
                }

                CheckBox checkBox = control as CheckBox;
                actionKey = checkBox == null ? null : checkBox.Tag as string;
                if (checkBox != null && !string.IsNullOrWhiteSpace(actionKey) &&
                    checkBox != chkCameraEnabled && checkBox != chkCalibrationEnabled)
                {
                    checkBox.CheckedChanged += CommandValue_Changed;
                }

                NumericUpDown numeric = control as NumericUpDown;
                if (numeric != null) numeric.MouseWheel += NumericInput_MouseWheel;
                actionKey = numeric == null ? null : numeric.Tag as string;
                if (numeric != null && !string.IsNullOrWhiteSpace(actionKey))
                    numeric.ValueChanged += CommandValue_Changed;
            }
        }

        private static void NumericInput_MouseWheel(object sender, MouseEventArgs e)
        {
            HandledMouseEventArgs handled = e as HandledMouseEventArgs;
            if (handled != null) handled.Handled = true;
            NumericUpDown input = sender as NumericUpDown;
            if (input == null || !input.Focused || e.Delta == 0) return;

            decimal value;
            if (e.Delta > 0)
            {
                decimal remaining = input.Maximum - input.Value;
                value = remaining <= input.Increment
                    ? input.Maximum
                    : input.Value + input.Increment;
            }
            else
            {
                decimal remaining = input.Value - input.Minimum;
                value = remaining <= input.Increment
                    ? input.Minimum
                    : input.Value - input.Increment;
            }
            if (value != input.Value) input.Value = value;
        }

        private void CalibrationPointButton_Click(object sender, EventArgs e)
        {
            Button button = sender as Button;
            int pointNumber;
            if (button == null || !int.TryParse(Convert.ToString(button.Tag, CultureInfo.InvariantCulture), out pointNumber)) return;

            SelectCalibrationPoint(pointNumber);
            EventHandler<CalibrationPointRequestedEventArgs> handler = CalibrationPointRequested;
            if (handler != null) handler(this, new CalibrationPointRequestedEventArgs(pointNumber));
        }

        private void CommandButton_Click(object sender, EventArgs e)
        {
            Control source = sender as Control;
            RaiseUiAction(source == null ? string.Empty : Convert.ToString(source.Tag), source);
        }

        private void CommandValue_Changed(object sender, EventArgs e)
        {
            Control source = sender as Control;
            RaiseUiAction(source == null ? string.Empty : Convert.ToString(source.Tag), source);
        }

        private void FeatureToggle_CheckedChanged(object sender, EventArgs e)
        {
            if (_suppressFeatureEvents) return;

            ApplyFeatureAvailability(chkCameraEnabled.Checked, chkCalibrationEnabled.Checked, _workflowBusy);
            EventHandler handler = FeatureAvailabilityChanged;
            if (handler != null) handler(this, EventArgs.Empty);
        }

        private void RaiseUiAction(string actionKey, Control source)
        {
            EventHandler<UiActionRequestedEventArgs> handler = UiActionRequested;
            if (handler != null) handler(this, new UiActionRequestedEventArgs(actionKey, source));
        }

        private void ImageViewport_ImagePositionChanged(object sender, ImagePositionEventArgs e)
        {
            lblCursorPosition.Text = e.IsInsideImage
                ? string.Format(CultureInfo.InvariantCulture, "X {0:0.0}   Y {1:0.0}", e.ImagePoint.X, e.ImagePoint.Y)
                : "X --   Y --";
        }

        private void ImageViewport_ImageAvailabilityChanged(object sender, EventArgs e)
        {
            RunOnUiThread(delegate
            {
                btnDetectCurrentImage.Enabled = !_workflowBusy && imageViewport.HasImage;
                SetActionControlEnabled("detection.debug", !_workflowBusy && imageViewport.HasImage);
            });
        }

        private void SetStatusLabel(Label label, bool connected, string prefix, string detail)
        {
            RunOnUiThread(delegate
            {
                label.Text = prefix + "  " + (connected ? "已连接" : "未连接") +
                             (string.IsNullOrWhiteSpace(detail) ? string.Empty : "  " + detail);
                label.BackColor = connected ? Color.FromArgb(43, 92, 68) : Color.FromArgb(77, 82, 89);
            });
        }

        private void ApplyFeatureAvailability(bool cameraEnabled, bool calibrationEnabled, bool workflowBusy)
        {
            _workflowBusy = workflowBusy;

            chkCameraEnabled.Enabled = !workflowBusy;
            chkCalibrationEnabled.Enabled = !workflowBusy;
            cmbCameraProvider.Enabled = cameraEnabled && !workflowBusy;
            cmbCameraDevice.Enabled = cameraEnabled && !workflowBusy;
            SetControlEnabled("nudCameraExposure", cameraEnabled && !workflowBusy);
            SetControlEnabled("nudCameraTimeout", cameraEnabled && !workflowBusy);

            string[] cameraActions =
            {
                "camera.snapshot",
                "camera.enumerate",
                "camera.connect",
                "camera.disconnect",
                "camera.apply",
                "camera.test"
            };
            foreach (string action in cameraActions) SetActionControlEnabled(action, cameraEnabled && !workflowBusy);

            btnImportImage.Enabled = !workflowBusy;
            btnDetectCurrentImage.Enabled = !workflowBusy && imageViewport.HasImage;
            SetActionControlEnabled("detection.debug", !workflowBusy && imageViewport.HasImage);

            if (tabCalibration != null) tabCalibration.Enabled = calibrationEnabled;
            bool acquisitionCalibrationAvailable = cameraEnabled && calibrationEnabled && !workflowBusy;
            foreach (Button button in calibrationPointButtons)
            {
                button.Enabled = acquisitionCalibrationAvailable;
            }
            SetActionControlEnabled("calibration.manual", acquisitionCalibrationAvailable);
            SetActionControlEnabled("calibration.auto", acquisitionCalibrationAvailable);
            SetActionControlEnabled("calibration.calculate", calibrationEnabled && !workflowBusy);
            SetActionControlEnabled("calibration.cancel", calibrationEnabled && workflowBusy);
            SetActionControlEnabled("teaching.left.capture", acquisitionCalibrationAvailable);
            SetActionControlEnabled("teaching.right.capture", acquisitionCalibrationAvailable);
            SetActionControlEnabled(
                "detection.test",
                !workflowBusy && (acquisitionCalibrationAvailable || imageViewport.HasImage));

            // Calibration-template operations are guarded by the same runtime
            // calibration switch. Keep the visible command state synchronized with
            // that guard so a click cannot turn into a seemingly silent failure.
            // Template editing/testing may use an imported image, so cameraEnabled is
            // intentionally not part of these conditions.
            bool calibrationTemplateAvailable = calibrationEnabled && !workflowBusy;
            bool positioningTemplateAvailable = !workflowBusy;
            SetActionControlEnabled("template.calibration.create", calibrationTemplateAvailable);
            SetActionControlEnabled("template.calibration.load", calibrationTemplateAvailable);
            SetActionControlEnabled("template.calibration.test", calibrationTemplateAvailable);
            SetActionControlEnabled("template.positioning.create", positioningTemplateAvailable);
            SetActionControlEnabled("template.positioning.load", positioningTemplateAvailable);
            SetActionControlEnabled("template.positioning.test", positioningTemplateAvailable);

            SetActionToolTip(
                "template.calibration.create",
                calibrationEnabled
                    ? "使用当前图像创建标定模板"
                    : "请先在设置中打开“启用标定”，再创建标定模板");
            SetActionToolTip(
                "template.calibration.load",
                calibrationEnabled
                    ? "加载标定模板文件"
                    : "请先在设置中打开“启用标定”，再加载标定模板");
            SetActionToolTip(
                "template.calibration.test",
                calibrationEnabled
                    ? "用当前图像测试标定模板"
                    : "请先在设置中打开“启用标定”，再测试标定模板");

            if (!cameraEnabled && _cameraUiState != CameraUiState.Disabled)
            {
                SetCameraStatus(CameraUiState.Disabled, string.Empty);
            }
            else if (cameraEnabled && _cameraUiState == CameraUiState.Disabled)
            {
                SetCameraStatus(CameraUiState.Disconnected, string.Empty);
            }
        }

        private void SetActionControlEnabled(string actionKey, bool enabled)
        {
            Control control = FindActionControl(actionKey);
            if (control != null) control.Enabled = enabled;
        }

        private void SetActionToolTip(string actionKey, string text)
        {
            Control control = FindActionControl(actionKey);
            if (control != null) toolTip.SetToolTip(control, text ?? string.Empty);
        }

        private void SetControlEnabled(string controlName, bool enabled)
        {
            Control control = FindControls(this)
                .FirstOrDefault(item => string.Equals(item.Name, controlName, StringComparison.Ordinal));
            if (control != null) control.Enabled = enabled;
        }

        private DataGridViewRow FindCalibrationRow(int pointNumber)
        {
            foreach (DataGridViewRow row in dgvCalibration.Rows)
            {
                int current;
                if (int.TryParse(Convert.ToString(row.Cells[0].Value, CultureInfo.InvariantCulture), out current) && current == pointNumber)
                {
                    return row;
                }
            }
            return null;
        }

        private static string FormatNumber(double? value)
        {
            return value.HasValue ? value.Value.ToString("0.###", CultureInfo.InvariantCulture) : string.Empty;
        }

        private bool IsPointButton(Button button)
        {
            return calibrationPointButtons.Contains(button);
        }

        private static IEnumerable<Control> FindControls(Control root)
        {
            foreach (Control child in root.Controls)
            {
                yield return child;
                foreach (Control descendant in FindControls(child)) yield return descendant;
            }
        }

        private void RunOnUiThread(MethodInvoker action)
        {
            if (action == null || IsDisposed || Disposing) return;
            if (IsHandleCreated && InvokeRequired)
            {
                try
                {
                    BeginInvoke(action);
                }
                catch (ObjectDisposedException)
                {
                }
                catch (InvalidOperationException)
                {
                }
            }
            else
            {
                action();
            }
        }
    }
}
