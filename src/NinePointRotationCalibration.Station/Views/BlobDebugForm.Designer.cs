using System;
using System.ComponentModel;
using System.Drawing;
using System.Windows.Forms;
using NinePointRotationCalibration.Station.Controls;

namespace NinePointRotationCalibration.Station.Views
{
    // BlobDebugForm 界面控件声明与布局构建
    partial class BlobDebugForm
    {
        private static readonly Color PageBackgroundColor = Color.FromArgb(248, 250, 252);
        private static readonly Color CardBackgroundColor = Color.White;
        private static readonly Color CardBorderColor = Color.FromArgb(226, 232, 240);
        private static readonly Color HeaderDarkColor = Color.FromArgb(15, 23, 42);
        private static readonly Color HeaderSurfaceColor = Color.FromArgb(30, 41, 59);
        private static readonly Color ViewportBackdropColor = Color.FromArgb(11, 15, 23);
        private static readonly Color ViewportHeaderColor = Color.FromArgb(17, 24, 39);
        private static readonly Color TextPrimaryColor = Color.FromArgb(15, 23, 42);
        private static readonly Color TextSecondaryColor = Color.FromArgb(71, 85, 105);
        private static readonly Color TextMutedColor = Color.FromArgb(100, 116, 139);
        private static readonly Color AccentPrimaryColor = Color.FromArgb(37, 99, 235);
        private static readonly Color AccentHoverColor = Color.FromArgb(29, 78, 216);
        private static readonly Color AccentDownColor = Color.FromArgb(30, 64, 175);

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
        private ComboBox cmbDetectionPolarity;
        private ComboBox cmbHoleFillMode;
        private CheckBox chkAutoThreshold;
        private Label lblModeStatus;
        private Label lblPreviewStatus;
        private Label lblSummary;
        private Button btnRefresh;
        private Button btnFit;
        private Button btnActualPixels;
        private Button btnToggleLayout;
        private Button btnApply;
        private Button btnCancel;
        private SplitContainer previewSplit;
        private ImageViewport sourceViewport;
        private ImageViewport binaryViewport;
        private DataGridView dgvBlobs;
        private Timer previewTimer;
        private ToolTip toolTip;

        /// <summary>释放后台分析资源及窗体组件。</summary>
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
            BackColor = PageBackgroundColor;
            ClientSize = new Size(1440, 880);
            Font = new Font("Microsoft YaHei UI", 9.0F, FontStyle.Regular, GraphicsUnit.Point, 134);
            ForeColor = TextPrimaryColor;
            FormBorderStyle = FormBorderStyle.Sizable;
            KeyPreview = true;
            MinimumSize = new Size(1040, 680);
            Name = "BlobDebugForm";
            ShowIcon = false;
            StartPosition = FormStartPosition.CenterParent;
            Text = "Blob 检测调试";

