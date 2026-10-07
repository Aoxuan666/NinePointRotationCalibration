using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Globalization;
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
    public sealed class CalibrationSetupForm : Form
    {
        private readonly CalibrationCanvas _canvas;
        private readonly BindingList<CalibrationSampleRow> _translationSamples = new BindingList<CalibrationSampleRow>();
        private readonly BindingList<CalibrationSampleRow> _rotationSamples = new BindingList<CalibrationSampleRow>();
        private readonly DataGridView _translationGrid;
        private readonly DataGridView _rotationGrid;
        private readonly RadioButton _translationModeButton;
        private readonly RadioButton _rotationModeButton;
        private readonly NumericUpDown _machineXInput;
        private readonly NumericUpDown _machineYInput;
        private readonly NumericUpDown _machineThetaInput;
        private readonly TextBox _tagInput;
        private readonly Label _currentMatchLabel;
        private readonly Button _locateButton;
        private readonly Button _addSampleButton;
        private readonly Button _replaceSampleButton;
        private readonly Button _deleteSampleButton;
        private readonly Button _calculateButton;
        private readonly Button _templateButton;
        private readonly Button _okButton;
        private readonly Button _cancelButton;
        private readonly Label _templateSummaryLabel;
        private readonly Label _sampleCountLabel;
        private readonly Label _resultBanner;
        private readonly TextBox _pixelToStageText;
        private readonly TextBox _stageToPixelText;
        private readonly TextBox _diagnosticsText;
        private readonly Dictionary<string, Label> _resultValues = new Dictionary<string, Label>();
        private readonly ToolStripStatusLabel _statusLabel;
        private readonly ToolStripProgressBar _progressBar;
        private readonly TextBox _nameInput;
        private readonly ComboBox _unitCombo;

        private Bitmap _currentImage;
        private CalibrationSetupState _state;
        private CalibrationLocateUiResult _currentLocate;
        private CancellationTokenSource _operationCancellation;
        private bool _busy;
        private bool _loading;
        private bool _initialBoundsConstrained;
        private bool _closing;

        public CalibrationSetupForm(
            Bitmap referenceImage,
            CalibrationSetupState initialState = null,
            ICalibrationWorkflow workflow = null)
        {
            Workflow = workflow;
            _state = initialState == null ? new CalibrationSetupState() : initialState.DeepClone();
            if (_state.Template == null)
            {
                _state.Template = new TemplateEditorState();
            }
            if (_state.Samples == null)
            {
                _state.Samples = new List<CalibrationSampleRow>();
            }

            Text = "九点加旋转标定";
            StartPosition = FormStartPosition.CenterParent;
            MinimumSize = new Size(1260, 760);
            Size = new Size(1540, 920);
            KeyPreview = true;
            ShowIcon = false;
            Font = new Font("Microsoft YaHei UI", 9.0f, FontStyle.Regular, GraphicsUnit.Point);
            BackColor = Color.FromArgb(244, 246, 248);

            _canvas = new CalibrationCanvas
            {
                Dock = DockStyle.Fill,
                Tool = CanvasEditTool.Select,
                ShowRois = true,
                TemplateRoiLocked = true,
                SearchRoiLocked = true,
                AnchorLocked = true
            };
            _translationGrid = CreateSampleGrid(false);
            _rotationGrid = CreateSampleGrid(true);
            _translationModeButton = new RadioButton
            {
                Text = "九点 XY",
                Appearance = Appearance.Button,
                AutoSize = false,
                Width = 100,
                Height = 32,
                TextAlign = ContentAlignment.MiddleCenter,
                Checked = true
            };
            _rotationModeButton = new RadioButton
            {
                Text = "旋转",
                Appearance = Appearance.Button,
                AutoSize = false,
                Width = 100,
                Height = 32,
                TextAlign = ContentAlignment.MiddleCenter
            };
            _machineXInput = CreateCoordinateInput();
            _machineYInput = CreateCoordinateInput();
            _machineThetaInput = CreateCoordinateInput();
            _tagInput = new TextBox { Dock = DockStyle.Fill };
            _currentMatchLabel = new Label
            {
                AutoSize = false,
                Height = 42,
                Dock = DockStyle.Top,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(8, 0, 8, 0),
                BackColor = Color.FromArgb(232, 235, 239),
                ForeColor = Color.FromArgb(50, 55, 62),
                Text = "尚未定位当前图像"
            };
            _locateButton = CreateButton("定位当前图", LocateCurrentImageAsync, 104);
            _addSampleButton = CreateButton("添加样本", AddCurrentSample, 96);
            _replaceSampleButton = CreateButton("重采选中", ReplaceSelectedSample, 96);
            _deleteSampleButton = CreateButton("删除选中", DeleteSelectedSamples, 96);
            _calculateButton = CreateButton("计算标定", CalculateCalibrationAsync, 112);
            _templateButton = CreateButton("模板设置", OpenTemplateEditor, 104);
            _okButton = CreateButton("确认", ConfirmSetup, 96);
            _cancelButton = new Button { Text = "取消", Width = 96, Height = 34, DialogResult = DialogResult.Cancel };
            _templateSummaryLabel = new Label { AutoSize = false, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
            _sampleCountLabel = new Label { AutoSize = true, ForeColor = Color.DimGray, Margin = new Padding(6, 9, 6, 3) };
            _resultBanner = new Label
            {
                AutoSize = false,
                Dock = DockStyle.Top,
                Height = 48,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(14, 0, 14, 0),
                Font = new Font(Font, FontStyle.Bold)
            };
            _pixelToStageText = CreateMatrixTextBox();
            _stageToPixelText = CreateMatrixTextBox();
            _diagnosticsText = CreateMatrixTextBox();
            _diagnosticsText.ScrollBars = ScrollBars.Vertical;
            _nameInput = new TextBox { Dock = DockStyle.Fill };
            _unitCombo = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
            _unitCombo.Items.AddRange(new object[] { "mm", "um", "inch", "custom" });
            _statusLabel = new ToolStripStatusLabel { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
            _progressBar = new ToolStripProgressBar
            {
                Style = ProgressBarStyle.Marquee,
                MarqueeAnimationSpeed = 30,
                Width = 140,
                Visible = false
            };

            BuildLayout();
            WireEvents();
            LoadStateToUi();
            if (referenceImage != null)
            {
                SetCurrentImage(referenceImage);
            }
            else
            {
                SetStatus("尚未提供图像。", true);
            }
            Shown += HandleFirstShown;
        }

        public event EventHandler SetupStateChanged;

        public event EventHandler CurrentImageChanged;

        public event EventHandler<CalibrationLocateCompletedEventArgs> LocateCompleted;

        public event EventHandler<CalibrationCalculationCompletedEventArgs> CalculationCompleted;

        public event EventHandler TemplateChanged;

        public ICalibrationWorkflow Workflow { get; set; }

        public CalibrationSetupState ResultState { get; private set; }

        public CalibrationSetupState CurrentState
        {
            get
            {
                SyncStateSamples();
                return _state.DeepClone();
            }
        }

        public CalibrationCanvas Canvas
        {
            get { return _canvas; }
        }

        public void SetCurrentImage(Bitmap image)
        {
            if (image == null)
            {
                throw new ArgumentNullException(nameof(image));
            }

            Bitmap replacement = new Bitmap(image);
            Bitmap previous = _currentImage;
            _currentImage = replacement;
            if (previous != null)
            {
                previous.Dispose();
            }

            _canvas.SetImage(_currentImage);
            ApplyTemplateToCanvas();
            ClearCurrentLocate();
            EventHandler handler = CurrentImageChanged;
            if (handler != null)
            {
                handler(this, EventArgs.Empty);
            }
            SetStatus(string.Format("当前图像：{0} × {1}", _currentImage.Width, _currentImage.Height), false);
        }

        public void SetState(CalibrationSetupState state)
        {
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            CancelCurrentOperation();
            _state = state.DeepClone();
            LoadStateToUi();
        }

        public void SetResult(CalibrationResultView result)
        {
            _state.Result = result == null ? null : result.DeepClone();
            DisplayResult(_state.Result);
            UpdateCanvasOverlay();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                CancelCurrentOperation();
                if (_currentImage != null)
                {
                    _currentImage.Dispose();
                    _currentImage = null;
                }
            }

            base.Dispose(disposing);
        }

        protected override void OnFormClosing(FormClosingEventArgs eventArgs)
        {
            _closing = true;
            if (_busy)
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
            ToolStrip canvasTools = new ToolStrip
            {
                GripStyle = ToolStripGripStyle.Hidden,
                Dock = DockStyle.Top,
                RenderMode = ToolStripRenderMode.System,
                Padding = new Padding(5, 3, 5, 3)
            };
            canvasTools.Items.Add(CreateToolButton("加载图像", "从文件加载当前采样图像", LoadImage));
            canvasTools.Items.Add(CreateToolButton("适应", "使整幅图像适应窗口", (sender, args) => _canvas.FitImage()));
            canvasTools.Items.Add(CreateToolButton("1:1", "以一个图像像素对应一个屏幕像素显示", (sender, args) => _canvas.ShowOneToOne()));
            canvasTools.Items.Add(new ToolStripSeparator());
            canvasTools.Items.Add(CreateToolButton("模板设置", "编辑模板、搜索区域、掩膜和基准中心", OpenTemplateEditor));

            SplitContainer mainSplit = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Size = new Size(1400, 820),
                Orientation = Orientation.Vertical,
                SplitterWidth = 6,
                FixedPanel = FixedPanel.Panel2,
                Panel2MinSize = 525,
                SplitterDistance = 850
            };
            mainSplit.Panel1.Controls.Add(_canvas);
            mainSplit.Panel1.Controls.Add(canvasTools);

            TabControl rightTabs = new TabControl { Dock = DockStyle.Fill };
            rightTabs.TabPages.Add(BuildSamplingPage());
            rightTabs.TabPages.Add(BuildResultPage());
            rightTabs.TabPages.Add(BuildSettingsPage());
            mainSplit.Panel2.Padding = new Padding(8);
            mainSplit.Panel2.Controls.Add(rightTabs);

            FlowLayoutPanel footer = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                Height = 54,
                FlowDirection = FlowDirection.RightToLeft,
                WrapContents = false,
                Padding = new Padding(8),
                BackColor = Color.White
            };
            footer.Controls.Add(_cancelButton);
            footer.Controls.Add(_okButton);
            footer.Controls.Add(_calculateButton);
            footer.Controls.Add(_sampleCountLabel);

            StatusStrip status = new StatusStrip();
            status.Items.Add(_statusLabel);
            status.Items.Add(_progressBar);

            Controls.Add(mainSplit);
            Controls.Add(footer);
            Controls.Add(status);
            AcceptButton = _okButton;
            CancelButton = _cancelButton;
        }

        private TabPage BuildSamplingPage()
        {
            TabPage page = new TabPage("采样");
            TableLayoutPanel layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 4,
                Padding = new Padding(8)
            };
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100.0f));

            FlowLayoutPanel modePanel = new FlowLayoutPanel
            {
                AutoSize = true,
                Dock = DockStyle.Top,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                Padding = new Padding(0, 0, 0, 5)
            };
            modePanel.Controls.Add(_translationModeButton);
            modePanel.Controls.Add(_rotationModeButton);

            TableLayoutPanel poseTable = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                ColumnCount = 4,
                RowCount = 2,
                Margin = new Padding(0, 2, 0, 6)
            };
            poseTable.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            poseTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50.0f));
            poseTable.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            poseTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50.0f));
            AddLabeledControl(poseTable, 0, "机械 X", _machineXInput);
            AddLabeledControl(poseTable, 1, "机械 Y", _machineYInput);
            AddLabeledControl(poseTable, 2, "机械角度 (°)", _machineThetaInput);
            AddLabeledControl(poseTable, 3, "标签", _tagInput);

            Panel actionBlock = new Panel { Dock = DockStyle.Top, Height = 95 };
            FlowLayoutPanel actions = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                Height = 43,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = true,
                Padding = new Padding(0, 5, 0, 0)
            };
            actions.Controls.Add(_locateButton);
            actions.Controls.Add(_addSampleButton);
            actions.Controls.Add(_replaceSampleButton);
            actions.Controls.Add(_deleteSampleButton);
            Button clearButton = CreateButton("清空本组", ClearActiveSampleGroup, 92);
            actions.Controls.Add(clearButton);
            actionBlock.Controls.Add(actions);
            actionBlock.Controls.Add(_currentMatchLabel);

            TabControl sampleTabs = new TabControl { Dock = DockStyle.Fill };
            TabPage translationPage = new TabPage("九点样本");
            TabPage rotationPage = new TabPage("旋转样本");
            translationPage.Controls.Add(_translationGrid);
            rotationPage.Controls.Add(_rotationGrid);
            sampleTabs.TabPages.Add(translationPage);
            sampleTabs.TabPages.Add(rotationPage);
            _translationModeButton.CheckedChanged += (sender, args) =>
            {
                if (_translationModeButton.Checked)
                {
                    sampleTabs.SelectedTab = translationPage;
                }
                UpdateModeControls();
            };
            _rotationModeButton.CheckedChanged += (sender, args) =>
            {
                if (_rotationModeButton.Checked)
                {
                    sampleTabs.SelectedTab = rotationPage;
                }
                UpdateModeControls();
            };
            sampleTabs.SelectedIndexChanged += (sender, args) =>
            {
                _translationModeButton.Checked = sampleTabs.SelectedIndex == 0;
                _rotationModeButton.Checked = sampleTabs.SelectedIndex == 1;
            };

            layout.Controls.Add(modePanel, 0, 0);
            layout.Controls.Add(poseTable, 0, 1);
            layout.Controls.Add(actionBlock, 0, 2);
            layout.Controls.Add(sampleTabs, 0, 3);
            page.Controls.Add(layout);
            return page;
        }

        private TabPage BuildResultPage()
        {
            TabPage page = new TabPage("结果");
            Panel scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
            TableLayoutPanel table = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                ColumnCount = 2,
                RowCount = 0,
                Padding = new Padding(10)
            };
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 46.0f));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 54.0f));
            AddResultRow(table, "X 解析度", "ResolutionX");
            AddResultRow(table, "Y 解析度", "ResolutionY");
            AddResultRow(table, "轴夹角", "AxisAngle");
            AddResultRow(table, "行列式", "Determinant");
            AddResultRow(table, "条件数", "ConditionNumber");
            AddResultRow(table, "RMS 残差", "RmsResidual");
            AddResultRow(table, "最大残差", "MaxResidual");
            AddResultRow(table, "内点 / 剔除", "Inliers");
            AddResultRow(table, "旋转方向", "RotationDirection");
            AddResultRow(table, "角度偏置", "RotationOffset");
            AddResultRow(table, "图像旋转中心", "RotationCenterImage");
            AddResultRow(table, "机械旋转中心", "RotationCenterStage");
            AddResultRow(table, "旋转角 RMS", "RotationAngleRms");
            AddResultRow(table, "旋转中心残差", "RotationCenterResidual");

            AddSectionHeader(table, "像素 -> 机械矩阵");
            AddSpanningControl(table, _pixelToStageText, 76);
            AddSectionHeader(table, "机械 -> 像素矩阵");
            AddSpanningControl(table, _stageToPixelText, 76);
            AddSectionHeader(table, "诊断");
            AddSpanningControl(table, _diagnosticsText, 130);

            scroll.Controls.Add(table);
            page.Controls.Add(scroll);
            page.Controls.Add(_resultBanner);
            return page;
        }

        private TabPage BuildSettingsPage()
        {
            TabPage page = new TabPage("配置");
            TableLayoutPanel table = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                ColumnCount = 2,
                RowCount = 0,
                Padding = new Padding(12)
            };
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 38.0f));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 62.0f));
            AddSettingsRow(table, "标定名称", _nameInput);
            AddSettingsRow(table, "机械长度单位", _unitCombo);

            Label templateTitle = new Label
            {
                Text = "模板状态",
                AutoSize = true,
                Anchor = AnchorStyles.Left,
                Margin = new Padding(3, 11, 6, 11)
            };
            Panel templatePanel = new Panel { Dock = DockStyle.Fill, Height = 76 };
            _templateButton.Dock = DockStyle.Bottom;
            _templateSummaryLabel.Dock = DockStyle.Fill;
            templatePanel.Controls.Add(_templateSummaryLabel);
            templatePanel.Controls.Add(_templateButton);
            int row = table.RowCount++;
            table.RowStyles.Add(new RowStyle(SizeType.Absolute, 90));
            table.Controls.Add(templateTitle, 0, row);
            table.Controls.Add(templatePanel, 1, row);

            Label revisionTitle = new Label { Text = "模板版本", AutoSize = true, Anchor = AnchorStyles.Left };
            Label revisionValue = new Label { Name = "TemplateRevisionLabel", AutoSize = true, Anchor = AnchorStyles.Left };
            row = table.RowCount++;
            table.Controls.Add(revisionTitle, 0, row);
            table.Controls.Add(revisionValue, 1, row);
            page.Controls.Add(table);
            return page;
        }

        private void WireEvents()
        {
            _translationGrid.DataSource = _translationSamples;
            _rotationGrid.DataSource = _rotationSamples;
            _translationGrid.SelectionChanged += SampleSelectionChanged;
            _rotationGrid.SelectionChanged += SampleSelectionChanged;
            _translationGrid.CellValueChanged += SampleCellValueChanged;
            _rotationGrid.CellValueChanged += SampleCellValueChanged;
            _translationGrid.CurrentCellDirtyStateChanged += CommitDirtyGridCell;
            _rotationGrid.CurrentCellDirtyStateChanged += CommitDirtyGridCell;
            _translationGrid.CellFormatting += FormatSampleCell;
            _rotationGrid.CellFormatting += FormatSampleCell;
            _nameInput.TextChanged += StateSettingChanged;
            _unitCombo.SelectedIndexChanged += StateSettingChanged;
            KeyDown += FormKeyDown;
            FormClosed += (sender, args) => CancelCurrentOperation();
        }

        private void LoadStateToUi()
        {
            _loading = true;
            try
            {
                _nameInput.Text = _state.Name ?? string.Empty;
                int unitIndex = _unitCombo.FindStringExact(_state.LinearUnit ?? "mm");
                _unitCombo.SelectedIndex = unitIndex >= 0 ? unitIndex : 0;
                _translationSamples.Clear();
                _rotationSamples.Clear();
                foreach (CalibrationSampleRow sample in _state.Samples.Where(item => item != null))
                {
                    if (sample.Kind == CalibrationUiSampleKind.Rotation)
                    {
                        _rotationSamples.Add(sample.DeepClone());
                    }
                    else
                    {
                        _translationSamples.Add(sample.DeepClone());
                    }
                }
                DisplayResult(_state.Result);
                ApplyTemplateToCanvas();
                UpdateTemplateSummary();
                UpdateSampleCount();
                UpdateModeControls();
                UpdateCanvasOverlay();
            }
            finally
            {
                _loading = false;
            }
            UpdateActionAvailability();
        }

        private async void LocateCurrentImageAsync(object sender, EventArgs eventArgs)
        {
            if (_busy)
            {
                return;
            }
            if (_currentImage == null)
            {
                MessageBox.Show(this, "请先提供或加载当前图像。", "定位", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (Workflow == null)
            {
                MessageBox.Show(this, "定位需要注入 ICalibrationWorkflow。", "定位", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (!_state.Template.HasModel || _state.Template.ModelDirty)
            {
                MessageBox.Show(this, "模板未创建或已失效，请先完成模板设置。", "定位", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            CalibrationLocateUiResult result = null;
            await RunBusyOperationAsync("正在定位当前图像...", async token =>
            {
                using (Bitmap image = new Bitmap(_currentImage))
                {
                    result = await Workflow.LocateAsync(image, _state.Template.DeepClone(), token);
                }
            });
            if (result == null)
            {
                return;
            }

            _currentLocate = result;
            if (result.Success)
            {
                _currentMatchLabel.BackColor = Color.FromArgb(218, 241, 229);
                _currentMatchLabel.ForeColor = Color.FromArgb(27, 107, 67);
                _currentMatchLabel.Text = string.Format(
                    "Row {0:0.###}  Column {1:0.###}  Angle {2:0.###}°  Score {3:0.000}",
                    result.Position.Row,
                    result.Position.Column,
                    result.AngleDegrees,
                    result.Score);
                SetStatus(string.Format("定位成功，耗时 {0:0.0} ms。", result.ElapsedMilliseconds), false);
            }
            else
            {
                _currentMatchLabel.BackColor = Color.FromArgb(249, 225, 220);
                _currentMatchLabel.ForeColor = Color.FromArgb(154, 53, 38);
                _currentMatchLabel.Text = result.Message ?? "定位失败";
                SetStatus(result.Message ?? "定位失败。", true);
            }
            UpdateCanvasOverlay();
            UpdateActionAvailability();
            EventHandler<CalibrationLocateCompletedEventArgs> handler = LocateCompleted;
            if (handler != null)
            {
                handler(this, new CalibrationLocateCompletedEventArgs(result));
            }
        }

        private void AddCurrentSample(object sender, EventArgs eventArgs)
        {
            if (!ValidateCurrentLocate())
            {
                return;
            }

            BindingList<CalibrationSampleRow> list = ActiveSamples;
            CalibrationSampleRow sample = CreateSampleFromInputs();
            sample.Sequence = list.Count == 0 ? 1 : list.Max(item => item.Sequence) + 1;
            list.Add(sample);
            SelectLastRow(ActiveGrid);
            InvalidateCalculatedResult();
            UpdateSampleCount();
            UpdateCanvasOverlay();
            RaiseStateChanged();
        }

        private void ReplaceSelectedSample(object sender, EventArgs eventArgs)
        {
            if (!ValidateCurrentLocate())
            {
                return;
            }
            CalibrationSampleRow selected = GetSelectedSample(ActiveGrid);
            if (selected == null)
            {
                MessageBox.Show(this, "请先选择要重采的样本。", "重采样本", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            CalibrationSampleRow replacement = CreateSampleFromInputs();
            replacement.Sequence = selected.Sequence;
            int index = ActiveSamples.IndexOf(selected);
            ActiveSamples[index] = replacement;
            SelectGridRow(ActiveGrid, index);
            InvalidateCalculatedResult();
            UpdateSampleCount();
            UpdateCanvasOverlay();
            RaiseStateChanged();
        }

        private void DeleteSelectedSamples(object sender, EventArgs eventArgs)
        {
            DataGridView grid = ActiveGrid;
            BindingList<CalibrationSampleRow> list = ActiveSamples;
            List<CalibrationSampleRow> selected = grid.SelectedRows
                .Cast<DataGridViewRow>()
                .Select(row => row.DataBoundItem as CalibrationSampleRow)
                .Where(item => item != null)
                .Distinct()
                .ToList();
            if (selected.Count == 0)
            {
                CalibrationSampleRow current = GetSelectedSample(grid);
                if (current != null)
                {
                    selected.Add(current);
                }
            }
            if (selected.Count == 0)
            {
                return;
            }

            foreach (CalibrationSampleRow sample in selected)
            {
                list.Remove(sample);
            }
            RenumberSamples(list);
            InvalidateCalculatedResult();
            UpdateSampleCount();
            UpdateCanvasOverlay();
            RaiseStateChanged();
        }

        private void ClearActiveSampleGroup(object sender, EventArgs eventArgs)
        {
            if (ActiveSamples.Count == 0)
            {
                return;
            }
            DialogResult answer = MessageBox.Show(
                this,
                _translationModeButton.Checked ? "确认清空全部九点 XY 样本？" : "确认清空全部旋转样本？",
                "清空样本",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.Yes)
            {
                return;
            }
            ActiveSamples.Clear();
            InvalidateCalculatedResult();
            UpdateSampleCount();
            UpdateCanvasOverlay();
            RaiseStateChanged();
        }

        private async void CalculateCalibrationAsync(object sender, EventArgs eventArgs)
        {
            if (_busy)
            {
                return;
            }
            if (Workflow == null)
            {
                MessageBox.Show(this, "计算需要注入 ICalibrationWorkflow。", "计算标定", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            SyncStateSamples();
            int translationCount = _state.Samples.Count(item => item.Enabled && item.Kind == CalibrationUiSampleKind.Translation && item.TemplateRevision == _state.Template.Revision);
            int rotationCount = _state.Samples.Count(item => item.Enabled && item.Kind == CalibrationUiSampleKind.Rotation && item.TemplateRevision == _state.Template.Revision);
            bool hasCurrentRotationSamples = _state.Samples.Any(item =>
                item != null
                && item.Enabled
                && item.Kind == CalibrationUiSampleKind.Rotation
                && item.TemplateRevision == _state.Template.Revision);
            if (translationCount < _state.MinimumTranslationSamples)
            {
                MessageBox.Show(
                    this,
                    string.Format("有效九点样本不足：当前 {0}，至少需要 {1}。", translationCount, _state.MinimumTranslationSamples),
                    "计算标定",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }
            if (hasCurrentRotationSamples && rotationCount < _state.MinimumRotationSamples)
            {
                MessageBox.Show(
                    this,
                    string.Format("有效旋转样本不足：当前 {0}，至少需要 {1}。", rotationCount, _state.MinimumRotationSamples),
                    "计算标定",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }

            CalibrationCalculationUiResult response = null;
            List<CalibrationSampleRow> samples = _state.Samples.Select(item => item.DeepClone()).ToList();
            await RunBusyOperationAsync("正在计算九点与旋转标定...", async token =>
            {
                response = await Workflow.CalculateAsync(samples, _state.Template.DeepClone(), token);
            });
            if (response == null)
            {
                return;
            }

            if (response.UpdatedSamples != null && response.UpdatedSamples.Count > 0)
            {
                _state.Samples = response.UpdatedSamples.Select(item => item.DeepClone()).ToList();
                ReloadSampleLists();
            }
            _state.Result = response.Result == null ? null : response.Result.DeepClone();
            if (_state.Result == null)
            {
                _state.Result = new CalibrationResultView
                {
                    Success = response.Success,
                    Message = response.Message,
                    ErrorCode = response.Success ? null : "CalculationFailed"
                };
            }
            DisplayResult(_state.Result);
            UpdateCanvasOverlay();
            UpdateSampleCount();
            RaiseStateChanged();
            SetStatus(response.Success ? "标定计算完成。" : response.Message ?? "标定计算失败。", !response.Success);
            EventHandler<CalibrationCalculationCompletedEventArgs> handler = CalculationCompleted;
            if (handler != null)
            {
                handler(this, new CalibrationCalculationCompletedEventArgs(response));
            }
        }

        private void OpenTemplateEditor(object sender, EventArgs eventArgs)
        {
            if (_currentImage == null)
            {
                MessageBox.Show(this, "请先提供或加载一张清晰的参考图像。", "模板设置", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            using (TemplateEditorForm editor = new TemplateEditorForm(
                _currentImage,
                _state.Template,
                Workflow == null ? null : Workflow.TemplateEditorService))
            {
                if (editor.ShowDialog(this) != DialogResult.OK || editor.ResultState == null)
                {
                    return;
                }
                long previousRevision = _state.Template == null ? 0L : _state.Template.Revision;
                _state.Template = editor.ResultState.DeepClone();
                if (_state.Template.Revision != previousRevision)
                {
                    foreach (CalibrationSampleRow sample in _translationSamples.Concat(_rotationSamples))
                    {
                        if (sample.TemplateRevision != _state.Template.Revision)
                        {
                            sample.Enabled = false;
                            sample.IsInlier = false;
                            sample.Status = "模板版本已变更";
                        }
                    }
                    _translationSamples.ResetBindings();
                    _rotationSamples.ResetBindings();
                    InvalidateCalculatedResult();
                }
                ApplyTemplateToCanvas();
                ClearCurrentLocate();
                UpdateTemplateSummary();
                UpdateSampleCount();
                RaiseStateChanged();
                EventHandler handler = TemplateChanged;
                if (handler != null)
                {
                    handler(this, EventArgs.Empty);
                }
            }
        }

        private void LoadImage(object sender, EventArgs eventArgs)
        {
            using (OpenFileDialog dialog = new OpenFileDialog
            {
                Title = "加载标定图像",
                Filter = "图像文件|*.bmp;*.png;*.jpg;*.jpeg;*.tif;*.tiff|所有文件|*.*",
                CheckFileExists = true,
                Multiselect = false
            })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK)
                {
                    return;
                }
                try
                {
                    using (Image loaded = Image.FromFile(dialog.FileName))
                    using (Bitmap bitmap = new Bitmap(loaded))
                    {
                        SetCurrentImage(bitmap);
                    }
                }
                catch (Exception exception)
                {
                    MessageBox.Show(this, "图像加载失败：" + exception.Message, "加载图像", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private async Task RunBusyOperationAsync(string status, Func<CancellationToken, Task> operation)
        {
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
                if (CanUpdateUi) SetStatus("操作失败：" + exception.Message, true);
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
            _locateButton.Enabled = !busy && Workflow != null && _currentImage != null;
            _calculateButton.Enabled = !busy && Workflow != null;
            _templateButton.Enabled = !busy && _currentImage != null;
            _addSampleButton.Enabled = !busy && _currentLocate != null && _currentLocate.Success;
            _replaceSampleButton.Enabled = _addSampleButton.Enabled && GetSelectedSample(ActiveGrid) != null;
            _deleteSampleButton.Enabled = !busy && GetSelectedSample(ActiveGrid) != null;
            _okButton.Enabled = !busy;
        }

        private void CancelCurrentOperation()
        {
            if (_operationCancellation != null && !_operationCancellation.IsCancellationRequested)
            {
                _operationCancellation.Cancel();
            }
        }

        private CalibrationSampleRow CreateSampleFromInputs()
        {
            return new CalibrationSampleRow
            {
                SampleId = Guid.NewGuid().ToString("N"),
                CapturedAtUtc = DateTime.UtcNow,
                Enabled = true,
                Kind = _translationModeButton.Checked ? CalibrationUiSampleKind.Translation : CalibrationUiSampleKind.Rotation,
                Tag = _tagInput.Text.Trim(),
                MachineX = (double)_machineXInput.Value,
                MachineY = (double)_machineYInput.Value,
                MachineThetaDegrees = (double)_machineThetaInput.Value,
                ImageRow = _currentLocate.Position.Row,
                ImageColumn = _currentLocate.Position.Column,
                ImageAngleDegrees = _currentLocate.AngleDegrees,
                Score = _currentLocate.Score,
                ResidualPixels = 0.0,
                IsInlier = true,
                TemplateRevision = _state.Template.Revision,
                Status = "已采集"
            };
        }

        private bool ValidateCurrentLocate()
        {
            if (_currentLocate == null || !_currentLocate.Success)
            {
                MessageBox.Show(this, "请先成功定位当前图像。", "添加样本", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return false;
            }
            if (_currentLocate.Score < _state.Template.Parameters.MinimumScore)
            {
                MessageBox.Show(this, "当前匹配分数低于模板最低分数。", "添加样本", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
            return true;
        }

        private void ApplyTemplateToCanvas()
        {
            TemplateEditorState template = _state.Template;
            if (template == null)
            {
                return;
            }
            _canvas.TemplateRoi = template.TemplateRoi;
            _canvas.SearchRoi = template.SearchRoi;
            _canvas.ReferenceAnchor = template.ReferenceAnchor;
            _canvas.AnchorVisible = template.TemplateRoi != null;
            _canvas.AnchorLocked = true;
            _canvas.TemplateRoiLocked = true;
            _canvas.SearchRoiLocked = true;
            _canvas.MaskVisible = false;
            _canvas.SetMaskStrokes(template.MaskStrokes);
            UpdateCanvasOverlay();
        }

        private void UpdateCanvasOverlay()
        {
            CanvasRuntimeOverlay overlay = new CanvasRuntimeOverlay();
            foreach (CalibrationSampleRow sample in _translationSamples.Concat(_rotationSamples))
            {
                Color color = !sample.Enabled || sample.TemplateRevision != _state.Template.Revision
                    ? Color.Gray
                    : sample.IsInlier ? Color.LimeGreen : Color.OrangeRed;
                overlay.Points.Add(new CanvasPointOverlay
                {
                    Position = new ImageCoordinate(sample.ImageRow, sample.ImageColumn),
                    Label = (sample.Kind == CalibrationUiSampleKind.Translation ? "P" : "R") + sample.Sequence,
                    Color = color,
                    RadiusPixels = sample.Kind == CalibrationUiSampleKind.Translation ? 5.0f : 4.0f,
                    IsSelected = ReferenceEquals(sample, GetSelectedSample(ActiveGrid))
                });
            }

            if (_currentLocate != null)
            {
                if (_currentLocate.Success)
                {
                    overlay.Poses.Add(new CanvasPoseOverlay
                    {
                        Position = _currentLocate.Position,
                        AngleDegrees = _currentLocate.AngleDegrees,
                        Score = _currentLocate.Score,
                        Color = Color.Cyan,
                        Label = string.Format("当前 {0:0.000}", _currentLocate.Score)
                    });
                    foreach (List<ImageCoordinate> contour in _currentLocate.MatchedContours ?? new List<List<ImageCoordinate>>())
                    {
                        overlay.Polylines.Add(new CanvasPolylineOverlay
                        {
                            Points = contour,
                            Color = Color.Cyan,
                            Closed = true,
                            WidthPixels = 1.5f
                        });
                    }
                }
                overlay.IsOk = _currentLocate.Success;
                overlay.StatusText = _currentLocate.Success ? "定位 OK" : "定位 NG";
            }
            if (_state.Result != null)
            {
                overlay.IsOk = _state.Result.Success;
                overlay.StatusText = _state.Result.Success ? "标定 OK" : "标定 NG";
                overlay.HudLines.Add(string.Format("XY RMS: {0:0.###} px", _state.Result.RmsResidualPixels));
                if (_state.Result.RotationCenterImage.HasValue)
                {
                    overlay.RotationCenter = _state.Result.RotationCenterImage;
                    overlay.HudLines.Add(string.Format("Rotation RMS: {0:0.###} px", _state.Result.RotationCenterResidualPixels));
                }
            }
            _canvas.RuntimeOverlay = overlay;
        }

        private void DisplayResult(CalibrationResultView result)
        {
            if (result == null)
            {
                _resultBanner.Text = "尚未计算";
                _resultBanner.BackColor = Color.FromArgb(226, 230, 234);
                _resultBanner.ForeColor = Color.FromArgb(64, 70, 78);
                foreach (Label value in _resultValues.Values)
                {
                    value.Text = "-";
                }
                _pixelToStageText.Text = string.Empty;
                _stageToPixelText.Text = string.Empty;
                _diagnosticsText.Text = string.Empty;
                return;
            }

            _resultBanner.Text = result.Success
                ? "OK  " + (string.IsNullOrWhiteSpace(result.Message) ? "标定结果有效" : result.Message)
                : "NG  " + (string.IsNullOrWhiteSpace(result.Message) ? result.ErrorCode : result.Message);
            _resultBanner.BackColor = result.Success ? Color.FromArgb(214, 240, 225) : Color.FromArgb(249, 221, 216);
            _resultBanner.ForeColor = result.Success ? Color.FromArgb(24, 111, 66) : Color.FromArgb(158, 48, 34);
            SetResultValue("ResolutionX", FormatValue(result.ResolutionX, _state.LinearUnit + "/px"));
            SetResultValue("ResolutionY", FormatValue(result.ResolutionY, _state.LinearUnit + "/px"));
            SetResultValue("AxisAngle", FormatValue(result.AxisAngleDegrees, "°"));
            SetResultValue("Determinant", result.Determinant.ToString("G8", CultureInfo.InvariantCulture));
            SetResultValue("ConditionNumber", result.ConditionNumber.ToString("0.###", CultureInfo.InvariantCulture));
            SetResultValue("RmsResidual", FormatValue(result.RmsResidualPixels, "px"));
            SetResultValue("MaxResidual", FormatValue(result.MaximumResidualPixels, "px"));
            SetResultValue("Inliers", string.Format("{0} / {1}", result.InlierCount, result.RejectedCount));
            SetResultValue("RotationDirection", result.RotationDirection > 0 ? "同向" : result.RotationDirection < 0 ? "反向" : "未知");
            SetResultValue("RotationOffset", FormatValue(result.RotationOffsetDegrees, "°"));
            SetResultValue("RotationCenterImage", result.RotationCenterImage.HasValue
                ? string.Format("R {0:0.###}, C {1:0.###}", result.RotationCenterImage.Value.Row, result.RotationCenterImage.Value.Column)
                : "-");
            SetResultValue("RotationCenterStage", result.RotationCenterImage.HasValue
                ? string.Format("X {0:0.######}, Y {1:0.######} {2}", result.RotationCenterStageX, result.RotationCenterStageY, _state.LinearUnit)
                : "-");
            SetResultValue("RotationAngleRms", FormatValue(result.RotationAngleRmsDegrees, "°"));
            SetResultValue("RotationCenterResidual", FormatValue(result.RotationCenterResidualPixels, "px"));
            _pixelToStageText.Text = FormatMatrix(result.PixelToStage);
            _stageToPixelText.Text = FormatMatrix(result.StageToPixel);
            _diagnosticsText.Text = string.Join(Environment.NewLine, result.Diagnostics ?? new List<string>());
        }

        private void UpdateTemplateSummary()
        {
            TemplateEditorState template = _state.Template;
            if (template == null || !template.HasModel)
            {
                _templateSummaryLabel.Text = "未创建模板";
                _templateSummaryLabel.ForeColor = Color.Firebrick;
            }
            else if (template.ModelDirty)
            {
                _templateSummaryLabel.Text = string.Format("Rev {0} · 模型需要重建", template.Revision);
                _templateSummaryLabel.ForeColor = Color.Firebrick;
            }
            else
            {
                _templateSummaryLabel.Text = string.Format(
                    "Rev {0} · {1} · 最低分数 {2:0.000}",
                    template.Revision,
                    template.Parameters.ModelType,
                    template.Parameters.MinimumScore);
                _templateSummaryLabel.ForeColor = Color.FromArgb(24, 111, 66);
            }
            Label revision = Controls.Find("TemplateRevisionLabel", true).OfType<Label>().FirstOrDefault();
            if (revision != null)
            {
                revision.Text = template == null ? "-" : template.Revision.ToString(CultureInfo.InvariantCulture);
            }
        }

        private void UpdateSampleCount()
        {
            int validTranslation = _translationSamples.Count(item => item.Enabled && item.TemplateRevision == _state.Template.Revision);
            int validRotation = _rotationSamples.Count(item => item.Enabled && item.TemplateRevision == _state.Template.Revision);
            _sampleCountLabel.Text = string.Format(
                "有效样本：XY {0}/{1}，旋转 {2}/{3}",
                validTranslation,
                _state.MinimumTranslationSamples,
                validRotation,
                _state.MinimumRotationSamples);
        }

        private void UpdateModeControls()
        {
            _machineThetaInput.Enabled = _rotationModeButton.Checked;
            if (_translationModeButton.Checked)
            {
                _machineThetaInput.Value = 0;
            }
            UpdateActionAvailability();
        }

        private void UpdateActionAvailability()
        {
            SetBusyState(_busy);
        }

        private void ClearCurrentLocate()
        {
            _currentLocate = null;
            _currentMatchLabel.BackColor = Color.FromArgb(232, 235, 239);
            _currentMatchLabel.ForeColor = Color.FromArgb(50, 55, 62);
            _currentMatchLabel.Text = "尚未定位当前图像";
            UpdateActionAvailability();
            UpdateCanvasOverlay();
        }

        private void InvalidateCalculatedResult()
        {
            _state.Result = null;
            DisplayResult(null);
        }

        private void ReloadSampleLists()
        {
            _loading = true;
            try
            {
                _translationSamples.Clear();
                _rotationSamples.Clear();
                foreach (CalibrationSampleRow sample in _state.Samples)
                {
                    if (sample.Kind == CalibrationUiSampleKind.Rotation)
                    {
                        _rotationSamples.Add(sample.DeepClone());
                    }
                    else
                    {
                        _translationSamples.Add(sample.DeepClone());
                    }
                }
            }
            finally
            {
                _loading = false;
            }
        }

        private void SyncStateSamples()
        {
            _state.Name = _nameInput.Text.Trim();
            _state.LinearUnit = Convert.ToString(_unitCombo.SelectedItem) ?? "mm";
            _state.Samples = _translationSamples
                .Concat(_rotationSamples)
                .Select(item => item.DeepClone())
                .ToList();
        }

        private void ConfirmSetup(object sender, EventArgs eventArgs)
        {
            SyncStateSamples();
            if (!_state.Template.HasModel || _state.Template.ModelDirty)
            {
                MessageBox.Show(this, "模板尚未准备完成，不能确认标定配置。", "确认标定", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (_state.Result == null || !_state.Result.Success)
            {
                DialogResult answer = MessageBox.Show(
                    this,
                    "当前没有有效的标定计算结果。是否仅保存配置和样本？",
                    "确认标定",
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

        private void StateSettingChanged(object sender, EventArgs eventArgs)
        {
            if (_loading)
            {
                return;
            }
            SyncStateSamples();
            if (sender == _unitCombo)
            {
                DisplayResult(_state.Result);
            }
            RaiseStateChanged();
        }

        private void SampleSelectionChanged(object sender, EventArgs eventArgs)
        {
            if (_loading)
            {
                return;
            }
            UpdateActionAvailability();
            UpdateCanvasOverlay();
        }

        private void SampleCellValueChanged(object sender, DataGridViewCellEventArgs eventArgs)
        {
            if (_loading || eventArgs.RowIndex < 0)
            {
                return;
            }
            InvalidateCalculatedResult();
            UpdateSampleCount();
            UpdateCanvasOverlay();
            RaiseStateChanged();
        }

        private static void CommitDirtyGridCell(object sender, EventArgs eventArgs)
        {
            DataGridView grid = (DataGridView)sender;
            if (grid.IsCurrentCellDirty)
            {
                grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
            }
        }

        private void FormatSampleCell(object sender, DataGridViewCellFormattingEventArgs eventArgs)
        {
            DataGridView grid = (DataGridView)sender;
            CalibrationSampleRow sample = grid.Rows[eventArgs.RowIndex].DataBoundItem as CalibrationSampleRow;
            if (sample == null)
            {
                return;
            }
            if (!sample.Enabled || sample.TemplateRevision != _state.Template.Revision)
            {
                eventArgs.CellStyle.ForeColor = Color.Gray;
                eventArgs.CellStyle.BackColor = Color.FromArgb(245, 245, 245);
            }
            else if (!sample.IsInlier)
            {
                eventArgs.CellStyle.ForeColor = Color.Firebrick;
                eventArgs.CellStyle.BackColor = Color.FromArgb(255, 238, 234);
            }
        }

        private void FormKeyDown(object sender, KeyEventArgs eventArgs)
        {
            if (eventArgs.KeyCode == Keys.Delete && ActiveGrid.Focused)
            {
                DeleteSelectedSamples(sender, EventArgs.Empty);
                eventArgs.Handled = true;
            }
            else if (eventArgs.KeyCode == Keys.F5)
            {
                LocateCurrentImageAsync(sender, EventArgs.Empty);
                eventArgs.Handled = true;
            }
            else if (eventArgs.KeyCode == Keys.Escape && _busy)
            {
                CancelCurrentOperation();
                eventArgs.Handled = true;
            }
        }

        private void RaiseStateChanged()
        {
            SyncStateSamples();
            EventHandler handler = SetupStateChanged;
            if (handler != null)
            {
                handler(this, EventArgs.Empty);
            }
        }

        private void SetStatus(string message, bool error)
        {
            _statusLabel.Text = message ?? string.Empty;
            _statusLabel.ForeColor = error ? Color.Firebrick : SystemColors.ControlText;
        }

        private BindingList<CalibrationSampleRow> ActiveSamples
        {
            get { return _translationModeButton.Checked ? _translationSamples : _rotationSamples; }
        }

        private DataGridView ActiveGrid
        {
            get { return _translationModeButton.Checked ? _translationGrid : _rotationGrid; }
        }

        private static CalibrationSampleRow GetSelectedSample(DataGridView grid)
        {
            return grid == null || grid.CurrentRow == null
                ? null
                : grid.CurrentRow.DataBoundItem as CalibrationSampleRow;
        }

        private static void SelectLastRow(DataGridView grid)
        {
            if (grid.Rows.Count > 0)
            {
                SelectGridRow(grid, grid.Rows.Count - 1);
            }
        }

        private static void SelectGridRow(DataGridView grid, int index)
        {
            if (index < 0 || index >= grid.Rows.Count)
            {
                return;
            }
            grid.ClearSelection();
            grid.Rows[index].Selected = true;
            grid.CurrentCell = grid.Rows[index].Cells.Cast<DataGridViewCell>().FirstOrDefault(cell => cell.Visible);
        }

        private static void RenumberSamples(BindingList<CalibrationSampleRow> samples)
        {
            for (int index = 0; index < samples.Count; index++)
            {
                samples[index].Sequence = index + 1;
            }
            samples.ResetBindings();
        }

        private static DataGridView CreateSampleGrid(bool rotation)
        {
            DataGridView grid = new DataGridView
            {
                Dock = DockStyle.Fill,
                AutoGenerateColumns = false,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                AllowUserToResizeRows = false,
                MultiSelect = true,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                RowHeadersVisible = false,
                BackgroundColor = Color.White,
                BorderStyle = BorderStyle.FixedSingle,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None,
                ClipboardCopyMode = DataGridViewClipboardCopyMode.EnableAlwaysIncludeHeaderText
            };
            grid.Columns.Add(CreateCheckColumn("Enabled", "启用", 48));
            grid.Columns.Add(CreateTextColumn("Sequence", "序号", 48, "0", true));
            grid.Columns.Add(CreateTextColumn("Tag", "标签", 66, null, false));
            grid.Columns.Add(CreateTextColumn("MachineX", "X", 70, "0.###", true));
            grid.Columns.Add(CreateTextColumn("MachineY", "Y", 70, "0.###", true));
            if (rotation)
            {
                grid.Columns.Add(CreateTextColumn("MachineThetaDegrees", "机械角", 70, "0.###", true));
            }
            grid.Columns.Add(CreateTextColumn("ImageRow", "Row", 76, "0.###", true));
            grid.Columns.Add(CreateTextColumn("ImageColumn", "Column", 76, "0.###", true));
            if (rotation)
            {
                grid.Columns.Add(CreateTextColumn("ImageAngleDegrees", "图像角", 70, "0.###", true));
            }
            grid.Columns.Add(CreateTextColumn("Score", "Score", 62, "0.000", true));
            grid.Columns.Add(CreateTextColumn("ResidualPixels", "残差", 64, "0.###", true));
            grid.Columns.Add(CreateTextColumn("Status", "状态", 110, null, true));
            return grid;
        }

        private static DataGridViewTextBoxColumn CreateTextColumn(
            string property,
            string title,
            int width,
            string format,
            bool readOnly)
        {
            DataGridViewTextBoxColumn column = new DataGridViewTextBoxColumn
            {
                DataPropertyName = property,
                HeaderText = title,
                Name = property,
                Width = width,
                ReadOnly = readOnly,
                SortMode = DataGridViewColumnSortMode.NotSortable
            };
            if (!string.IsNullOrWhiteSpace(format))
            {
                column.DefaultCellStyle.Format = format;
                column.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
            }
            return column;
        }

        private static DataGridViewCheckBoxColumn CreateCheckColumn(string property, string title, int width)
        {
            return new DataGridViewCheckBoxColumn
            {
                DataPropertyName = property,
                HeaderText = title,
                Name = property,
                Width = width,
                SortMode = DataGridViewColumnSortMode.NotSortable
            };
        }

        private static NumericUpDown CreateCoordinateInput()
        {
            return new NumericUpDown
            {
                Minimum = -100000000,
                Maximum = 100000000,
                DecimalPlaces = 4,
                Increment = 0.1m,
                ThousandsSeparator = true,
                Dock = DockStyle.Fill
            };
        }

        private static Button CreateButton(string text, EventHandler click, int width)
        {
            Button button = new Button { Text = text, Width = width, Height = 34 };
            button.Click += click;
            return button;
        }

        private static ToolStripButton CreateToolButton(string text, string tooltip, EventHandler click)
        {
            ToolStripButton button = new ToolStripButton(text)
            {
                ToolTipText = tooltip,
                DisplayStyle = ToolStripItemDisplayStyle.Text
            };
            button.Click += click;
            return button;
        }

        private static TextBox CreateMatrixTextBox()
        {
            return new TextBox
            {
                Multiline = true,
                ReadOnly = true,
                BorderStyle = BorderStyle.FixedSingle,
                BackColor = Color.White,
                Font = new Font("Consolas", 9.0f, FontStyle.Regular, GraphicsUnit.Point),
                Dock = DockStyle.Fill,
                WordWrap = false
            };
        }

        private void AddResultRow(TableLayoutPanel table, string title, string key)
        {
            int row = table.RowCount++;
            table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Label titleLabel = new Label
            {
                Text = title,
                AutoSize = true,
                Anchor = AnchorStyles.Left,
                Margin = new Padding(3, 6, 6, 6),
                ForeColor = Color.DimGray
            };
            Label valueLabel = new Label
            {
                Text = "-",
                AutoSize = true,
                Anchor = AnchorStyles.Left,
                Margin = new Padding(3, 6, 3, 6)
            };
            _resultValues[key] = valueLabel;
            table.Controls.Add(titleLabel, 0, row);
            table.Controls.Add(valueLabel, 1, row);
        }

        private static void AddSectionHeader(TableLayoutPanel table, string text)
        {
            int row = table.RowCount++;
            Label label = new Label
            {
                Text = text,
                AutoSize = true,
                Font = new Font(SystemFonts.MessageBoxFont, FontStyle.Bold),
                Margin = new Padding(3, 15, 3, 6)
            };
            table.Controls.Add(label, 0, row);
            table.SetColumnSpan(label, 2);
        }

        private static void AddSpanningControl(TableLayoutPanel table, Control control, int height)
        {
            int row = table.RowCount++;
            table.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
            control.Height = height;
            table.Controls.Add(control, 0, row);
            table.SetColumnSpan(control, 2);
        }

        private static void AddSettingsRow(TableLayoutPanel table, string title, Control control)
        {
            int row = table.RowCount++;
            table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Label label = new Label
            {
                Text = title,
                AutoSize = true,
                Anchor = AnchorStyles.Left,
                Margin = new Padding(3, 8, 6, 8)
            };
            control.Margin = new Padding(3, 4, 3, 4);
            table.Controls.Add(label, 0, row);
            table.Controls.Add(control, 1, row);
        }

        private static void AddLabeledControl(TableLayoutPanel table, int cellIndex, string title, Control control)
        {
            int columnPair = (cellIndex % 2) * 2;
            int row = cellIndex / 2;
            Label label = new Label
            {
                Text = title,
                AutoSize = true,
                Anchor = AnchorStyles.Left,
                Margin = new Padding(2, 7, 5, 7)
            };
            control.Margin = new Padding(2, 3, 8, 3);
            table.Controls.Add(label, columnPair, row);
            table.Controls.Add(control, columnPair + 1, row);
        }

        private void SetResultValue(string key, string value)
        {
            Label label;
            if (_resultValues.TryGetValue(key, out label))
            {
                label.Text = value;
            }
        }

        private static string FormatValue(double value, string unit)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                return "-";
            }
            return string.Format(CultureInfo.InvariantCulture, "{0:0.######} {1}", value, unit);
        }

        private static string FormatMatrix(double[] matrix)
        {
            if (matrix == null || matrix.Length < 6)
            {
                return string.Empty;
            }
            return string.Format(
                CultureInfo.InvariantCulture,
                "[{0,13:0.########} {1,13:0.########} {2,13:0.########}]\r\n[{3,13:0.########} {4,13:0.########} {5,13:0.########}]\r\n[            0             0             1]",
                matrix[0], matrix[1], matrix[2], matrix[3], matrix[4], matrix[5]);
        }
    }

    public sealed class CalibrationLocateCompletedEventArgs : EventArgs
    {
        public CalibrationLocateCompletedEventArgs(CalibrationLocateUiResult result)
        {
            Result = result;
        }

        public CalibrationLocateUiResult Result { get; private set; }
    }

    public sealed class CalibrationCalculationCompletedEventArgs : EventArgs
    {
        public CalibrationCalculationCompletedEventArgs(CalibrationCalculationUiResult result)
        {
            Result = result;
        }

        public CalibrationCalculationUiResult Result { get; private set; }
    }
}
