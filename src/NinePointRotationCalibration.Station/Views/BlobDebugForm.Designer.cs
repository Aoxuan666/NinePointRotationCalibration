using System;
using System.ComponentModel;
using System.Drawing;
using System.Windows.Forms;
using NinePointRotationCalibration.Station.Controls;

namespace NinePointRotationCalibration.Station.Views
{
    partial class BlobDebugForm
    {
        private IContainer components;
        private SplitContainer mainSplit;
        private CheckBox chkTemplateMatchingEnabled;
        private CheckBox chkFollowTemplateTransform;
        private CheckBox chkRectangleFilter;
        private NumericUpDown nudBrightThreshold;
        private NumericUpDown nudMinimumBlobArea;
        private NumericUpDown nudMaximumBlobArea;
        private NumericUpDown nudRectangularity;
        private NumericUpDown nudMorphologyRadius;
        private CheckBox chkFillHoles;
        private CheckBox chkAutoThreshold;
        private CheckBox chkDarkForeground;
        private Label lblModeStatus;
        private Label lblPreviewStatus;
        private Label lblSummary;
        private Button btnRefresh;
        private Button btnFit;
        private Button btnActualPixels;
        private Button btnApply;
        private Button btnCancel;
        private TabControl tabPreview;
        private TabPage tabSourceImage;
        private TabPage tabBinaryImage;
        private ImageViewport sourceViewport;
        private ImageViewport binaryViewport;
        private DataGridView dgvBlobs;
        private Timer previewTimer;
        private ToolTip toolTip;

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                ReleaseAnalysisResources();
                if (components != null) components.Dispose();
            }
            base.Dispose(disposing);
        }

        private void InitializeComponent()
        {
            components = new Container();
            previewTimer = new Timer(components);
            toolTip = new ToolTip(components);

            SuspendLayout();
            AutoScaleDimensions = new SizeF(96.0F, 96.0F);
            AutoScaleMode = AutoScaleMode.Dpi;
            BackColor = Color.FromArgb(232, 235, 238);
            ClientSize = new Size(1320, 820);
            Font = new Font("Microsoft YaHei UI", 9.0F, FontStyle.Regular, GraphicsUnit.Point, 134);
            ForeColor = Color.FromArgb(39, 43, 48);
            FormBorderStyle = FormBorderStyle.Sizable;
            KeyPreview = true;
            MinimumSize = new Size(1040, 680);
            Name = "BlobDebugForm";
            ShowIcon = false;
            StartPosition = FormStartPosition.CenterParent;
            Text = "Blob 检测调试";

            TableLayoutPanel root = new TableLayoutPanel();
            root.BackColor = BackColor;
            root.ColumnCount = 1;
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100.0F));
            root.Dock = DockStyle.Fill;
            root.Margin = Padding.Empty;
            root.RowCount = 2;
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100.0F));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 56.0F));

            mainSplit = new SplitContainer();
            mainSplit.BackColor = Color.FromArgb(202, 207, 212);
            mainSplit.Dock = DockStyle.Fill;
            mainSplit.FixedPanel = FixedPanel.Panel1;
            mainSplit.IsSplitterFixed = false;
            mainSplit.Name = "mainSplit";
            mainSplit.Size = new Size(1320, 764);
            mainSplit.Panel1.BackColor = Color.FromArgb(244, 246, 248);
            mainSplit.Panel1MinSize = 300;
            mainSplit.Panel2.BackColor = Color.Black;
            mainSplit.Panel2MinSize = 560;
            mainSplit.SplitterDistance = 326;
            mainSplit.SplitterWidth = 6;

            BuildParameterPanel(mainSplit.Panel1);
            BuildPreviewPanel(mainSplit.Panel2);
            root.Controls.Add(mainSplit, 0, 0);
            root.Controls.Add(BuildFooter(), 0, 1);
            Controls.Add(root);

            previewTimer.Interval = 250;
            ResumeLayout(false);
        }

        private void BuildParameterPanel(Control parent)
        {
            TableLayoutPanel layout = new TableLayoutPanel();
            layout.AutoScroll = true;
            layout.BackColor = Color.FromArgb(244, 246, 248);
            layout.ColumnCount = 1;
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100.0F));
            layout.Dock = DockStyle.Fill;
            layout.Padding = new Padding(10);
            layout.RowCount = 5;
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100.0F));

            GroupBox modeGroup = CreateGroupBox("检测路径");
            TableLayoutPanel modeFields = CreateFieldTable();
            chkTemplateMatchingEnabled = CreateCheckBox(
                "chkTemplateMatchingEnabled",
                "启用模板匹配并使用检测 ROI");
            chkFollowTemplateTransform = CreateCheckBox(
                "chkFollowTemplateTransform",
                "检测 ROI 跟随模板位姿");
            lblModeStatus = new Label();
            lblModeStatus.AutoEllipsis = true;
            lblModeStatus.Dock = DockStyle.Fill;
            lblModeStatus.ForeColor = Color.FromArgb(94, 101, 109);
            lblModeStatus.Name = "lblModeStatus";
            lblModeStatus.Padding = new Padding(0, 4, 0, 2);
            lblModeStatus.Text = "等待分析";
            lblModeStatus.TextAlign = ContentAlignment.MiddleLeft;
            modeFields.Controls.Add(chkTemplateMatchingEnabled, 0, 0);
            modeFields.SetColumnSpan(chkTemplateMatchingEnabled, 2);
            modeFields.Controls.Add(chkFollowTemplateTransform, 0, 1);
            modeFields.SetColumnSpan(chkFollowTemplateTransform, 2);
            modeFields.Controls.Add(lblModeStatus, 0, 2);
            modeFields.SetColumnSpan(lblModeStatus, 2);
            modeGroup.Controls.Add(modeFields);

            GroupBox thresholdGroup = CreateGroupBox("阈值与 Blob");
            TableLayoutPanel thresholdFields = CreateFieldTable();
            nudBrightThreshold = CreateNumeric("nudBrightThreshold", 128.0M, 0.0M, 255.0M, 0, 1.0M);
            nudMinimumBlobArea = CreateNumeric("nudMinimumBlobArea", 20.0M, 0.0M, 10000000.0M, 0, 1.0M);
            nudMaximumBlobArea = CreateNumeric("nudMaximumBlobArea", 100000.0M, 0.0M, 10000000.0M, 0, 10.0M);
            nudRectangularity = CreateNumeric("nudRectangularity", 0.92M, 0.0M, 1.0M, 2, 0.01M);
            nudMorphologyRadius = CreateNumeric("nudMorphologyRadius", 1.0M, 0.0M, 5.0M, 0, 1.0M);
            AddField(thresholdFields, 0, "手动阈值", nudBrightThreshold);
            AddField(thresholdFields, 1, "最小面积", nudMinimumBlobArea);
            AddField(thresholdFields, 2, "最大面积", nudMaximumBlobArea);
            AddField(thresholdFields, 3, "矩形度阈值", nudRectangularity);
            AddField(thresholdFields, 4, "白孔去除半径", nudMorphologyRadius);
            chkRectangleFilter = CreateCheckBox("chkRectangleFilter", "启用矩形标签过滤");
            thresholdFields.Controls.Add(chkRectangleFilter, 0, 5);
            thresholdFields.SetColumnSpan(chkRectangleFilter, 2);
            chkFillHoles = CreateCheckBox("chkFillHoles", "填充保留白目标内部黑洞");
            thresholdFields.Controls.Add(chkFillHoles, 0, 6);
            thresholdFields.SetColumnSpan(chkFillHoles, 2);
            chkAutoThreshold = CreateCheckBox("chkAutoThreshold", "检测 ROI 内自动阈值");
            thresholdFields.Controls.Add(chkAutoThreshold, 0, 7);
            thresholdFields.SetColumnSpan(chkAutoThreshold, 2);
            chkDarkForeground = CreateCheckBox(
                "chkDarkForeground",
                "白底黑点（检测暗目标）");
            thresholdFields.Controls.Add(chkDarkForeground, 0, 8);
            thresholdFields.SetColumnSpan(chkDarkForeground, 2);
            thresholdGroup.Controls.Add(thresholdFields);

            GroupBox resultGroup = CreateGroupBox("当前结果");
            TableLayoutPanel resultLayout = new TableLayoutPanel();
            resultLayout.ColumnCount = 1;
            resultLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100.0F));
            resultLayout.Dock = DockStyle.Fill;
            resultLayout.Padding = new Padding(8, 7, 8, 8);
            resultLayout.RowCount = 2;
            resultLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            resultLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            lblSummary = new Label();
            lblSummary.AutoEllipsis = true;
            lblSummary.Dock = DockStyle.Fill;
            lblSummary.Font = new Font(Font.FontFamily, 9.0F, FontStyle.Bold);
            lblSummary.ForeColor = Color.FromArgb(94, 101, 109);
            lblSummary.Name = "lblSummary";
            lblSummary.Padding = new Padding(0, 2, 0, 7);
            lblSummary.Text = "Blob --";
            lblSummary.TextAlign = ContentAlignment.MiddleLeft;
            btnRefresh = CreateButton("btnRefresh", "立即刷新", 92, 32, false);
            btnRefresh.Anchor = AnchorStyles.Left;
            resultLayout.Controls.Add(lblSummary, 0, 0);
            resultLayout.Controls.Add(btnRefresh, 0, 1);
            resultGroup.Controls.Add(resultLayout);

            layout.Controls.Add(modeGroup, 0, 0);
            layout.Controls.Add(thresholdGroup, 0, 1);
            layout.Controls.Add(resultGroup, 0, 2);
            parent.Controls.Add(layout);
        }

        private void BuildPreviewPanel(Control parent)
        {
            TableLayoutPanel layout = new TableLayoutPanel();
            layout.BackColor = Color.Black;
            layout.ColumnCount = 1;
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100.0F));
            layout.Dock = DockStyle.Fill;
            layout.Margin = Padding.Empty;
            layout.RowCount = 3;
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42.0F));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100.0F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 190.0F));

            TableLayoutPanel toolbar = new TableLayoutPanel();
            toolbar.BackColor = Color.FromArgb(38, 43, 49);
            toolbar.ColumnCount = 3;
            toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100.0F));
            toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 74.0F));
            toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 64.0F));
            toolbar.Dock = DockStyle.Fill;
            toolbar.Margin = Padding.Empty;
            toolbar.Padding = new Padding(10, 5, 6, 4);
            toolbar.RowCount = 1;
            toolbar.RowStyles.Add(new RowStyle(SizeType.Percent, 100.0F));

            lblPreviewStatus = new Label();
            lblPreviewStatus.AutoEllipsis = true;
            lblPreviewStatus.Dock = DockStyle.Fill;
            lblPreviewStatus.ForeColor = Color.FromArgb(205, 211, 218);
            lblPreviewStatus.Name = "lblPreviewStatus";
            lblPreviewStatus.Text = "等待更新";
            lblPreviewStatus.TextAlign = ContentAlignment.MiddleLeft;
            btnFit = CreateButton("btnFit", "适应", 66, 30, false);
            btnFit.Dock = DockStyle.Fill;
            btnFit.Margin = new Padding(4, 0, 4, 0);
            btnActualPixels = CreateButton("btnActualPixels", "1:1", 54, 30, false);
            btnActualPixels.Dock = DockStyle.Fill;
            btnActualPixels.Margin = new Padding(4, 0, 0, 0);
            toolbar.Controls.Add(lblPreviewStatus, 0, 0);
            toolbar.Controls.Add(btnFit, 1, 0);
            toolbar.Controls.Add(btnActualPixels, 2, 0);

            tabPreview = new TabControl();
            tabPreview.Dock = DockStyle.Fill;
            tabPreview.Name = "tabPreview";
            tabPreview.Padding = new Point(12, 5);

            tabSourceImage = CreatePreviewPage("tabSourceImage", "原图");
            sourceViewport = new ImageViewport();
            sourceViewport.Dock = DockStyle.Fill;
            sourceViewport.Name = "sourceViewport";
            tabSourceImage.Controls.Add(sourceViewport);

            tabBinaryImage = CreatePreviewPage("tabBinaryImage", "局部二值图");
            binaryViewport = new ImageViewport();
            binaryViewport.Dock = DockStyle.Fill;
            binaryViewport.Name = "binaryViewport";
            tabBinaryImage.Controls.Add(binaryViewport);
            tabPreview.Controls.Add(tabSourceImage);
            tabPreview.Controls.Add(tabBinaryImage);

            dgvBlobs = new DataGridView();
            ConfigureGrid(dgvBlobs);
            dgvBlobs.Columns.Add(CreateGridColumn("colBlobIndex", "#", 44));
            dgvBlobs.Columns.Add(CreateGridColumn("colBlobColumn", "X", 82));
            dgvBlobs.Columns.Add(CreateGridColumn("colBlobRow", "Y", 82));
            dgvBlobs.Columns.Add(CreateGridColumn("colBlobArea", "面积", 92));
            dgvBlobs.Columns.Add(CreateGridColumn("colBlobRectangularity", "矩形度", 82));
            DataGridViewTextBoxColumn stateColumn = CreateGridColumn("colBlobState", "判定", 96);
            stateColumn.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
            dgvBlobs.Columns.Add(stateColumn);

            layout.Controls.Add(toolbar, 0, 0);
            layout.Controls.Add(tabPreview, 0, 1);
            layout.Controls.Add(dgvBlobs, 0, 2);
            parent.Controls.Add(layout);
        }

        private Control BuildFooter()
        {
            TableLayoutPanel footer = new TableLayoutPanel();
            footer.BackColor = Color.White;
            footer.ColumnCount = 2;
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100.0F));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            footer.Dock = DockStyle.Fill;
            footer.Padding = new Padding(12, 10, 10, 9);
            footer.RowCount = 1;
            footer.RowStyles.Add(new RowStyle(SizeType.Percent, 100.0F));

            Label resultHint = new Label();
            resultHint.AutoEllipsis = true;
            resultHint.Dock = DockStyle.Fill;
            resultHint.ForeColor = Color.FromArgb(94, 101, 109);
            resultHint.Text = "预览显示完整阈值分割；检测结果从每个 ROI 中提取最大目标区域";
            resultHint.TextAlign = ContentAlignment.MiddleLeft;

            FlowLayoutPanel actions = new FlowLayoutPanel();
            actions.AutoSize = true;
            actions.Dock = DockStyle.Fill;
            actions.FlowDirection = FlowDirection.RightToLeft;
            actions.Margin = Padding.Empty;
            actions.WrapContents = false;
            btnApply = CreateButton("btnApply", "应用参数", 96, 34, true);
            btnCancel = CreateButton("btnCancel", "取消", 82, 34, false);
            btnCancel.DialogResult = DialogResult.Cancel;
            actions.Controls.Add(btnApply);
            actions.Controls.Add(btnCancel);
            footer.Controls.Add(resultHint, 0, 0);
            footer.Controls.Add(actions, 1, 0);
            return footer;
        }

        private static GroupBox CreateGroupBox(string text)
        {
            GroupBox group = new GroupBox();
            group.AutoSize = true;
            group.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            group.Dock = DockStyle.Top;
            group.Font = new Font("Microsoft YaHei UI", 9.0F, FontStyle.Bold, GraphicsUnit.Point, 134);
            group.Margin = new Padding(0, 0, 0, 9);
            group.Padding = new Padding(6, 21, 6, 6);
            group.Text = text;
            return group;
        }

        private static TableLayoutPanel CreateFieldTable()
        {
            TableLayoutPanel table = new TableLayoutPanel();
            table.AutoSize = true;
            table.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            table.ColumnCount = 2;
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 104.0F));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100.0F));
            table.Dock = DockStyle.Top;
            table.Font = new Font("Microsoft YaHei UI", 9.0F, FontStyle.Regular, GraphicsUnit.Point, 134);
            table.Padding = new Padding(8, 5, 8, 7);
            return table;
        }

        private static void AddField(TableLayoutPanel table, int row, string text, Control control)
        {
            Label label = new Label();
            label.AutoEllipsis = true;
            label.Dock = DockStyle.Fill;
            label.Margin = new Padding(0, 5, 5, 5);
            label.Text = text;
            label.TextAlign = ContentAlignment.MiddleLeft;
            control.Dock = DockStyle.Fill;
            control.Margin = new Padding(0, 4, 0, 4);
            table.Controls.Add(label, 0, row);
            table.Controls.Add(control, 1, row);
        }

        private static CheckBox CreateCheckBox(string name, string text)
        {
            CheckBox checkBox = new CheckBox();
            checkBox.AutoSize = true;
            checkBox.Dock = DockStyle.Fill;
            checkBox.Margin = new Padding(0, 5, 0, 5);
            checkBox.Name = name;
            checkBox.Text = text;
            checkBox.UseVisualStyleBackColor = true;
            return checkBox;
        }

        private static NumericUpDown CreateNumeric(
            string name,
            decimal value,
            decimal minimum,
            decimal maximum,
            int decimalPlaces,
            decimal increment)
        {
            NumericUpDown input = new NumericUpDown();
            input.DecimalPlaces = decimalPlaces;
            input.Increment = increment;
            input.Maximum = maximum;
            input.Minimum = minimum;
            input.Name = name;
            input.ThousandsSeparator = maximum >= 10000.0M;
            input.Value = Math.Max(minimum, Math.Min(maximum, value));
            return input;
        }

        private static Button CreateButton(string name, string text, int width, int height, bool primary)
        {
            Button button = new Button();
            button.BackColor = primary ? Color.FromArgb(17, 122, 101) : Color.FromArgb(72, 78, 86);
            button.Cursor = Cursors.Hand;
            button.FlatAppearance.BorderColor = primary
                ? Color.FromArgb(17, 122, 101)
                : Color.FromArgb(100, 107, 115);
            button.FlatStyle = FlatStyle.Flat;
            button.ForeColor = Color.White;
            button.Margin = new Padding(0, 0, 7, 0);
            button.Name = name;
            button.Size = new Size(width, height);
            button.Text = text;
            button.UseVisualStyleBackColor = false;
            return button;
        }

        private static TabPage CreatePreviewPage(string name, string text)
        {
            TabPage page = new TabPage();
            page.BackColor = Color.Black;
            page.Name = name;
            page.Padding = Padding.Empty;
            page.Text = text;
            page.UseVisualStyleBackColor = false;
            return page;
        }

        private static void ConfigureGrid(DataGridView grid)
        {
            grid.AllowUserToAddRows = false;
            grid.AllowUserToDeleteRows = false;
            grid.AllowUserToResizeRows = false;
            grid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None;
            grid.BackgroundColor = Color.White;
            grid.BorderStyle = BorderStyle.FixedSingle;
            grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.Single;
            grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(226, 230, 234);
            grid.ColumnHeadersDefaultCellStyle.ForeColor = Color.FromArgb(39, 43, 48);
            grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = Color.FromArgb(226, 230, 234);
            grid.ColumnHeadersDefaultCellStyle.SelectionForeColor = Color.FromArgb(39, 43, 48);
            grid.ColumnHeadersHeight = 30;
            grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
            grid.DefaultCellStyle.BackColor = Color.White;
            grid.DefaultCellStyle.ForeColor = Color.FromArgb(45, 49, 54);
            grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(205, 226, 220);
            grid.DefaultCellStyle.SelectionForeColor = Color.FromArgb(25, 45, 40);
            grid.Dock = DockStyle.Fill;
            grid.EnableHeadersVisualStyles = false;
            grid.GridColor = Color.FromArgb(218, 222, 226);
            grid.MultiSelect = false;
            grid.Name = "dgvBlobs";
            grid.ReadOnly = true;
            grid.RowHeadersVisible = false;
            grid.RowTemplate.Height = 25;
            grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        }

        private static DataGridViewTextBoxColumn CreateGridColumn(string name, string text, int width)
        {
            DataGridViewTextBoxColumn column = new DataGridViewTextBoxColumn();
            column.HeaderText = text;
            column.Name = name;
            column.ReadOnly = true;
            column.SortMode = DataGridViewColumnSortMode.NotSortable;
            column.Width = width;
            return column;
        }
    }
}
