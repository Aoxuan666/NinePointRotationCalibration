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
    public sealed class TemplateEditorForm : Form
    {
        private const int HistoryLimit = 30;

        private readonly CalibrationCanvas _canvas;
        private readonly Stack<TemplateEditorState> _undoStack = new Stack<TemplateEditorState>();
        private readonly Stack<TemplateEditorState> _redoStack = new Stack<TemplateEditorState>();
        private readonly ToolStripButton _undoButton;
        private readonly ToolStripButton _redoButton;
        private readonly ToolStripButton _maskVisibleButton;
        private readonly ToolStripButton _selectToolButton;
        private readonly ToolStripButton _templateToolButton;
        private readonly ToolStripButton _searchToolButton;
        private readonly ToolStripButton _eraseToolButton;
        private readonly ToolStripButton _restoreToolButton;
        private readonly TabControl _parameterTabs;
        private readonly ComboBox _modelTypeCombo;
        private readonly NumericUpDown _levelsInput;
        private readonly NumericUpDown _angleStartInput;
        private readonly NumericUpDown _angleExtentInput;
        private readonly NumericUpDown _angleStepInput;
        private readonly ComboBox _optimizationCombo;
        private readonly ComboBox _metricCombo;
        private readonly NumericUpDown _contrastInput;
        private readonly NumericUpDown _minimumContrastInput;
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
        private readonly Label _modelStateLabel;
        private readonly ToolStripStatusLabel _statusLabel;
        private readonly ToolStripProgressBar _progressBar;

        private Bitmap _referenceImage;
        private TemplateEditorState _state;
        private TemplateEditorState _originalState;
        private CancellationTokenSource _operationCancellation;
        private bool _loadingControls;
        private bool _busy;
        private bool _initialBoundsConstrained;
        private bool _closing;

        public TemplateEditorForm(
            Bitmap referenceImage,
            TemplateEditorState initialState = null,
            ITemplateEditorService service = null)
        {
            if (referenceImage == null)
            {
                throw new ArgumentNullException(nameof(referenceImage));
            }

            Service = service;
            _referenceImage = new Bitmap(referenceImage);
            _state = initialState == null ? new TemplateEditorState() : initialState.DeepClone();
            EnsureInitialGeometry(_state, _referenceImage.Size);
            _originalState = _state.DeepClone();

            Text = "模板匹配设置";
            StartPosition = FormStartPosition.CenterParent;
            MinimumSize = new Size(1120, 700);
            Size = new Size(1380, 840);
            KeyPreview = true;
            ShowIcon = false;
            Font = new Font("Microsoft YaHei UI", 9.0f, FontStyle.Regular, GraphicsUnit.Point);
            BackColor = Color.FromArgb(244, 246, 248);

            _canvas = new CalibrationCanvas { Dock = DockStyle.Fill };

            _undoButton = CreateToolButton("撤销", "撤销上一步模板编辑", UndoEdit);
            _redoButton = CreateToolButton("重做", "重做刚才撤销的模板编辑", RedoEdit);
            _maskVisibleButton = CreateToolButton("掩膜", "显示或隐藏掩膜叠加", ToggleMaskVisibility, true);
            _selectToolButton = CreateToolButton("选择", "选择、移动、缩放或旋转 ROI", (sender, args) => SelectTool(CanvasEditTool.Select), true);
            _templateToolButton = CreateToolButton("模板区", "拖动创建新的模板 ROI", (sender, args) => SelectTool(CanvasEditTool.DrawTemplateRoi), true);
            _searchToolButton = CreateToolButton("搜索区", "拖动创建新的搜索 ROI", (sender, args) => SelectTool(CanvasEditTool.DrawSearchRoi), true);
            _eraseToolButton = CreateToolButton("擦除", "使用画笔从模板有效域中排除区域", (sender, args) => SelectTool(CanvasEditTool.EraseMask), true);
            _restoreToolButton = CreateToolButton("恢复", "使用画笔恢复已擦除的模板区域", (sender, args) => SelectTool(CanvasEditTool.RestoreMask), true);

            _modelTypeCombo = CreateCombo(new[] { "Shape", "NCC" });
            _levelsInput = CreateIntegerInput(0, 12, 0);
            _angleStartInput = CreateDecimalInput(-180, 180, -30, 1, 2);
            _angleExtentInput = CreateDecimalInput(0, 360, 60, 1, 2);
            _angleStepInput = CreateDecimalInput(0, 30, 0, 0.1m, 3);
            _optimizationCombo = CreateCombo(new[] { "auto", "none", "point_reduction_low", "point_reduction_medium", "point_reduction_high" });
            _metricCombo = CreateCombo(new[] { "use_polarity", "ignore_global_polarity", "ignore_local_polarity", "ignore_color_polarity" });
            _contrastInput = CreateDecimalInput(1, 255, 30, 1, 1);
            _minimumContrastInput = CreateDecimalInput(0, 255, 10, 1, 1);
            _minimumScoreInput = CreateDecimalInput(0, 1, 0.6m, 0.01m, 3);
            _greedinessInput = CreateDecimalInput(0, 1, 0.8m, 0.01m, 3);
            _maximumOverlapInput = CreateDecimalInput(0, 1, 0.5m, 0.01m, 3);
            _matchCountInput = CreateIntegerInput(0, 100, 1);
            _subPixelCombo = CreateCombo(new[] { "none", "interpolation", "least_squares", "least_squares_high", "least_squares_very_high" });
            _timeoutInput = CreateIntegerInput(50, 60000, 2000, 50);
            _contourPointSpacingInput = CreateDecimalInput(0.5m, 100, 3, 0.5m, 1);
            _allowPartialCheck = new CheckBox { Text = "允许模板部分超出搜索区域", AutoSize = true };
            _brushRadiusInput = CreateDecimalInput(1, 500, 12, 1, 1);
            _anchorRowInput = CreateDecimalInput(-100000, 100000, 0, 1, 3);
            _anchorColumnInput = CreateDecimalInput(-100000, 100000, 0, 1, 3);
            _anchorLockedCheck = new CheckBox { Text = "锁定基准中心", AutoSize = true };
            _templateLockedCheck = new CheckBox { Text = "锁定模板 ROI", AutoSize = true };
            _searchLockedCheck = new CheckBox { Text = "锁定搜索 ROI", AutoSize = true };

            _extractButton = CreateActionButton("提取特征", ExtractFeaturesAsync);
            _createButton = CreateActionButton("创建模型", CreateModelAsync);
            _testButton = CreateActionButton("测试匹配", TestMatchAsync);
            _okButton = new Button { Text = "确认", Width = 96, Height = 34 };
            _cancelButton = new Button { Text = "取消", Width = 96, Height = 34, DialogResult = DialogResult.Cancel };
            _operationLog = new TextBox
            {
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                Dock = DockStyle.Fill,
                BackColor = Color.White,
                MinimumSize = new Size(0, 100)
            };
            _modelStateLabel = new Label
            {
                AutoSize = false,
                Height = 34,
                Dock = DockStyle.Top,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(8, 0, 8, 0)
            };
            _statusLabel = new ToolStripStatusLabel { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
            _progressBar = new ToolStripProgressBar
            {
                Style = ProgressBarStyle.Marquee,
                MarqueeAnimationSpeed = 30,
                Visible = false,
                Width = 130
            };

            _parameterTabs = new TabControl { Dock = DockStyle.Fill };
            BuildLayout();
            WireEvents();
            LoadStateToControls(true);
            SelectTool(CanvasEditTool.Select);
            SetStatus(Service == null
                ? "尚未注入模板服务；可编辑已有模板，提取/建模/测试暂不可用。"
                : "请设置模板区域、搜索区域和基准中心。", false);
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

        public void SetState(TemplateEditorState state)
        {
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            CancelCurrentOperation();
            _state = state.DeepClone();
            EnsureInitialGeometry(_state, _referenceImage.Size);
            _originalState = _state.DeepClone();
            _undoStack.Clear();
            _redoStack.Clear();
            LoadStateToControls(true);
        }

        public void SetReferenceImage(Bitmap image, bool resetView = true)
        {
            if (image == null)
            {
                throw new ArgumentNullException(nameof(image));
            }

            Bitmap previous = _referenceImage;
            _referenceImage = new Bitmap(image);
            previous.Dispose();
            EnsureInitialGeometry(_state, _referenceImage.Size);
            _canvas.SetImage(_referenceImage);
            if (resetView)
            {
                _canvas.FitImage();
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                CancelCurrentOperation();

                if (_referenceImage != null)
                {
                    _referenceImage.Dispose();
                    _referenceImage = null;
                }
            }

            base.Dispose(disposing);
        }

        protected override void OnFormClosing(FormClosingEventArgs eventArgs)
        {
            _closing = true;
            if (_busy && eventArgs.CloseReason == CloseReason.UserClosing)
            {
                CancelCurrentOperation();
            }

            base.OnFormClosing(eventArgs);
        }

        private void HandleFirstShown(object sender, EventArgs eventArgs)
        {
            if (_initialBoundsConstrained)
            {
                return;
            }

            _initialBoundsConstrained = true;
            ConstrainToWorkingArea();
        }

        private void ConstrainToWorkingArea()
        {
            Screen screen = Screen.FromControl(this);
            Rectangle workingArea = screen.WorkingArea;
            if (workingArea.Width <= 0 || workingArea.Height <= 0)
            {
                return;
            }

            // DPI scaling is complete by Shown. Clamp the scaled minimum and
            // requested size together so the footer remains above the taskbar.
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
                RenderMode = ToolStripRenderMode.System,
                Padding = new Padding(5, 3, 5, 3)
            };
            tools.Items.Add(CreateToolButton("适应", "使整幅图像适应窗口", (sender, args) => _canvas.FitImage()));
            tools.Items.Add(CreateToolButton("1:1", "以一个图像像素对应一个屏幕像素显示", (sender, args) => _canvas.ShowOneToOne()));
            tools.Items.Add(new ToolStripSeparator());
            tools.Items.Add(_selectToolButton);
            tools.Items.Add(_templateToolButton);
            tools.Items.Add(_searchToolButton);
            tools.Items.Add(new ToolStripSeparator());
            tools.Items.Add(_eraseToolButton);
            tools.Items.Add(_restoreToolButton);
            tools.Items.Add(_maskVisibleButton);
            tools.Items.Add(CreateToolButton("清除掩膜", "清除所有擦除和恢复笔画", ClearMask));
            tools.Items.Add(CreateToolButton("反选掩膜", "反转模板有效域的包含关系", InvertMask));
            tools.Items.Add(new ToolStripSeparator());
            tools.Items.Add(_undoButton);
            tools.Items.Add(_redoButton);

            SplitContainer mainSplit = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Size = new Size(1200, 700),
                Orientation = Orientation.Vertical,
                SplitterWidth = 6,
                FixedPanel = FixedPanel.Panel2,
                Panel2MinSize = 370,
                SplitterDistance = 800
            };
            mainSplit.Panel1.Controls.Add(_canvas);
            mainSplit.Panel1.Controls.Add(tools);

            BuildParameterTabs();
            Panel rightPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8) };
            rightPanel.Controls.Add(_parameterTabs);
            rightPanel.Controls.Add(_modelStateLabel);
            mainSplit.Panel2.Controls.Add(rightPanel);

            FlowLayoutPanel footer = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                Height = 52,
                FlowDirection = FlowDirection.RightToLeft,
                WrapContents = false,
                Padding = new Padding(8),
                BackColor = Color.White
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
            TabPage teachPage = new TabPage("示教参数");
            TableLayoutPanel teachTable = CreateParameterTable();
            AddParameterRow(teachTable, "模型类型", _modelTypeCombo);
            AddParameterRow(teachTable, "金字塔层数", _levelsInput);
            AddParameterRow(teachTable, "起始角度 (°)", _angleStartInput);
            AddParameterRow(teachTable, "角度范围 (°)", _angleExtentInput);
            AddParameterRow(teachTable, "角度步长 (°)", _angleStepInput);
            AddParameterRow(teachTable, "优化方式", _optimizationCombo);
            AddParameterRow(teachTable, "极性 / Metric", _metricCombo);
            AddParameterRow(teachTable, "对比度", _contrastInput);
            AddParameterRow(teachTable, "最小对比度", _minimumContrastInput);
            AddParameterRow(teachTable, "画笔半径 (px)", _brushRadiusInput);
            teachTable.Controls.Add(_templateLockedCheck, 0, teachTable.RowCount);
            teachTable.SetColumnSpan(_templateLockedCheck, 2);
            teachTable.RowCount++;
            teachTable.Controls.Add(_searchLockedCheck, 0, teachTable.RowCount);
            teachTable.SetColumnSpan(_searchLockedCheck, 2);
            teachTable.RowCount++;
            teachPage.Controls.Add(WrapScrollable(teachTable));

            TabPage matchPage = new TabPage("运行参数");
            TableLayoutPanel matchTable = CreateParameterTable();
            AddParameterRow(matchTable, "最低分数", _minimumScoreInput);
            AddParameterRow(matchTable, "Greediness", _greedinessInput);
            AddParameterRow(matchTable, "最大重叠", _maximumOverlapInput);
            AddParameterRow(matchTable, "匹配数量 (0=全部)", _matchCountInput);
            AddParameterRow(matchTable, "亚像素", _subPixelCombo);
            AddParameterRow(matchTable, "超时 (ms)", _timeoutInput);
            AddParameterRow(matchTable, "轮廓采点间隔半径 (px)", _contourPointSpacingInput);
            matchTable.Controls.Add(_allowPartialCheck, 0, matchTable.RowCount);
            matchTable.SetColumnSpan(_allowPartialCheck, 2);
            matchTable.RowCount++;
            matchPage.Controls.Add(WrapScrollable(matchTable));

            TabPage anchorPage = new TabPage("基准中心");
            TableLayoutPanel anchorTable = CreateParameterTable();
            AddParameterRow(anchorTable, "Row", _anchorRowInput);
            AddParameterRow(anchorTable, "Column", _anchorColumnInput);
            anchorTable.Controls.Add(_anchorLockedCheck, 0, anchorTable.RowCount);
            anchorTable.SetColumnSpan(_anchorLockedCheck, 2);
            anchorTable.RowCount++;
            Button resetAnchor = new Button { Text = "重置为模板 ROI 中心", AutoSize = true, Height = 32 };
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

            TabPage operationsPage = new TabPage("模型与测试");
            TableLayoutPanel operationLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 3,
                Padding = new Padding(8)
            };
            operationLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            operationLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            operationLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100.0f));
            FlowLayoutPanel operationButtons = new FlowLayoutPanel
            {
                AutoSize = true,
                Dock = DockStyle.Top,
                WrapContents = true
            };
            operationButtons.Controls.Add(_extractButton);
            operationButtons.Controls.Add(_createButton);
            operationButtons.Controls.Add(_testButton);
            Label flowHelp = new Label
            {
                Text = "推荐流程：提取特征 -> 检查掩膜 -> 创建模型 -> 测试匹配。",
                AutoSize = true,
                ForeColor = Color.DimGray,
                Padding = new Padding(0, 6, 0, 8)
            };
            operationLayout.Controls.Add(operationButtons, 0, 0);
            operationLayout.Controls.Add(flowHelp, 0, 1);
            operationLayout.Controls.Add(_operationLog, 0, 2);
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
            _canvas.SelectedRoiChanged += (sender, args) => UpdateRoiToolSelection();

            _modelTypeCombo.SelectedIndexChanged += StructuralParameterChanged;
            _levelsInput.ValueChanged += StructuralParameterChanged;
            _angleStartInput.ValueChanged += StructuralParameterChanged;
            _angleExtentInput.ValueChanged += StructuralParameterChanged;
            _angleStepInput.ValueChanged += StructuralParameterChanged;
            _optimizationCombo.SelectedIndexChanged += StructuralParameterChanged;
            _metricCombo.SelectedIndexChanged += StructuralParameterChanged;
            _contrastInput.ValueChanged += StructuralParameterChanged;
            _minimumContrastInput.ValueChanged += StructuralParameterChanged;

            _minimumScoreInput.ValueChanged += RuntimeParameterChanged;
            _greedinessInput.ValueChanged += RuntimeParameterChanged;
            _maximumOverlapInput.ValueChanged += RuntimeParameterChanged;
            _matchCountInput.ValueChanged += RuntimeParameterChanged;
            _subPixelCombo.SelectedIndexChanged += RuntimeParameterChanged;
            _timeoutInput.ValueChanged += RuntimeParameterChanged;
            _contourPointSpacingInput.ValueChanged += DisplayParameterChanged;
            _allowPartialCheck.CheckedChanged += RuntimeParameterChanged;
            _brushRadiusInput.ValueChanged += BrushRadiusChanged;

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
                SetNumeric(_levelsInput, parameters.NumLevels);
                SetNumeric(_angleStartInput, parameters.AngleStartDegrees);
                SetNumeric(_angleExtentInput, parameters.AngleExtentDegrees);
                SetNumeric(_angleStepInput, parameters.AngleStepDegrees);
                SelectComboValue(_optimizationCombo, parameters.Optimization);
                SelectComboValue(_metricCombo, parameters.Metric);
                SetNumeric(_contrastInput, parameters.Contrast);
                SetNumeric(_minimumContrastInput, parameters.MinimumContrast);
                SetNumeric(_minimumScoreInput, parameters.MinimumScore);
                SetNumeric(_greedinessInput, parameters.Greediness);
                SetNumeric(_maximumOverlapInput, parameters.MaximumOverlap);
                SetNumeric(_matchCountInput, parameters.MatchCount);
                SelectComboValue(_subPixelCombo, parameters.SubPixel);
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
                    _canvas.SetImage(_referenceImage);
                }
                _canvas.TemplateRoi = _state.TemplateRoi;
                _canvas.SearchRoi = _state.SearchRoi;
                _canvas.ReferenceAnchor = _state.ReferenceAnchor;
                _canvas.AnchorLocked = _state.AnchorLocked;
                _canvas.TemplateRoiLocked = _state.TemplateRoiLocked;
                _canvas.SearchRoiLocked = _state.SearchRoiLocked;
                _canvas.MaskVisible = _state.MaskVisible;
                _canvas.BrushRadiusImage = parameters.BrushRadius;
                _canvas.SetMaskStrokes(_state.MaskStrokes);
                _maskVisibleButton.Checked = _state.MaskVisible;
                UpdateEditorOverlay();
                UpdateModelState();
                UpdateHistoryButtons();
                UpdateAnchorEnabledState();
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
                _state.TemplateRoi = eventArgs.Roi;
                if (eventArgs.IsFinal)
                {
                    MarkChanged(true);
                }
            }
            else if (eventArgs.Target == CanvasRoiTarget.Search)
            {
                _state.SearchRoi = eventArgs.Roi;
                if (eventArgs.IsFinal)
                {
                    MarkChanged(false);
                }
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
            _state.MaskStrokes = _canvas.GetMaskStrokes().ToList();
            MarkChanged(true);
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
            parameters.ModelType = _modelTypeCombo.SelectedIndex == 1 ? TemplateModelType.Ncc : TemplateModelType.Shape;
            parameters.NumLevels = (int)_levelsInput.Value;
            parameters.AngleStartDegrees = (double)_angleStartInput.Value;
            parameters.AngleExtentDegrees = (double)_angleExtentInput.Value;
            parameters.AngleStepDegrees = (double)_angleStepInput.Value;
            parameters.Optimization = Convert.ToString(_optimizationCombo.SelectedItem);
            parameters.Metric = Convert.ToString(_metricCombo.SelectedItem);
            parameters.Contrast = (double)_contrastInput.Value;
            parameters.MinimumContrast = (double)_minimumContrastInput.Value;
            parameters.MinimumScore = (double)_minimumScoreInput.Value;
            parameters.Greediness = (double)_greedinessInput.Value;
            parameters.MaximumOverlap = (double)_maximumOverlapInput.Value;
            parameters.MatchCount = (int)_matchCountInput.Value;
            parameters.SubPixel = Convert.ToString(_subPixelCombo.SelectedItem);
            parameters.TimeoutMilliseconds = (int)_timeoutInput.Value;
            parameters.ContourPointSpacingPixels = (double)_contourPointSpacingInput.Value;
            parameters.AllowPartialMatch = _allowPartialCheck.Checked;
            parameters.BrushRadius = (double)_brushRadiusInput.Value;
            _state.Parameters = parameters;
        }

        private async void ExtractFeaturesAsync(object sender, EventArgs eventArgs)
        {
            if (!CanRunServiceOperation("提取特征"))
            {
                return;
            }

            TemplateFeatureResult result = null;
            await RunBusyOperationAsync("正在提取模板特征...", async token =>
            {
                using (Bitmap image = new Bitmap(_referenceImage))
                {
                    result = await Service.ExtractFeaturesAsync(image, _state.DeepClone(), token);
                }
            });
            if (result == null)
            {
                return;
            }

            AppendOperationLog(result.Success, result.Message);
            if (!result.Success)
            {
                SetStatus(result.Message ?? "提取特征失败。", true);
                return;
            }

            _state.FeaturePoints = result.FeaturePoints ?? new List<ImageCoordinate>();
            _state.ModelContours = result.Contours ?? new List<List<ImageCoordinate>>();
            if (result.RecommendedParameters != null)
            {
                PushUndoState();
                _state.Parameters = result.RecommendedParameters.DeepClone();
                _state.ModelDirty = true;
                LoadStateToControls(false);
            }
            else
            {
                UpdateEditorOverlay();
            }

            SetStatus(string.Format("特征提取完成，共 {0} 个特征点。", _state.FeaturePoints.Count), false);
        }

        private async void CreateModelAsync(object sender, EventArgs eventArgs)
        {
            if (!CanRunServiceOperation("创建模型"))
            {
                return;
            }

            string validationMessage;
            if (!ValidateGeometry(out validationMessage))
            {
                if (!string.IsNullOrWhiteSpace(validationMessage))
                {
                    MessageBox.Show(this, validationMessage, "模板参数", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
                return;
            }

            ReadParametersFromControls();
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
                return;
            }

            AppendOperationLog(result.Success, result.Message);
            if (!result.Success || result.ModelData == null || result.ModelData.Length == 0)
            {
                SetStatus(result.Message ?? "模型创建失败。", true);
                return;
            }

            PushUndoState();
            EnsureRevisionAdvanced();
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
            SetStatus(string.Format("模型创建完成，数据大小 {0:N0} 字节。", _state.ModelData.Length), false);
        }

        private async void TestMatchAsync(object sender, EventArgs eventArgs)
        {
            if (!CanRunServiceOperation("测试匹配"))
            {
                return;
            }

            if (!_state.HasModel || _state.ModelDirty)
            {
                MessageBox.Show(this, "模板尚未创建或参数已改变，请先创建模型。", "测试匹配", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            ReadParametersFromControls();
            // Test view should contain only the current match result, never the previous feature overlay.
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
            if (result == null)
            {
                return;
            }

            AppendOperationLog(result.Success, result.Message);
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
            _extractButton.Enabled = !busy && Service != null;
            _createButton.Enabled = !busy && Service != null;
            _testButton.Enabled = !busy && Service != null;
            _okButton.Enabled = !busy;
            _parameterTabs.Enabled = !busy;
            _canvas.Enabled = !busy;
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
                // Render the matched model contour as green points only. Do not connect
                // contour samples with polylines because the sampled order is not a
                // display topology contract for every HALCON model.
                double spacing = Math.Max(0.5, _state.Parameters.ContourPointSpacingPixels);
                double minimumDistanceSquared = spacing * spacing;
                foreach (List<ImageCoordinate> contour in result.MatchedContours ?? new List<List<ImageCoordinate>>())
                {
                    bool hasLastPoint = false;
                    ImageCoordinate lastPoint = new ImageCoordinate();
                    foreach (ImageCoordinate point in contour ?? new List<ImageCoordinate>())
                    {
                        double deltaRow = point.Row - lastPoint.Row;
                        double deltaColumn = point.Column - lastPoint.Column;
                        if (hasLastPoint &&
                            ((deltaRow * deltaRow) + (deltaColumn * deltaColumn)) < minimumDistanceSquared)
                        {
                            continue;
                        }

                        overlay.Points.Add(new CanvasPointOverlay
                        {
                            Position = point,
                            Color = Color.Lime,
                            RadiusPixels = 2.0f
                        });
                        lastPoint = point;
                        hasLastPoint = true;
                    }
                }

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
            foreach (ImageCoordinate point in _state.FeaturePoints ?? new List<ImageCoordinate>())
            {
                overlay.Points.Add(new CanvasPointOverlay
                {
                    Position = point,
                    Color = Color.Cyan,
                    RadiusPixels = 2.0f
                });
            }

            foreach (List<ImageCoordinate> contour in _state.ModelContours ?? new List<List<ImageCoordinate>>())
            {
                overlay.Polylines.Add(new CanvasPolylineOverlay
                {
                    Points = contour,
                    Color = Color.Orange,
                    WidthPixels = 1.2f,
                    Closed = true
                });
            }

            _canvas.EditorOverlay = overlay;
        }

        private void SelectTool(CanvasEditTool tool)
        {
            _canvas.Tool = tool;
            _selectToolButton.Checked = tool == CanvasEditTool.Select;
            _templateToolButton.Checked = tool == CanvasEditTool.DrawTemplateRoi;
            _searchToolButton.Checked = tool == CanvasEditTool.DrawSearchRoi;
            _eraseToolButton.Checked = tool == CanvasEditTool.EraseMask;
            _restoreToolButton.Checked = tool == CanvasEditTool.RestoreMask;
            if (tool == CanvasEditTool.DrawTemplateRoi)
            {
                _canvas.SelectedRoi = CanvasRoiTarget.Template;
            }
            else if (tool == CanvasEditTool.DrawSearchRoi)
            {
                _canvas.SelectedRoi = CanvasRoiTarget.Search;
            }
        }

        private void UpdateRoiToolSelection()
        {
            if (_canvas.Tool != CanvasEditTool.Select)
            {
                return;
            }

            _templateToolButton.Checked = false;
            _searchToolButton.Checked = false;
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
            MarkChanged(true);
        }

        private void InvertMask(object sender, EventArgs eventArgs)
        {
            PushUndoState();
            _state.MaskInverted = !_state.MaskInverted;
            MarkChanged(true);
            SetStatus(_state.MaskInverted ? "掩膜已反选。" : "掩膜已恢复正常包含关系。", false);
        }

        private void ToggleMaskVisibility(object sender, EventArgs eventArgs)
        {
            _state.MaskVisible = _maskVisibleButton.Checked;
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

            _undoStack.Push(_state.DeepClone());
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

            _redoStack.Push(_state.DeepClone());
            _state = _undoStack.Pop();
            LoadStateToControls(false);
            RaiseEditorStateChanged();
        }

        private void RedoEdit(object sender, EventArgs eventArgs)
        {
            if (_redoStack.Count == 0 || _busy)
            {
                return;
            }

            _undoStack.Push(_state.DeepClone());
            _state = _redoStack.Pop();
            LoadStateToControls(false);
            RaiseEditorStateChanged();
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
            if (!_state.HasModel)
            {
                _modelStateLabel.Text = "未创建模型";
                _modelStateLabel.ForeColor = Color.White;
                _modelStateLabel.BackColor = Color.FromArgb(165, 94, 36);
            }
            else if (_state.ModelDirty)
            {
                _modelStateLabel.Text = "参数已修改，需要重新创建模型";
                _modelStateLabel.ForeColor = Color.White;
                _modelStateLabel.BackColor = Color.FromArgb(183, 74, 52);
            }
            else
            {
                _modelStateLabel.Text = string.Format("模型有效 · Rev {0} · {1:N0} bytes", _state.Revision, _state.ModelData.Length);
                _modelStateLabel.ForeColor = Color.White;
                _modelStateLabel.BackColor = Color.FromArgb(39, 132, 87);
            }

            _extractButton.Enabled = !_busy && Service != null;
            _createButton.Enabled = !_busy && Service != null;
            _testButton.Enabled = !_busy && Service != null && _state.HasModel && !_state.ModelDirty;
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

        private void ConfirmEditor(object sender, EventArgs eventArgs)
        {
            ReadParametersFromControls();
            if (!ValidateGeometry(out string message))
            {
                MessageBox.Show(this, message, "模板参数", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (!_state.HasModel || _state.ModelDirty)
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

        private bool ValidateGeometry(out string message)
        {
            if (_state.TemplateRoi == null || _state.TemplateRoi.HalfWidth < 2.0 || _state.TemplateRoi.HalfHeight < 2.0)
            {
                message = "请设置有效的模板 ROI。";
                return false;
            }

            if (_state.SearchRoi == null || _state.SearchRoi.HalfWidth < 2.0 || _state.SearchRoi.HalfHeight < 2.0)
            {
                message = "请设置有效的搜索 ROI。";
                return false;
            }

            if (!_state.SearchRoi.Contains(new ImageCoordinate(_state.TemplateRoi.CenterRow, _state.TemplateRoi.CenterColumn)))
            {
                message = "搜索 ROI 必须覆盖模板 ROI 的中心。";
                return false;
            }

            if (_state.Parameters.AngleExtentDegrees <= 0.0)
            {
                message = "角度范围必须大于 0°。";
                return false;
            }

            if (_state.Parameters.MinimumContrast > _state.Parameters.Contrast)
            {
                message = "最小对比度不能大于模板对比度。";
                return false;
            }

            message = null;
            return true;
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

        private static void EnsureInitialGeometry(TemplateEditorState state, Size imageSize)
        {
            if (state.TemplateRoi == null)
            {
                state.TemplateRoi = new RotatedRectangle(
                    imageSize.Height / 2.0,
                    imageSize.Width / 2.0,
                    Math.Max(10.0, imageSize.Width * 0.15),
                    Math.Max(10.0, imageSize.Height * 0.15),
                    0.0);
            }

            if (state.SearchRoi == null)
            {
                state.SearchRoi = new RotatedRectangle(
                    imageSize.Height / 2.0,
                    imageSize.Width / 2.0,
                    Math.Max(20.0, imageSize.Width * 0.42),
                    Math.Max(20.0, imageSize.Height * 0.42),
                    0.0);
            }

            if (state.ReferenceAnchor.Row == 0.0 && state.ReferenceAnchor.Column == 0.0)
            {
                state.ReferenceAnchor = new ImageCoordinate(state.TemplateRoi.CenterRow, state.TemplateRoi.CenterColumn);
            }
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
                AutoSize = true
            };
            button.Click += click;
            return button;
        }

        private static Button CreateActionButton(string text, EventHandler click)
        {
            Button button = new Button { Text = text, AutoSize = true, Height = 34, MinimumSize = new Size(96, 34) };
            button.Click += click;
            return button;
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
            return new NumericUpDown
            {
                Minimum = minimum,
                Maximum = maximum,
                Value = Math.Max(minimum, Math.Min(maximum, value)),
                Increment = increment,
                DecimalPlaces = decimals,
                Dock = DockStyle.Fill,
                ThousandsSeparator = true
            };
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

        private static void SelectComboValue(ComboBox combo, string value)
        {
            int index = combo.FindStringExact(value ?? string.Empty);
            combo.SelectedIndex = index >= 0 ? index : 0;
        }
    }

    public sealed class TemplateTestCompletedEventArgs : EventArgs
    {
        public TemplateTestCompletedEventArgs(TemplateTestResult result)
        {
            Result = result;
        }

        public TemplateTestResult Result { get; private set; }
    }
}
