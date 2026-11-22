using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using NinePointRotationCalibration.WinForms.Controls;
using NinePointRotationCalibration.WinForms.Geometry;
using NinePointRotationCalibration.WinForms.Models;
using NinePointRotationCalibration.WinForms.Services;

namespace NinePointRotationCalibration.WinForms.Forms
{
    /// <summary>
    /// 模板匹配编辑器窗体，支持模板/搜索/检测 ROI 绘制、掩膜涂抹、特征提取、模型创建及匹配测试。
    /// </summary>
    public sealed class TemplateEditorForm : Form
    {
        private const string DefaultMetric = "use_polarity";
        private const string EdgeContourMetric = "ignore_local_polarity";

        private static readonly ComboOption[] OptimizationOptions =
        {
            new ComboOption("auto", "自动 (auto)"),
            new ComboOption("none", "不降采样 (none)"),
            new ComboOption("point_reduction_low", "低点数缩减"),
            new ComboOption("point_reduction_medium", "中点数缩减"),
            new ComboOption("point_reduction_high", "高点数缩减")
        };

        private static readonly ComboOption[] ShapeMetricOptions =
        {
            new ComboOption("use_polarity", "保持极性 (use_polarity)"),
            new ComboOption("ignore_global_polarity", "忽略全局极性"),
            new ComboOption("ignore_local_polarity", "忽略局部极性"),
            new ComboOption("ignore_color_polarity", "忽略颜色极性")
        };

        private static readonly ComboOption[] NccMetricOptions =
        {
            new ComboOption("use_polarity", "保持极性 (use_polarity)"),
            new ComboOption("ignore_global_polarity", "忽略全局极性")
        };

        private static readonly ComboOption[] ShapeSubPixelOptions =
        {
            new ComboOption("none", "关闭 (none)"),
            new ComboOption("interpolation", "插值 (interpolation)"),
            new ComboOption("least_squares", "最小二乘 (least_squares)"),
            new ComboOption("least_squares_high", "高精度最小二乘"),
            new ComboOption("least_squares_very_high", "超高精度最小二乘")
        };

        private static readonly ComboOption[] NccSubPixelOptions =
        {
            new ComboOption("none", "关闭 (none)"),
            new ComboOption("interpolation", "插值 (interpolation)")
        };

        private const int HistoryLimit = 30;

        private readonly CalibrationCanvas _canvas;
        private readonly Stack<TemplateEditorState> _undoStack = new Stack<TemplateEditorState>();
        private readonly Stack<TemplateEditorState> _redoStack = new Stack<TemplateEditorState>();
        private readonly ToolStripButton _undoButton;
        private readonly ToolStripButton _redoButton;
        private readonly Button _addTemplateRoiButton;
        private readonly Button _addSearchRoiButton;
        private readonly Button _addDetectionRoiButton;
        private readonly Button _deleteRoiButton;
        private readonly Button _moveAnchorToolButton;
        private readonly Button _maskToolButton;
        private readonly ComboBox _maskShapeCombo;
        private readonly CheckBox _maskVisibleCheck;
        private readonly Button _invertMaskButton;
        private readonly Button _clearMaskButton;
        private readonly System.Windows.Forms.Timer _autoPreviewTimer;
        private readonly System.Windows.Forms.Timer _maskSyncTimer;
        private readonly TabControl _parameterTabs;
        private readonly CheckBox _teachAdvancedToggle;
        private readonly CheckBox _matchAdvancedToggle;
        private Panel _teachAdvancedPanel;
        private Panel _matchAdvancedPanel;
        private readonly ComboBox _modelTypeCombo;
        private readonly CheckBox _autoThresholdCheck;
        private readonly NumericUpDown _levelsInput;
        private readonly NumericUpDown _matchLevelsInput;
        private readonly NumericUpDown _angleStartInput;
        private readonly NumericUpDown _angleExtentInput;
        private readonly NumericUpDown _matchAngleStartInput;
        private readonly NumericUpDown _matchAngleExtentInput;
        private readonly NumericUpDown _angleStepInput;
        private readonly ComboBox _optimizationCombo;
        private readonly ComboBox _metricCombo;
        private readonly NumericUpDown _contrastInput;
        private readonly NumericUpDown _minimumContrastInput;
        private readonly CheckBox _edgeContourModeCheck;
        private readonly NumericUpDown _edgeSigmaInput;
        private readonly NumericUpDown _edgeLowThresholdInput;
        private readonly NumericUpDown _edgeHighThresholdInput;
        private readonly NumericUpDown _minimumContourLengthInput;
        private readonly NumericUpDown _featurePointSpacingInput;
        private readonly NumericUpDown _maximumFeaturePointCountInput;
        private readonly NumericUpDown _minimumScoreInput;
        private readonly NumericUpDown _greedinessInput;
        private readonly NumericUpDown _maximumOverlapInput;
        private readonly NumericUpDown _matchCountInput;
        private readonly ComboBox _subPixelCombo;
        private readonly NumericUpDown _timeoutInput;
        private readonly NumericUpDown _contourPointSpacingInput;
        private readonly CheckBox _allowPartialCheck;
        private readonly NumericUpDown _brushRadiusInput;
        private readonly NumericUpDown _anchorRowInput;
        private readonly NumericUpDown _anchorColumnInput;
        private readonly CheckBox _anchorLockedCheck;
        private readonly CheckBox _templateLockedCheck;
        private readonly CheckBox _searchLockedCheck;
        private readonly Button _extractButton;
        private readonly Button _createButton;
        private readonly Button _testButton;
        private readonly Button _okButton;
        private readonly Button _cancelButton;
        private readonly TextBox _operationLog;
        private readonly Label _testResultLabel;
        private readonly Label _modelStateLabel;
        private readonly ToolStripStatusLabel _statusLabel;
        private readonly ToolStripProgressBar _progressBar;
        private readonly bool _enableDetectionRoi;

        private Bitmap _referenceImage;
        private PreviewImageHolder _previewImageHolder;
        private TemplateEditorState _state;
        private TemplateEditorState _originalState;
        private List<List<ImageCoordinate>> _unmaskedBaseContours = new List<List<ImageCoordinate>>();
        private RotatedRectangle _unmaskedBaseRoi;
        private List<ImageCoordinate> _committedVisiblePoints = new List<ImageCoordinate>();
        private readonly List<CanvasPointOverlay> _overlayPointPool = new List<CanvasPointOverlay>();
        private int _lastMaskPreviewFilterTick;
        private CancellationTokenSource _operationCancellation;
        private CancellationTokenSource _autoPreviewCancellation;
        private bool _autoPreviewRunning;
        private bool _autoPreviewPending;
        private int _autoPreviewGeneration;
        private bool _loadingControls;
        private bool _busy;
        private bool _initialBoundsConstrained;
        private bool _closing;

        public TemplateEditorForm(
            Bitmap referenceImage,
            TemplateEditorState initialState = null,
            ITemplateEditorService service = null,
            bool enableDetectionRoi = false)
        {
            if (referenceImage == null)
            {
                throw new ArgumentNullException(nameof(referenceImage));
            }

            Service = service;
            _enableDetectionRoi = enableDetectionRoi;
            _referenceImage = CloneToFastPArgb(referenceImage);
            // PreviewImageHolder 复用已转换的 _referenceImage，打开窗口时消除一次冗余 20MP 同步位图复制
            _previewImageHolder = new PreviewImageHolder(_referenceImage, false);
            _state = initialState == null ? new TemplateEditorState() : initialState.DeepClone();
            if (_state.Parameters == null)
            {
                _state.Parameters = new TemplateEditorParameters();
            }
            // 创建新模板时默认启用自动阈值，避免旧配置或空状态误落入手动阈值
            if (_state.TemplateRoi == null && !_state.HasModel)
            {
                _state.Parameters.AutoThreshold = true;
            }
            EnsureInitialGeometry(_state, _referenceImage.Size, _enableDetectionRoi);
            _originalState = _state.DeepClone();

            Text = _enableDetectionRoi ? "定位模板与检测区域" : "模板匹配设置";
            StartPosition = FormStartPosition.CenterParent;
            MinimumSize = new Size(1120, 700);
            Size = new Size(1380, 840);
            KeyPreview = true;
            ShowIcon = false;
            Font = new Font("Microsoft YaHei UI", 9.0f, FontStyle.Regular, GraphicsUnit.Point);
            BackColor = Color.FromArgb(248, 250, 252);
            ForeColor = Color.FromArgb(15, 23, 42);

            _canvas = new CalibrationCanvas { Dock = DockStyle.Fill };
            _autoPreviewTimer = new System.Windows.Forms.Timer { Interval = 40 };
            _autoPreviewTimer.Tick += AutoPreviewTimer_Tick;
            _maskSyncTimer = new System.Windows.Forms.Timer { Interval = 350 };
            _maskSyncTimer.Tick += MaskSyncTimer_Tick;

            _undoButton = CreateToolButton("撤销", "撤销上一步模板编辑", UndoEdit);
            _redoButton = CreateToolButton("重做", "重做刚才撤销的模板编辑", RedoEdit);
            _addTemplateRoiButton = CreatePanelToggleButton("添加模板ROI", (sender, args) => AddOrSelectRoi(CanvasRoiTarget.Template));
            _addSearchRoiButton = CreatePanelToggleButton("添加搜索ROI", (sender, args) => AddOrSelectRoi(CanvasRoiTarget.Search));
            _addDetectionRoiButton = CreatePanelToggleButton("添加检测ROI", (sender, args) => AddOrSelectRoi(CanvasRoiTarget.Detection));
            _moveAnchorToolButton = CreatePanelToggleButton("移动中心", (sender, args) => ActivateMoveAnchorTool());
            _deleteRoiButton = CreatePanelToggleButton("删除", (sender, args) => DeleteSelectedRoi());
            _deleteRoiButton.ForeColor = Color.Firebrick;

            _maskToolButton = CreatePanelToggleButton("掩膜", (sender, args) => ToggleMaskDrawTool());
            _maskShapeCombo = CreateCombo(new[] { "矩形区域", "圆形区域", "多边形区域", "画笔涂抹", "画笔恢复" });
            _maskShapeCombo.SelectedIndex = 0;
            _maskVisibleCheck = new CheckBox
            {
                Text = "显示掩膜",
                AutoSize = true,
                Checked = true
            };
            _invertMaskButton = CreateCompactButton("反选", InvertMask);
            _clearMaskButton = CreateCompactButton("清空", ClearMask);

            _modelTypeCombo = CreateCombo(new[] { "形状匹配 (Shape)", "灰度相关 (NCC)" });
            _autoThresholdCheck = new CheckBox
            {
                Text = "自动阈值（自适应对比度与边缘）",
                AutoSize = true,
                Checked = true
            };
            _teachAdvancedToggle = CreateAdvancedToggle();
            _matchAdvancedToggle = CreateAdvancedToggle();
            _levelsInput = CreateIntegerInput(0, 10, 5);
            _matchLevelsInput = CreateIntegerInput(0, 10, 0);
            _angleStartInput = CreateDecimalInput(-180, 180, -15.0m, 1.0m, 2);
            _angleExtentInput = CreateDecimalInput(0.001m, 360, 30.0m, 1.0m, 2);
            _matchAngleStartInput = CreateDecimalInput(-180, 180, -15.0m, 1.0m, 2);
            _matchAngleExtentInput = CreateDecimalInput(0.001m, 360, 30.0m, 1.0m, 2);
            _angleStepInput = CreateDecimalInput(0, 30, 0, 0.1m, 3);
            _optimizationCombo = CreateOptionCombo(OptimizationOptions);
            _metricCombo = CreateOptionCombo(ShapeMetricOptions);
            _contrastInput = CreateDecimalInput(1, 255, 30, 1, 1);
            _minimumContrastInput = CreateDecimalInput(0, 255, 10, 1, 1);
            _edgeContourModeCheck = new CheckBox { Text = "使用外轮廓 XLD 匹配", AutoSize = true };
            _edgeSigmaInput = CreateDecimalInput(0.2m, 20, 1.2m, 0.1m, 2);
            _edgeLowThresholdInput = CreateDecimalInput(1, 254, 20, 1, 1);
            _edgeHighThresholdInput = CreateDecimalInput(2, 255, 45, 1, 1);
            _minimumContourLengthInput = CreateDecimalInput(2, 10000, 9, 1, 1);
            _featurePointSpacingInput = CreateDecimalInput(0.5m, 100, 6, 0.5m, 1);
            _maximumFeaturePointCountInput = CreateIntegerInput(100, 100000, 20000, 100);
            _minimumScoreInput = CreateDecimalInput(0, 1, 0.5m, 0.01m, 3);
            _greedinessInput = CreateDecimalInput(0, 1, 0.85m, 0.01m, 3);
            _maximumOverlapInput = CreateDecimalInput(0, 1, 0.3m, 0.01m, 3);
            _matchCountInput = CreateIntegerInput(0, 100, 1);
            _subPixelCombo = CreateOptionCombo(ShapeSubPixelOptions);
            _timeoutInput = CreateIntegerInput(0, 60000, 0, 50);
            _contourPointSpacingInput = CreateDecimalInput(0.5m, 100, 3, 0.5m, 1);
            _allowPartialCheck = new CheckBox { Text = "允许模板部分超出搜索区域", AutoSize = true };
            _brushRadiusInput = CreateDecimalInput(1, 500, 12, 1, 1);
            _anchorRowInput = CreateDecimalInput(-100000, 100000, 0, 1, 3);
            _anchorColumnInput = CreateDecimalInput(-100000, 100000, 0, 1, 3);
            _anchorLockedCheck = new CheckBox { Text = "锁定基准中心", AutoSize = true };
            _templateLockedCheck = new CheckBox { Text = "锁定模板 ROI", AutoSize = true };
            _searchLockedCheck = new CheckBox { Text = "锁定搜索 ROI", AutoSize = true };

            _extractButton = CreateActionButton("刷新特征", ExtractFeaturesAsync, false);
            _createButton = CreateActionButton("创建模型", CreateModelAsync, true);
            _testButton = CreateActionButton("测试匹配", TestMatchAsync, false);
            _okButton = CreateModernButton("确认", 96, 34, true);
            _cancelButton = CreateModernButton("取消", 88, 34, false);
            _cancelButton.DialogResult = DialogResult.Cancel;
            _operationLog = new TextBox
            {
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                BorderStyle = BorderStyle.FixedSingle,
                Dock = DockStyle.Fill,
                BackColor = Color.White,
                ForeColor = Color.FromArgb(51, 65, 85),
                Font = new Font("Consolas", 8.75f, FontStyle.Regular, GraphicsUnit.Point),
                MinimumSize = new Size(0, 100)
            };
            _testResultLabel = new Label
            {
                AutoSize = false,
                Dock = DockStyle.Top,
                Height = 40,
                Text = "测试结果：尚未测试",
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(10, 0, 10, 0),
                BackColor = Color.FromArgb(241, 245, 249),
                ForeColor = Color.FromArgb(51, 65, 85)
            };
            _modelStateLabel = new Label
            {
                AutoSize = false,
                Height = 34,
                Dock = DockStyle.Top,
                Font = new Font("Microsoft YaHei UI", 8.75f, FontStyle.Bold, GraphicsUnit.Point),
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(10, 0, 10, 0)
            };
            _statusLabel = new ToolStripStatusLabel { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
            _progressBar = new ToolStripProgressBar
            {
                Style = ProgressBarStyle.Marquee,
                MarqueeAnimationSpeed = 30,
                Visible = false,
                Width = 130
            };

            _parameterTabs = new BufferedTabControl { Dock = DockStyle.Fill };
            BuildLayout();
            UiRenderOptimizer.EnableDoubleBufferingRecursive(this);
            WireEvents();
            LoadStateToControls(true);
            SelectTool(CanvasEditTool.Select);
            SetStatus(Service == null
                ? "尚未注入模板服务；可编辑已有模板，提取/建模/测试暂不可用。"
                : _state.TemplateRoi == null
                    ? "默认无 ROI：请点击右侧「添加模板ROI」「添加搜索ROI」「添加检测ROI」按钮添加或切换 ROI。"
                    : "已加载模板 ROI，拖动或调参时将自动实时刷新特征点。", false);
            Shown += HandleFirstShown;
        }

        public event EventHandler EditorStateChanged;

        public event EventHandler ModelCreated;

        public event EventHandler<TemplateTestCompletedEventArgs> TestCompleted;

        public ITemplateEditorService Service { get; set; }

        public TemplateEditorState ResultState { get; private set; }

        public TemplateEditorState CurrentState
        {
            get { return _state.DeepClone(); }
        }

        /// <summary>
        /// 替换当前编辑的模板状态并重置撤销/重做历史。
        /// </summary>
        public void SetState(TemplateEditorState state)
        {
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            CancelCurrentOperation();
            CancelAutoPreview();
            _state = state.DeepClone();
            if (_state.Parameters == null)
            {
                _state.Parameters = new TemplateEditorParameters();
            }
            if (_state.TemplateRoi == null && !_state.HasModel)
            {
                _state.Parameters.AutoThreshold = true;
            }
            EnsureInitialGeometry(_state, _referenceImage.Size, _enableDetectionRoi);
            _originalState = _state.DeepClone();
            _undoStack.Clear();
            _redoStack.Clear();
            LoadStateToControls(true);
            if (_state.TemplateRoi != null && !HasAnyOverlayFeatures(_state))
            {
                RequestAutoFeaturePreview(true);
            }
        }

        /// <summary>
        /// 更新模板示教参考图像（内部持有深拷贝），并按需自适应画布视图。
        /// </summary>
        public void SetReferenceImage(Bitmap image, bool resetView = true)
        {
            if (image == null)
            {
                throw new ArgumentNullException(nameof(image));
            }

            CancelAutoPreview();
            PreviewImageHolder previousHolder = _previewImageHolder;
            _referenceImage = CloneToFastPArgb(image);
            _previewImageHolder = new PreviewImageHolder(_referenceImage, false);
            if (previousHolder != null) previousHolder.Dispose();
            EnsureInitialGeometry(_state, _referenceImage.Size, _enableDetectionRoi);
            _canvas.SetImageOwned(CloneToFastPArgb(_referenceImage));
            if (resetView)
            {
                _canvas.FitImage();
            }
            if (_state.TemplateRoi != null)
            {
                RequestAutoFeaturePreview(true);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                CancelCurrentOperation();
                CancelAutoPreview();
                _autoPreviewTimer.Stop();
                _autoPreviewTimer.Dispose();
                _maskSyncTimer.Stop();
                _maskSyncTimer.Dispose();

                if (_previewImageHolder != null)
                {
                    _previewImageHolder.Dispose();
                    _previewImageHolder = null;
                }
                _referenceImage = null;
            }

            base.Dispose(disposing);
        }

        protected override void OnLoad(EventArgs eventArgs)
        {
            base.OnLoad(eventArgs);
            if (!_initialBoundsConstrained && StartPosition != FormStartPosition.Manual)
            {
                _initialBoundsConstrained = true;
                ConstrainToWorkingArea();
            }

            UiRenderOptimizer.WarmUpTabControlPages(_parameterTabs);
        }

        protected override void OnFormClosing(FormClosingEventArgs eventArgs)
        {
            _closing = true;
            _autoPreviewTimer.Stop();
            _maskSyncTimer.Stop();
            CancelAutoPreview();
            if (_busy && eventArgs.CloseReason == CloseReason.UserClosing)
            {
                CancelCurrentOperation();
            }

            base.OnFormClosing(eventArgs);
        }

        private void HandleFirstShown(object sender, EventArgs eventArgs)
        {
            if (!_initialBoundsConstrained)
            {
                _initialBoundsConstrained = true;
                ConstrainToWorkingArea();
            }

            if (_state.TemplateRoi != null && Service != null && !HasAnyOverlayFeatures(_state))
            {
                RequestAutoFeaturePreview(true);
            }
        }

        private void ConstrainToWorkingArea()
        {
            Screen screen = Screen.FromControl(this);
            Rectangle workingArea = screen.WorkingArea;
            if (workingArea.Width <= 0 || workingArea.Height <= 0)
            {
                return;
            }

            // Shown 阶段已完成 DPI 缩放；联合钳位最小尺寸与窗体尺寸，防止底部操作栏被任务栏遮挡。
            MinimumSize = new Size(
                Math.Max(1, Math.Min(MinimumSize.Width, workingArea.Width)),
                Math.Max(1, Math.Min(MinimumSize.Height, workingArea.Height)));
            int width = Math.Max(MinimumSize.Width, Math.Min(Width, workingArea.Width));
            int height = Math.Max(MinimumSize.Height, Math.Min(Height, workingArea.Height));
            int left = workingArea.Left + Math.Max(0, (workingArea.Width - width) / 2);
            int top = workingArea.Top + Math.Max(0, (workingArea.Height - height) / 2);
            Bounds = new Rectangle(left, top, width, height);
        }

        private void BuildLayout()
        {
            ToolStrip tools = new ToolStrip
            {
                GripStyle = ToolStripGripStyle.Hidden,
                Dock = DockStyle.Top,
                LayoutStyle = ToolStripLayoutStyle.Flow,
                Renderer = new DarkToolStripRenderer(),
                BackColor = Color.FromArgb(15, 23, 42),
                ForeColor = Color.FromArgb(226, 232, 240),
                Padding = new Padding(8, 4, 8, 4)
            };
            tools.Items.Add(CreateToolButton("适应", "使整幅图像适应窗口 (F / 双击空白)", (sender, args) => _canvas.FitImage()));
            tools.Items.Add(CreateToolButton("1:1", "以一个图像像素对应一个屏幕像素显示 (1)", (sender, args) => _canvas.ShowOneToOne()));
            tools.Items.Add(new ToolStripSeparator());
            tools.Items.Add(_undoButton);
            tools.Items.Add(_redoButton);

            SplitContainer mainSplit = new SplitContainer
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(226, 232, 240),
                Size = new Size(1200, 700),
                Orientation = Orientation.Vertical,
                SplitterWidth = 4,
                FixedPanel = FixedPanel.Panel2,
                Panel2MinSize = 410,
                SplitterDistance = 780
            };
            mainSplit.Panel1.BackColor = Color.FromArgb(11, 15, 23);
            mainSplit.Panel1.Controls.Add(_canvas);
            mainSplit.Panel1.Controls.Add(tools);

            BuildParameterTabs();
            // 常驻底部操作栏：无论切换到“示教参数”或“运行参数”页，都可直接刷新特征、创建模型或测试匹配
            Panel persistentActions = new Panel
            {
                Dock = DockStyle.Bottom,
                AutoSize = true,
                BackColor = Color.FromArgb(248, 250, 252),
                Padding = new Padding(0, 6, 0, 2)
            };
            FlowLayoutPanel actionButtons = new FlowLayoutPanel
            {
                AutoSize = true,
                Dock = DockStyle.Bottom,
                WrapContents = true,
                Padding = new Padding(0, 8, 0, 0)
            };
            actionButtons.Controls.Add(_extractButton);
            actionButtons.Controls.Add(_createButton);
            actionButtons.Controls.Add(_testButton);
            persistentActions.Controls.Add(_testResultLabel);
            persistentActions.Controls.Add(actionButtons);

            Panel rightPanel = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(248, 250, 252), Padding = new Padding(10) };
            rightPanel.Controls.Add(_parameterTabs);
            rightPanel.Controls.Add(persistentActions);
            rightPanel.Controls.Add(_modelStateLabel);
            mainSplit.Panel2.BackColor = Color.FromArgb(248, 250, 252);
            mainSplit.Panel2.Controls.Add(rightPanel);

