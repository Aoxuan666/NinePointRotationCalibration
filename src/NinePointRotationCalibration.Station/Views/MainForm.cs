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
    /// <summary>界面显示的相机连接状态。</summary>
    public enum CameraUiState
    {
        Disabled,
        Disconnected,
        Connecting,
        Connected,
        Error
    }

    /// <summary>界面显示的 TCP 服务与上位机连接状态。</summary>
    public enum TcpUiState
    {
        NotListening,
        Listening,
        Connected
    }

    /// <summary>用户点击 1-14 号标定点位按钮时的事件参数。</summary>
    public sealed class CalibrationPointRequestedEventArgs : EventArgs
    {
        public CalibrationPointRequestedEventArgs(int pointNumber)
        {
            PointNumber = pointNumber;
        }

        public int PointNumber { get; private set; }
    }

    /// <summary>基于控件 Tag 路由界面操作请求的通用事件参数。</summary>
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

    /// <summary>视觉工作站主窗体；仅负责界面布局与交互事件转发，运行时绑定由外部协调器完成。</summary>
    [DesignerCategory("Form")]
    public partial class MainForm : Form
    {
        private readonly Dictionary<int, Button> _pointButtonMap = new Dictionary<int, Button>();
        private readonly Color _idlePointColor = Color.FromArgb(51, 65, 85);
        private readonly Color _activePointColor = Color.FromArgb(15, 118, 110);
        private bool _workflowBusy;
        private bool _suppressFeatureEvents;
        private bool _leftTeachingValid;
        private bool _rightTeachingValid;
        private double _leftTeachX;
        private double _leftTeachY;
        private double _leftTeachR;
        private double _rightTeachX;
        private double _rightTeachY;
        private double _rightTeachR;
        private CameraUiState _cameraUiState = CameraUiState.Disconnected;
        private TcpUiState _tcpUiState = TcpUiState.NotListening;
        private bool _dualViewActive;
        private bool _syncingViewports;
        private bool _logCollapsed;
        private bool _userExpandedLogInDualView;

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
        /// 供组合根调用的兼容重载；窗体故意不持有传入服务引用，运行时绑定由外部绑定器处理以保持设计器可用。
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
        public ImageViewport EffectViewport
        {
            get { return effectViewport; }
        }

        [Browsable(false)]
        public bool IsDetectionDualViewActive
        {
            get { return _dualViewActive; }
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
        public CheckBox ArchiveImagesInput
        {
            get { return chkArchiveImages; }
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
            RefreshFeatureAvailability(
                cameraEnabled,
                calibrationEnabled,
                chkArchiveImages != null && chkArchiveImages.Checked,
                workflowBusy);
        }

        public void RefreshFeatureAvailability(
            bool cameraEnabled,
            bool calibrationEnabled,
            bool archiveImages,
            bool workflowBusy)
        {
            RunOnUiThread(delegate
            {
                _suppressFeatureEvents = true;
                try
                {
                    chkCameraEnabled.Checked = cameraEnabled;
                    chkCalibrationEnabled.Checked = calibrationEnabled;
                    if (chkArchiveImages != null) chkArchiveImages.Checked = archiveImages;
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

        public void SetEffectViewportInformation(string text)
        {
            RunOnUiThread(delegate
            {
                if (lblEffectViewportInfo != null)
                {
                    lblEffectViewportInfo.Text = string.IsNullOrWhiteSpace(text) ? "等待图像" : text;
                }
            });
        }

        /// <summary>切换检测页上下双图像窗口（上：原图窗口；下：检测效果图窗口）的展开与折叠状态。</summary>
        public void SetDetectionDualViewActive(bool active)
        {
            RunOnUiThread(delegate
            {
                _dualViewActive = active;
                if (pnlSourceViewportHeader != null)
                {
                    pnlSourceViewportHeader.Visible = active;
                }

                // 进入检测双视图时自动折叠底部日志以释放纵深空间；切离时恢复显示
                if (active)
                {
                    if (!_userExpandedLogInDualView) SetLogCollapsed(true);
                }
                else
                {
                    _userExpandedLogInDualView = false;
                    SetLogCollapsed(false);
                }

                if (imageSplit != null)
                {
                    imageSplit.Panel2Collapsed = !active;
                    if (active)
                    {
                        BalanceImageSplit();
                    }
                }

                Control hudParent = active ? (Control)effectViewport : imageViewport;
                if (lblInspectionHud != null && hudParent != null && !ReferenceEquals(lblInspectionHud.Parent, hudParent))
                {
                    lblInspectionHud.Parent = hudParent;
                    lblInspectionHud.BringToFront();
                }

                if (active && imageViewport.HasImage && effectViewport.HasImage)
                {
                    SyncLinkedViewport(imageViewport, effectViewport);
                }
            });
        }

        private void BalanceImageSplit()
        {
            if (imageSplit == null || imageSplit.IsDisposed || imageSplit.Panel2Collapsed)
            {
                return;
            }

            int total = imageSplit.Orientation == Orientation.Horizontal
                ? imageSplit.Height
                : imageSplit.Width;
            int available = total - imageSplit.SplitterWidth;
            int min1 = imageSplit.Panel1MinSize;
            int max1 = available - imageSplit.Panel2MinSize;
            if (max1 >= min1)
            {
                imageSplit.SplitterDistance = Math.Max(min1, Math.Min(max1, available / 2));
            }
        }

        private void ToggleDualViewportLayout()
        {
            if (imageSplit == null || imageSplit.IsDisposed) return;

            bool toVertical = imageSplit.Orientation == Orientation.Horizontal;
            imageSplit.Orientation = toVertical ? Orientation.Vertical : Orientation.Horizontal;
            if (btnToggleDualLayout != null)
            {
                btnToggleDualLayout.Text = toVertical ? "⇅ 切换上下" : "⇄ 切换左右";
            }
            BalanceImageSplit();
            if (_dualViewActive && imageViewport.HasImage && effectViewport.HasImage)
            {
                SyncLinkedViewport(imageViewport, effectViewport);
            }
        }

        private void SetLogCollapsed(bool collapsed)
        {
            _logCollapsed = collapsed;
            if (rtbLog != null) rtbLog.Visible = !collapsed;
            if (btnClearLog != null) btnClearLog.Visible = !collapsed;
            if (btnToggleLog != null) btnToggleLog.Text = collapsed ? "▲ 展开" : "▼ 收起";
            if (logLayout != null && logLayout.RowStyles.Count > 1)
            {
                logLayout.RowStyles[1] = collapsed
                    ? new RowStyle(SizeType.Absolute, 0.0F)
                    : new RowStyle(SizeType.Percent, 100.0F);
            }
            if (rootLayout != null && rootLayout.RowStyles.Count > 2)
            {
                rootLayout.RowStyles[2].Height = collapsed ? 32.0F : 128.0F;
            }
            if (_dualViewActive)
            {
                BalanceImageSplit();
            }
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            if (StartPosition == FormStartPosition.Manual) return;

            Rectangle workingArea = Screen.FromControl(this).WorkingArea;
            int targetWidth = Math.Min(Width, workingArea.Width);
            int targetHeight = Math.Min(Height, workingArea.Height);
            if (targetWidth != Width || targetHeight != Height)
            {
                Bounds = new Rectangle(
                    workingArea.Left + Math.Max(0, (workingArea.Width - targetWidth) / 2),
                    workingArea.Top + Math.Max(0, (workingArea.Height - targetHeight) / 2),
                    targetWidth,
                    targetHeight);
            }
        }

        private void SyncLinkedViewport(ImageViewport source, ImageViewport target)
        {
            if (_syncingViewports || !_dualViewActive || source == null || target == null)
            {
                return;
            }

            if (!source.HasImage || !target.HasImage)
            {
                return;
            }

            _syncingViewports = true;
            try
            {
                target.SyncViewFrom(source);
            }
            finally
            {
                _syncingViewports = false;
            }
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
                row.Cells["MachineX"].Value = FormatNumber(machineX);
                row.Cells["MachineY"].Value = FormatNumber(machineY);
                row.Cells["MachineR"].Value = FormatNumber(machineR);
                row.Cells["ImageRow"].Value = FormatNumber(imageRow);
                row.Cells["ImageColumn"].Value = FormatNumber(imageColumn);
                row.Cells["ImageAngle"].Value = FormatNumber(imageAngle);
                row.Cells["Residual"].Value = string.Empty;
                row.Cells["Status"].Value = status ?? string.Empty;
                row.DefaultCellStyle.BackColor = Color.Empty;
                row.DefaultCellStyle.ForeColor = string.Equals(status, "NG", StringComparison.OrdinalIgnoreCase)
                    ? Color.FromArgb(190, 55, 55)
                    : Color.FromArgb(45, 49, 54);
            });
        }

        /// <summary>刷新标定页 RMS 误差评分卡片，展示等级、百分制评分、各维度 RMS 及合理区间。</summary>
        public void SetCalibrationRmsSummary(CalibrationResult result)
        {
            RunOnUiThread(delegate
            {
                if (lblCalibrationRmsSummary == null) return;

                if (result == null || (result.CandidateCount == 0 && result.InlierCount == 0 && !result.Success))
                {
                    lblCalibrationRmsSummary.BackColor = Color.FromArgb(226, 232, 240);
                    lblCalibrationRmsSummary.ForeColor = Color.FromArgb(30, 41, 59);
                    lblCalibrationRmsSummary.Text =
                        "RMS 评估：待计算（采点后点击「计算并保存」）\r\n" +
                        "合理标准：≤0.20 px 优(90-100) | ≤0.50 良(75-89) | ≤1.00 合格(60-74)\r\n" +
                        "旋转参考：旋中 RMS ≤ 1.00 px（≤0.30 优） | 角度 RMS ≤ 0.50°（≤0.05° 优）";
                    return;
                }

                CalibrationRmsAssessment assessment = result.EvaluateRms();
                switch (assessment.Grade)
                {
                    case CalibrationRmsGrade.Excellent:
                        lblCalibrationRmsSummary.BackColor = Color.FromArgb(204, 251, 241);
                        lblCalibrationRmsSummary.ForeColor = Color.FromArgb(17, 94, 89);
                        break;
                    case CalibrationRmsGrade.Good:
                        lblCalibrationRmsSummary.BackColor = Color.FromArgb(219, 234, 254);
                        lblCalibrationRmsSummary.ForeColor = Color.FromArgb(30, 64, 175);
                        break;
                    case CalibrationRmsGrade.Acceptable:
                        lblCalibrationRmsSummary.BackColor = Color.FromArgb(254, 243, 199);
                        lblCalibrationRmsSummary.ForeColor = Color.FromArgb(146, 64, 14);
                        break;
                    default:
                        lblCalibrationRmsSummary.BackColor = Color.FromArgb(254, 226, 226);
                        lblCalibrationRmsSummary.ForeColor = Color.FromArgb(185, 28, 28);
                        break;
                }

                string xyText = IsFinite(assessment.TranslationRmsPixels)
                    ? string.Format(
                        CultureInfo.InvariantCulture,
                        "XY {0:0.###} px ({1:0.####} mm)",
                        assessment.TranslationRmsPixels,
                        IsFinite(assessment.TranslationRmsStage) ? assessment.TranslationRmsStage : 0.0)
                    : "XY --";
                string rotText = IsFinite(assessment.RotationCenterRmsPixels)
                    ? string.Format(
                        CultureInfo.InvariantCulture,
                        "旋中 {0:0.###} px | 角度 {1:0.###}°",
                        assessment.RotationCenterRmsPixels,
                        assessment.RotationAngleRmsDegrees)
                    : "旋中 -- | 角度 --";

                lblCalibrationRmsSummary.Text = string.Format(
                    CultureInfo.InvariantCulture,
                    "RMS 评级：{0}（{1} 分·{2}）   {3}   {4}\r\n{5}\r\n建议：{6}",
                    assessment.GradeLabel,
                    assessment.Score,
                    assessment.IsReasonable ? "合理范围" : "超差需重标",
                    xyText,
                    rotText,
                    assessment.CriteriaText,
                    assessment.Recommendation);
            });
        }

        /// <summary>将求解输出的逐点像素残差与内点/外点标记写入 14 点标定明细表。</summary>
        public void ApplyCalibrationDiagnostics(CalibrationResult result)
        {
            RunOnUiThread(delegate
            {
                if (result == null || result.Diagnostics == null) return;
                Dictionary<int, CalibrationSampleDiagnostic> byPoint = new Dictionary<int, CalibrationSampleDiagnostic>();
                foreach (CalibrationSampleDiagnostic diagnostic in result.Diagnostics)
                {
                    int pointNumber;
                    if (diagnostic == null || !TryParsePointNumberFromTag(diagnostic.Tag, out pointNumber)) continue;
                    byPoint[pointNumber] = diagnostic;
                }

                foreach (DataGridViewRow row in dgvCalibration.Rows)
                {
                    int pointNumber;
                    if (!int.TryParse(Convert.ToString(row.Cells[0].Value, CultureInfo.InvariantCulture), out pointNumber))
                        continue;

                    CalibrationSampleDiagnostic diagnostic;
                    if (!byPoint.TryGetValue(pointNumber, out diagnostic) || !diagnostic.IsEligible)
                        continue;

                    bool isRotation = pointNumber >= 10;
                    double residual = diagnostic.ResidualPixels;
                    row.Cells["Residual"].Value = IsFinite(residual)
                        ? residual.ToString("0.###", CultureInfo.InvariantCulture)
                        : "--";
                    if (!diagnostic.IsInlier)
                    {
                        row.Cells["Status"].Value = "外点";
                        row.DefaultCellStyle.ForeColor = Color.FromArgb(185, 28, 28);
                        row.DefaultCellStyle.BackColor = Color.FromArgb(254, 242, 242);
                    }
                    else
                    {
                        bool high = isRotation
                            ? residual > CalibrationRmsEvaluator.DefaultAcceptableCenterRms
                            : residual > CalibrationRmsEvaluator.DefaultAcceptablePixelRms;
                        row.Cells["Status"].Value = "OK";
                        row.DefaultCellStyle.ForeColor = high
                            ? Color.FromArgb(180, 83, 9)
                            : Color.FromArgb(21, 128, 61);
                        row.DefaultCellStyle.BackColor = Color.Empty;
                    }
                }
            });
        }

        private static bool TryParsePointNumberFromTag(string tag, out int pointNumber)
        {
            pointNumber = 0;
            if (string.IsNullOrWhiteSpace(tag) || tag.Length < 2) return false;
            string trimmed = tag.Trim();
            int index;
            if (!int.TryParse(trimmed.Substring(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out index))
                return false;
            if ((trimmed[0] == 'P' || trimmed[0] == 'p') && index >= 1 && index <= 9)
            {
                pointNumber = index;
                return true;
            }
            if ((trimmed[0] == 'R' || trimmed[0] == 'r') && index >= 1 && index <= 5)
            {
                pointNumber = 9 + index;
                return true;
            }
            return false;
        }

        private static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }

        public void ClearCalibrationSamples()
        {
            RunOnUiThread(delegate
            {
                foreach (DataGridViewRow row in dgvCalibration.Rows)
                {
                    for (int column = 2; column < row.Cells.Count; column++) row.Cells[column].Value = string.Empty;
                    row.DefaultCellStyle.BackColor = Color.Empty;
                    row.DefaultCellStyle.ForeColor = Color.FromArgb(45, 49, 54);
                    row.Selected = false;
                }
                SelectCalibrationPoint(0);
                SetCalibrationRmsSummary(null);
            });
        }

        /// <summary>更新左右吸嘴的图像/机械示教显示，并同步刷新双吸嘴同轴间距与盲摆策略提示。</summary>
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
                statusLabel.Text = valid ? "● 已示教" : "○ 未示教";
                statusLabel.ForeColor = valid ? Color.FromArgb(15, 118, 110) : Color.FromArgb(185, 28, 28);

                if (left)
                {
                    _leftTeachingValid = valid;
                    _leftTeachX = machineX;
                    _leftTeachY = machineY;
                    _leftTeachR = machineR;
                }
                else
                {
                    _rightTeachingValid = valid;
                    _rightTeachX = machineX;
                    _rightTeachY = machineY;
                    _rightTeachR = machineR;
                }

                UpdateDualNozzleSummary();
            });
        }

        private void UpdateDualNozzleSummary()
        {
            if (lblDualNozzleSummary == null) return;
            // 两侧均已示教时计算双吸嘴机械坐标间距，并标明吸取对齐转角 + 固定角盲摆策略
            if (_leftTeachingValid && _rightTeachingValid)
            {
                double dx = _rightTeachX - _leftTeachX;
                double dy = _rightTeachY - _leftTeachY;
                double distance = Math.Sqrt((dx * dx) + (dy * dy));
                lblDualNozzleSummary.BackColor = Color.FromArgb(204, 251, 241);
                lblDualNozzleSummary.ForeColor = Color.FromArgb(17, 94, 89);
                lblDualNozzleSummary.Text = string.Format(
                    CultureInfo.InvariantCulture,
                    "双吸嘴已就绪：同轴间距 ΔX={0:0.###}  ΔY={1:0.###}  |ΔD|={2:0.###} mm\r\n盲摆策略：吸取旋转 R 对齐板角 → 盲摆转回固定角 (左:{3:0.##}° / 右:{4:0.##}°)",
                    dx,
                    dy,
                    distance,
                    _leftTeachR,
                    _rightTeachR);
            }
            else
            {
                lblDualNozzleSummary.BackColor = Color.FromArgb(226, 232, 240);
                lblDualNozzleSummary.ForeColor = Color.FromArgb(51, 65, 85);
                lblDualNozzleSummary.Text = string.Format(
                    CultureInfo.InvariantCulture,
                    "双吸嘴状态：左吸嘴 [{0}] / 右吸嘴 [{1}]（需两侧均完成示教）\r\n工艺模式：吸取时旋转 R 轴对齐来料板角 → 盲摆转回固定示教角 (0°)",
                    _leftTeachingValid ? "已示教" : "未示教",
                    _rightTeachingValid ? "已示教" : "未示教");
            }
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
                foreach (object[] row in rows)
                {
                    int rowIndex = dgvDetectionResults.Rows.Add(row);
                    if (rowIndex >= 0 && row != null && row.Length > 0)
                    {
                        string status = Convert.ToString(row[row.Length - 1], CultureInfo.InvariantCulture);
                        bool isNg = string.Equals(status, "NG", StringComparison.OrdinalIgnoreCase);
                        DataGridViewRow gridRow = dgvDetectionResults.Rows[rowIndex];
                        gridRow.DefaultCellStyle.ForeColor = isNg
                            ? Color.FromArgb(185, 28, 28)
                            : Color.FromArgb(21, 128, 61);
                        if (isNg)
                        {
                            gridRow.DefaultCellStyle.BackColor = Color.FromArgb(254, 242, 242);
                        }
                    }
                }
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
                dgvCalibration.Rows.Add(point, kind, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty);
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
            btnFit.Click += delegate
            {
                imageViewport.FitImage();
                if (effectViewport.HasImage) effectViewport.FitImage();
            };
            btnActualPixels.Click += delegate
            {
                imageViewport.ShowActualPixels();
                if (effectViewport.HasImage) effectViewport.ShowActualPixels();
            };
            btnClearLog.Click += delegate { rtbLog.Clear(); };
            if (btnToggleLog != null)
            {
                btnToggleLog.Click += delegate
                {
                    bool nextCollapsed = !_logCollapsed;
                    if (_dualViewActive) _userExpandedLogInDualView = !nextCollapsed;
                    SetLogCollapsed(nextCollapsed);
                };
            }
            if (btnToggleDualLayout != null)
            {
                btnToggleDualLayout.Click += delegate { ToggleDualViewportLayout(); };
            }
            if (imageSplit != null)
            {
                imageSplit.SizeChanged += delegate { BalanceImageSplit(); };
            }
            tabWork.DrawItem += TabWork_DrawItem;
            imageViewport.ImagePositionChanged += ImageViewport_ImagePositionChanged;
            imageViewport.ImageAvailabilityChanged += ImageViewport_ImageAvailabilityChanged;
            imageViewport.ViewChanged += delegate { SyncLinkedViewport(imageViewport, effectViewport); };
            effectViewport.ImagePositionChanged += ImageViewport_ImagePositionChanged;
            effectViewport.ViewChanged += delegate { SyncLinkedViewport(effectViewport, imageViewport); };
            dgvDetectionResults.SelectionChanged += delegate { RaiseUiAction("detection.rowSelected", dgvDetectionResults); };
            chkCameraEnabled.CheckedChanged += FeatureToggle_CheckedChanged;
            chkCalibrationEnabled.CheckedChanged += FeatureToggle_CheckedChanged;

            foreach (Button button in calibrationPointButtons)
            {
                button.Click += CalibrationPointButton_Click;
            }

            // 统一按控件 Tag 路由常规操作按钮、复选框和数值框；标定点按钮单独通过 CalibrationPointRequested 派发
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

        private void TabWork_DrawItem(object sender, DrawItemEventArgs e)
        {
            // 自绘右侧工作区标签页：突出选中态并在底部绘制青绿色强调指示条
            if (e.Index < 0 || e.Index >= tabWork.TabPages.Count) return;
            TabPage page = tabWork.TabPages[e.Index];
            Rectangle bounds = e.Bounds;
            bool selected = (e.State & DrawItemState.Selected) == DrawItemState.Selected;
            bool enabled = page.Enabled;

            Color backColor = selected
                ? Color.FromArgb(255, 255, 255)
                : Color.FromArgb(226, 232, 240);
            Color foreColor = !enabled
                ? Color.FromArgb(148, 163, 184)
                : selected ? Color.FromArgb(15, 118, 110) : Color.FromArgb(51, 65, 85);

            using (SolidBrush fillBrush = new SolidBrush(backColor))
            {
                e.Graphics.FillRectangle(fillBrush, bounds);
            }

            if (selected)
            {
                using (SolidBrush accentBrush = new SolidBrush(Color.FromArgb(15, 118, 110)))
                {
                    e.Graphics.FillRectangle(accentBrush, bounds.X, bounds.Bottom - 3, bounds.Width, 3);
                }
            }

            TextRenderer.DrawText(
                e.Graphics,
                page.Text,
                tabWork.Font,
                bounds,
                foreColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        }

        private void CommandButton_Click(object sender, EventArgs e)
        {
            Control source = sender as Control;
            RaiseUiAction(source == null ? string.Empty : Convert.ToString(source.Tag), source);
        }

        private void CommandValue_Changed(object sender, EventArgs e)
        {
            if (_suppressFeatureEvents) return;
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
            if (chkArchiveImages != null) chkArchiveImages.Enabled = !workflowBusy;
            cmbCameraProvider.Enabled = cameraEnabled && !workflowBusy;
            cmbCameraDevice.Enabled = cameraEnabled && !workflowBusy;
            string[] cameraParameterControls =
            {
                "nudCameraExposure",
                "nudCameraGain",
                "nudCameraGamma",
                "cmbCameraTriggerMode",
                "nudCameraTimeout",
                "nudPreviewInterval",
                "flpCameraAdvancedParameters"
            };
            foreach (string controlName in cameraParameterControls)
                SetControlEnabled(controlName, cameraEnabled && !workflowBusy);

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

            // 标定模板相关按钮受运行时标定开关保护，需与后台守卫保持同步，避免点击后无响应；
            // 模板创建与测试可使用本地导入图像，因此不依赖相机开关。
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
            // 安全封送到 UI 线程；窗体已销毁或正在关闭时忽略滞后回调
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
