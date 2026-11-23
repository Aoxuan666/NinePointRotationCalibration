using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;
using NinePointRotationCalibration.Station.Controls;
using NinePointRotationCalibration.WinForms.Controls;

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
        private readonly Dictionary<string, Control> _actionControlMap = new Dictionary<string, Control>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Control> _namedControlMap = new Dictionary<string, Control>(StringComparer.Ordinal);
        private readonly Color _idlePointColor = Color.White;
        private readonly Color _originPointColor = Color.FromArgb(239, 246, 255);
        private readonly Color _activePointColor = Color.FromArgb(37, 99, 235);
        private Color _hudAccentColor = Color.FromArgb(59, 130, 246);
        private Color _cameraHeroAccentColor = Color.FromArgb(148, 163, 184);
        private bool _cameraConnected;
        private bool _liveStreamRunning;
        private bool _workflowBusy;
        private bool _suppressFeatureEvents;
        private int _lastCursorTextTick;
        private string _lastCursorText;
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
        private volatile bool _dualViewActive;
        private volatile bool _cameraTabActive;
        private bool _dualViewStateInitialized;
        private bool _suppressImageSplitBalance;
        private bool _warmingTabs;
        private bool _logCollapsed;
        private bool _userExpandedLogInDualView;

        public MainForm()
        {
            InitializeComponent();
            BuildControlLookups();
            tabWork.SelectedIndexChanged += delegate { UpdateCameraTabState(); };
            UpdateCameraTabState();
            UiRenderOptimizer.EnableDoubleBufferingRecursive(this);
            BufferedTabControl bufferedTabs = tabWork as BufferedTabControl;
            if (bufferedTabs != null)
            {
                bufferedTabs.TransitionScopeFactory = CreateWorkTabTransitionScope;
            }
            InitializeCalibrationRows();
            WireUiEvents();
            toolTip.SetToolTip(
                chkTemplateMatchingEnabled,
                "关闭后跳过模板定位，对当前图像直接做整图二值化/Blob；生产检测建议保持开启。");
            RefreshFeatureAvailability(false);
        }

        private void BuildControlLookups()
        {
            _actionControlMap.Clear();
            _namedControlMap.Clear();
            foreach (Control control in FindControls(this))
            {
                if (!string.IsNullOrWhiteSpace(control.Name) && !_namedControlMap.ContainsKey(control.Name))
                {
                    _namedControlMap[control.Name] = control;
                }
                string actionKey = control.Tag as string;
                if (!string.IsNullOrWhiteSpace(actionKey) && !_actionControlMap.ContainsKey(actionKey))
                {
                    _actionControlMap[actionKey] = control;
                }
            }
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
        public CheckBox LiveAcquisitionInput
        {
            get { return chkLiveAcquisition; }
        }

        [Browsable(false)]
        public Button ToggleLiveStreamButton
        {
            get { return btnToggleLiveStream; }
        }

        [Browsable(false)]
        public bool IsCameraTabActive
        {
            get { return _cameraTabActive; }
        }

        private void UpdateCameraTabState()
        {
            TabPage selected = tabWork.SelectedTab;
            _cameraTabActive = selected != null && selected.Name == "tabCamera";
        }

        [Browsable(false)]
        public bool IsDetectionDualViewActive
        {
            get { return _dualViewActive; }
        }

        [Browsable(false)]
        public bool IsWarmingTabs
        {
            get { return _warmingTabs; }
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
        public ComboBox DetectionPolarityInput
        {
            get { return cmbDetectionPolarity; }
        }

        [Browsable(false)]
        public ComboBox HoleFillModeInput
        {
            get { return cmbHoleFillMode; }
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
        public ComboBox CalibrationNozzleInput
        {
            get { return cmbCalibrationNozzle; }
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
        public CheckBox InspectionEnabledInput
        {
            get { return chkInspectionEnabled; }
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
                string heroStateText;
                Color indicatorColor;
                Color heroTextColor;
                switch (state)
                {
                    case CameraUiState.Disabled:
                        stateText = "相机已停用";
                        heroStateText = "⊘ 相机已停用";
                        indicatorColor = Color.FromArgb(148, 163, 184);
                        heroTextColor = TextMutedColor;
                        break;
                    case CameraUiState.Connecting:
                        stateText = "相机连接中";
                        heroStateText = "◐ 正在连接相机...";
                        indicatorColor = Color.FromArgb(245, 158, 11);
                        heroTextColor = Color.FromArgb(180, 83, 9);
                        break;
                    case CameraUiState.Connected:
                        stateText = "相机已连接";
                        heroStateText = "● 相机已连接";
                        indicatorColor = Color.FromArgb(34, 197, 94);
                        heroTextColor = Color.FromArgb(21, 128, 61);
                        break;
                    case CameraUiState.Error:
                        stateText = "相机异常";
                        heroStateText = "✕ 连接异常";
                        indicatorColor = Color.FromArgb(239, 68, 68);
                        heroTextColor = Color.FromArgb(185, 28, 28);
                        break;
                    default:
                        stateText = "相机未连接";
                        heroStateText = "○ 相机未连接";
                        indicatorColor = Color.FromArgb(148, 163, 184);
                        heroTextColor = TextSecondaryColor;
                        break;
                }

                _cameraUiState = state;
                _cameraConnected = state == CameraUiState.Connected;
                _cameraHeroAccentColor = indicatorColor;
                string cleanDetail = string.IsNullOrWhiteSpace(detail) ? string.Empty : detail.Trim();
                lblCameraIndicator.ForeColor = indicatorColor;
                lblCameraStatus.Text = stateText;
                toolTip.SetToolTip(lblCameraStatus, cleanDetail.Length == 0 ? stateText : stateText + "  " + cleanDetail);

                if (lblCameraHeroState != null && !lblCameraHeroState.IsDisposed)
                {
                    lblCameraHeroState.Text = heroStateText;
                    lblCameraHeroState.ForeColor = heroTextColor;
                }
                if (lblCameraHeroDetail != null && !lblCameraHeroDetail.IsDisposed)
                {
                    string providerLabel = cmbCameraProvider != null && cmbCameraProvider.SelectedItem != null
                        ? Convert.ToString(cmbCameraProvider.SelectedItem, CultureInfo.InvariantCulture)
                        : "工业相机";
                    lblCameraHeroDetail.Text = cleanDetail.Length > 0
                        ? cleanDetail
                        : (state == CameraUiState.Disabled ? "请在下方勾选「启用相机」" : providerLabel + " · 当前未连接真实硬件");
                }
                if (pnlCameraHeroCard != null && !pnlCameraHeroCard.IsDisposed)
                {
                    pnlCameraHeroCard.Invalidate();
                }
            });
        }

        /// <summary>更新相机页遥测磁贴中的画面分辨率显示。</summary>
        public void SetCameraResolution(int width, int height)
        {
            RunOnUiThread(delegate
            {
                if (lblCameraResolution == null || lblCameraResolution.IsDisposed) return;
                lblCameraResolution.Text = (width > 0 && height > 0)
                    ? string.Format(CultureInfo.InvariantCulture, "{0} × {1}", width, height)
                    : "--";
            });
        }

        public void RefreshFeatureAvailability(bool cameraEnabled, bool calibrationEnabled, bool workflowBusy)
        {
            RefreshFeatureAvailability(
                cameraEnabled,
                calibrationEnabled,
                chkInspectionEnabled == null || chkInspectionEnabled.Checked,
                _cameraConnected,
                chkArchiveImages != null && chkArchiveImages.Checked,
                chkLiveAcquisition == null || chkLiveAcquisition.Checked,
                workflowBusy);
        }

        public void RefreshFeatureAvailability(
            bool cameraEnabled,
            bool calibrationEnabled,
            bool archiveImages,
            bool workflowBusy)
        {
            RefreshFeatureAvailability(
                cameraEnabled,
                calibrationEnabled,
                chkInspectionEnabled == null || chkInspectionEnabled.Checked,
                _cameraConnected,
                archiveImages,
                chkLiveAcquisition == null || chkLiveAcquisition.Checked,
                workflowBusy);
        }

        public void RefreshFeatureAvailability(
            bool cameraEnabled,
            bool calibrationEnabled,
            bool archiveImages,
            bool liveAcquisitionEnabled,
            bool workflowBusy)
        {
            RefreshFeatureAvailability(
                cameraEnabled,
                calibrationEnabled,
                chkInspectionEnabled == null || chkInspectionEnabled.Checked,
                _cameraConnected,
                archiveImages,
                liveAcquisitionEnabled,
                workflowBusy);
        }

        public void RefreshFeatureAvailability(
            bool cameraEnabled,
            bool calibrationEnabled,
            bool inspectionEnabled,
            bool cameraConnected,
            bool archiveImages,
            bool liveAcquisitionEnabled,
            bool workflowBusy)
        {
            RunOnUiThread(delegate
            {
                _suppressFeatureEvents = true;
                try
                {
                    chkCameraEnabled.Checked = cameraEnabled;
                    chkCalibrationEnabled.Checked = calibrationEnabled;
                    if (chkInspectionEnabled != null) chkInspectionEnabled.Checked = inspectionEnabled;
                    if (chkArchiveImages != null) chkArchiveImages.Checked = archiveImages;
                    if (chkLiveAcquisition != null) chkLiveAcquisition.Checked = liveAcquisitionEnabled;
                }
                finally
                {
                    _suppressFeatureEvents = false;
                }

                ApplyFeatureAvailability(cameraEnabled, calibrationEnabled, inspectionEnabled, cameraConnected, workflowBusy);
            });
        }

        /// <summary>同步真实硬件连接状态并立即刷新相关按钮可用性（不覆盖当前工作流繁忙状态）。</summary>
        public void UpdateCameraConnectionAvailability(bool cameraConnected)
        {
            RunOnUiThread(delegate
            {
                _cameraConnected = cameraConnected;
                ApplyFeatureAvailability(
                    chkCameraEnabled.Checked,
                    chkCalibrationEnabled.Checked,
                    chkInspectionEnabled == null || chkInspectionEnabled.Checked,
                    cameraConnected,
                    _workflowBusy);
            });
        }

        /// <summary>更新相机页实时采集状态徽章与开始/停止按钮文案。</summary>
        public void SetCameraLiveStatus(bool running, string detail)
        {
            RunOnUiThread(delegate
            {
                _liveStreamRunning = running;
                if (btnToggleLiveStream != null && !btnToggleLiveStream.IsDisposed)
                {
                    btnToggleLiveStream.Text = running ? "停止实时" : "开始实时";
                    if (btnToggleLiveStream.Enabled)
                    {
                        btnToggleLiveStream.BackColor = running
                            ? Color.FromArgb(254, 242, 242)
                            : AccentPrimaryColor;
                        btnToggleLiveStream.ForeColor = running
                            ? Color.FromArgb(185, 28, 28)
                            : Color.White;
                        btnToggleLiveStream.FlatAppearance.BorderColor = running
                            ? Color.FromArgb(254, 202, 202)
                            : AccentPrimaryColor;
                    }
                }

                if (lblCameraLiveStatus != null && !lblCameraLiveStatus.IsDisposed)
                {
                    lblCameraLiveStatus.BackColor = running
                        ? Color.FromArgb(236, 253, 245)
                        : Color.FromArgb(241, 245, 249);
                    lblCameraLiveStatus.ForeColor = running
                        ? Color.FromArgb(21, 128, 61)
                        : TextMutedColor;
                    lblCameraLiveStatus.Text = string.IsNullOrWhiteSpace(detail)
                        ? (running ? "● 实时采集中" : "未采集")
                        : detail;
                }
            });
        }

        public void RefreshFeatureAvailability(bool workflowBusy)
        {
            RunOnUiThread(delegate
            {
                ApplyFeatureAvailability(
                    chkCameraEnabled.Checked,
                    chkCalibrationEnabled.Checked,
                    chkInspectionEnabled == null || chkInspectionEnabled.Checked,
                    _cameraConnected,
                    workflowBusy);
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
                    ? Color.FromArgb(127, 29, 29)
                    : isBusy ? Color.FromArgb(120, 53, 15) : Color.FromArgb(20, 83, 45);
                lblOperationStatus.ForeColor = isError
                    ? Color.FromArgb(254, 202, 202)
                    : isBusy ? Color.FromArgb(254, 243, 199) : Color.FromArgb(187, 247, 208);
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
                if (_dualViewStateInitialized && _dualViewActive == active)
                {
                    return;
                }

                _dualViewStateInitialized = true;
                _dualViewActive = active;

                _suppressImageSplitBalance = true;
                using (UiRenderOptimizer.BeginFreezeScope(this))
                {
                    if (imageSplit != null) imageSplit.SuspendLayout();
                    if (workspaceSplit != null) workspaceSplit.SuspendLayout();
                    if (rootLayout != null) rootLayout.SuspendLayout();
                    SuspendLayout();
                    try
                    {
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
                        }

                        Control hudParent = active ? (Control)effectViewport : imageViewport;
                        if (lblInspectionHud != null && hudParent != null && !ReferenceEquals(lblInspectionHud.Parent, hudParent))
                        {
                            lblInspectionHud.Parent = hudParent;
                            lblInspectionHud.BringToFront();
                        }
                    }
                    finally
                    {
                        _suppressImageSplitBalance = false;
                        if (imageSplit != null) imageSplit.ResumeLayout(true);
                        if (workspaceSplit != null) workspaceSplit.ResumeLayout(true);
                        if (rootLayout != null) rootLayout.ResumeLayout(true);
                        ResumeLayout(true);
                    }

                    if (active)
                    {
                        BalanceImageSplit();
                    }
                }
            });
        }

        private void BalanceImageSplit()
        {
            if (_suppressImageSplitBalance ||
                imageSplit == null ||
                imageSplit.IsDisposed ||
                imageSplit.Panel2Collapsed)
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

            using (UiRenderOptimizer.BeginFreezeScope(imageSplit, imageSplit))
            {
                bool toVertical = imageSplit.Orientation == Orientation.Horizontal;
                imageSplit.Orientation = toVertical ? Orientation.Vertical : Orientation.Horizontal;
                if (btnToggleDualLayout != null)
                {
                    btnToggleDualLayout.Text = toVertical ? "⇅ 切换上下" : "⇄ 切换左右";
                }
                BalanceImageSplit();
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
            if (_dualViewActive && !_suppressImageSplitBalance)
            {
                BalanceImageSplit();
            }
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            ClampToWorkingArea();
            if (rootLayout != null) rootLayout.PerformLayout();
            if (workspaceSplit != null) workspaceSplit.PerformLayout();
            if (tabWork != null) tabWork.PerformLayout();
            _warmingTabs = true;
            try
            {
                UiRenderOptimizer.WarmUpTabControlPages(tabWork);
            }
            finally
            {
                _warmingTabs = false;
            }
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            ClampToWorkingArea();
        }

        private void ClampToWorkingArea()
        {
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

        private IDisposable CreateWorkTabTransitionScope(TabPage fromTab, TabPage toTab)
        {
            if (_warmingTabs) return null;
            bool involvesDetection =
                (fromTab != null && string.Equals(fromTab.Name, "tabDetection", StringComparison.Ordinal)) ||
                (toTab != null && string.Equals(toTab.Name, "tabDetection", StringComparison.Ordinal));
            if (involvesDetection)
            {
                return UiRenderOptimizer.BeginFreezeScope(this, imageSplit, workspaceSplit, rootLayout, this);
            }

            return UiRenderOptimizer.BeginFreezeScope(tabWork, tabWork);
        }

        public void SelectCalibrationPoint(int pointNumber)
        {
            RunOnUiThread(delegate
            {
                foreach (KeyValuePair<int, Button> pair in _pointButtonMap)
                {
                    bool selected = pair.Key == pointNumber;
                    bool isOrigin = pair.Key == 5 || pair.Key == 12;
                    pair.Value.BackColor = selected
                        ? _activePointColor
                        : (isOrigin ? _originPointColor : _idlePointColor);
                    pair.Value.ForeColor = selected
                        ? Color.White
                        : (isOrigin ? AccentPrimaryColor : TextPrimaryColor);
                    pair.Value.FlatAppearance.BorderColor = selected
                        ? _activePointColor
                        : (isOrigin ? Color.FromArgb(147, 197, 253) : Color.FromArgb(203, 213, 225));
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
                statusLabel.ForeColor = valid ? Color.FromArgb(21, 128, 61) : Color.FromArgb(185, 28, 28);

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
                lblDualNozzleSummary.BackColor = Color.FromArgb(236, 253, 245);
                lblDualNozzleSummary.ForeColor = Color.FromArgb(21, 128, 61);
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
                    "双吸嘴状态：左吸嘴 [{0}] / 右吸嘴 [{1}]（需两侧均完成示教）\r\n工艺模式：吸取时旋转 R 轴对齐来料板角 → 盲摆转回固定示教 R 角",
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
                    ? Color.FromArgb(21, 128, 61)
                    : Color.FromArgb(185, 28, 28);
            });
        }

        private void LblInspectionHud_Paint(object sender, PaintEventArgs e)
        {
            if (lblInspectionHud == null) return;
            using (SolidBrush accent = new SolidBrush(_hudAccentColor))
            {
                e.Graphics.FillRectangle(accent, 0, 0, 4, lblInspectionHud.Height);
            }
            using (Pen border = new Pen(Color.FromArgb(51, 65, 85), 1.0F))
            {
                e.Graphics.DrawRectangle(border, 0, 0, lblInspectionHud.Width - 1, lblInspectionHud.Height - 1);
            }
        }

        private void PnlCameraHeroCard_Paint(object sender, PaintEventArgs e)
        {
            if (pnlCameraHeroCard == null || pnlCameraHeroCard.Width <= 1 || pnlCameraHeroCard.Height <= 1) return;
            using (SolidBrush accent = new SolidBrush(_cameraHeroAccentColor))
            {
                e.Graphics.FillRectangle(accent, 0, 0, 5, pnlCameraHeroCard.Height);
            }
            using (Pen border = new Pen(CardBorderColor, 1.0F))
            {
                e.Graphics.DrawRectangle(border, 0, 0, pnlCameraHeroCard.Width - 1, pnlCameraHeroCard.Height - 1);
            }
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
            bool hasManualMarks,
            int okCount = -1,
            int ngCount = -1,
            string ngReason = null)
        {
            RunOnUiThread(delegate
            {
                string resultText = pass
                    ? "OK"
                    : (string.IsNullOrWhiteSpace(ngReason) ? "NG" : "NG·" + ngReason);
                string breakdown = (okCount >= 0 && ngCount >= 0)
                    ? string.Format(CultureInfo.InvariantCulture, " (OK {0}/NG {1})", okCount, ngCount)
                    : string.Empty;
                lblInspectionHud.Text = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}   板 {1}/{2}{3}   CT {4:0} ms\r\n定位 {5:0}  检测 {6:0}  |  图转 {7:0} 阈 {8:0} 形 {9:0} 孔 {10:0} 连 {11:0}  标记:{12}",
                    resultText,
                    actual,
                    expected,
                    breakdown,
                    totalMilliseconds,
                    matchMilliseconds,
                    blobMilliseconds,
                    imageConversionMilliseconds,
                    thresholdMilliseconds,
                    morphologyMilliseconds,
                    holeFillMilliseconds,
                    componentMilliseconds,
                    hasManualMarks ? "有" : "无");
                _hudAccentColor = pass
                    ? Color.FromArgb(34, 197, 94)
                    : Color.FromArgb(239, 68, 68);
                lblInspectionHud.ForeColor = pass
                    ? Color.FromArgb(187, 247, 208)
                    : Color.FromArgb(254, 202, 202);
                lblInspectionHud.Visible = true;
                lblInspectionHud.BringToFront();
                lblInspectionHud.Invalidate();
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

        /// <summary>
        /// 实时预览刷新后同步更新检测结果表中的“标记”和“状态”列及行颜色，
        /// 确保效果图 ROI 标签、结果表、汇总栏和左上角 HUD 的 OK/NG 始终完全一致。
        /// </summary>
        public void SyncDetectionBoardStatuses(
            int[] boardIndices,
            bool[] hasManualMarks,
            double[] columns,
            double[] rows,
            double[] angles)
        {
            RunOnUiThread(delegate
            {
                int count = boardIndices == null ? 0 : boardIndices.Length;
                if (count == 0)
                {
                    dgvDetectionResults.Rows.Clear();
                    return;
                }

                bool canUpdateInPlace = dgvDetectionResults.Rows.Count == count;
                if (canUpdateInPlace)
                {
                    for (int i = 0; i < count; i++)
                    {
                        object raw = dgvDetectionResults.Rows[i].Cells[0].Value;
                        int existingIndex;
                        if (raw == null || !int.TryParse(Convert.ToString(raw, CultureInfo.InvariantCulture), out existingIndex) ||
                            existingIndex != boardIndices[i])
                        {
                            canUpdateInPlace = false;
                            break;
                        }
                    }
                }

                if (canUpdateInPlace)
                {
                    for (int i = 0; i < count; i++)
                    {
                        bool isNg = hasManualMarks[i];
                        DataGridViewRow gridRow = dgvDetectionResults.Rows[i];
                        gridRow.Cells[7].Value = isNg ? "有" : "无";
                        gridRow.Cells[8].Value = isNg ? "NG" : "OK";
                        gridRow.DefaultCellStyle.ForeColor = isNg
                            ? Color.FromArgb(185, 28, 28)
                            : Color.FromArgb(21, 128, 61);
                        gridRow.DefaultCellStyle.BackColor = isNg
                            ? Color.FromArgb(254, 242, 242)
                            : Color.White;
                    }
                    return;
                }

                List<object[]> rebuilt = new List<object[]>(count);
                for (int i = 0; i < count; i++)
                {
                    bool isNg = hasManualMarks[i];
                    rebuilt.Add(new object[]
                    {
                        boardIndices[i],
                        "--",
                        columns[i].ToString("0.###", CultureInfo.InvariantCulture),
                        rows[i].ToString("0.###", CultureInfo.InvariantCulture),
                        angles[i].ToString("0.###", CultureInfo.InvariantCulture),
                        "--",
                        "--",
                        isNg ? "有" : "无",
                        isNg ? "NG" : "OK"
                    });
                }
                ReplaceDetectionResults(rebuilt);
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
            if (string.IsNullOrWhiteSpace(actionKey)) return null;
            Control cached;
            if (_actionControlMap.TryGetValue(actionKey, out cached) && cached != null && !cached.IsDisposed)
                return cached;
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
                if (_dualViewActive && effectViewport.HasImage) effectViewport.FitImage();
            };
            btnActualPixels.Click += delegate
            {
                imageViewport.ShowActualPixels();
                if (_dualViewActive && effectViewport.HasImage) effectViewport.ShowActualPixels();
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
            effectViewport.ImagePositionChanged += ImageViewport_ImagePositionChanged;
            dgvDetectionResults.SelectionChanged += delegate { RaiseUiAction("detection.rowSelected", dgvDetectionResults); };
            chkCameraEnabled.CheckedChanged += FeatureToggle_CheckedChanged;
            chkCalibrationEnabled.CheckedChanged += FeatureToggle_CheckedChanged;
            if (chkInspectionEnabled != null) chkInspectionEnabled.CheckedChanged += FeatureToggle_CheckedChanged;

            foreach (Button button in calibrationPointButtons)
            {
                button.Click += CalibrationPointButton_Click;
            }

            // 统一按控件 Tag 路由常规操作按钮、复选框、下拉框和数值框；标定点按钮单独通过 CalibrationPointRequested 派发
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
                    checkBox != chkCameraEnabled && checkBox != chkCalibrationEnabled &&
                    checkBox != chkInspectionEnabled)
                {
                    checkBox.CheckedChanged += CommandValue_Changed;
                }

                ComboBox comboBox = control as ComboBox;
                actionKey = comboBox == null ? null : comboBox.Tag as string;
                if (comboBox != null && !string.IsNullOrWhiteSpace(actionKey))
                {
                    comboBox.SelectedIndexChanged += CommandValue_Changed;
                }

                NumericUpDown numeric = control as NumericUpDown;
                if (numeric != null) numeric.MouseWheel += NumericInput_MouseWheel;
                if (numeric != null && string.Equals(numeric.Name, "nudCalibrationStepR", StringComparison.Ordinal))
                {
                    numeric.ValueChanged += delegate { UpdateCalibrationSpanHint(numeric.Value); };
                    UpdateCalibrationSpanHint(numeric.Value);
                }
                actionKey = numeric == null ? null : numeric.Tag as string;
                if (numeric != null && !string.IsNullOrWhiteSpace(actionKey))
                    numeric.ValueChanged += CommandValue_Changed;
            }
        }

        private void UpdateCalibrationSpanHint(decimal stepR)
        {
            if (lblCalibrationSpanHint == null || lblCalibrationSpanHint.IsDisposed) return;
            double totalSpan = Decimal.ToDouble(stepR) * 4.0;
            bool sufficient = totalSpan >= 20.0 - 1e-6;
            lblCalibrationSpanHint.ForeColor = sufficient
                ? TextMutedColor
                : Color.FromArgb(185, 28, 28);
            lblCalibrationSpanHint.Text = sufficient
                ? string.Format(CultureInfo.InvariantCulture, "5/12 原点 · 旋转总跨度 {0:0.#}° (要求≥20°)", totalSpan)
                : string.Format(CultureInfo.InvariantCulture, "跨度 {0:0.#}° < 20° 最低要求，请增大 R 步距", totalSpan);
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
            // 极简现代标签栏：纯白底色 + 底部 1px 浅灰分隔 + 选中态 2px 钴蓝下划线与加粗文案
            if (e.Index < 0 || e.Index >= tabWork.TabPages.Count) return;
            TabPage page = tabWork.TabPages[e.Index];
            Rectangle bounds = e.Bounds;
            bool selected = (e.State & DrawItemState.Selected) == DrawItemState.Selected;
            bool enabled = page.Enabled;

            Color backColor = selected
                ? Color.White
                : PageBackgroundColor;
            Color foreColor = !enabled
                ? Color.FromArgb(148, 163, 184)
                : selected ? AccentPrimaryColor : TextSecondaryColor;

            using (SolidBrush fillBrush = new SolidBrush(backColor))
            {
                e.Graphics.FillRectangle(fillBrush, bounds);
            }

            if (selected)
            {
                using (SolidBrush accentBrush = new SolidBrush(AccentPrimaryColor))
                {
                    e.Graphics.FillRectangle(accentBrush, bounds.X + 6, bounds.Bottom - 2, Math.Max(0, bounds.Width - 12), 2);
                }
            }
            else
            {
                using (SolidBrush dividerBrush = new SolidBrush(CardBorderColor))
                {
                    e.Graphics.FillRectangle(dividerBrush, bounds.X, bounds.Bottom - 1, bounds.Width, 1);
                }
            }

            Font drawFont = selected
                ? new Font(tabWork.Font, FontStyle.Bold)
                : tabWork.Font;
            try
            {
                TextRenderer.DrawText(
                    e.Graphics,
                    page.Text,
                    drawFont,
                    bounds,
                    foreColor,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
            }
            finally
            {
                if (!ReferenceEquals(drawFont, tabWork.Font)) drawFont.Dispose();
            }
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

            ApplyFeatureAvailability(
                chkCameraEnabled.Checked,
                chkCalibrationEnabled.Checked,
                chkInspectionEnabled == null || chkInspectionEnabled.Checked,
                _cameraConnected,
                _workflowBusy);
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
            // 30Hz 节流刷新像素坐标文本，避免鼠标高频滑过视口触发冗余 Label 重绘拖慢拖拽/缩放流畅度
            int now = Environment.TickCount;
            if (e.IsInsideImage && unchecked(now - _lastCursorTextTick) < 33) return;
            string nextText = e.IsInsideImage
                ? string.Format(CultureInfo.InvariantCulture, "X {0:0.0}   Y {1:0.0}", e.ImagePoint.X, e.ImagePoint.Y)
                : "X --   Y --";
            if (string.Equals(nextText, _lastCursorText, StringComparison.Ordinal)) return;
            _lastCursorText = nextText;
            _lastCursorTextTick = now;
            lblCursorPosition.Text = nextText;
        }

        private void ImageViewport_ImageAvailabilityChanged(object sender, EventArgs e)
        {
            RunOnUiThread(delegate
            {
                ApplyFeatureAvailability(
                    chkCameraEnabled.Checked,
                    chkCalibrationEnabled.Checked,
                    chkInspectionEnabled == null || chkInspectionEnabled.Checked,
                    _cameraConnected,
                    _workflowBusy);
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

        private void ApplyFeatureAvailability(
            bool cameraEnabled,
            bool calibrationEnabled,
            bool inspectionEnabled,
            bool cameraConnected,
            bool workflowBusy)
        {
            _workflowBusy = workflowBusy;
            _cameraConnected = cameraEnabled && cameraConnected;

            chkCameraEnabled.Enabled = !workflowBusy;
            chkCalibrationEnabled.Enabled = !workflowBusy;
            if (chkInspectionEnabled != null) chkInspectionEnabled.Enabled = !workflowBusy;
            if (chkArchiveImages != null) chkArchiveImages.Enabled = !workflowBusy;
            if (chkLiveAcquisition != null) chkLiveAcquisition.Enabled = cameraEnabled && !workflowBusy;
            cmbCameraProvider.Enabled = cameraEnabled && !workflowBusy;
            cmbCameraDevice.Enabled = cameraEnabled && !workflowBusy;

            bool cameraReady = cameraEnabled && _cameraConnected && !workflowBusy;
            SetControlEnabled("nudCameraExposure", cameraReady);

            // 枚举与连接仅要求相机模块已启用；所有实际取流、拍照与参数读写均严格要求真实相机已连接
            SetActionControlEnabled("camera.enumerate", cameraEnabled && !workflowBusy);
            SetActionControlEnabled("camera.connect", cameraEnabled && !workflowBusy);
            string[] connectedCameraActions =
            {
                "camera.snapshot",
                "camera.disconnect",
                "camera.apply",
                "camera.read",
                "camera.test",
                "camera.liveStreamToggle",
                "camera.resumeMainLive"
            };
            foreach (string action in connectedCameraActions) SetActionControlEnabled(action, cameraReady);
            if (btnResumeLive != null) btnResumeLive.Enabled = cameraReady;
            if (btnSyncLiveToMain != null) btnSyncLiveToMain.Enabled = cameraReady;
            if (btnToggleLiveStream != null && btnToggleLiveStream.Enabled)
            {
                btnToggleLiveStream.BackColor = _liveStreamRunning
                    ? Color.FromArgb(254, 242, 242)
                    : AccentPrimaryColor;
                btnToggleLiveStream.ForeColor = _liveStreamRunning
                    ? Color.FromArgb(185, 28, 28)
                    : Color.White;
                btnToggleLiveStream.FlatAppearance.BorderColor = _liveStreamRunning
                    ? Color.FromArgb(254, 202, 202)
                    : AccentPrimaryColor;
            }
            if (btnSnapshot != null)
            {
                btnSnapshot.Enabled = cameraReady;
                toolTip.SetToolTip(
                    btnSnapshot,
                    !cameraEnabled
                        ? "相机已停用，请先在「相机」页启用相机"
                        : !_cameraConnected
                            ? "相机未连接，请先在「相机」页连接真实相机"
                            : "触发已连接相机采集单帧图像");
            }

            btnImportImage.Enabled = !workflowBusy;

            // 检测模块与标定模块完全独立：不开启标定（或未标定）时仍可独立执行视觉检测
            if (tabDetection != null) tabDetection.Enabled = inspectionEnabled;
            bool hasImage = imageViewport.HasImage;
            btnDetectCurrentImage.Enabled = !workflowBusy && inspectionEnabled && hasImage;
            SetActionControlEnabled("detection.debug", !workflowBusy && inspectionEnabled && hasImage);
            SetActionControlEnabled("detection.save", !workflowBusy && inspectionEnabled);
            SetActionControlEnabled("detection.test", !workflowBusy && inspectionEnabled && (cameraReady || hasImage));
            SetActionToolTip(
                "detection.test",
                !inspectionEnabled
                    ? "请先在「相机」页开启「启用检测」"
                    : (cameraReady || hasImage)
                        ? "执行视觉检测（有锁定图像时检测锁定图，实时预览时抓取相机新帧，无需先完成标定）"
                        : "请先连接相机或点击「导入图片」后再执行检测");

            // 标定与示教模块独立受“启用标定”控制；涉及硬件采图时额外要求真实相机已连接
            if (tabCalibration != null) tabCalibration.Enabled = calibrationEnabled;
            if (cmbCalibrationNozzle != null) cmbCalibrationNozzle.Enabled = calibrationEnabled && !workflowBusy;
            bool acquisitionCalibrationAvailable = cameraReady && calibrationEnabled;
            foreach (Button button in calibrationPointButtons)
            {
                button.Enabled = acquisitionCalibrationAvailable;
            }
            SetActionControlEnabled("calibration.manual", acquisitionCalibrationAvailable);
            SetActionControlEnabled("calibration.auto", acquisitionCalibrationAvailable);
            SetActionControlEnabled("calibration.calculate", calibrationEnabled && !workflowBusy);
            SetActionControlEnabled("calibration.cancel", calibrationEnabled);
            SetActionControlEnabled("teaching.left.capture", acquisitionCalibrationAvailable);
            SetActionControlEnabled("teaching.right.capture", acquisitionCalibrationAvailable);
            SetActionControlEnabled("teaching.left.clear", calibrationEnabled && !workflowBusy);
            SetActionControlEnabled("teaching.right.clear", calibrationEnabled && !workflowBusy);

            // 标定模板相关按钮受运行时标定开关保护，需与后台守卫保持同步，避免点击后无响应；
            // 模板创建与测试可使用本地导入图像，因此不依赖相机开关。
            bool calibrationTemplateAvailable = calibrationEnabled && !workflowBusy;
            bool positioningTemplateAvailable = !workflowBusy;
            SetActionControlEnabled("template.calibration.create", calibrationTemplateAvailable);
            SetActionControlEnabled("template.calibration.browse", calibrationTemplateAvailable);
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
            if (tabWork != null) tabWork.Invalidate();
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
            Control control;
            if (!_namedControlMap.TryGetValue(controlName, out control) || control == null || control.IsDisposed)
            {
                control = FindControls(this)
                    .FirstOrDefault(item => string.Equals(item.Name, controlName, StringComparison.Ordinal));
            }
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

        internal void RunOnUiThread(MethodInvoker action)
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