            FlowLayoutPanel footer = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                Height = 54,
                FlowDirection = FlowDirection.RightToLeft,
                WrapContents = false,
                Padding = new Padding(12, 10, 12, 10),
                BackColor = Color.White
            };
            footer.Paint += (s, pe) =>
            {
                using (Pen border = new Pen(Color.FromArgb(226, 232, 240), 1.0f))
                    pe.Graphics.DrawLine(border, 0, 0, footer.Width, 0);
            };
            footer.Controls.Add(_cancelButton);
            footer.Controls.Add(_okButton);

            StatusStrip status = new StatusStrip();
            status.Items.Add(_statusLabel);
            status.Items.Add(_progressBar);

            Controls.Add(mainSplit);
            Controls.Add(footer);
            Controls.Add(status);
            AcceptButton = _okButton;
            CancelButton = _cancelButton;
        }

        private void BuildParameterTabs()
        {
            TabPage teachPage = new TabPage("示教设置");
            Panel teachScroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(4) };
            TableLayoutPanel teachStack = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 1,
                RowCount = 0,
                Padding = new Padding(2)
            };
            teachStack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100.0f));

            // 1. ROI 区域操作卡片（位于高级选项上方）
            TableLayoutPanel roiGrid = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                ColumnCount = 3,
                RowCount = 2
            };
            roiGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33f));
            roiGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33f));
            roiGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.34f));
            roiGrid.RowStyles.Add(new RowStyle(SizeType.Absolute, 36.0f));
            roiGrid.RowStyles.Add(new RowStyle(SizeType.Absolute, 36.0f));
            roiGrid.Controls.Add(_addTemplateRoiButton, 0, 0);
            roiGrid.Controls.Add(_addSearchRoiButton, 1, 0);
            if (_enableDetectionRoi)
            {
                roiGrid.Controls.Add(_addDetectionRoiButton, 2, 0);
            }
            roiGrid.Controls.Add(_moveAnchorToolButton, 0, 1);
            roiGrid.SetColumnSpan(_moveAnchorToolButton, 2);
            roiGrid.Controls.Add(_deleteRoiButton, 2, 1);
            Panel roiGroup = CreateCardSection("ROI 区域（点击添加或切换选中）", roiGrid);
            AddStackSection(teachStack, roiGroup);

            // 2. 模板掩膜操作卡片（紧接 ROI 下方，位于高级选项上方）
            TableLayoutPanel maskGrid = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                ColumnCount = 4,
                RowCount = 2
            };
            maskGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 30.0f));
            maskGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 30.0f));
            maskGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 20.0f));
            maskGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 20.0f));
            maskGrid.RowStyles.Add(new RowStyle(SizeType.Absolute, 34.0f));
            maskGrid.RowStyles.Add(new RowStyle(SizeType.Absolute, 34.0f));
            maskGrid.Controls.Add(_maskToolButton, 0, 0);
            _maskShapeCombo.Margin = new Padding(4, 4, 2, 4);
            maskGrid.Controls.Add(_maskShapeCombo, 1, 0);
            maskGrid.SetColumnSpan(_maskShapeCombo, 3);
            Label radiusLabel = new Label
            {
                Text = "画笔半径",
                AutoSize = true,
                Anchor = AnchorStyles.Left,
                ForeColor = Color.FromArgb(71, 85, 105),
                Margin = new Padding(2, 8, 2, 4)
            };
            _brushRadiusInput.Margin = new Padding(2, 4, 4, 4);
            maskGrid.Controls.Add(radiusLabel, 0, 1);
            maskGrid.Controls.Add(_brushRadiusInput, 1, 1);
            maskGrid.Controls.Add(_invertMaskButton, 2, 1);
            maskGrid.Controls.Add(_clearMaskButton, 3, 1);

            Panel maskInner = new Panel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = Color.White };
            FlowLayoutPanel maskFooter = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                FlowDirection = FlowDirection.LeftToRight,
                Padding = new Padding(2, 4, 2, 0)
            };
            maskFooter.Controls.Add(_maskVisibleCheck);
            maskInner.Controls.Add(maskFooter);
            maskInner.Controls.Add(maskGrid);
            Panel maskGroup = CreateCardSection("模板掩膜", maskInner);
            AddStackSection(teachStack, maskGroup);

            // 3. 基础示教参数
            TableLayoutPanel teachBasicTable = CreateParameterTable();
            teachBasicTable.Padding = new Padding(4, 2, 4, 2);
            AddParameterRow(teachBasicTable, "模型类型", _modelTypeCombo);
            AddParameterRow(teachBasicTable, "起始角度 (°)", _angleStartInput);
            AddParameterRow(teachBasicTable, "角度范围 (°)", _angleExtentInput);
            AddControlRow(teachBasicTable, _autoThresholdCheck);
            AddParameterRow(teachBasicTable, "对比度", _contrastInput);
            AddStackSection(teachStack, teachBasicTable);

            // 4. 单容器原子折叠高级选项（一次性切换 Panel.Visible，彻底消除逐行卡顿闪烁）
            AddStackSection(teachStack, _teachAdvancedToggle);
            TableLayoutPanel teachAdvancedTable = CreateParameterTable();
            teachAdvancedTable.Padding = new Padding(4, 2, 4, 6);
            AddParameterRow(teachAdvancedTable, "金字塔层数 (0=自动)", _levelsInput);
            AddParameterRow(teachAdvancedTable, "角度步长 (°, 0=自动)", _angleStepInput);
            AddParameterRow(teachAdvancedTable, "点云优化方式", _optimizationCombo);
            AddParameterRow(teachAdvancedTable, "匹配极性 (Metric)", _metricCombo);
            AddParameterRow(teachAdvancedTable, "最小对比度", _minimumContrastInput);
            AddControlRow(teachAdvancedTable, _edgeContourModeCheck);
            AddParameterRow(teachAdvancedTable, "边缘 Sigma", _edgeSigmaInput);
            AddParameterRow(teachAdvancedTable, "边缘低阈值", _edgeLowThresholdInput);
            AddParameterRow(teachAdvancedTable, "边缘高阈值", _edgeHighThresholdInput);
            AddParameterRow(teachAdvancedTable, "最小轮廓长度 (px)", _minimumContourLengthInput);
            AddParameterRow(teachAdvancedTable, "特征点间距 (px)", _featurePointSpacingInput);
            AddParameterRow(teachAdvancedTable, "最大特征点数", _maximumFeaturePointCountInput);
            AddControlRow(teachAdvancedTable, _templateLockedCheck);
            AddControlRow(teachAdvancedTable, _searchLockedCheck);
            _teachAdvancedPanel = new Panel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Visible = false
            };
            _teachAdvancedPanel.Controls.Add(teachAdvancedTable);
            AddStackSection(teachStack, _teachAdvancedPanel);
            _teachAdvancedToggle.CheckedChanged += (s, a) => ToggleAdvancedPanel(teachScroll, teachStack, _teachAdvancedToggle, _teachAdvancedPanel);

            teachScroll.Controls.Add(teachStack);
            teachPage.Controls.Add(teachScroll);

            TabPage matchPage = new TabPage("运行参数");
            Panel matchScroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(4) };
            TableLayoutPanel matchStack = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 1,
                RowCount = 0,
                Padding = new Padding(2)
            };
            matchStack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100.0f));
            TableLayoutPanel matchBasicTable = CreateParameterTable();
            matchBasicTable.Padding = new Padding(4, 2, 4, 2);
            AddParameterRow(matchBasicTable, "最低分数", _minimumScoreInput);
            AddParameterRow(matchBasicTable, "测试返回上限 (0=全部)", _matchCountInput);
            AddParameterRow(matchBasicTable, "搜索起始角度 (°)", _matchAngleStartInput);
            AddParameterRow(matchBasicTable, "搜索角度范围 (°)", _matchAngleExtentInput);
            AddStackSection(matchStack, matchBasicTable);
            AddStackSection(matchStack, _matchAdvancedToggle);
            TableLayoutPanel matchAdvancedTable = CreateParameterTable();
            matchAdvancedTable.Padding = new Padding(4, 2, 4, 6);
            AddParameterRow(matchAdvancedTable, "搜索金字塔层数 (0=自动)", _matchLevelsInput);
            AddParameterRow(matchAdvancedTable, "贪婪度 (Greediness)", _greedinessInput);
            AddParameterRow(matchAdvancedTable, "最大重叠度", _maximumOverlapInput);
            AddParameterRow(matchAdvancedTable, "亚像素精度", _subPixelCombo);
            AddParameterRow(matchAdvancedTable, "超时 (ms, 0=不限)", _timeoutInput);
            AddParameterRow(matchAdvancedTable, "轮廓采点间距 (px)", _contourPointSpacingInput);
            AddControlRow(matchAdvancedTable, _allowPartialCheck);
            _matchAdvancedPanel = new Panel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Visible = false
            };
            _matchAdvancedPanel.Controls.Add(matchAdvancedTable);
            AddStackSection(matchStack, _matchAdvancedPanel);
            _matchAdvancedToggle.CheckedChanged += (s, a) => ToggleAdvancedPanel(matchScroll, matchStack, _matchAdvancedToggle, _matchAdvancedPanel);
            matchScroll.Controls.Add(matchStack);
            matchPage.Controls.Add(matchScroll);

            TabPage anchorPage = new TabPage("基准中心");
            TableLayoutPanel anchorTable = CreateParameterTable();
            AddParameterRow(anchorTable, "行坐标 Row (px)", _anchorRowInput);
            AddParameterRow(anchorTable, "列坐标 Col (px)", _anchorColumnInput);
            anchorTable.Controls.Add(_anchorLockedCheck, 0, anchorTable.RowCount);
            anchorTable.SetColumnSpan(_anchorLockedCheck, 2);
            anchorTable.RowCount++;
            Button resetAnchor = CreateModernButton("重置为模板 ROI 中心", 180, 32, false);
            resetAnchor.Click += ResetAnchor;
            anchorTable.Controls.Add(resetAnchor, 0, anchorTable.RowCount);
            anchorTable.SetColumnSpan(resetAnchor, 2);
            anchorTable.RowCount++;
            Label anchorHelp = new Label
            {
                AutoSize = true,
                MaximumSize = new Size(320, 0),
                ForeColor = Color.DimGray,
                Text = "解锁后可拖动绿色原点，或用方向键微调；Shift + 方向键每次移动 10 px。中心是上位机获得的定位基准，不要求等于 HALCON 模型域中心。",
                Padding = new Padding(0, 12, 0, 0)
            };
            anchorTable.Controls.Add(anchorHelp, 0, anchorTable.RowCount);
            anchorTable.SetColumnSpan(anchorHelp, 2);
            anchorTable.RowCount++;
            anchorPage.Controls.Add(WrapScrollable(anchorTable));

            TabPage operationsPage = new TabPage("操作日志");
            TableLayoutPanel operationLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 2,
                Padding = new Padding(8)
            };
            operationLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            operationLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100.0f));
            Label flowHelp = new Label
            {
                Text = "推荐流程：点击「添加模板ROI」→ 自动显示特征 → 框选/涂抹掩膜或调整「移动中心」→ 测试匹配或确认。",
                AutoSize = true,
                ForeColor = Color.DimGray,
                Padding = new Padding(0, 2, 0, 8)
            };
            operationLayout.Controls.Add(flowHelp, 0, 0);
            operationLayout.Controls.Add(_operationLog, 0, 1);
            operationsPage.Controls.Add(operationLayout);

            _parameterTabs.TabPages.Add(teachPage);
            _parameterTabs.TabPages.Add(matchPage);
            _parameterTabs.TabPages.Add(anchorPage);
            _parameterTabs.TabPages.Add(operationsPage);
        }

        private void WireEvents()
        {
            _canvas.EditTransactionStarted += (sender, args) => PushUndoState();
            _canvas.RoiChanged += CanvasRoiChanged;
            _canvas.AnchorChanged += CanvasAnchorChanged;
            _canvas.MaskStrokeCreated += CanvasMaskStrokeCreated;
            _canvas.MaskPreviewChanged += CanvasMaskPreviewChanged;
            _canvas.BrushRadiusChangedByCanvas += CanvasBrushRadiusChanged;
            _canvas.MouseUp += CanvasMouseUpForContextMenu;
            _canvas.SelectedRoiChanged += (sender, args) =>
            {
                UpdateRoiToolSelection();
                UpdateRoiToolbarButtons();
            };
            _canvas.ToolChanged += (sender, args) => SyncToolButtonCheckedState(_canvas.Tool);

            _modelTypeCombo.SelectedIndexChanged += ModelTypeChanged;
            _autoThresholdCheck.CheckedChanged += ShapeFeatureParameterChanged;
            _levelsInput.ValueChanged += StructuralParameterChanged;
            _angleStartInput.ValueChanged += StructuralParameterChanged;
            _angleExtentInput.ValueChanged += StructuralParameterChanged;
            _angleStepInput.ValueChanged += StructuralParameterChanged;
            _optimizationCombo.SelectedIndexChanged += StructuralParameterChanged;
            _metricCombo.SelectedIndexChanged += StructuralParameterChanged;
            _contrastInput.ValueChanged += StructuralParameterChanged;
            _minimumContrastInput.ValueChanged += StructuralParameterChanged;
            _edgeContourModeCheck.CheckedChanged += ShapeFeatureParameterChanged;
            _edgeSigmaInput.ValueChanged += StructuralParameterChanged;
            _edgeLowThresholdInput.ValueChanged += StructuralParameterChanged;
            _edgeHighThresholdInput.ValueChanged += StructuralParameterChanged;
            _minimumContourLengthInput.ValueChanged += StructuralParameterChanged;
            _featurePointSpacingInput.ValueChanged += StructuralParameterChanged;
            _maximumFeaturePointCountInput.ValueChanged += StructuralParameterChanged;

            _minimumScoreInput.ValueChanged += RuntimeParameterChanged;
            _matchLevelsInput.ValueChanged += RuntimeParameterChanged;
            _matchAngleStartInput.ValueChanged += RuntimeParameterChanged;
            _matchAngleExtentInput.ValueChanged += RuntimeParameterChanged;
            _greedinessInput.ValueChanged += RuntimeParameterChanged;
            _maximumOverlapInput.ValueChanged += RuntimeParameterChanged;
            _matchCountInput.ValueChanged += RuntimeParameterChanged;
            _subPixelCombo.SelectedIndexChanged += RuntimeParameterChanged;
            _timeoutInput.ValueChanged += RuntimeParameterChanged;
            _contourPointSpacingInput.ValueChanged += DisplayParameterChanged;
            _allowPartialCheck.CheckedChanged += RuntimeParameterChanged;
            _brushRadiusInput.ValueChanged += BrushRadiusChanged;
            _maskShapeCombo.SelectedIndexChanged += MaskShapeComboChanged;
            _maskVisibleCheck.CheckedChanged += ToggleMaskVisibility;

            _anchorRowInput.ValueChanged += AnchorNumericChanged;
            _anchorColumnInput.ValueChanged += AnchorNumericChanged;
            _anchorLockedCheck.CheckedChanged += LockSettingsChanged;
            _templateLockedCheck.CheckedChanged += LockSettingsChanged;
            _searchLockedCheck.CheckedChanged += LockSettingsChanged;
            _okButton.Click += ConfirmEditor;
            FormClosed += (sender, args) => CancelCurrentOperation();
            KeyDown += EditorKeyDown;
        }

        private void LoadStateToControls(bool resetImage)
        {
            _loadingControls = true;
            try
            {
                TemplateEditorParameters parameters = _state.Parameters ?? new TemplateEditorParameters();
                _state.Parameters = parameters;
                _modelTypeCombo.SelectedIndex = parameters.ModelType == TemplateModelType.Shape ? 0 : 1;
                UpdateSubPixelOptions(parameters.SubPixel);
                SetNumeric(_levelsInput, parameters.NumLevels);
                SetNumeric(_matchLevelsInput, parameters.MatchNumLevels);
                SetNumeric(_angleStartInput, parameters.AngleStartDegrees);
                SetNumeric(_angleExtentInput, parameters.AngleExtentDegrees);
                SetNumeric(_matchAngleStartInput, parameters.MatchAngleStartDegrees);
                SetNumeric(_matchAngleExtentInput, parameters.MatchAngleExtentDegrees);
                SetNumeric(_angleStepInput, parameters.AngleStepDegrees);
                SelectComboValue(_optimizationCombo, parameters.Optimization);
                NormalizeStoredMetrics(parameters);
                parameters.Metric = ResolveActiveMetric(parameters);
                UpdateMetricOptions(parameters.ModelType == TemplateModelType.Ncc, parameters.Metric);
                _autoThresholdCheck.Checked = parameters.AutoThreshold;
                SetNumeric(_contrastInput, parameters.Contrast);
                SetNumeric(_minimumContrastInput, parameters.MinimumContrast);
                _edgeContourModeCheck.Checked = parameters.FeatureExtractionMode == ShapeFeatureExtractionMode.EdgeContours;
                SetNumeric(_edgeSigmaInput, parameters.EdgeSigma);
                SetNumeric(_edgeLowThresholdInput, parameters.EdgeLowThreshold);
                SetNumeric(_edgeHighThresholdInput, parameters.EdgeHighThreshold);
                SetNumeric(_minimumContourLengthInput, parameters.MinimumContourLengthPixels);
                SetNumeric(_featurePointSpacingInput, parameters.FeaturePointSpacingPixels);
                SetNumeric(_maximumFeaturePointCountInput, parameters.MaximumFeaturePointCount);
                SetNumeric(_minimumScoreInput, parameters.MinimumScore);
                SetNumeric(_greedinessInput, parameters.Greediness);
                SetNumeric(_maximumOverlapInput, parameters.MaximumOverlap);
                SetNumeric(_matchCountInput, parameters.MatchCount);
                SetNumeric(_timeoutInput, parameters.TimeoutMilliseconds);
                SetNumeric(_contourPointSpacingInput, parameters.ContourPointSpacingPixels > 0.0
                    ? parameters.ContourPointSpacingPixels
                    : 3.0);
                _allowPartialCheck.Checked = parameters.AllowPartialMatch;
                SetNumeric(_brushRadiusInput, parameters.BrushRadius);
                SetNumeric(_anchorRowInput, _state.ReferenceAnchor.Row);
                SetNumeric(_anchorColumnInput, _state.ReferenceAnchor.Column);
                _anchorLockedCheck.Checked = _state.AnchorLocked;
                _templateLockedCheck.Checked = _state.TemplateRoiLocked;
                _searchLockedCheck.Checked = _state.SearchRoiLocked;
                if (resetImage)
                {
                    _canvas.SetImageOwned(CloneToFastPArgb(_referenceImage));
                }
                _canvas.TemplateRoi = _state.TemplateRoi;
                _canvas.SearchRoi = _state.SearchRoi;
                _canvas.DetectionRoi = _enableDetectionRoi ? _state.DetectionRoi : null;
                _canvas.ReferenceAnchor = _state.ReferenceAnchor;
                _canvas.AnchorLocked = _state.AnchorLocked;
                _canvas.TemplateRoiLocked = _state.TemplateRoiLocked;
                _canvas.SearchRoiLocked = _state.SearchRoiLocked;
                _canvas.MaskVisible = _state.MaskVisible;
                _canvas.BrushRadiusImage = parameters.BrushRadius;
                _canvas.SetMaskStrokes(_state.MaskStrokes);
                _maskVisibleCheck.Checked = _state.MaskVisible;
                SyncUnmaskedBaseIfApplicable();
                UpdateEditorOverlay();
                UpdateModelState();
                UpdateHistoryButtons();
                UpdateAnchorEnabledState();
                UpdateShapeFeatureEnabledState();
                UpdateRoiToolbarButtons();
            }
            finally
            {
                _loadingControls = false;
            }
        }

        private void CanvasRoiChanged(object sender, RoiChangedEventArgs eventArgs)
        {
            if (eventArgs.Target == CanvasRoiTarget.Template)
            {
                _state.TemplateRoi = eventArgs.Roi == null ? null : eventArgs.Roi.DeepClone();
                _canvas.ClearRuntimeOverlay();
                if (_state.TemplateRoi == null)
                {
                    _state.ModelContours = new List<List<ImageCoordinate>>();
                    _state.FeaturePoints = new List<ImageCoordinate>();
                    _unmaskedBaseContours = new List<List<ImageCoordinate>>();
                    _unmaskedBaseRoi = null;
                    _state.ModelData = null;
                    UpdateEditorOverlay();
                    MarkChanged(true);
                    UpdateRoiToolbarButtons();
                    return;
                }

                if (!_state.AnchorLocked && _state.ReferenceAnchor.Row == 0.0 && _state.ReferenceAnchor.Column == 0.0)
                {
                    SetReferenceAnchorInternal(new ImageCoordinate(
                        _state.TemplateRoi.CenterRow,
                        _state.TemplateRoi.CenterColumn));
                }

                if (Service == null)
                {
                    // 无后台服务模式（如离线编辑或单元测试）：仅在拖动时清空旧轮廓并在结束时标脏
                    if ((_state.ModelContours != null && _state.ModelContours.Count > 0) ||
                        (_state.FeaturePoints != null && _state.FeaturePoints.Count > 0))
                    {
                        _state.ModelContours = new List<List<ImageCoordinate>>();
                        _state.FeaturePoints = new List<ImageCoordinate>();
                        UpdateEditorOverlay();
                    }
                    if (eventArgs.IsFinal) MarkChanged(true);
                }
                else if (eventArgs.IsFinal)
                {
                    UpdateFilteredFeatureOverlay(_state.TemplateRoi);
                    MarkChanged(true);
                    RequestAutoFeaturePreview(true);
                }
                else
                {
                    // 拖拽期间先按新 ROI 多边形即时裁剪已有特征点消除拖影，同时 25ms 节流异步刷新 HALCON 真值
                    UpdateFilteredFeatureOverlay(_state.TemplateRoi);
                    RequestAutoFeaturePreview(false);
                }
                UpdateRoiToolbarButtons();
            }
            else if (eventArgs.Target == CanvasRoiTarget.Search)
            {
                _state.SearchRoi = eventArgs.Roi == null ? null : eventArgs.Roi.DeepClone();
                if (eventArgs.IsFinal)
                {
                    MarkChanged(false);
                }
                UpdateRoiToolbarButtons();
            }
            else if (eventArgs.Target == CanvasRoiTarget.Detection && _enableDetectionRoi)
            {
                _state.DetectionRoi = eventArgs.Roi == null ? null : eventArgs.Roi.DeepClone();
                if (eventArgs.IsFinal)
                {
                    MarkChanged(false);
                }
                UpdateRoiToolbarButtons();
            }
        }

        private void CanvasAnchorChanged(object sender, AnchorChangedEventArgs eventArgs)
        {
            _state.ReferenceAnchor = eventArgs.Anchor;
            _loadingControls = true;
            try
            {
                SetNumeric(_anchorRowInput, eventArgs.Anchor.Row);
                SetNumeric(_anchorColumnInput, eventArgs.Anchor.Column);
            }
            finally
            {
                _loadingControls = false;
            }

            if (eventArgs.IsFinal)
            {
                MarkChanged(false);
            }
        }

        private void CanvasMaskStrokeCreated(object sender, MaskStrokeEventArgs eventArgs)
        {
            MaskStroke stroke = eventArgs.Stroke;
            _state.MaskStrokes.Add(stroke);
            // 新增擦除掩膜且未反选时，直接对当前已可见特征点做 O(P_vis) 单块增量剔除，
            // 无论历史累计多少画笔/区域掩膜均保持 <1ms 恒定耗时，彻底消除掩膜越多越卡的问题。
            if (stroke != null && stroke.IsValid && stroke.IsErase && !_state.MaskInverted &&
                _committedVisiblePoints != null)
            {
                if (_committedVisiblePoints.Count > 0)
                {
                    ApplyIncrementalEraseMask(stroke);
                }
                else
                {
                    _canvas.Invalidate();
                }
            }
            else
            {
                ApplyInstantMaskFilter(null);
            }
            MarkChanged(true);
            ScheduleDebouncedMaskSync();
        }

        private void ApplyIncrementalEraseMask(MaskStroke eraseMask)
        {
            double minRow, maxRow, minCol, maxCol;
            if (!eraseMask.TryGetBounds(out minRow, out maxRow, out minCol, out maxCol))
            {
                return;
            }

            CanvasRuntimeOverlay overlay = _canvas.EditorOverlay ?? new CanvasRuntimeOverlay();
            List<CanvasPointOverlay> overlayPoints = overlay.Points;
            overlayPoints.Clear();
            int write = 0;
            for (int i = 0; i < _committedVisiblePoints.Count; i++)
            {
                ImageCoordinate pt = _committedVisiblePoints[i];
                if (pt.Row >= minRow && pt.Row <= maxRow && pt.Column >= minCol && pt.Column <= maxCol &&
                    MaskGeometry.IsPointInsideMask(pt, eraseMask))
                {
                    continue;
                }
                _committedVisiblePoints[write] = pt;
                overlayPoints.Add(AcquireOverlayPoint(write, pt, Color.Lime));
                write++;
            }
            if (write < _committedVisiblePoints.Count)
            {
                _committedVisiblePoints.RemoveRange(write, _committedVisiblePoints.Count - write);
            }
            _canvas.Invalidate();
        }

        private void CanvasMaskPreviewChanged(object sender, MaskPreviewEventArgs eventArgs)
        {
            if (!CanUpdateUi || _state.TemplateRoi == null) return;
            if (eventArgs == null)
            {
                ApplyInstantMaskFilter(null);
                return;
            }

            // 擦除画笔拖动过程中增量隐藏当前画笔段覆盖的特征点（微秒级 0 延迟）
            if (eventArgs.IsIncrementalEraseSegment && !_state.MaskInverted &&
                eventArgs.ActiveMask != null && _canvas.EditorOverlay != null &&
                _canvas.EditorOverlay.Points != null)
            {
                List<CanvasPointOverlay> points = _canvas.EditorOverlay.Points;
                if (points.Count == 0 && (_committedVisiblePoints == null || _committedVisiblePoints.Count == 0))
                {
                    return;
                }
                if (points.Count > 0)
                {
                    double radius = eventArgs.ActiveMask.Radius;
                    double minRow = Math.Min(eventArgs.SegmentStart.Row, eventArgs.SegmentEnd.Row) - radius;
                    double maxRow = Math.Max(eventArgs.SegmentStart.Row, eventArgs.SegmentEnd.Row) + radius;
                    double minCol = Math.Min(eventArgs.SegmentStart.Column, eventArgs.SegmentEnd.Column) - radius;
                    double maxCol = Math.Max(eventArgs.SegmentStart.Column, eventArgs.SegmentEnd.Column) + radius;
                    int write = 0;
                    for (int i = 0; i < points.Count; i++)
                    {
                        CanvasPointOverlay item = points[i];
                        ImageCoordinate p = item.Position;
                        if (p.Row >= minRow && p.Row <= maxRow && p.Column >= minCol && p.Column <= maxCol &&
                            MaskGeometry.IsPointNearSegment(p, eventArgs.SegmentStart, eventArgs.SegmentEnd, radius))
                        {
                            continue;
                        }
                        points[write++] = item;
                    }
                    if (write < points.Count)
                    {
                        points.RemoveRange(write, points.Count - write);
                    }
                    return;
                }
            }

            // 矩形/圆形/多边形区域掩膜预览期间：按 33ms 节流并仅用单块 activeMask 过滤已提交可见点集，
            // 原地复用点对象池避免堆分配，确保多掩膜叠加拖拽零卡顿。
            if (eventArgs.ActiveMask != null &&
                (eventArgs.ActiveMask.Kind == MaskShapeKind.Rectangle ||
                 eventArgs.ActiveMask.Kind == MaskShapeKind.Circle ||
                 eventArgs.ActiveMask.Kind == MaskShapeKind.Polygon) &&
                !_state.MaskInverted && _committedVisiblePoints != null)
            {
                if (_committedVisiblePoints.Count == 0)
                {
                    return;
                }
                int now = Environment.TickCount;
                if (eventArgs.ActiveMask.Kind != MaskShapeKind.Polygon &&
                    unchecked(now - _lastMaskPreviewFilterTick) < 33)
                {
                    return;
                }
                _lastMaskPreviewFilterTick = now;
                FilterCommittedPointsBySingleMask(eventArgs.ActiveMask);
                return;
            }

            // 恢复画笔或反选掩膜拖动期间同样按 33ms 节流，防止高频 mousemove 堆积 UI 线程
            if (eventArgs.ActiveMask != null && eventArgs.ActiveMask.Kind == MaskShapeKind.Brush)
            {
                int now = Environment.TickCount;
                if (unchecked(now - _lastMaskPreviewFilterTick) < 33)
                {
                    return;
                }
                _lastMaskPreviewFilterTick = now;
            }

            ApplyInstantMaskFilter(eventArgs.ActiveMask);
        }

        private void FilterCommittedPointsBySingleMask(MaskStroke activeMask)
        {
            double minRow, maxRow, minCol, maxCol;
            if (activeMask == null || !activeMask.TryGetBounds(out minRow, out maxRow, out minCol, out maxCol))
            {
                return;
            }

            CanvasRuntimeOverlay overlay = _canvas.EditorOverlay ?? new CanvasRuntimeOverlay();
            List<CanvasPointOverlay> overlayPoints = overlay.Points;
            overlayPoints.Clear();
            int poolIndex = 0;
            for (int i = 0; i < _committedVisiblePoints.Count; i++)
            {
                ImageCoordinate pt = _committedVisiblePoints[i];
                if (pt.Row >= minRow && pt.Row <= maxRow && pt.Column >= minCol && pt.Column <= maxCol &&
                    MaskGeometry.IsPointInsideMask(pt, activeMask))
                {
                    continue;
                }
                overlayPoints.Add(AcquireOverlayPoint(poolIndex++, pt, Color.Lime));
            }
            if (!ReferenceEquals(_canvas.EditorOverlay, overlay))
            {
                _canvas.EditorOverlay = overlay;
            }
        }

        private void ApplyInstantMaskFilter(MaskStroke activePreview)
        {
            if (_state.TemplateRoi == null) return;
            IEnumerable<List<ImageCoordinate>> sourceContours = GetMaskFilterSourceContours();
            if (sourceContours == null) return;

            CanvasRuntimeOverlay overlay = _canvas.EditorOverlay ?? new CanvasRuntimeOverlay();
            List<CanvasPointOverlay> overlayPoints = overlay.Points;
            overlayPoints.Clear();
            bool hasMasks = _state.MaskInverted ||
                            (_state.MaskStrokes != null && _state.MaskStrokes.Count > 0) ||
                            (activePreview != null && activePreview.IsValid);
            List<ImageCoordinate> committed = activePreview == null ? new List<ImageCoordinate>() : null;
            int poolIndex = 0;
            foreach (List<ImageCoordinate> contour in sourceContours)
            {
                if (contour == null) continue;
                for (int i = 0; i < contour.Count; i++)
                {
                    ImageCoordinate point = contour[i];
                    if (!IsFinite(point.Row) || !IsFinite(point.Column)) continue;
                    if (hasMasks && MaskGeometry.IsPointExcluded(point, _state.MaskStrokes, _state.MaskInverted, activePreview))
                    {
                        continue;
                    }
                    if (committed != null) committed.Add(point);
                    overlayPoints.Add(AcquireOverlayPoint(poolIndex++, point, Color.Lime));
                }
            }
            if (committed != null)
            {
                _committedVisiblePoints = committed;
            }
            if (!ReferenceEquals(_canvas.EditorOverlay, overlay))
            {
                _canvas.EditorOverlay = overlay;
            }
            else
            {
                _canvas.Invalidate();
            }
        }

        private CanvasPointOverlay AcquireOverlayPoint(int index, ImageCoordinate position, Color color)
        {
            while (_overlayPointPool.Count <= index)
            {
                _overlayPointPool.Add(new CanvasPointOverlay { RadiusPixels = 1.5f, Color = color });
            }
            CanvasPointOverlay item = _overlayPointPool[index];
            item.Position = position;
            item.Color = color;
            item.Label = null;
            item.IsSelected = false;
            item.RadiusPixels = 1.5f;
            return item;
        }

        private IEnumerable<List<ImageCoordinate>> GetMaskFilterSourceContours()
        {
            if (_unmaskedBaseContours != null && _unmaskedBaseContours.Count > 0 &&
                SameRoiGeometry(_unmaskedBaseRoi, _state.TemplateRoi))
            {
                return _unmaskedBaseContours;
            }
            if (_state.ModelContours != null && _state.ModelContours.Any(item => item != null && item.Count > 0))
            {
                return _state.ModelContours;
            }
            return _state.FeaturePoints != null && _state.FeaturePoints.Count > 0
                ? new[] { _state.FeaturePoints }
                : null;
        }

        private void SyncUnmaskedBaseIfApplicable()
        {
            bool noMasks = !_state.MaskInverted && (_state.MaskStrokes == null || _state.MaskStrokes.Count == 0);
            if (!noMasks) return;
            if (_state.ModelContours != null && _state.ModelContours.Any(item => item != null && item.Count > 0))
            {
                _unmaskedBaseContours = _state.ModelContours.Where(c => c != null).Select(c => new List<ImageCoordinate>(c)).ToList();
                _unmaskedBaseRoi = _state.TemplateRoi == null ? null : _state.TemplateRoi.DeepClone();
            }
            else if (_state.FeaturePoints != null && _state.FeaturePoints.Count > 0)
            {
                _unmaskedBaseContours = new List<List<ImageCoordinate>> { new List<ImageCoordinate>(_state.FeaturePoints) };
                _unmaskedBaseRoi = _state.TemplateRoi == null ? null : _state.TemplateRoi.DeepClone();
            }
        }

        private static bool SameRoiGeometry(RotatedRectangle a, RotatedRectangle b)
        {
            if (a == null || b == null) return false;
            return Math.Abs(a.CenterRow - b.CenterRow) < 0.01 &&
                   Math.Abs(a.CenterColumn - b.CenterColumn) < 0.01 &&
                   Math.Abs(a.HalfWidth - b.HalfWidth) < 0.01 &&
                   Math.Abs(a.HalfHeight - b.HalfHeight) < 0.01 &&
                   Math.Abs(a.AngleDegrees - b.AngleDegrees) < 0.01;
        }

        private void StructuralParameterChanged(object sender, EventArgs eventArgs)
        {
            if (_loadingControls)
            {
                return;
            }

            PushUndoState();
            ReadParametersFromControls();
            MarkChanged(true);
            RequestAutoFeaturePreview(false);
        }

        private void ModelTypeChanged(object sender, EventArgs eventArgs)
        {
            StructuralParameterChanged(sender, eventArgs);
            UpdateShapeFeatureEnabledState();
            UpdateSubPixelOptions(null);
        }

        private void UpdateSubPixelOptions(string preferredValue)
        {
            string selected = string.IsNullOrWhiteSpace(preferredValue)
                ? GetComboValue(_subPixelCombo, "interpolation")
                : preferredValue;
            ComboOption[] options = _modelTypeCombo.SelectedIndex == 1
                ? NccSubPixelOptions
                : ShapeSubPixelOptions;

            bool prevLoading = _loadingControls;
            _loadingControls = true;
            _subPixelCombo.BeginUpdate();
            try
            {
                _subPixelCombo.Items.Clear();
                _subPixelCombo.Items.AddRange(options);
                string normalized = _modelTypeCombo.SelectedIndex == 1 &&
                    !string.Equals(selected, "none", StringComparison.OrdinalIgnoreCase)
                    ? "interpolation"
                    : selected;
                SelectComboValue(_subPixelCombo, normalized);
            }
            finally
            {
                _subPixelCombo.EndUpdate();
                _loadingControls = prevLoading;
            }
        }

        private void UpdateMetricOptions(bool isNcc, string preferredMetric)
        {
            string selected = string.IsNullOrWhiteSpace(preferredMetric)
                ? GetComboValue(_metricCombo, DefaultMetric)
                : preferredMetric;
            ComboOption[] options = isNcc ? NccMetricOptions : ShapeMetricOptions;

            bool prevLoading = _loadingControls;
            _loadingControls = true;
            _metricCombo.BeginUpdate();
            try
            {
                _metricCombo.Items.Clear();
                _metricCombo.Items.AddRange(options);
                string normalized = isNcc &&
                    !string.Equals(selected, "ignore_global_polarity", StringComparison.OrdinalIgnoreCase)
                    ? DefaultMetric
                    : selected;
                SelectComboValue(_metricCombo, normalized);
            }
            finally
            {
                _metricCombo.EndUpdate();
                _loadingControls = prevLoading;
            }
        }

        private void ShapeFeatureParameterChanged(object sender, EventArgs eventArgs)
        {
            StructuralParameterChanged(sender, eventArgs);
            UpdateShapeFeatureEnabledState();
        }

        private void RuntimeParameterChanged(object sender, EventArgs eventArgs)
        {
            if (_loadingControls)
            {
                return;
            }

            PushUndoState();
            ReadParametersFromControls();
            MarkChanged(false);
        }

        private void BrushRadiusChanged(object sender, EventArgs eventArgs)
        {
            if (_loadingControls)
            {
                return;
            }

            _state.Parameters.BrushRadius = (double)_brushRadiusInput.Value;
            _canvas.BrushRadiusImage = _state.Parameters.BrushRadius;
        }

        private void CanvasBrushRadiusChanged(object sender, EventArgs eventArgs)
        {
            _state.Parameters.BrushRadius = _canvas.BrushRadiusImage;
            bool prev = _loadingControls;
            _loadingControls = true;
            try
            {
                SetNumeric(_brushRadiusInput, _state.Parameters.BrushRadius);
            }
            finally { _loadingControls = prev; }
        }

        private void MaskShapeComboChanged(object sender, EventArgs eventArgs)
        {
            if (_loadingControls) return;
            SelectTool(GetSelectedMaskTool());
        }

        private void ToggleMaskDrawTool()
        {
            if (IsMaskEditTool(_canvas.Tool))
            {
                SelectTool(CanvasEditTool.Select);
            }
            else
            {
                SelectTool(GetSelectedMaskTool());
            }
        }

        private CanvasEditTool GetSelectedMaskTool()
        {
            switch (_maskShapeCombo.SelectedIndex)
            {
                case 1: return CanvasEditTool.MaskCircle;
                case 2: return CanvasEditTool.MaskPolygon;
                case 3: return CanvasEditTool.EraseMask;
                case 4: return CanvasEditTool.RestoreMask;
                default: return CanvasEditTool.MaskRectangle;
            }
        }

        private static bool IsMaskEditTool(CanvasEditTool tool)
        {
            return tool == CanvasEditTool.MaskRectangle ||
                   tool == CanvasEditTool.MaskCircle ||
                   tool == CanvasEditTool.MaskPolygon ||
                   tool == CanvasEditTool.EraseMask ||
                   tool == CanvasEditTool.RestoreMask;
        }

        private void CanvasMouseUpForContextMenu(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Right || _busy || _canvas.Tool == CanvasEditTool.MaskPolygon)
            {
                return;
            }

            int hitIndex = _canvas.HitTestMaskIndex(e.Location);
            if (hitIndex < 0 || _state.MaskStrokes == null || hitIndex >= _state.MaskStrokes.Count)
            {
                return;
            }

            ContextMenuStrip menu = new ContextMenuStrip();
            ToolStripMenuItem deleteOne = new ToolStripMenuItem("删除此掩膜区域");
            deleteOne.Click += (s, args) =>
            {
                if (hitIndex >= 0 && hitIndex < _state.MaskStrokes.Count)
                {
                    PushUndoState();
                    _state.MaskStrokes.RemoveAt(hitIndex);
                    _canvas.SetMaskStrokes(_state.MaskStrokes);
                    ApplyInstantMaskFilter(null);
                    MarkChanged(true);
                    ScheduleDebouncedMaskSync();
                    SetStatus("已删除选中掩膜区域。", false);
                }
            };
            ToolStripMenuItem clearAll = new ToolStripMenuItem("清除全部掩膜");
            clearAll.Click += (s, args) => ClearMask(this, EventArgs.Empty);
            menu.Items.Add(deleteOne);
            menu.Items.Add(clearAll);
            menu.Closed += (s, args) => menu.Dispose();
            menu.Show(_canvas, e.Location);
        }

        private void DisplayParameterChanged(object sender, EventArgs eventArgs)
        {
            if (_loadingControls)
            {
                return;
            }

            PushUndoState();
            _state.Parameters.ContourPointSpacingPixels = (double)_contourPointSpacingInput.Value;
            RaiseEditorStateChanged();
        }

        private void AnchorNumericChanged(object sender, EventArgs eventArgs)
        {
            if (_loadingControls || _state.AnchorLocked)
            {
                return;
            }

            PushUndoState();
            _state.ReferenceAnchor = new ImageCoordinate(
                (double)_anchorRowInput.Value,
                (double)_anchorColumnInput.Value);
            _canvas.ReferenceAnchor = _state.ReferenceAnchor;
            MarkChanged(false);
        }

        private void LockSettingsChanged(object sender, EventArgs eventArgs)
        {
            if (_loadingControls)
            {
                return;
            }

            _state.AnchorLocked = _anchorLockedCheck.Checked;
            _state.TemplateRoiLocked = _templateLockedCheck.Checked;
            _state.SearchRoiLocked = _searchLockedCheck.Checked;
            _canvas.AnchorLocked = _state.AnchorLocked;
            _canvas.TemplateRoiLocked = _state.TemplateRoiLocked;
            _canvas.SearchRoiLocked = _state.SearchRoiLocked;
            UpdateAnchorEnabledState();
            _canvas.Invalidate();
            RaiseEditorStateChanged();
        }

        private void ReadParametersFromControls()
        {
            TemplateEditorParameters parameters = _state.Parameters ?? new TemplateEditorParameters();
            NormalizeStoredMetrics(parameters);
            string selectedMetric = NormalizeMetric(GetComboValue(_metricCombo, DefaultMetric));
            if (parameters.ModelType == TemplateModelType.Ncc)
            {
                parameters.NccMetric = string.Equals(selectedMetric, "ignore_global_polarity", StringComparison.OrdinalIgnoreCase)
                    ? "ignore_global_polarity"
                    : DefaultMetric;
            }
            else if (parameters.FeatureExtractionMode != ShapeFeatureExtractionMode.EdgeContours)
            {
                parameters.ShapeMetric = selectedMetric;
            }

            parameters.ModelType = _modelTypeCombo.SelectedIndex == 1 ? TemplateModelType.Ncc : TemplateModelType.Shape;
            parameters.AutoThreshold = _autoThresholdCheck.Checked;
            parameters.NumLevels = (int)_levelsInput.Value;
            parameters.MatchNumLevels = (int)_matchLevelsInput.Value;
            parameters.AngleStartDegrees = (double)_angleStartInput.Value;
            parameters.AngleExtentDegrees = (double)_angleExtentInput.Value;
            parameters.MatchAngleStartDegrees = (double)_matchAngleStartInput.Value;
            parameters.MatchAngleExtentDegrees = (double)_matchAngleExtentInput.Value;
            parameters.AngleStepDegrees = (double)_angleStepInput.Value;
            parameters.Optimization = GetComboValue(_optimizationCombo, "auto");
            parameters.Contrast = (double)_contrastInput.Value;
            parameters.MinimumContrast = (double)_minimumContrastInput.Value;
            parameters.FeatureExtractionMode = _edgeContourModeCheck.Checked
                ? ShapeFeatureExtractionMode.EdgeContours
                : ShapeFeatureExtractionMode.LegacyImageDomain;
            parameters.Metric = ResolveActiveMetric(parameters);
            parameters.EdgeSigma = (double)_edgeSigmaInput.Value;
            parameters.EdgeLowThreshold = (double)_edgeLowThresholdInput.Value;
            parameters.EdgeHighThreshold = (double)_edgeHighThresholdInput.Value;
            parameters.MinimumContourLengthPixels = (double)_minimumContourLengthInput.Value;
            parameters.FeaturePointSpacingPixels = (double)_featurePointSpacingInput.Value;
            parameters.MaximumFeaturePointCount = (int)_maximumFeaturePointCountInput.Value;
            parameters.MinimumScore = (double)_minimumScoreInput.Value;
            parameters.Greediness = (double)_greedinessInput.Value;
            parameters.MaximumOverlap = (double)_maximumOverlapInput.Value;
            parameters.MatchCount = (int)_matchCountInput.Value;
            parameters.SubPixel = GetComboValue(_subPixelCombo, "interpolation");
            parameters.TimeoutMilliseconds = (int)_timeoutInput.Value;
            parameters.ContourPointSpacingPixels = (double)_contourPointSpacingInput.Value;
            parameters.AllowPartialMatch = _allowPartialCheck.Checked;
            parameters.BrushRadius = (double)_brushRadiusInput.Value;
            _state.Parameters = parameters;
        }

        private async void ExtractFeaturesAsync(object sender, EventArgs eventArgs)
        {
            try
            {
                if (!CanRunServiceOperation("刷新特征"))
                {
                    return;
                }

                ReadParametersFromControls();
                string validationMessage;
                if (!ValidateFeatureGeometry(out validationMessage))
                {
                    MessageBox.Show(this, validationMessage, "模板参数", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                CancelAutoPreview();
                TemplateFeatureResult result = null;
                await RunBusyOperationAsync("正在提取模板特征...", async token =>
                {
                    using (Bitmap image = new Bitmap(_referenceImage))
                    {
                        result = await Service.ExtractFeaturesAsync(image, _state.DeepClone(), token);
                    }
                });
                if (result == null || !CanUpdateUi)
                {
                    return;
                }

                AppendOperationLog(result.Success, result.Message);
                if (!result.Success)
                {
                    SetStatus(result.Message ?? "提取特征失败。", true);
                    return;
                }

                ApplyFeaturePreviewResult(result, true);
                SetStatus(
                    string.Format(
                        "特征提取完成（{0}个特征点，Canny {1:0.#}/{2:0.#}，最小对比度 {3}）。",
                        _state.FeaturePoints.Count,
                        _state.Parameters.EdgeLowThreshold,
                        _state.Parameters.EdgeHighThreshold,
                        _state.Parameters.MinimumContrast),
                    false);
            }
            catch (ObjectDisposedException)
            {
            }
            catch (Exception exception)
            {
                if (CanUpdateUi)
                {
                    AppendOperationLog(false, "提取特征异常：" + exception.Message);
                    SetStatus("提取特征异常：" + exception.Message, true);
                }
            }
        }

        private async void CreateModelAsync(object sender, EventArgs eventArgs)
        {
            try
            {
                if (!CanRunServiceOperation("创建模型"))
                {
                    return;
                }

                await BuildModelCoreAsync();
            }
            catch (ObjectDisposedException)
            {
            }
            catch (Exception exception)
            {
                if (CanUpdateUi)
                {
                    AppendOperationLog(false, "创建模型异常：" + exception.Message);
                    SetStatus("创建模型异常：" + exception.Message, true);
                }
            }
        }

        private async Task<bool> BuildModelCoreAsync()
        {
            if (_busy || Service == null)
            {
                return false;
            }

            ReadParametersFromControls();
            EnsureCompanionRois();
            string validationMessage;
            if (!ValidateGeometry(out validationMessage))
            {
                if (!string.IsNullOrWhiteSpace(validationMessage))
                {
                    MessageBox.Show(this, validationMessage, "模板参数", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
                return false;
            }

            CancelAutoPreview();
            TemplateBuildResult result = null;
            await RunBusyOperationAsync("正在创建模板模型...", async token =>
            {
                using (Bitmap image = new Bitmap(_referenceImage))
                {
                    result = await Service.CreateModelAsync(image, _state.DeepClone(), token);
                }
            });
            if (result == null)
            {
                return false;
            }

            AppendOperationLog(result.Success, result.Message);
            if (!result.Success || result.ModelData == null || result.ModelData.Length == 0)
            {
                SetStatus(result.Message ?? "模型创建失败。", true);
                return false;
            }

            PushUndoState();
            EnsureRevisionAdvanced();
            SyncResolvedThresholdsQuietly(result.ResolvedParameters);
            _state.ModelData = (byte[])result.ModelData.Clone();
            _state.ModelFormat = result.ModelFormat;
            _state.ModelHash = result.ModelHash;
            _state.DomainCenter = result.DomainCenter;
            _state.ModelReferencePosition = result.ModelReferencePosition;
            _state.ModelReferenceAngleDegrees = result.ModelReferenceAngleDegrees;
            _state.ReferenceImageWidth = result.ReferenceImageWidth > 0
                ? result.ReferenceImageWidth
                : _referenceImage.Width;
            _state.ReferenceImageHeight = result.ReferenceImageHeight > 0
                ? result.ReferenceImageHeight
                : _referenceImage.Height;
            _state.HalconVersion = result.HalconVersion;
            _state.ModelContours = result.Contours ?? new List<List<ImageCoordinate>>();
            _state.ModelDirty = false;
            UpdateEditorOverlay();
            UpdateModelState();
            RaiseEditorStateChanged();
            EventHandler handler = ModelCreated;
            if (handler != null)
            {
                handler(this, EventArgs.Empty);
            }
            SetStatus(
                string.Format(
                    "模型创建完成（{0:N0} 字节 · {1}）。",
                    _state.ModelData.Length,
                    _state.Parameters.AutoThreshold
                        ? string.Format("自适应 Canny {0:0.#}/{1:0.#}, 对比度 {2}/{3}",
                            _state.Parameters.EdgeLowThreshold,
                            _state.Parameters.EdgeHighThreshold,
                            _state.Parameters.Contrast,
                            _state.Parameters.MinimumContrast)
                        : "手动阈值"),
                false);
            return true;
        }

        private void SyncResolvedThresholdsQuietly(TemplateEditorParameters resolved)
        {
            if (resolved == null)
            {
                return;
            }

            if (_state.Parameters == null)
            {
                _state.Parameters = new TemplateEditorParameters();
            }

            _state.Parameters.Contrast = resolved.Contrast;
            _state.Parameters.MinimumContrast = resolved.MinimumContrast;
            _state.Parameters.EdgeLowThreshold = resolved.EdgeLowThreshold;
            _state.Parameters.EdgeHighThreshold = resolved.EdgeHighThreshold;

            bool wasLoading = _loadingControls;
            _loadingControls = true;
            try
            {
                SetNumeric(_contrastInput, resolved.Contrast);
                SetNumeric(_minimumContrastInput, resolved.MinimumContrast);
                SetNumeric(_edgeLowThresholdInput, resolved.EdgeLowThreshold);
                SetNumeric(_edgeHighThresholdInput, resolved.EdgeHighThreshold);
            }
            finally
            {
                _loadingControls = wasLoading;
            }
        }

        private async void TestMatchAsync(object sender, EventArgs eventArgs)
        {
            try
            {
                if (!CanRunServiceOperation("测试匹配"))
                {
                    return;
                }

                if (!_state.HasModel || _state.ModelDirty)
                {
                    bool built = await BuildModelCoreAsync();
                    if (!built || !CanUpdateUi)
                    {
                        return;
                    }
                }

                ReadParametersFromControls();
                EnsureCompanionRois();
                // 测试匹配时保留清晰原图并清空旧的特征编辑叠加，突出匹配轮廓与中心点
                _canvas.EditorOverlay = new CanvasRuntimeOverlay();
                _canvas.ClearRuntimeOverlay();
                TemplateTestResult result = null;
                await RunBusyOperationAsync("正在测试模板匹配...", async token =>
                {
                    using (Bitmap image = new Bitmap(_referenceImage))
                    {
                        result = await Service.TestMatchAsync(image, _state.DeepClone(), token);
                    }
                });
                if (result == null || !CanUpdateUi)
                {
                    return;
                }

                string testSummary = result.Success
                    ? string.Format(
                        "测试结果：CT {0:0.0} ms   Score {1:0.000}   Row {2:0.###}   Column {3:0.###}   Angle {4:0.###}°",
                        result.ElapsedMilliseconds,
                        result.Score,
                        result.Position.Row,
                        result.Position.Column,
                        result.AngleDegrees)
                    : "测试结果：NG   " + (result.Message ?? "测试匹配失败。");
                _testResultLabel.Text = testSummary;
                _testResultLabel.ForeColor = result.Success ? Color.DarkGreen : Color.Firebrick;
                AppendOperationLog(result.Success, testSummary);
                ShowTestOverlay(result);
                SetStatus(
                    result.Success
                        ? string.Format("匹配成功：Score={0:0.000}，Row={1:0.###}，Column={2:0.###}，Angle={3:0.###}°，{4:0.0} ms",
                            result.Score,
                            result.Position.Row,
                            result.Position.Column,
                            result.AngleDegrees,
                            result.ElapsedMilliseconds)
                        : result.Message ?? "测试匹配失败。",
                    !result.Success);
                EventHandler<TemplateTestCompletedEventArgs> handler = TestCompleted;
                if (handler != null)
                {
                    handler(this, new TemplateTestCompletedEventArgs(result));
                }
            }
            catch (ObjectDisposedException)
            {
            }
            catch (Exception exception)
            {
                if (CanUpdateUi)
                {
                    AppendOperationLog(false, "测试匹配异常：" + exception.Message);
                    SetStatus("测试匹配异常：" + exception.Message, true);
                }
            }
        }

        private void RequestAutoFeaturePreview(bool immediate)
        {
            if (!CanUpdateUi || _busy || _loadingControls || Service == null)
            {
                return;
            }

            if (_state.TemplateRoi == null || _state.TemplateRoi.HalfWidth < 2.0 || _state.TemplateRoi.HalfHeight < 2.0)
            {
                return;
            }

            _autoPreviewGeneration++;
            if (immediate)
            {
                _maskSyncTimer.Stop();
                _autoPreviewTimer.Stop();
                StartNextAutoPreview();
                return;
            }

            // 采用固定周期合并节流而非重置防抖，确保拖动 ROI 过程中按 ~40Hz 持续平滑刷新特征点
            if (!_autoPreviewTimer.Enabled)
            {
                _autoPreviewTimer.Start();
            }
        }

        /// <summary>
        /// 掩膜编辑结束后的停手防抖同步：连续涂抹/框选多个掩膜期间不抢占后台 HALCON 线程，
        /// 等用户停手 350ms 后再统一同步一次最终特征点真值。
        /// </summary>
        private void ScheduleDebouncedMaskSync()
        {
            if (!CanUpdateUi || _busy || _loadingControls || Service == null)
            {
                return;
            }

            _autoPreviewGeneration++;
            _autoPreviewTimer.Stop();
            _maskSyncTimer.Stop();
            _maskSyncTimer.Start();
        }

        private void MaskSyncTimer_Tick(object sender, EventArgs eventArgs)
        {
            _maskSyncTimer.Stop();
            if (_canvas.IsInteracting) return;
            StartNextAutoPreview();
        }

        private void AutoPreviewTimer_Tick(object sender, EventArgs eventArgs)
        {
            _autoPreviewTimer.Stop();
            StartNextAutoPreview();
        }

        private void StartNextAutoPreview()
        {
            if (!CanUpdateUi || _busy || Service == null ||
                _state.TemplateRoi == null || _state.TemplateRoi.HalfWidth < 2.0 || _state.TemplateRoi.HalfHeight < 2.0)
            {
                return;
            }

            if (_autoPreviewRunning)
            {
                // 单路最新槽位合并：前一次提取尚未返回时仅记录待执行标记，完成后立即追赶最新位姿
                _autoPreviewPending = true;
                return;
            }

            _autoPreviewPending = false;
            _autoPreviewRunning = true;
            ReadParametersFromControls();
            TemplateEditorState snapshot = CreatePreviewSnapshot();
            int generation = _autoPreviewGeneration;

            if (_autoPreviewCancellation != null)
            {
                try { _autoPreviewCancellation.Cancel(); }
                catch (ObjectDisposedException) { }
                _autoPreviewCancellation.Dispose();
            }
            _autoPreviewCancellation = new CancellationTokenSource();
            CancellationToken token = _autoPreviewCancellation.Token;

            RunAutoFeaturePreviewAsync(snapshot, generation, token);
        }

        /// <summary>创建用于后台特征预览的轻量快照，跳过体积庞大的二进制 ModelData 与既有特征点复制。</summary>
        private TemplateEditorState CreatePreviewSnapshot()
        {
            return new TemplateEditorState
            {
                TemplateRoi = _state.TemplateRoi == null ? null : _state.TemplateRoi.DeepClone(),
                SearchRoi = _state.SearchRoi == null ? null : _state.SearchRoi.DeepClone(),
                DetectionRoi = _state.DetectionRoi == null ? null : _state.DetectionRoi.DeepClone(),
                ReferenceAnchor = _state.ReferenceAnchor,
                AnchorLocked = _state.AnchorLocked,
                MaskInverted = _state.MaskInverted,
                Parameters = _state.Parameters == null ? new TemplateEditorParameters() : _state.Parameters.DeepClone(),
                ReferenceImageWidth = _state.ReferenceImageWidth,
                ReferenceImageHeight = _state.ReferenceImageHeight,
                MaskStrokes = (_state.MaskStrokes ?? new List<MaskStroke>())
                    .Where(item => item != null)
                    .Select(item => item.DeepClone())
                    .ToList()
            };
        }

        private async void RunAutoFeaturePreviewAsync(
            TemplateEditorState snapshot,
            int generation,
            CancellationToken token)
        {
            try
            {
                PreviewImageLease lease;
                if (_previewImageHolder == null || !_previewImageHolder.TryAcquire(out lease))
                {
                    return;
                }

                TemplateFeatureResult result;
                using (lease)
                {
                    result = await Service.ExtractFeaturesAsync(lease.Image, snapshot, token);
                }

                if (!CanUpdateUi || token.IsCancellationRequested || _busy || _state.TemplateRoi == null)
                {
                    return;
                }
                // 后台预览返回时若用户已开始新一轮拖拽/掩膜，丢弃过期结果避免覆盖实时预览并造成画面跳顿
                if (generation != _autoPreviewGeneration || _canvas.IsInteracting)
                {
                    return;
                }

                if (result != null && result.Success)
                {
                    ApplyFeaturePreviewResult(result, false);
                    if (generation == _autoPreviewGeneration)
                    {
                        SetStatus(
                            string.Format(
                                "已提取 {0} 个特征点（自适应/当前阈值：{1:0.#}/{2:0.#}，最小对比度 {3}）。",
                                _state.FeaturePoints.Count,
                                _state.Parameters.EdgeLowThreshold,
                                _state.Parameters.EdgeHighThreshold,
                                _state.Parameters.MinimumContrast),
                            false);
                    }
                }
                else if (result != null && generation == _autoPreviewGeneration)
                {
                    _state.FeaturePoints = new List<ImageCoordinate>();
                    _state.ModelContours = new List<List<ImageCoordinate>>();
                    UpdateEditorOverlay();
                    SetStatus(result.Message ?? "当前模板区域未检测到足够特征点。", false);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exception)
            {
                if (CanUpdateUi && generation == _autoPreviewGeneration)
                {
                    SetStatus("特征预览异常：" + exception.Message, false);
                }
            }
            finally
            {
                _autoPreviewRunning = false;
                if (CanUpdateUi && !_busy && _autoPreviewPending)
                {
                    StartNextAutoPreview();
                }
            }
        }

        private void CancelAutoPreview()
        {
            _autoPreviewGeneration++;
            _autoPreviewPending = false;
            _autoPreviewTimer.Stop();
            _maskSyncTimer.Stop();
            if (_autoPreviewCancellation != null && !_autoPreviewCancellation.IsCancellationRequested)
            {
                try { _autoPreviewCancellation.Cancel(); }
                catch (ObjectDisposedException) { }
            }
        }

        private void ApplyFeaturePreviewResult(TemplateFeatureResult result, bool pushUndo)
        {
            if (result == null) return;
            if (pushUndo && result.RecommendedParameters != null)
            {
                PushUndoState();
            }

            _state.FeaturePoints = result.FeaturePoints ?? new List<ImageCoordinate>();
            _state.ModelContours = result.Contours ?? new List<List<ImageCoordinate>>();
            if (result.RecommendedParameters != null)
            {
                SyncResolvedThresholdsQuietly(result.RecommendedParameters);
            }
            SyncUnmaskedBaseIfApplicable();
            UpdateEditorOverlay();
        }

        private async Task RunBusyOperationAsync(string status, Func<CancellationToken, Task> operation)
        {
            if (_busy)
            {
                return;
            }

            _busy = true;
            CancellationTokenSource operationCancellation = new CancellationTokenSource();
            _operationCancellation = operationCancellation;
            SetBusyState(true);
            SetStatus(status, false);
            try
            {
                await operation(operationCancellation.Token);
            }
            catch (OperationCanceledException)
            {
                if (CanUpdateUi) SetStatus("操作已取消。", false);
            }
            catch (Exception exception)
            {
                if (CanUpdateUi)
                {
                    AppendOperationLog(false, exception.Message);
                    SetStatus("操作失败：" + exception.Message, true);
                }
            }
            finally
            {
                operationCancellation.Dispose();
                if (ReferenceEquals(_operationCancellation, operationCancellation))
                    _operationCancellation = null;
                _busy = false;
                if (CanUpdateUi) SetBusyState(false);
            }
        }

        private bool CanUpdateUi
        {
            get { return !_closing && !IsDisposed && !Disposing; }
        }

        private void SetBusyState(bool busy)
        {
            _progressBar.Visible = busy;
            _okButton.Enabled = !busy;
            _parameterTabs.Enabled = !busy;
            _canvas.Enabled = !busy;
            UpdateActionButtonsEnabled();
            UpdateRoiToolbarButtons();
        }

        private void CancelCurrentOperation()
        {
            if (_operationCancellation != null && !_operationCancellation.IsCancellationRequested)
            {
                _operationCancellation.Cancel();
            }
        }

        private void ShowTestOverlay(TemplateTestResult result)
        {
            CanvasRuntimeOverlay overlay = new CanvasRuntimeOverlay();
            if (result.Success)
            {
                AddContourPoints(overlay, result.MatchedContours, Color.Lime);
                overlay.Points.Add(new CanvasPointOverlay
                {
                    Position = result.Position,
                    Color = Color.Lime,
                    RadiusPixels = 6.0f
                });
            }

            _canvas.RuntimeOverlay = overlay;
        }

        private void UpdateEditorOverlay()
        {
            CanvasRuntimeOverlay overlay = new CanvasRuntimeOverlay();
            // 优先将已建模型或特征提取得到的轮廓采样点叠加到画布，无轮廓数据时回退到离散特征点集合
            IEnumerable<List<ImageCoordinate>> contours = _state.ModelContours;
            if (contours == null || !_state.ModelContours.Any(item => item != null && item.Count > 0))
            {
                contours = new[] { _state.FeaturePoints ?? new List<ImageCoordinate>() };
            }

            List<ImageCoordinate> visible = new List<ImageCoordinate>();
            foreach (List<ImageCoordinate> contour in contours)
            {
                if (contour == null) continue;
                for (int i = 0; i < contour.Count; i++)
                    if (IsFinite(contour[i].Row) && IsFinite(contour[i].Column)) visible.Add(contour[i]);
            }
            _committedVisiblePoints = visible;
            AddFeaturePoints(overlay, contours, Color.Lime);
            _canvas.EditorOverlay = overlay;
        }

        private static void AddFeaturePoints(
            CanvasRuntimeOverlay overlay,
            IEnumerable<List<ImageCoordinate>> contours,
            Color color)
        {
            if (overlay == null)
            {
                throw new ArgumentNullException("overlay");
            }

            foreach (List<ImageCoordinate> contour in contours ?? Enumerable.Empty<List<ImageCoordinate>>())
            {
                if (contour == null) continue;
                foreach (ImageCoordinate point in contour)
                {
                    if (IsFinite(point.Row) && IsFinite(point.Column))
                        AddPreviewPoint(overlay, point, color);
                }
            }
        }

        private static void AddContourPoints(
            CanvasRuntimeOverlay overlay,
            IEnumerable<List<ImageCoordinate>> contours,
            Color color)
        {
            if (overlay == null)
            {
                throw new ArgumentNullException("overlay");
            }

            foreach (List<ImageCoordinate> contour in contours ?? Enumerable.Empty<List<ImageCoordinate>>())
            {
                if (contour == null || contour.Count == 0)
                {
                    continue;
                }

                List<ImageCoordinate> finitePoints = contour
                    .Where(point => IsFinite(point.Row) && IsFinite(point.Column))
                    .ToList();
                if (finitePoints.Count < 2)
                {
                    foreach (ImageCoordinate point in finitePoints)
                        AddPreviewPoint(overlay, point, color);
                    continue;
                }

                overlay.Polylines.Add(new CanvasPolylineOverlay
                {
                    Points = finitePoints,
                    Color = color,
                    WidthPixels = 1.5f,
                    Closed = false
                });
            }
        }

        private static void AddPreviewPoint(
            CanvasRuntimeOverlay overlay,
            ImageCoordinate point,
            Color color)
        {
            overlay.Points.Add(new CanvasPointOverlay
            {
                Position = point,
                Color = color,
                RadiusPixels = 1.5f
            });
        }

        private static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }

        private void SelectTool(CanvasEditTool tool)
        {
            _canvas.Tool = tool;
            SyncToolButtonCheckedState(tool);
            if (tool == CanvasEditTool.DrawTemplateRoi)
            {
                _canvas.SelectedRoi = CanvasRoiTarget.Template;
            }
            else if (tool == CanvasEditTool.DrawSearchRoi)
            {
                _canvas.SelectedRoi = CanvasRoiTarget.Search;
            }
            else if (tool == CanvasEditTool.DrawDetectionRoi)
            {
                _canvas.SelectedRoi = CanvasRoiTarget.Detection;
            }
        }

        private void ActivateMoveAnchorTool()
        {
            if (_canvas.Tool == CanvasEditTool.MoveAnchor)
            {
                SelectTool(CanvasEditTool.Select);
                return;
            }
            if (_state.AnchorLocked)
            {
                SetStatus("基准中心已锁定，请先在右侧「基准中心」页取消勾选“锁定基准中心”。", true);
                _parameterTabs.SelectedIndex = 2;
                SyncToolButtonCheckedState(_canvas.Tool);
                return;
            }

            SelectTool(CanvasEditTool.MoveAnchor);
            SetStatus("移动中心模式：在图上点击或拖动即可移动模板中心，支持方向键微调（Shift+方向键 10px）。", false);
        }

        private void SyncToolButtonCheckedState(CanvasEditTool tool)
        {
            bool maskActive = IsMaskEditTool(tool);
            if (maskActive)
            {
                int shapeIndex = tool == CanvasEditTool.MaskCircle ? 1
                    : tool == CanvasEditTool.MaskPolygon ? 2
                    : tool == CanvasEditTool.EraseMask ? 3
                    : tool == CanvasEditTool.RestoreMask ? 4 : 0;
                if (_maskShapeCombo.SelectedIndex != shapeIndex)
                {
                    bool prev = _loadingControls;
                    _loadingControls = true;
                    try { _maskShapeCombo.SelectedIndex = shapeIndex; }
                    finally { _loadingControls = prev; }
                }
            }

            StyleToggleButton(_moveAnchorToolButton, tool == CanvasEditTool.MoveAnchor, Color.FromArgb(190, 24, 93));
            StyleToggleButton(_maskToolButton, maskActive, Color.FromArgb(220, 38, 38));
            _brushRadiusInput.Enabled = !_busy && (tool == CanvasEditTool.EraseMask || tool == CanvasEditTool.RestoreMask ||
                _maskShapeCombo.SelectedIndex == 3 || _maskShapeCombo.SelectedIndex == 4);
            UpdateRoiToolbarButtons();
        }

        private void UpdateRoiToolSelection()
        {
            UpdateRoiToolbarButtons();
        }

        private void AddOrSelectRoi(CanvasRoiTarget target)
        {
            if (_busy || _referenceImage == null)
            {
                return;
            }

            RotatedRectangle existing = GetStateRoi(target);
            if (existing != null)
            {
                _canvas.SelectedRoi = target;
                SelectTool(CanvasEditTool.Select);
                if (target == CanvasRoiTarget.Template && !HasAnyOverlayFeatures(_state))
                {
                    RequestAutoFeaturePreview(true);
                }
                UpdateRoiToolbarButtons();
                return;
            }

            PushUndoState();
            RotatedRectangle created = CreateDefaultRoiForTarget(target);
            if (target == CanvasRoiTarget.Template)
            {
                _state.TemplateRoi = created;
                _canvas.TemplateRoi = created;
                if (!_state.AnchorLocked && _state.ReferenceAnchor.Row == 0.0 && _state.ReferenceAnchor.Column == 0.0)
                {
                    SetReferenceAnchorInternal(new ImageCoordinate(created.CenterRow, created.CenterColumn));
                }
                _canvas.SelectedRoi = CanvasRoiTarget.Template;
                SelectTool(CanvasEditTool.Select);
                MarkChanged(true);
                RequestAutoFeaturePreview(true);
                SetStatus("已添加模板 ROI，正在自动提取特征点...", false);
            }
            else if (target == CanvasRoiTarget.Search)
            {
                _state.SearchRoi = created;
                _canvas.SearchRoi = created;
                _canvas.SelectedRoi = CanvasRoiTarget.Search;
                SelectTool(CanvasEditTool.Select);
                MarkChanged(false);
                SetStatus("已添加搜索 ROI。", false);
            }
            else if (target == CanvasRoiTarget.Detection && _enableDetectionRoi)
            {
                _state.DetectionRoi = created;
                _canvas.DetectionRoi = created;
                _canvas.SelectedRoi = CanvasRoiTarget.Detection;
                SelectTool(CanvasEditTool.Select);
                MarkChanged(false);
                SetStatus("已添加检测 ROI。", false);
            }

            UpdateRoiToolbarButtons();
        }

        private void DeleteSelectedRoi()
        {
            if (_busy)
            {
                return;
            }

            CanvasRoiTarget selected = _canvas.SelectedRoi;
            if (selected == CanvasRoiTarget.None || GetStateRoi(selected) == null)
            {
                SetStatus("请先选中一个 ROI 再执行删除。", false);
                return;
            }

            bool locked = (selected == CanvasRoiTarget.Template && _state.TemplateRoiLocked) ||
                          (selected == CanvasRoiTarget.Search && _state.SearchRoiLocked);
            if (locked)
            {
                SetStatus("当前选中的 ROI 已锁定，请先在参数页取消锁定后再删除。", true);
                return;
            }

            if (_canvas.DeleteSelectedRoi())
            {
                SetStatus("已删除选中 ROI。", false);
                UpdateRoiToolbarButtons();
            }
        }

        private RotatedRectangle CreateDefaultRoiForTarget(CanvasRoiTarget target)
        {
            if (target == CanvasRoiTarget.Search)
            {
                return CreateFullImageSearchRoi(_referenceImage.Size);
            }

            if (target == CanvasRoiTarget.Detection && _state.TemplateRoi != null)
            {
                return _state.TemplateRoi.DeepClone();
            }

            // 以当前画布视野中心为默认中心落点，方便用户在放大查看单块板时就地加框
            PointF viewportClientCenter = new PointF(
                Math.Max(0.0f, _canvas.ClientSize.Width * 0.5f),
                Math.Max(0.0f, _canvas.ClientSize.Height * 0.5f));
            ImageCoordinate viewCenter = _canvas.ClientToImage(viewportClientCenter);
            double halfWidth = Math.Max(10.0, _referenceImage.Width * 0.12);
            double halfHeight = Math.Max(10.0, _referenceImage.Height * 0.14);
            double centerRow = viewCenter.Row > 0.0 && viewCenter.Row < _referenceImage.Height
                ? Math.Max(halfHeight, Math.Min(_referenceImage.Height - 1.0 - halfHeight, viewCenter.Row))
                : _referenceImage.Height * 0.5;
            double centerColumn = viewCenter.Column > 0.0 && viewCenter.Column < _referenceImage.Width
                ? Math.Max(halfWidth, Math.Min(_referenceImage.Width - 1.0 - halfWidth, viewCenter.Column))
                : _referenceImage.Width * 0.5;
            return new RotatedRectangle(centerRow, centerColumn, halfWidth, halfHeight, 0.0);
        }

        private static RotatedRectangle CreateFullImageSearchRoi(Size imageSize)
        {
            return new RotatedRectangle(
                imageSize.Height * 0.5,
                imageSize.Width * 0.5,
                Math.Max(20.0, (imageSize.Width * 0.5) - 1.0),
                Math.Max(20.0, (imageSize.Height * 0.5) - 1.0),
                0.0);
        }

        private RotatedRectangle GetStateRoi(CanvasRoiTarget target)
        {
            if (target == CanvasRoiTarget.Template) return _state.TemplateRoi;
            if (target == CanvasRoiTarget.Search) return _state.SearchRoi;
            if (target == CanvasRoiTarget.Detection && _enableDetectionRoi) return _state.DetectionRoi;
            return null;
        }

        private void UpdateRoiToolbarButtons()
        {
            bool hasTemplate = _state.TemplateRoi != null;
            bool hasSearch = _state.SearchRoi != null;
            bool hasDetection = _enableDetectionRoi && _state.DetectionRoi != null;
            CanvasRoiTarget selected = _canvas.SelectedRoi;
            bool inSelectMode = _canvas.Tool == CanvasEditTool.Select;
            Color activeBlue = Color.FromArgb(37, 99, 235);

            _addTemplateRoiButton.Text = "添加模板ROI";
            StyleToggleButton(_addTemplateRoiButton, inSelectMode && selected == CanvasRoiTarget.Template && hasTemplate, activeBlue);

            _addSearchRoiButton.Text = "添加搜索ROI";
            StyleToggleButton(_addSearchRoiButton, inSelectMode && selected == CanvasRoiTarget.Search && hasSearch, activeBlue);

            _addDetectionRoiButton.Text = "添加检测ROI";
            StyleToggleButton(_addDetectionRoiButton, inSelectMode && selected == CanvasRoiTarget.Detection && hasDetection, activeBlue);

            bool locked = (selected == CanvasRoiTarget.Template && _state.TemplateRoiLocked) ||
                          (selected == CanvasRoiTarget.Search && _state.SearchRoiLocked);
            _deleteRoiButton.Enabled = !_busy && selected != CanvasRoiTarget.None && GetStateRoi(selected) != null && !locked;
        }

        private static void StyleToggleButton(Button button, bool active, Color accent)
        {
            button.BackColor = active ? accent : Color.White;
            button.ForeColor = active ? Color.White : Color.FromArgb(30, 41, 59);
            button.FlatAppearance.BorderColor = active ? accent : Color.FromArgb(203, 213, 225);
        }

        private void SetReferenceAnchorInternal(ImageCoordinate anchor)
        {
            _state.ReferenceAnchor = anchor;
            _canvas.ReferenceAnchor = anchor;
            bool wasLoading = _loadingControls;
            _loadingControls = true;
            try
            {
                SetNumeric(_anchorRowInput, anchor.Row);
                SetNumeric(_anchorColumnInput, anchor.Column);
            }
            finally
            {
                _loadingControls = wasLoading;
            }
        }

        private void UpdateFilteredFeatureOverlay(RotatedRectangle activeRoi)
        {
            if (activeRoi == null)
            {
                _canvas.EditorOverlay = new CanvasRuntimeOverlay();
                return;
            }

            IEnumerable<List<ImageCoordinate>> contours = _state.ModelContours;
            if (contours == null || !_state.ModelContours.Any(item => item != null && item.Count > 0))
            {
                contours = new[] { _state.FeaturePoints ?? new List<ImageCoordinate>() };
            }

            CanvasRuntimeOverlay overlay = new CanvasRuntimeOverlay();
            foreach (List<ImageCoordinate> contour in contours)
            {
                if (contour == null) continue;
                foreach (ImageCoordinate point in contour)
                {
                    if (IsFinite(point.Row) && IsFinite(point.Column) && activeRoi.Contains(point))
                    {
                        AddPreviewPoint(overlay, point, Color.Lime);
                    }
                }
            }
            _canvas.EditorOverlay = overlay;
        }

        private static bool HasAnyOverlayFeatures(TemplateEditorState state)
        {
            if (state == null) return false;
            if (state.FeaturePoints != null && state.FeaturePoints.Count > 0) return true;
            return state.ModelContours != null && state.ModelContours.Any(item => item != null && item.Count > 0);
        }

        private void ClearMask(object sender, EventArgs eventArgs)
        {
            if (_state.MaskStrokes == null || _state.MaskStrokes.Count == 0)
            {
                return;
            }

            PushUndoState();
            _state.MaskStrokes.Clear();
            _canvas.SetMaskStrokes(_state.MaskStrokes);
            ApplyInstantMaskFilter(null);
            MarkChanged(true);
            ScheduleDebouncedMaskSync();
        }

        private void InvertMask(object sender, EventArgs eventArgs)
        {
            PushUndoState();
            _state.MaskInverted = !_state.MaskInverted;
            ApplyInstantMaskFilter(null);
            MarkChanged(true);
            ScheduleDebouncedMaskSync();
            SetStatus(_state.MaskInverted ? "掩膜已反选。" : "掩膜已恢复正常包含关系。", false);
        }

        private void ToggleMaskVisibility(object sender, EventArgs eventArgs)
        {
            if (_loadingControls) return;
            _state.MaskVisible = _maskVisibleCheck.Checked;
            _canvas.MaskVisible = _state.MaskVisible;
            _canvas.Invalidate();
        }

        private void ResetAnchor(object sender, EventArgs eventArgs)
        {
            if (_state.AnchorLocked || _state.TemplateRoi == null)
            {
                return;
            }

            PushUndoState();
            _state.ReferenceAnchor = new ImageCoordinate(_state.TemplateRoi.CenterRow, _state.TemplateRoi.CenterColumn);
            _canvas.ReferenceAnchor = _state.ReferenceAnchor;
            _loadingControls = true;
            try
            {
                SetNumeric(_anchorRowInput, _state.ReferenceAnchor.Row);
                SetNumeric(_anchorColumnInput, _state.ReferenceAnchor.Column);
            }
            finally
            {
                _loadingControls = false;
            }
            MarkChanged(false);
        }

        private void PushUndoState()
        {
            if (_loadingControls || _busy)
            {
                return;
            }

            // 保留最近 HistoryLimit(30) 步轻量快照（复用不可变 ModelData 与已提交掩膜项，不深拷贝万级特征点）
            _undoStack.Push(_state.CloneForUndo());
            while (_undoStack.Count > HistoryLimit)
            {
                TemplateEditorState[] items = _undoStack.ToArray();
                _undoStack.Clear();
                for (int index = Math.Min(HistoryLimit - 1, items.Length - 1); index >= 0; index--)
                {
                    _undoStack.Push(items[index]);
                }
            }
            _redoStack.Clear();
            UpdateHistoryButtons();
        }

        private void UndoEdit(object sender, EventArgs eventArgs)
        {
            if (_undoStack.Count == 0 || _busy)
            {
                return;
            }

            _redoStack.Push(_state.CloneForUndo());
            _state = _undoStack.Pop();
            LoadStateToControls(false);
            RaiseEditorStateChanged();
            if (_state.TemplateRoi != null)
            {
                if (_unmaskedBaseContours != null && _unmaskedBaseContours.Count > 0 && SameRoiGeometry(_unmaskedBaseRoi, _state.TemplateRoi))
                {
                    ApplyInstantMaskFilter(null);
                    ScheduleDebouncedMaskSync();
                }
                else if (!HasAnyOverlayFeatures(_state))
                {
                    RequestAutoFeaturePreview(true);
                }
            }
        }

        private void RedoEdit(object sender, EventArgs eventArgs)
        {
            if (_redoStack.Count == 0 || _busy)
            {
                return;
            }

            _undoStack.Push(_state.CloneForUndo());
            _state = _redoStack.Pop();
            LoadStateToControls(false);
            RaiseEditorStateChanged();
            if (_state.TemplateRoi != null)
            {
                if (_unmaskedBaseContours != null && _unmaskedBaseContours.Count > 0 && SameRoiGeometry(_unmaskedBaseRoi, _state.TemplateRoi))
                {
                    ApplyInstantMaskFilter(null);
                    ScheduleDebouncedMaskSync();
                }
                else if (!HasAnyOverlayFeatures(_state))
                {
                    RequestAutoFeaturePreview(true);
                }
            }
        }

        private void EditorKeyDown(object sender, KeyEventArgs eventArgs)
        {
            if (eventArgs.Control && eventArgs.KeyCode == Keys.Z)
            {
                UndoEdit(sender, EventArgs.Empty);
                eventArgs.Handled = true;
            }
            else if (eventArgs.Control && eventArgs.KeyCode == Keys.Y)
            {
                RedoEdit(sender, EventArgs.Empty);
                eventArgs.Handled = true;
            }
            else if (eventArgs.KeyCode == Keys.Escape && _busy)
            {
                CancelCurrentOperation();
                eventArgs.Handled = true;
            }
            else if (eventArgs.KeyCode == Keys.Escape && _canvas.Tool != CanvasEditTool.Select)
            {
                SelectTool(CanvasEditTool.Select);
                eventArgs.Handled = true;
            }
        }

        private void MarkChanged(bool requiresModelRebuild)
        {
            EnsureRevisionAdvanced();
            if (requiresModelRebuild)
            {
                _state.ModelDirty = true;
            }
            UpdateModelState();
            RaiseEditorStateChanged();
        }

        private void EnsureRevisionAdvanced()
        {
            long minimumRevision = Math.Max(1L, _originalState.Revision + 1L);
            if (_state.Revision < minimumRevision)
            {
                _state.Revision = minimumRevision;
            }
        }

        private void UpdateModelState()
        {
            if (_state.TemplateRoi == null)
            {
                _modelStateLabel.Text = "未添加模板 ROI（请点击「添加模板ROI」按钮添加）";
                _modelStateLabel.ForeColor = Color.White;
                _modelStateLabel.BackColor = Color.FromArgb(100, 116, 139);
            }
            else if (!_state.HasModel)
            {
                _modelStateLabel.Text = "未创建模型（已显示实时特征，可点击「创建模型」或直接「确认」）";
                _modelStateLabel.ForeColor = Color.White;
                _modelStateLabel.BackColor = Color.FromArgb(165, 94, 36);
            }
            else if (_state.ModelDirty)
            {
                _modelStateLabel.Text = "参数已修改，需重建模型（确认/测试将自动重建）";
                _modelStateLabel.ForeColor = Color.White;
                _modelStateLabel.BackColor = Color.FromArgb(183, 74, 52);
            }
            else
            {
                _modelStateLabel.Text = string.Format("模型有效 · Rev {0} · {1:N0} bytes", _state.Revision, _state.ModelData.Length);
                _modelStateLabel.ForeColor = Color.White;
                _modelStateLabel.BackColor = Color.FromArgb(39, 132, 87);
            }

            UpdateActionButtonsEnabled();
        }

        private void UpdateActionButtonsEnabled()
        {
            bool hasTemplate = _state.TemplateRoi != null &&
                               _state.TemplateRoi.HalfWidth >= 2.0 &&
                               _state.TemplateRoi.HalfHeight >= 2.0;
            _extractButton.Enabled = !_busy && Service != null && hasTemplate && _modelTypeCombo.SelectedIndex == 0;
            _createButton.Enabled = !_busy && Service != null && hasTemplate;
            _testButton.Enabled = !_busy && Service != null && hasTemplate;
        }

        private void UpdateHistoryButtons()
        {
            _undoButton.Enabled = _undoStack.Count > 0 && !_busy;
            _redoButton.Enabled = _redoStack.Count > 0 && !_busy;
        }

        private void UpdateAnchorEnabledState()
        {
            _anchorRowInput.Enabled = !_state.AnchorLocked;
            _anchorColumnInput.Enabled = !_state.AnchorLocked;
        }

        private void UpdateShapeFeatureEnabledState()
        {
            bool isShapeModel = _modelTypeCombo.SelectedIndex == 0;
            bool edgeContoursEnabled = isShapeModel && _edgeContourModeCheck.Checked;
            bool autoThreshold = isShapeModel && _autoThresholdCheck.Checked;
            TemplateEditorParameters parameters = _state.Parameters ?? new TemplateEditorParameters();
            _state.Parameters = parameters;
            NormalizeStoredMetrics(parameters);
            // 外轮廓 XLD 模式固定采用 ignore_local_polarity；其他模式恢复对应模型类型的独立极性配置
            parameters.Metric = edgeContoursEnabled
                ? EdgeContourMetric
                : isShapeModel ? parameters.ShapeMetric : parameters.NccMetric;

            UpdateMetricOptions(!isShapeModel, parameters.Metric);

            _autoThresholdCheck.Enabled = isShapeModel;
            _edgeContourModeCheck.Enabled = isShapeModel;
            _optimizationCombo.Enabled = isShapeModel;
            _metricCombo.Enabled = !edgeContoursEnabled;
            _contrastInput.Enabled = isShapeModel && !edgeContoursEnabled && !autoThreshold;
            _minimumContrastInput.Enabled = isShapeModel && !autoThreshold;
            _edgeSigmaInput.Enabled = edgeContoursEnabled;
            _edgeLowThresholdInput.Enabled = edgeContoursEnabled && !autoThreshold;
            _edgeHighThresholdInput.Enabled = edgeContoursEnabled && !autoThreshold;
            _minimumContourLengthInput.Enabled = edgeContoursEnabled;
            _featurePointSpacingInput.Enabled = edgeContoursEnabled;
            _maximumFeaturePointCountInput.Enabled = edgeContoursEnabled;
            UpdateActionButtonsEnabled();
        }

        private async void ConfirmEditor(object sender, EventArgs eventArgs)
        {
            try
            {
                ReadParametersFromControls();
                EnsureCompanionRois();
                if (!ValidateGeometry(out string message))
                {
                    MessageBox.Show(this, message, "模板参数", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                if (Service != null && (!_state.HasModel || _state.ModelDirty))
                {
                    bool built = await BuildModelCoreAsync();
                    if (!built || !CanUpdateUi)
                    {
                        return;
                    }
                }
                else if (!_state.HasModel || _state.ModelDirty)
                {
                    DialogResult answer = MessageBox.Show(
                        this,
                        _state.HasModel
                            ? "模板参数已经修改，但模型尚未重建。继续确认会使该模板不可用于定位，是否仍要保存？"
                            : "尚未创建模板模型。继续确认后需要由上位机重新进入模板设置并创建模型，是否仍要保存？",
                        "模板尚未就绪",
                        MessageBoxButtons.YesNo,
                        MessageBoxIcon.Warning,
                        MessageBoxDefaultButton.Button2);
                    if (answer != DialogResult.Yes)
                    {
                        return;
                    }
                }

                ResultState = _state.DeepClone();
                DialogResult = DialogResult.OK;
                Close();
            }
            catch (ObjectDisposedException)
            {
            }
            catch (Exception exception)
            {
                if (CanUpdateUi)
                {
                    AppendOperationLog(false, "确认模板异常：" + exception.Message);
                    SetStatus("确认模板异常：" + exception.Message, true);
                }
            }
        }

        private void EnsureCompanionRois()
        {
            if (_state.TemplateRoi == null || _referenceImage == null)
            {
                return;
            }

            ImageCoordinate templateCenter = new ImageCoordinate(
                _state.TemplateRoi.CenterRow,
                _state.TemplateRoi.CenterColumn);
            if (_state.SearchRoi == null ||
                _state.SearchRoi.HalfWidth < 2.0 ||
                _state.SearchRoi.HalfHeight < 2.0 ||
                !_state.SearchRoi.Contains(templateCenter))
            {
                _state.SearchRoi = CreateFullImageSearchRoi(_referenceImage.Size);
                _canvas.SearchRoi = _state.SearchRoi;
                AppendOperationLog(true, "未配置覆盖模板中心的搜索 ROI，已自动补全全图搜索 ROI。");
            }

            if (_enableDetectionRoi &&
                (_state.DetectionRoi == null ||
                 _state.DetectionRoi.HalfWidth < 2.0 ||
                 _state.DetectionRoi.HalfHeight < 2.0))
            {
                _state.DetectionRoi = _state.TemplateRoi.DeepClone();
                _canvas.DetectionRoi = _state.DetectionRoi;
                AppendOperationLog(true, "未配置检测 ROI，已按模板 ROI 自动初始化。");
            }

            UpdateRoiToolbarButtons();
        }

        private bool ValidateFeatureGeometry(out string message)
        {
            if (_state.TemplateRoi == null || _state.TemplateRoi.HalfWidth < 2.0 || _state.TemplateRoi.HalfHeight < 2.0)
            {
                message = "请先点击「添加模板ROI」按钮设置有效的模板 ROI。";
                return false;
            }

            return ValidateParameterConstraints(out message);
        }

        private bool ValidateGeometry(out string message)
        {
            if (!ValidateFeatureGeometry(out message))
            {
                return false;
            }

            if (_state.SearchRoi == null || _state.SearchRoi.HalfWidth < 2.0 || _state.SearchRoi.HalfHeight < 2.0)
            {
                message = "请设置有效的搜索 ROI。";
                return false;
            }

            if (_enableDetectionRoi &&
                (_state.DetectionRoi == null ||
                 _state.DetectionRoi.HalfWidth < 2.0 ||
                 _state.DetectionRoi.HalfHeight < 2.0))
            {
                message = "请设置有效的检测 ROI。";
                return false;
            }

            if (!_state.SearchRoi.Contains(new ImageCoordinate(_state.TemplateRoi.CenterRow, _state.TemplateRoi.CenterColumn)))
            {
                message = "搜索 ROI 必须覆盖模板 ROI 的中心。";
                return false;
            }

            message = null;
            return true;
        }

        private bool ValidateParameterConstraints(out string message)
        {
            if (_state.Parameters.AngleExtentDegrees <= 0.0)
            {
                message = "示教角度范围必须大于 0°。";
                return false;
            }

            if (_state.Parameters.MatchAngleExtentDegrees <= 0.0)
            {
                message = "搜索角度范围必须大于 0°。";
                return false;
            }

            if (_state.Parameters.ModelType == TemplateModelType.Shape &&
                !_state.Parameters.AutoThreshold &&
                _state.Parameters.FeatureExtractionMode == ShapeFeatureExtractionMode.LegacyImageDomain &&
                _state.Parameters.MinimumContrast > _state.Parameters.Contrast)
            {
                message = "最小对比度不能大于模板对比度。";
                return false;
            }

            if (_state.Parameters.ModelType == TemplateModelType.Shape &&
                !_state.Parameters.AutoThreshold &&
                _state.Parameters.FeatureExtractionMode == ShapeFeatureExtractionMode.EdgeContours &&
                _state.Parameters.EdgeHighThreshold <= _state.Parameters.EdgeLowThreshold)
            {
                message = "边缘高阈值必须大于边缘低阈值。";
                return false;
            }

            message = null;
            return true;
        }

        private static void NormalizeStoredMetrics(TemplateEditorParameters parameters)
        {
            string activeMetric = NormalizeMetric(parameters.Metric);
            if (string.IsNullOrWhiteSpace(parameters.ShapeMetric))
            {
                parameters.ShapeMetric = parameters.ModelType == TemplateModelType.Shape &&
                                         parameters.FeatureExtractionMode != ShapeFeatureExtractionMode.EdgeContours
                    ? activeMetric
                    : DefaultMetric;
            }

            if (string.IsNullOrWhiteSpace(parameters.NccMetric))
            {
                parameters.NccMetric = parameters.ModelType == TemplateModelType.Ncc
                    ? activeMetric
                    : DefaultMetric;
            }
        }

        private static string ResolveActiveMetric(TemplateEditorParameters parameters)
        {
            if (parameters.ModelType == TemplateModelType.Ncc)
            {
                return NormalizeMetric(parameters.NccMetric);
            }

            return parameters.FeatureExtractionMode == ShapeFeatureExtractionMode.EdgeContours
                ? EdgeContourMetric
                : NormalizeMetric(parameters.ShapeMetric);
        }

        private static string NormalizeMetric(string metric)
        {
            return string.IsNullOrWhiteSpace(metric) ? DefaultMetric : metric;
        }

        private bool CanRunServiceOperation(string operationName)
        {
            if (_busy)
            {
                return false;
            }

            if (Service == null)
            {
                MessageBox.Show(
                    this,
                    operationName + "需要注入 ITemplateEditorService。",
                    operationName,
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return false;
            }

            return true;
        }

        private void RaiseEditorStateChanged()
        {
            EventHandler handler = EditorStateChanged;
            if (handler != null)
            {
                handler(this, EventArgs.Empty);
            }
        }

        private void SetStatus(string message, bool isError)
        {
            _statusLabel.Text = message ?? string.Empty;
            _statusLabel.ForeColor = isError ? Color.Firebrick : SystemColors.ControlText;
        }

        private void AppendOperationLog(bool success, string message)
        {
            string line = string.Format(
                "[{0:HH:mm:ss}] {1} {2}",
                DateTime.Now,
                success ? "OK" : "NG",
                string.IsNullOrWhiteSpace(message) ? string.Empty : message);
            _operationLog.AppendText(line + Environment.NewLine);
        }

        private static void EnsureInitialGeometry(
            TemplateEditorState state,
            Size imageSize,
            bool enableDetectionRoi)
        {
            // 默认不自动创建模板/搜索/检测 ROI，保留由用户显式添加或框选；
            // 若已有模板 ROI 但尚未设置基准中心，则自动将其置于模板 ROI 中心。
            if (state.TemplateRoi != null &&
                state.ReferenceAnchor.Row == 0.0 &&
                state.ReferenceAnchor.Column == 0.0)
            {
                state.ReferenceAnchor = new ImageCoordinate(
                    state.TemplateRoi.CenterRow,
                    state.TemplateRoi.CenterColumn);
            }
        }

        /// <summary>线程安全的后台预览图像引用计数持有器，避免每次拖拽重复复制大尺寸 Bitmap。</summary>
        private sealed class PreviewImageHolder : IDisposable
        {
            private readonly object _sync = new object();
            private Bitmap _bitmap;
            private int _refCount;
            private bool _disposed;

            public PreviewImageHolder(Bitmap source, bool clone = true)
            {
                _bitmap = clone ? CloneToFastPArgb(source) : source;
            }

            public bool TryAcquire(out PreviewImageLease lease)
            {
                lock (_sync)
                {
                    if (_disposed || _bitmap == null)
                    {
                        lease = null;
                        return false;
                    }

                    _refCount++;
                    lease = new PreviewImageLease(this, _bitmap);
                    return true;
                }
            }

            public void Release()
            {
                Bitmap toDispose = null;
                lock (_sync)
                {
                    if (_refCount > 0) _refCount--;
                    if (_disposed && _refCount == 0 && _bitmap != null)
                    {
                        toDispose = _bitmap;
                        _bitmap = null;
                    }
                }
                if (toDispose != null) toDispose.Dispose();
            }

            public void Dispose()
            {
                Bitmap toDispose = null;
                lock (_sync)
                {
                    if (_disposed) return;
                    _disposed = true;
                    if (_refCount == 0 && _bitmap != null)
                    {
                        toDispose = _bitmap;
                        _bitmap = null;
                    }
                }
                if (toDispose != null) toDispose.Dispose();
            }
        }

        private sealed class PreviewImageLease : IDisposable
        {
            private PreviewImageHolder _owner;

            public PreviewImageLease(PreviewImageHolder owner, Bitmap image)
            {
                _owner = owner;
                Image = image;
            }

            public Bitmap Image { get; private set; }

            public void Dispose()
            {
                PreviewImageHolder owner = _owner;
                _owner = null;
                Image = null;
                if (owner != null) owner.Release();
            }
        }

        private static Bitmap CloneToFastPArgb(Bitmap source)
        {
            Bitmap copy = new Bitmap(
                Math.Max(1, source.Width),
                Math.Max(1, source.Height),
                System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            copy.SetResolution(
                source.HorizontalResolution > 0.0f ? source.HorizontalResolution : 96.0f,
                source.VerticalResolution > 0.0f ? source.VerticalResolution : 96.0f);
            using (Graphics g = Graphics.FromImage(copy))
            {
                g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
                g.DrawImageUnscaled(source, 0, 0);
            }
            return copy;
        }

        private static Panel CreateCardSection(string titleText, Control body)
        {
            Panel card = new Panel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                BackColor = Color.White,
                Padding = new Padding(10, 8, 10, 10),
                Margin = new Padding(0, 0, 0, 8)
            };
            card.Paint += (sender, e) =>
            {
                if (card.Width <= 1 || card.Height <= 1) return;
                using (Pen border = new Pen(Color.FromArgb(226, 232, 240), 1.0f))
                {
                    e.Graphics.DrawRectangle(border, 0, 0, card.Width - 1, card.Height - 1);
                }
            };
            TableLayoutPanel frame = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                BackColor = Color.White,
                ColumnCount = 1,
                RowCount = 2,
                Margin = Padding.Empty
            };
            frame.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100.0f));
            frame.RowStyles.Add(new RowStyle(SizeType.Absolute, 24.0f));
            frame.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Label title = new Label
            {
                Text = titleText,
                Dock = DockStyle.Fill,
                Font = new Font("Microsoft YaHei UI", 8.75f, FontStyle.Bold, GraphicsUnit.Point),
                ForeColor = Color.FromArgb(15, 23, 42),
                TextAlign = ContentAlignment.MiddleLeft
            };
            frame.Controls.Add(title, 0, 0);
            frame.Controls.Add(body, 0, 1);
            card.Controls.Add(frame);
            return card;
        }

        private static ToolStripButton CreateToolButton(
            string text,
            string toolTip,
            EventHandler click,
            bool checkOnClick = false)
        {
            ToolStripButton button = new ToolStripButton(text)
            {
                ToolTipText = toolTip,
                CheckOnClick = checkOnClick,
                DisplayStyle = ToolStripItemDisplayStyle.Text,
                ForeColor = Color.FromArgb(226, 232, 240),
                AutoSize = true
            };
            button.Click += click;
            return button;
        }

        private static Button CreateActionButton(string text, EventHandler click, bool primary)
        {
            Button button = CreateModernButton(text, 96, 34, primary);
            button.AutoSize = true;
            button.MinimumSize = new Size(96, 34);
            button.Click += click;
            return button;
        }

        private static Button CreateModernButton(string text, int width, int height, bool primary)
        {
            Button button = new Button
            {
                Text = text,
                Width = width,
                Height = height,
                FlatStyle = FlatStyle.Flat,
                Cursor = Cursors.Hand,
                BackColor = primary ? Color.FromArgb(37, 99, 235) : Color.White,
                ForeColor = primary ? Color.White : Color.FromArgb(15, 23, 42),
                Font = new Font("Microsoft YaHei UI", 8.75f, primary ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Point),
                UseVisualStyleBackColor = false,
                Margin = new Padding(0, 0, 8, 0)
            };
            button.FlatAppearance.BorderColor = primary ? Color.FromArgb(37, 99, 235) : Color.FromArgb(203, 213, 225);
            button.FlatAppearance.BorderSize = 1;
            button.FlatAppearance.MouseOverBackColor = primary ? Color.FromArgb(29, 78, 216) : Color.FromArgb(241, 245, 249);
            button.FlatAppearance.MouseDownBackColor = primary ? Color.FromArgb(30, 64, 175) : Color.FromArgb(226, 232, 240);
            return button;
        }

        private static CheckBox CreateAdvancedToggle()
        {
            CheckBox toggle = new CheckBox
            {
                Appearance = Appearance.Button,
                AutoSize = false,
                Checked = false,
                Cursor = Cursors.Hand,
                Dock = DockStyle.Fill,
                FlatStyle = FlatStyle.Flat,
                Height = 32,
                Margin = new Padding(3, 8, 3, 6),
                Text = "高级选项 ▶",
                TextAlign = ContentAlignment.MiddleCenter,
                UseVisualStyleBackColor = true
            };
            toggle.FlatAppearance.BorderColor = Color.FromArgb(188, 194, 201);
            return toggle;
        }

        private static ComboBox CreateOptionCombo(IEnumerable<ComboOption> items)
        {
            ComboBox combo = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Dock = DockStyle.Fill,
                IntegralHeight = false,
                DropDownHeight = 240
            };
            combo.Items.AddRange(items.Cast<object>().ToArray());
            if (combo.Items.Count > 0)
            {
                combo.SelectedIndex = 0;
            }
            return combo;
        }

        private static ComboBox CreateCombo(IEnumerable<string> items)
        {
            ComboBox combo = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Dock = DockStyle.Fill,
                IntegralHeight = false,
                DropDownHeight = 240
            };
            combo.Items.AddRange(items.Cast<object>().ToArray());
            if (combo.Items.Count > 0)
            {
                combo.SelectedIndex = 0;
            }
            return combo;
        }

        private static NumericUpDown CreateDecimalInput(
            decimal minimum,
            decimal maximum,
            decimal value,
            decimal increment,
            int decimals)
        {
            NumericUpDown input = new NumericUpDown
            {
                Minimum = minimum,
                Maximum = maximum,
                Value = Math.Max(minimum, Math.Min(maximum, value)),
                Increment = increment,
                DecimalPlaces = decimals,
                Dock = DockStyle.Fill,
                ThousandsSeparator = maximum >= 10000m
            };
            input.MouseWheel += NumericInput_MouseWheel;
            return input;
        }

        private static void NumericInput_MouseWheel(object sender, MouseEventArgs eventArgs)
        {
            // 拦截系统默认 3 倍滚轮跳跃，按单倍 Increment 步进并钳位到合法上下限
            HandledMouseEventArgs handled = eventArgs as HandledMouseEventArgs;
            if (handled != null) handled.Handled = true;

            NumericUpDown input = sender as NumericUpDown;
            if (input == null || !input.Focused || eventArgs.Delta == 0) return;

            decimal value;
            if (eventArgs.Delta > 0)
            {
                decimal remaining = input.Maximum - input.Value;
                value = remaining <= input.Increment ? input.Maximum : input.Value + input.Increment;
            }
            else
            {
                decimal remaining = input.Value - input.Minimum;
                value = remaining <= input.Increment ? input.Minimum : input.Value - input.Increment;
            }

            if (value != input.Value) input.Value = value;
        }

        private static NumericUpDown CreateIntegerInput(int minimum, int maximum, int value, int increment = 1)
        {
            return CreateDecimalInput(minimum, maximum, value, increment, 0);
        }

        private static TableLayoutPanel CreateParameterTable()
        {
            TableLayoutPanel table = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                ColumnCount = 2,
                RowCount = 0,
                Padding = new Padding(8)
            };
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 48.0f));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 52.0f));
            return table;
        }

        private static Button CreatePanelToggleButton(string text, EventHandler onClick)
        {
            Button button = new Button
            {
                Text = text,
                Dock = DockStyle.Fill,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.White,
                ForeColor = Color.FromArgb(30, 41, 59),
                TextAlign = ContentAlignment.MiddleCenter,
                Margin = new Padding(2, 2, 2, 2),
                Padding = Padding.Empty,
                Cursor = Cursors.Hand
            };
            button.FlatAppearance.BorderColor = Color.FromArgb(203, 213, 225);
            button.FlatAppearance.BorderSize = 1;
            button.Click += onClick;
            return button;
        }

        private static Button CreateCompactButton(string text, EventHandler onClick)
        {
            Button button = new Button
            {
                Text = text,
                Dock = DockStyle.Fill,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.White,
                ForeColor = Color.FromArgb(51, 65, 85),
                Margin = new Padding(2, 3, 2, 3),
                Cursor = Cursors.Hand
            };
            button.FlatAppearance.BorderColor = Color.FromArgb(203, 213, 225);
            button.FlatAppearance.BorderSize = 1;
            button.Click += onClick;
            return button;
        }

        private static void AddStackSection(TableLayoutPanel stack, Control section)
        {
            int row = stack.RowCount++;
            stack.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            section.Dock = DockStyle.Top;
            stack.Controls.Add(section, 0, row);
        }

        private static void ToggleAdvancedPanel(Panel scrollContainer, TableLayoutPanel stack, CheckBox toggle, Panel advancedPanel)
        {
            bool expanded = toggle.Checked;
            toggle.Text = expanded ? "高级选项 ▼" : "高级选项 ▶";
            // 单容器 + WM_SETREDRAW 冻结，一次性原子展开/折叠，彻底消除逐行重排卡顿
            using (UiRenderOptimizer.BeginFreezeScope(scrollContainer, stack, advancedPanel))
            {
                advancedPanel.Visible = expanded;
            }
        }

        private static void AddParameterRow(TableLayoutPanel table, string labelText, Control control)
        {
            int row = table.RowCount++;
            table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Label label = new Label
            {
                Text = labelText,
                AutoSize = true,
                Anchor = AnchorStyles.Left,
                Margin = new Padding(3, 8, 6, 8)
            };
            control.Margin = new Padding(3, 4, 3, 4);
            table.Controls.Add(label, 0, row);
            table.Controls.Add(control, 1, row);
        }

        private static void AddControlRow(TableLayoutPanel table, Control control)
        {
            int row = table.RowCount++;
            table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            control.Margin = new Padding(3, 7, 3, 7);
            table.Controls.Add(control, 0, row);
            table.SetColumnSpan(control, 2);
        }

        private static Control WrapScrollable(Control child)
        {
            Panel panel = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
            panel.Controls.Add(child);
            return panel;
        }

        private static void SetNumeric(NumericUpDown control, double value)
        {
            decimal decimalValue;
            try
            {
                decimalValue = Convert.ToDecimal(value);
            }
            catch (OverflowException)
            {
                decimalValue = value < 0 ? control.Minimum : control.Maximum;
            }
            control.Value = Math.Max(control.Minimum, Math.Min(control.Maximum, decimalValue));
        }

        private static string GetComboValue(ComboBox combo, string fallback)
        {
            if (combo == null || combo.SelectedItem == null) return fallback;
            ComboOption option = combo.SelectedItem as ComboOption;
            if (option != null) return option.Value;
            string raw = Convert.ToString(combo.SelectedItem);
            return string.IsNullOrWhiteSpace(raw) ? fallback : raw;
        }

        private static void SelectComboValue(ComboBox combo, string value)
        {
            if (combo == null || combo.Items.Count == 0) return;
            string target = value ?? string.Empty;
            for (int i = 0; i < combo.Items.Count; i++)
            {
                ComboOption option = combo.Items[i] as ComboOption;
                if (option != null && string.Equals(option.Value, target, StringComparison.OrdinalIgnoreCase))
                {
                    combo.SelectedIndex = i;
                    return;
                }
            }
            int index = combo.FindStringExact(target);
            combo.SelectedIndex = index >= 0 ? index : 0;
        }

        private sealed class ComboOption
        {
            public ComboOption(string value, string label)
            {
                Value = value;
                Label = label;
            }

            public string Value { get; private set; }
            public string Label { get; private set; }
            public override string ToString() { return Label; }
        }
    }

    /// <summary>
    /// 模板测试匹配完成事件参数。
    /// </summary>
    public sealed class TemplateTestCompletedEventArgs : EventArgs
    {
        public TemplateTestCompletedEventArgs(TemplateTestResult result)
        {
            Result = result;
        }

        public TemplateTestResult Result { get; private set; }
    }
}