            TableLayoutPanel root = new TableLayoutPanel();
            root.BackColor = PageBackgroundColor;
            root.ColumnCount = 1;
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100.0F));
            root.Dock = DockStyle.Fill;
            root.Margin = Padding.Empty;
            root.RowCount = 2;
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100.0F));
            // 底部操作栏高度 56px，确保 34px 高的「应用参数 / 取消」按钮完整展示且留有均衡边距
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 56.0F));

            mainSplit = new SplitContainer();
            mainSplit.BackColor = CardBorderColor;
            mainSplit.Dock = DockStyle.Fill;
            mainSplit.FixedPanel = FixedPanel.Panel1;
            mainSplit.IsSplitterFixed = false;
            mainSplit.Name = "mainSplit";
            mainSplit.Size = new Size(1440, 824);
            mainSplit.Panel1.BackColor = PageBackgroundColor;
            mainSplit.Panel1MinSize = 310;
            mainSplit.Panel2.BackColor = ViewportBackdropColor;
            mainSplit.Panel2MinSize = 560;
            mainSplit.SplitterDistance = 336;
            mainSplit.SplitterWidth = 4;

            BuildParameterPanel(mainSplit.Panel1);
            BuildPreviewPanel(mainSplit.Panel2);
            root.Controls.Add(mainSplit, 0, 0);
            root.Controls.Add(BuildFooter(), 0, 1);
            Controls.Add(root);

            previewTimer.Interval = 40;
            ResumeLayout(false);
        }

        // 构建左侧极简白卡 Blob 参数调试面板
        private void BuildParameterPanel(Control parent)
        {
            TableLayoutPanel layout = new TableLayoutPanel();
            layout.AutoScroll = true;
            layout.BackColor = PageBackgroundColor;
            layout.ColumnCount = 1;
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100.0F));
            layout.Dock = DockStyle.Fill;
            layout.Padding = new Padding(10);
            layout.RowCount = 4;
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100.0F));

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
            lblModeStatus.Font = new Font("Microsoft YaHei UI", 8.25F, FontStyle.Regular, GraphicsUnit.Point, 134);
            lblModeStatus.ForeColor = TextMutedColor;
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
            Panel modeGroup = CreateCard("检测路径", modeFields);

            TableLayoutPanel thresholdFields = CreateFieldTable();
            cmbDetectionPolarity = CreateComboBox("cmbDetectionPolarity");
            cmbDetectionPolarity.Items.AddRange(new object[] { "黑底白点 (亮)", "白底黑点 (暗)" });
            cmbDetectionPolarity.SelectedIndex = 0;
            cmbHoleFillMode = CreateComboBox("cmbHoleFillMode");
            cmbHoleFillMode.Items.AddRange(new object[] { "填充内部空洞", "不填充空洞" });
            cmbHoleFillMode.SelectedIndex = 0;
            nudBrightThreshold = CreateNumeric("nudBrightThreshold", 128.0M, 0.0M, 255.0M, 0, 1.0M);
            nudMinimumBlobArea = CreateNumeric("nudMinimumBlobArea", 20.0M, 0.0M, 10000000.0M, 0, 10.0M);
            nudMaximumBlobArea = CreateNumeric("nudMaximumBlobArea", 100000.0M, 0.0M, 10000000.0M, 0, 100.0M);
            nudRectangularity = CreateNumeric("nudRectangularity", 0.92M, 0.0M, 1.0M, 2, 0.01M);
            nudMorphologyRadius = CreateNumeric("nudMorphologyRadius", 1.0M, 0.0M, 20.0M, 0, 1.0M);
            AddField(thresholdFields, 0, "检测极性", cmbDetectionPolarity);
            AddField(thresholdFields, 1, "空洞填充", cmbHoleFillMode);
            AddField(thresholdFields, 2, "手动阈值", nudBrightThreshold);
            AddField(thresholdFields, 3, "最小面积 (px²)", nudMinimumBlobArea);
            AddField(thresholdFields, 4, "最大面积 (px²)", nudMaximumBlobArea);
            AddField(thresholdFields, 5, "矩形度阈值", nudRectangularity);
            AddField(thresholdFields, 6, "噪点半径 (px)", nudMorphologyRadius);
            chkAutoThreshold = CreateCheckBox("chkAutoThreshold", "检测 ROI 内自动阈值 (Otsu)");
            thresholdFields.Controls.Add(chkAutoThreshold, 0, 7);
            thresholdFields.SetColumnSpan(chkAutoThreshold, 2);
            chkRectangleFilter = CreateCheckBox("chkRectangleFilter", "启用矩形标签过滤");
            thresholdFields.Controls.Add(chkRectangleFilter, 0, 8);
            thresholdFields.SetColumnSpan(chkRectangleFilter, 2);
            Panel thresholdGroup = CreateCard("极性与 Blob 参数", thresholdFields);

            TableLayoutPanel resultLayout = new TableLayoutPanel();
            resultLayout.AutoSize = true;
            resultLayout.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            resultLayout.BackColor = CardBackgroundColor;
            resultLayout.ColumnCount = 1;
            resultLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100.0F));
            resultLayout.Dock = DockStyle.Top;
            resultLayout.Margin = Padding.Empty;
            resultLayout.Padding = Padding.Empty;
            resultLayout.RowCount = 2;
            resultLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            resultLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            lblSummary = new Label();
            lblSummary.AutoEllipsis = true;
            lblSummary.Dock = DockStyle.Fill;
            lblSummary.Font = new Font("Microsoft YaHei UI", 8.75F, FontStyle.Bold, GraphicsUnit.Point, 134);
            lblSummary.ForeColor = TextSecondaryColor;
            lblSummary.Name = "lblSummary";
            lblSummary.Padding = new Padding(0, 2, 0, 8);
            lblSummary.Text = "Blob --";
            lblSummary.TextAlign = ContentAlignment.MiddleLeft;
            btnRefresh = CreateButton("btnRefresh", "立即刷新", 92, 32, false);
            btnRefresh.Anchor = AnchorStyles.Left;
            resultLayout.Controls.Add(lblSummary, 0, 0);
            resultLayout.Controls.Add(btnRefresh, 0, 1);
            Panel resultGroup = CreateCard("当前结果", resultLayout);

            layout.Controls.Add(modeGroup, 0, 0);
            layout.Controls.Add(thresholdGroup, 0, 1);
            layout.Controls.Add(resultGroup, 0, 2);
            parent.Controls.Add(layout);
        }

        // 构建右侧原图/局部二值图对比视口及 Blob 结果表格
        private void BuildPreviewPanel(Control parent)
        {
            TableLayoutPanel layout = new TableLayoutPanel();
            layout.BackColor = ViewportBackdropColor;
            layout.ColumnCount = 1;
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100.0F));
            layout.Dock = DockStyle.Fill;
            layout.Margin = Padding.Empty;
            layout.RowCount = 3;
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38.0F));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100.0F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 118.0F));

            TableLayoutPanel toolbar = new TableLayoutPanel();
            toolbar.BackColor = HeaderDarkColor;
            toolbar.ColumnCount = 4;
            toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100.0F));
            toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96.0F));
            toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 68.0F));
            toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 58.0F));
            toolbar.Dock = DockStyle.Fill;
            toolbar.Margin = Padding.Empty;
            toolbar.Padding = new Padding(12, 5, 8, 5);
            toolbar.RowCount = 1;
            toolbar.RowStyles.Add(new RowStyle(SizeType.Percent, 100.0F));

            lblPreviewStatus = new Label();
            lblPreviewStatus.AutoEllipsis = true;
            lblPreviewStatus.Dock = DockStyle.Fill;
            lblPreviewStatus.Font = new Font("Microsoft YaHei UI", 8.5F, FontStyle.Regular, GraphicsUnit.Point, 134);
            lblPreviewStatus.ForeColor = Color.FromArgb(203, 213, 225);
            lblPreviewStatus.Name = "lblPreviewStatus";
            lblPreviewStatus.Text = "等待更新";
            lblPreviewStatus.TextAlign = ContentAlignment.MiddleLeft;
            btnToggleLayout = CreateDarkButton("btnToggleLayout", "⇄ 切换左右", 88, 26);
            btnToggleLayout.Dock = DockStyle.Fill;
            btnToggleLayout.Margin = new Padding(4, 0, 4, 0);
            btnFit = CreateDarkButton("btnFit", "适应", 62, 26);
            btnFit.Dock = DockStyle.Fill;
            btnFit.Margin = new Padding(4, 0, 4, 0);
            btnActualPixels = CreateDarkButton("btnActualPixels", "1:1", 52, 26);
            btnActualPixels.Dock = DockStyle.Fill;
            btnActualPixels.Margin = new Padding(4, 0, 0, 0);
            toolbar.Controls.Add(lblPreviewStatus, 0, 0);
            toolbar.Controls.Add(btnToggleLayout, 1, 0);
            toolbar.Controls.Add(btnFit, 2, 0);
            toolbar.Controls.Add(btnActualPixels, 3, 0);

            previewSplit = new SplitContainer();
            previewSplit.BackColor = HeaderSurfaceColor;
            previewSplit.Dock = DockStyle.Fill;
            previewSplit.IsSplitterFixed = false;
            previewSplit.Margin = Padding.Empty;
            previewSplit.Name = "previewSplit";
            previewSplit.Orientation = Orientation.Horizontal;
            previewSplit.Size = new Size(1100, 668);
            previewSplit.Panel1MinSize = 110;
            previewSplit.Panel2MinSize = 110;
            previewSplit.SplitterDistance = 332;
            previewSplit.SplitterWidth = 3;

            sourceViewport = new ImageViewport();
            sourceViewport.Dock = DockStyle.Fill;
            sourceViewport.Name = "sourceViewport";
            previewSplit.Panel1.Controls.Add(CreateViewportPane("原图视口", sourceViewport));

            binaryViewport = new ImageViewport();
            binaryViewport.Dock = DockStyle.Fill;
            binaryViewport.Name = "binaryViewport";
            previewSplit.Panel2.Controls.Add(CreateViewportPane("局部二值效果视口（ROI 分割 / 轮廓叠加）", binaryViewport));

            dgvBlobs = new DataGridView();
            ConfigureGrid(dgvBlobs);
            dgvBlobs.Columns.Add(CreateGridColumn("colBlobIndex", "#", 44));
            dgvBlobs.Columns.Add(CreateGridColumn("colBlobColumn", "X", 84));
            dgvBlobs.Columns.Add(CreateGridColumn("colBlobRow", "Y", 84));
            dgvBlobs.Columns.Add(CreateGridColumn("colBlobArea", "面积", 92));
            dgvBlobs.Columns.Add(CreateGridColumn("colBlobRectangularity", "矩形度", 84));
            DataGridViewTextBoxColumn stateColumn = CreateGridColumn("colBlobState", "判定", 96);
            stateColumn.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
            dgvBlobs.Columns.Add(stateColumn);

            layout.Controls.Add(toolbar, 0, 0);
            layout.Controls.Add(previewSplit, 0, 1);
            layout.Controls.Add(dgvBlobs, 0, 2);
            parent.Controls.Add(layout);
        }

        // 构建底部说明文字与应用/取消按钮栏
        private Control BuildFooter()
        {
            TableLayoutPanel footer = new TableLayoutPanel();
            footer.BackColor = Color.White;
            footer.ColumnCount = 2;
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100.0F));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            footer.Dock = DockStyle.Fill;
            footer.Margin = Padding.Empty;
            footer.Padding = new Padding(14, 10, 14, 10);
            footer.RowCount = 1;
            footer.RowStyles.Add(new RowStyle(SizeType.Percent, 100.0F));
            footer.Paint += delegate(object sender, PaintEventArgs e)
            {
                using (Pen border = new Pen(CardBorderColor, 1.0F))
                {
                    e.Graphics.DrawLine(border, 0, 0, footer.Width, 0);
                }
            };

            Label resultHint = new Label();
            resultHint.AutoEllipsis = true;
            resultHint.Dock = DockStyle.Fill;
            resultHint.Font = new Font("Microsoft YaHei UI", 8.5F, FontStyle.Regular, GraphicsUnit.Point, 134);
            resultHint.ForeColor = TextMutedColor;
            resultHint.Text = "提示：预览显示完整阈值分割；检测时优先在面积区间内检出非矩形不良标记，原图与二值视口支持各自独立滚轮缩放与拖拽。";
            resultHint.TextAlign = ContentAlignment.MiddleLeft;

            FlowLayoutPanel actions = new FlowLayoutPanel();
            actions.AutoSize = true;
            actions.Dock = DockStyle.Fill;
            actions.FlowDirection = FlowDirection.RightToLeft;
            actions.Margin = Padding.Empty;
            actions.WrapContents = false;
            btnApply = CreateButton("btnApply", "应用参数", 100, 34, true);
            btnApply.Margin = new Padding(0, 0, 0, 0);
            btnCancel = CreateButton("btnCancel", "取消", 84, 34, false);
            btnCancel.Margin = new Padding(0, 0, 8, 0);
            btnCancel.DialogResult = DialogResult.Cancel;
            actions.Controls.Add(btnApply);
            actions.Controls.Add(btnCancel);
            footer.Controls.Add(resultHint, 0, 0);
            footer.Controls.Add(actions, 1, 0);
            return footer;
        }

        // 控件构建与网格样式辅助方法
        private static Panel CreateCard(string text, Control body)
        {
            Panel card = new Panel();
            card.AutoSize = true;
            card.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            card.BackColor = CardBackgroundColor;
            card.Dock = DockStyle.Top;
            card.Margin = new Padding(0, 0, 0, 10);
            card.Padding = new Padding(12, 10, 12, 10);
            card.Paint += delegate(object sender, PaintEventArgs e)
            {
                if (card.Width <= 1 || card.Height <= 1) return;
                using (Pen border = new Pen(CardBorderColor, 1.0F))
                {
                    e.Graphics.DrawRectangle(border, 0, 0, card.Width - 1, card.Height - 1);
                }
            };

            TableLayoutPanel frame = new TableLayoutPanel();
            frame.AutoSize = true;
            frame.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            frame.BackColor = CardBackgroundColor;
            frame.ColumnCount = 1;
            frame.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100.0F));
            frame.Dock = DockStyle.Top;
            frame.Margin = Padding.Empty;
            frame.RowCount = 2;
            frame.RowStyles.Add(new RowStyle(SizeType.Absolute, 24.0F));
            frame.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            Label title = new Label();
            title.AutoEllipsis = true;
            title.Dock = DockStyle.Fill;
            title.Font = new Font("Microsoft YaHei UI", 9.0F, FontStyle.Bold, GraphicsUnit.Point, 134);
            title.ForeColor = TextPrimaryColor;
            title.Margin = new Padding(0, 0, 0, 4);
            title.Text = text;
            title.TextAlign = ContentAlignment.MiddleLeft;

            frame.Controls.Add(title, 0, 0);
            frame.Controls.Add(body, 0, 1);
            card.Controls.Add(frame);
            return card;
        }

        private static TableLayoutPanel CreateFieldTable()
        {
            TableLayoutPanel table = new TableLayoutPanel();
            table.AutoSize = true;
            table.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            table.BackColor = CardBackgroundColor;
            table.ColumnCount = 2;
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100.0F));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100.0F));
            table.Dock = DockStyle.Top;
            table.Font = new Font("Microsoft YaHei UI", 9.0F, FontStyle.Regular, GraphicsUnit.Point, 134);
            table.Margin = Padding.Empty;
            table.Padding = Padding.Empty;
            return table;
        }

        private static void AddField(TableLayoutPanel table, int row, string text, Control control)
        {
            Label label = new Label();
            label.AutoEllipsis = true;
            label.Dock = DockStyle.Fill;
            label.ForeColor = TextSecondaryColor;
            label.Margin = new Padding(0, 4, 6, 4);
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
            checkBox.Cursor = Cursors.Hand;
            checkBox.Dock = DockStyle.Fill;
            checkBox.ForeColor = TextPrimaryColor;
            checkBox.Margin = new Padding(0, 4, 0, 4);
            checkBox.Name = name;
            checkBox.Text = text;
            checkBox.UseVisualStyleBackColor = true;
            return checkBox;
        }

        private static ComboBox CreateComboBox(string name)
        {
            ComboBox comboBox = new ComboBox();
            comboBox.BackColor = Color.White;
            comboBox.DropDownStyle = ComboBoxStyle.DropDownList;
            comboBox.FlatStyle = FlatStyle.Flat;
            comboBox.ForeColor = TextPrimaryColor;
            comboBox.FormattingEnabled = true;
            comboBox.Name = name;
            return comboBox;
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
            input.BackColor = Color.White;
            input.BorderStyle = BorderStyle.FixedSingle;
            input.DecimalPlaces = decimalPlaces;
            input.ForeColor = TextPrimaryColor;
            input.Increment = increment;
            input.Maximum = maximum;
            input.Minimum = minimum;
            input.Name = name;
            input.TextAlign = HorizontalAlignment.Right;
            input.ThousandsSeparator = maximum >= 10000.0M;
            input.Value = Math.Max(minimum, Math.Min(maximum, value));
            return input;
        }

        private static Button CreateButton(string name, string text, int width, int height, bool primary)
        {
            Button button = new Button();
            button.BackColor = primary ? AccentPrimaryColor : Color.White;
            button.Cursor = Cursors.Hand;
            button.FlatAppearance.BorderColor = primary ? AccentPrimaryColor : Color.FromArgb(203, 213, 225);
            button.FlatAppearance.BorderSize = 1;
            button.FlatAppearance.MouseOverBackColor = primary ? AccentHoverColor : Color.FromArgb(241, 245, 249);
            button.FlatAppearance.MouseDownBackColor = primary ? AccentDownColor : Color.FromArgb(226, 232, 240);
            button.FlatStyle = FlatStyle.Flat;
            button.Font = new Font("Microsoft YaHei UI", 8.75F, primary ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Point, 134);
            button.ForeColor = primary ? Color.White : TextPrimaryColor;
            button.Margin = new Padding(0, 0, 7, 0);
            button.Name = name;
            button.Size = new Size(width, height);
            button.Text = text;
            button.UseVisualStyleBackColor = false;
            return button;
        }

        private static Button CreateDarkButton(string name, string text, int width, int height)
        {
            Button button = new Button();
            button.BackColor = HeaderSurfaceColor;
            button.Cursor = Cursors.Hand;
            button.FlatAppearance.BorderColor = Color.FromArgb(51, 65, 85);
            button.FlatAppearance.BorderSize = 1;
            button.FlatAppearance.MouseOverBackColor = Color.FromArgb(51, 65, 85);
            button.FlatAppearance.MouseDownBackColor = HeaderDarkColor;
            button.FlatStyle = FlatStyle.Flat;
            button.Font = new Font("Microsoft YaHei UI", 8.25F, FontStyle.Regular, GraphicsUnit.Point, 134);
            button.ForeColor = Color.FromArgb(226, 232, 240);
            button.Margin = new Padding(0, 0, 6, 0);
            button.Name = name;
            button.Size = new Size(width, height);
            button.Text = text;
            button.UseVisualStyleBackColor = false;
            button.Paint += delegate(object sender, PaintEventArgs e)
            {
                if (button.Enabled) return;
                Rectangle bounds = new Rectangle(0, 0, button.Width, button.Height);
                using (SolidBrush bg = new SolidBrush(Color.FromArgb(26, 36, 54)))
                    e.Graphics.FillRectangle(bg, bounds);
                using (Pen border = new Pen(Color.FromArgb(51, 65, 85), 1.0F))
                    e.Graphics.DrawRectangle(border, 0, 0, button.Width - 1, button.Height - 1);
                TextRenderer.DrawText(
                    e.Graphics,
                    button.Text,
                    button.Font,
                    bounds,
                    Color.FromArgb(148, 163, 184),
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
            };
            return button;
        }

        private static Control CreateViewportPane(string headerText, ImageViewport viewport)
        {
            Panel container = new Panel();
            container.BackColor = ViewportBackdropColor;
            container.Dock = DockStyle.Fill;

            Label header = new Label();
            header.BackColor = ViewportHeaderColor;
            header.Dock = DockStyle.Top;
            header.Font = new Font("Microsoft YaHei UI", 8.25F, FontStyle.Bold, GraphicsUnit.Point, 134);
            header.ForeColor = Color.FromArgb(148, 163, 184);
            header.Height = 24;
            header.Padding = new Padding(10, 0, 0, 0);
            header.Text = headerText;
            header.TextAlign = ContentAlignment.MiddleLeft;

            container.Controls.Add(viewport);
            container.Controls.Add(header);
            return container;
        }

        private static void ConfigureGrid(DataGridView grid)
        {
            System.Reflection.PropertyInfo doubleBuffered = typeof(DataGridView).GetProperty(
                "DoubleBuffered",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            if (doubleBuffered != null) doubleBuffered.SetValue(grid, true, null);

            grid.AllowUserToAddRows = false;
            grid.AllowUserToDeleteRows = false;
            grid.AllowUserToResizeRows = false;
            grid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None;
            grid.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(250, 252, 255);
            grid.BackgroundColor = Color.White;
            grid.BorderStyle = BorderStyle.None;
            grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
            grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
            grid.ColumnHeadersDefaultCellStyle.BackColor = PageBackgroundColor;
            grid.ColumnHeadersDefaultCellStyle.Font = new Font("Microsoft YaHei UI", 8.5F, FontStyle.Bold, GraphicsUnit.Point, 134);
            grid.ColumnHeadersDefaultCellStyle.ForeColor = TextSecondaryColor;
            grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = PageBackgroundColor;
            grid.ColumnHeadersDefaultCellStyle.SelectionForeColor = TextSecondaryColor;
            grid.ColumnHeadersHeight = 30;
            grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
            grid.DefaultCellStyle.BackColor = Color.White;
            grid.DefaultCellStyle.ForeColor = TextPrimaryColor;
            grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(239, 246, 255);
            grid.DefaultCellStyle.SelectionForeColor = AccentHoverColor;
            grid.Dock = DockStyle.Fill;
            grid.EnableHeadersVisualStyles = false;
            grid.GridColor = Color.FromArgb(241, 245, 249);
            grid.MultiSelect = false;
            grid.Name = "dgvBlobs";
            grid.ReadOnly = true;
            grid.RowHeadersVisible = false;
            grid.RowTemplate.Height = 27;
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
