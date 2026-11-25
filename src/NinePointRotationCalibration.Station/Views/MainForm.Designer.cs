using System;
using System.ComponentModel;
using System.Drawing;
using System.Windows.Forms;
using NinePointRotationCalibration.Station.Controls;
using NinePointRotationCalibration.WinForms.Controls;

namespace NinePointRotationCalibration.Station.Views
{
    // MainForm 界面控件声明与布局构建
    partial class MainForm
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

        private TableLayoutPanel rootLayout;
        private SplitContainer workspaceSplit;
        private SplitContainer imageSplit;
        private ImageViewport imageViewport;
        private ImageViewport effectViewport;
        private Panel pnlSourceViewportHeader;
        private Panel pnlEffectViewportHeader;
        private Label lblEffectViewportInfo;
        private Panel pnlCameraHeroCard;
        private Label lblCameraHeroState;
        private Label lblCameraHeroDetail;
        private Label lblCameraLiveStatus;
        private Label lblCameraResolution;
        private Button btnToggleDualLayout;
        private Button btnToggleLiveStream;
        private Button btnSyncLiveToMain;
        private Button btnResumeLive;
        private TableLayoutPanel logLayout;
        private Button btnToggleLog;
        private DataGridView dgvCalibration;
        private DataGridView dgvDetectionResults;
        private RichTextBox rtbLog;
        private TabControl tabWork;
        private NumericUpDown nudExpectedCount;
        private ComboBox cmbDetectionPolarity;
        private ComboBox cmbHoleFillMode;
        private CheckBox chkRectangleFilter;
        private CheckBox chkTemplateMatchingEnabled;
        private CheckBox chkBinaryPreview;
        private ComboBox cmbCameraProvider;
        private ComboBox cmbCameraDevice;
        private ComboBox cmbCalibrationNozzle;
        private CheckBox chkCameraEnabled;
        private CheckBox chkCalibrationEnabled;
        private CheckBox chkInspectionEnabled;
        private CheckBox chkLiveAcquisition;
        private CheckBox chkArchiveImages;
        private TextBox txtTcpBindAddress;
        private NumericUpDown nudTcpPort;
        private Label lblCameraIndicator;
        private Label lblCameraStatus;
        private Label lblTcpIndicator;
        private Label lblTcpStatus;
        private Label lblOperationStatus;
        private Label lblImageInfo;
        private Label lblCursorPosition;
        private Label lblInspectionHud;
        private Label lblDetectionSummary;
        private Label lblCalibrationSpanHint;
        private Button btnSnapshot;
        private Button btnImportImage;
        private Button btnDetectCurrentImage;
        private Button btnFit;
        private Button btnActualPixels;
        private Button btnClearLog;
        private Button[] calibrationPointButtons;
        private TextBox[] leftTeachingValues;
        private TextBox[] rightTeachingValues;
        private Label lblLeftTeachingStatus;
        private Label lblRightTeachingStatus;
        private Label lblDualNozzleSummary;
        private Label lblCalibrationRmsSummary;
        private TabPage tabCalibration;
        private TabPage tabDetection;
        private ToolTip toolTip;

        protected override void Dispose(bool disposing)
        {
            if (disposing && components != null)
            {
                components.Dispose();
            }
            base.Dispose(disposing);
        }

        private void InitializeComponent()
        {
            components = new Container();
            toolTip = new ToolTip(components);

            SuspendLayout();
            AutoScaleDimensions = new SizeF(96.0F, 96.0F);
            AutoScaleMode = AutoScaleMode.Dpi;
            BackColor = PageBackgroundColor;
            ClientSize = new Size(1600, 960);
            Font = new Font("Microsoft YaHei UI", 9.0F, FontStyle.Regular, GraphicsUnit.Point, 134);
            ForeColor = TextPrimaryColor;
            MinimumSize = new Size(1280, 720);
            Name = "MainForm";
            StartPosition = FormStartPosition.CenterScreen;
            Text = "视觉工作站";

            rootLayout = new TableLayoutPanel();
            rootLayout.BackColor = PageBackgroundColor;
            rootLayout.ColumnCount = 1;
            rootLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100.0F));
            rootLayout.Dock = DockStyle.Fill;
            rootLayout.Margin = Padding.Empty;
            rootLayout.Name = "rootLayout";
            rootLayout.RowCount = 3;
            rootLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 52.0F));
            rootLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100.0F));
            rootLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 124.0F));

            rootLayout.Controls.Add(BuildHeader(), 0, 0);
            rootLayout.Controls.Add(BuildWorkspace(), 0, 1);
            rootLayout.Controls.Add(BuildLogPanel(), 0, 2);
            Controls.Add(rootLayout);

            ResumeLayout(false);
        }

        // 构建顶部极简深色导航状态栏与全局快捷命令栏
        private Control BuildHeader()
        {
            TableLayoutPanel header = new TableLayoutPanel();
            header.BackColor = HeaderDarkColor;
            header.ColumnCount = 2;
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100.0F));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            header.Dock = DockStyle.Fill;
            header.Margin = Padding.Empty;
            header.Padding = new Padding(14, 8, 12, 8);
            header.RowCount = 1;
            header.RowStyles.Add(new RowStyle(SizeType.Percent, 100.0F));

            FlowLayoutPanel statuses = new FlowLayoutPanel();
            statuses.AutoSize = true;
            statuses.Dock = DockStyle.Left;
            statuses.FlowDirection = FlowDirection.LeftToRight;
            statuses.Margin = Padding.Empty;
            statuses.Padding = Padding.Empty;
            statuses.WrapContents = false;

            FlowLayoutPanel cameraStatus = new FlowLayoutPanel();
            cameraStatus.AutoSize = false;
            cameraStatus.BackColor = HeaderSurfaceColor;
            cameraStatus.FlowDirection = FlowDirection.LeftToRight;
            cameraStatus.Margin = new Padding(0, 1, 8, 1);
            cameraStatus.Name = "cameraStatusPanel";
            cameraStatus.Padding = new Padding(8, 0, 8, 0);
            cameraStatus.Size = new Size(138, 34);
            cameraStatus.WrapContents = false;

            lblCameraIndicator = new Label();
            lblCameraIndicator.AutoSize = false;
            lblCameraIndicator.Font = new Font("Microsoft YaHei UI", 10.0F, FontStyle.Regular, GraphicsUnit.Point, 134);
            lblCameraIndicator.ForeColor = Color.FromArgb(148, 163, 184);
            lblCameraIndicator.Margin = Padding.Empty;
            lblCameraIndicator.Name = "lblCameraIndicator";
            lblCameraIndicator.Size = new Size(16, 34);
            lblCameraIndicator.Text = "\u25CF";
            lblCameraIndicator.TextAlign = ContentAlignment.MiddleCenter;

            lblCameraStatus = new Label();
            lblCameraStatus.AutoEllipsis = true;
            lblCameraStatus.ForeColor = Color.FromArgb(241, 245, 249);
            lblCameraStatus.Margin = new Padding(2, 0, 0, 0);
            lblCameraStatus.Name = "lblCameraStatus";
            lblCameraStatus.Size = new Size(102, 34);
            lblCameraStatus.Text = "相机未连接";
            lblCameraStatus.TextAlign = ContentAlignment.MiddleLeft;
            cameraStatus.Controls.Add(lblCameraIndicator);
            cameraStatus.Controls.Add(lblCameraStatus);

            FlowLayoutPanel tcpStatus = new FlowLayoutPanel();
            tcpStatus.AutoSize = false;
            tcpStatus.BackColor = HeaderSurfaceColor;
            tcpStatus.FlowDirection = FlowDirection.LeftToRight;
            tcpStatus.Margin = new Padding(0, 1, 8, 1);
            tcpStatus.Name = "tcpStatusPanel";
            tcpStatus.Padding = new Padding(8, 0, 8, 0);
            tcpStatus.Size = new Size(134, 34);
            tcpStatus.WrapContents = false;

            lblTcpIndicator = new Label();
            lblTcpIndicator.AutoSize = false;
            lblTcpIndicator.Font = new Font("Microsoft YaHei UI", 10.0F, FontStyle.Regular, GraphicsUnit.Point, 134);
            lblTcpIndicator.ForeColor = Color.FromArgb(148, 163, 184);
            lblTcpIndicator.Margin = Padding.Empty;
            lblTcpIndicator.Name = "lblTcpIndicator";
            lblTcpIndicator.Size = new Size(16, 34);
            lblTcpIndicator.Text = "\u25CF";
            lblTcpIndicator.TextAlign = ContentAlignment.MiddleCenter;

            lblTcpStatus = new Label();
            lblTcpStatus.AutoEllipsis = true;
            lblTcpStatus.ForeColor = Color.FromArgb(241, 245, 249);
            lblTcpStatus.Margin = new Padding(2, 0, 0, 0);
            lblTcpStatus.Name = "lblTcpStatus";
            lblTcpStatus.Size = new Size(98, 34);
            lblTcpStatus.Text = "TCP 未监听";
            lblTcpStatus.TextAlign = ContentAlignment.MiddleLeft;
            tcpStatus.Controls.Add(lblTcpIndicator);
            tcpStatus.Controls.Add(lblTcpStatus);

            lblOperationStatus = CreateStatusLabel("lblOperationStatus", "就绪", 168);
            lblOperationStatus.BackColor = Color.FromArgb(20, 83, 45);
            lblOperationStatus.ForeColor = Color.FromArgb(187, 247, 208);
            statuses.Controls.Add(cameraStatus);
            statuses.Controls.Add(tcpStatus);
            statuses.Controls.Add(lblOperationStatus);

            FlowLayoutPanel commands = new FlowLayoutPanel();
            commands.AutoSize = true;
            commands.Dock = DockStyle.Fill;
            commands.FlowDirection = FlowDirection.LeftToRight;
            commands.Margin = Padding.Empty;
            commands.Padding = new Padding(0, 1, 0, 0);
            commands.WrapContents = false;

            btnSnapshot = CreateHeaderButton("btnSnapshot", "拍照", 72, 34, true, "camera.snapshot");
            btnResumeLive = CreateHeaderButton("btnResumeLive", "实时预览", 82, 34, false, "camera.resumeMainLive");
            btnImportImage = CreateHeaderButton("btnImportImage", "导入图片", 82, 34, false, "image.import");
            btnDetectCurrentImage = CreateHeaderButton("btnDetectCurrentImage", "检测当前图", 96, 34, true, "detection.currentImage");
            btnFit = CreateHeaderButton("btnFit", "适应", 58, 34, false, null);
            btnActualPixels = CreateHeaderButton("btnActualPixels", "1:1", 50, 34, false, null);
            toolTip.SetToolTip(btnSnapshot, "触发单次采集");
            toolTip.SetToolTip(btnResumeLive, "解锁主视口并切回相机实时采集画面");
            toolTip.SetToolTip(btnImportImage, "从本地导入一张图片");
            toolTip.SetToolTip(btnDetectCurrentImage, "检测图像窗口中的当前图片");
            toolTip.SetToolTip(btnFit, "图像适应窗口");
            toolTip.SetToolTip(btnActualPixels, "按原始像素显示");
            commands.Controls.Add(btnSnapshot);
            commands.Controls.Add(btnResumeLive);
            commands.Controls.Add(btnImportImage);
            commands.Controls.Add(btnDetectCurrentImage);
            commands.Controls.Add(btnFit);
            commands.Controls.Add(btnActualPixels);

            header.Controls.Add(statuses, 0, 0);
            header.Controls.Add(commands, 1, 0);
            return header;
        }

        // 构建中央左右分栏工作区：左侧为深色图像视口与极简 HUD，右侧为白色卡片式工作区
        private Control BuildWorkspace()
        {
            workspaceSplit = new SplitContainer();
            workspaceSplit.BackColor = CardBorderColor;
            workspaceSplit.Dock = DockStyle.Fill;
            workspaceSplit.FixedPanel = FixedPanel.None;
            workspaceSplit.IsSplitterFixed = false;
            workspaceSplit.Margin = Padding.Empty;
            workspaceSplit.Name = "workspaceSplit";
            workspaceSplit.Size = new Size(1584, 784);
            workspaceSplit.Panel1MinSize = 520;
            workspaceSplit.Panel2MinSize = 460;
            workspaceSplit.SplitterDistance = 940;
            workspaceSplit.SplitterWidth = 4;

            TableLayoutPanel imageLayout = new TableLayoutPanel();
            imageLayout.BackColor = ViewportBackdropColor;
            imageLayout.ColumnCount = 1;
            imageLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100.0F));
            imageLayout.Dock = DockStyle.Fill;
            imageLayout.Padding = Padding.Empty;
            imageLayout.RowCount = 2;
            imageLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100.0F));
            imageLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 26.0F));

            imageSplit = new SplitContainer();
            imageSplit.BackColor = Color.FromArgb(30, 41, 59);
            imageSplit.Dock = DockStyle.Fill;
            imageSplit.FixedPanel = FixedPanel.None;
            imageSplit.IsSplitterFixed = false;
            imageSplit.Margin = Padding.Empty;
            imageSplit.Name = "imageSplit";
            imageSplit.Orientation = Orientation.Horizontal;
            imageSplit.Size = new Size(940, 758);
            imageSplit.Panel1MinSize = 150;
            imageSplit.Panel2MinSize = 150;
            imageSplit.SplitterWidth = 3;
            imageSplit.SplitterDistance = 378;
            imageSplit.Panel2Collapsed = true;

            pnlSourceViewportHeader = new Panel();
            pnlSourceViewportHeader.Name = "pnlSourceViewportHeader";
            pnlSourceViewportHeader.BackColor = ViewportHeaderColor;
            pnlSourceViewportHeader.Dock = DockStyle.Top;
            pnlSourceViewportHeader.Height = 26;
            pnlSourceViewportHeader.Padding = new Padding(10, 2, 8, 2);
            pnlSourceViewportHeader.Visible = false;
            btnToggleDualLayout = new Button();
            btnToggleDualLayout.BackColor = HeaderSurfaceColor;
            btnToggleDualLayout.Cursor = Cursors.Hand;
            btnToggleDualLayout.Dock = DockStyle.Right;
            btnToggleDualLayout.FlatAppearance.BorderColor = Color.FromArgb(51, 65, 85);
            btnToggleDualLayout.FlatAppearance.BorderSize = 1;
            btnToggleDualLayout.FlatAppearance.MouseOverBackColor = Color.FromArgb(51, 65, 85);
            btnToggleDualLayout.FlatStyle = FlatStyle.Flat;
            btnToggleDualLayout.Font = new Font("Microsoft YaHei UI", 8.0F, FontStyle.Regular, GraphicsUnit.Point, 134);
            btnToggleDualLayout.ForeColor = Color.FromArgb(226, 232, 240);
            btnToggleDualLayout.Name = "btnToggleDualLayout";
            btnToggleDualLayout.Text = "⇄ 切换左右";
            btnToggleDualLayout.Width = 88;
            toolTip.SetToolTip(btnToggleDualLayout, "切换原图与检测效果图窗口的上下 / 左右排列方式");
            Label lblSourceViewportTitle = new Label();
            lblSourceViewportTitle.Dock = DockStyle.Fill;
            lblSourceViewportTitle.Font = new Font("Microsoft YaHei UI", 8.25F, FontStyle.Bold, GraphicsUnit.Point, 134);
            lblSourceViewportTitle.ForeColor = Color.FromArgb(203, 213, 225);
            lblSourceViewportTitle.Text = "原图视口（独立缩放）";
            lblSourceViewportTitle.TextAlign = ContentAlignment.MiddleLeft;
            toolTip.SetToolTip(lblSourceViewportTitle, "原图与检测效果视口各自独立滚轮缩放与拖拽平移，双击视口恢复适应");
            pnlSourceViewportHeader.Controls.Add(lblSourceViewportTitle);
            pnlSourceViewportHeader.Controls.Add(btnToggleDualLayout);

            imageViewport = new ImageViewport();
            imageViewport.Dock = DockStyle.Fill;
            imageViewport.Margin = Padding.Empty;
            imageViewport.Name = "imageViewport";
            imageViewport.TabIndex = 0;

            imageSplit.Panel1.BackColor = ViewportBackdropColor;
            imageSplit.Panel1.Controls.Add(imageViewport);
            imageSplit.Panel1.Controls.Add(pnlSourceViewportHeader);

            pnlEffectViewportHeader = new Panel();
            pnlEffectViewportHeader.Name = "pnlEffectViewportHeader";
            pnlEffectViewportHeader.BackColor = ViewportHeaderColor;
            pnlEffectViewportHeader.Dock = DockStyle.Top;
            pnlEffectViewportHeader.Height = 26;
            pnlEffectViewportHeader.Padding = new Padding(10, 0, 10, 0);
            Label lblEffectViewportTitle = new Label();
            lblEffectViewportTitle.AutoSize = true;
            lblEffectViewportTitle.Dock = DockStyle.Left;
            lblEffectViewportTitle.Font = new Font("Microsoft YaHei UI", 8.25F, FontStyle.Bold, GraphicsUnit.Point, 134);
            lblEffectViewportTitle.ForeColor = Color.FromArgb(96, 165, 250);
            lblEffectViewportTitle.Text = "检测效果视口";
            lblEffectViewportTitle.TextAlign = ContentAlignment.MiddleLeft;
            lblEffectViewportInfo = new Label();
            lblEffectViewportInfo.Name = "lblEffectViewportInfo";
            lblEffectViewportInfo.AutoEllipsis = true;
            lblEffectViewportInfo.Dock = DockStyle.Fill;
            lblEffectViewportInfo.Font = new Font("Microsoft YaHei UI", 8.0F, FontStyle.Regular, GraphicsUnit.Point, 134);
            lblEffectViewportInfo.ForeColor = Color.FromArgb(148, 163, 184);
            lblEffectViewportInfo.Padding = new Padding(12, 0, 0, 0);
            lblEffectViewportInfo.Text = "未加载图像 · 请先导入图片";
            lblEffectViewportInfo.TextAlign = ContentAlignment.MiddleLeft;
            pnlEffectViewportHeader.Controls.Add(lblEffectViewportInfo);
            pnlEffectViewportHeader.Controls.Add(lblEffectViewportTitle);

            effectViewport = new ImageViewport();
            effectViewport.Dock = DockStyle.Fill;
            effectViewport.Margin = Padding.Empty;
            effectViewport.Name = "effectViewport";
            effectViewport.TabIndex = 1;

            imageSplit.Panel2.BackColor = ViewportBackdropColor;
            imageSplit.Panel2.Controls.Add(effectViewport);
            imageSplit.Panel2.Controls.Add(pnlEffectViewportHeader);

            // 精简双行悬浮 HUD：左侧彩色状态条 + 紧凑核心指标，不遮挡大面积检测画面
            lblInspectionHud = new Label();
            lblInspectionHud.AutoEllipsis = true;
            lblInspectionHud.BackColor = Color.FromArgb(17, 24, 39);
            lblInspectionHud.Font = new Font("Microsoft YaHei UI", 8.75F, FontStyle.Bold, GraphicsUnit.Point, 134);
            lblInspectionHud.ForeColor = Color.FromArgb(226, 232, 240);
            lblInspectionHud.Location = new Point(12, 12);
            lblInspectionHud.Name = "lblInspectionHud";
            lblInspectionHud.Padding = new Padding(12, 6, 10, 6);
            lblInspectionHud.Size = new Size(412, 54);
            lblInspectionHud.Text = "等待检测\r\nCT --";
            lblInspectionHud.TextAlign = ContentAlignment.MiddleLeft;
            lblInspectionHud.Visible = false;
            lblInspectionHud.Paint += LblInspectionHud_Paint;
            imageViewport.Controls.Add(lblInspectionHud);
            lblInspectionHud.BringToFront();

            TableLayoutPanel imageFooter = new TableLayoutPanel();
            imageFooter.BackColor = ViewportHeaderColor;
            imageFooter.ColumnCount = 2;
            imageFooter.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100.0F));
            imageFooter.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 184.0F));
            imageFooter.Dock = DockStyle.Fill;
            imageFooter.Margin = Padding.Empty;
            imageFooter.Padding = new Padding(10, 0, 10, 0);

            lblImageInfo = new Label();
            lblImageInfo.AutoEllipsis = true;
            lblImageInfo.Dock = DockStyle.Fill;
            lblImageInfo.Font = new Font("Microsoft YaHei UI", 8.25F, FontStyle.Regular, GraphicsUnit.Point, 134);
            lblImageInfo.ForeColor = Color.FromArgb(148, 163, 184);
            lblImageInfo.Text = "未加载图像 · 请点击上方「导入图片」或「拍照」";
            lblImageInfo.TextAlign = ContentAlignment.MiddleLeft;

            lblCursorPosition = new Label();
            lblCursorPosition.Dock = DockStyle.Fill;
            lblCursorPosition.Font = new Font("Consolas", 8.5F, FontStyle.Regular, GraphicsUnit.Point);
            lblCursorPosition.ForeColor = Color.FromArgb(148, 163, 184);
            lblCursorPosition.Text = "X --   Y --";
            lblCursorPosition.TextAlign = ContentAlignment.MiddleRight;

            imageFooter.Controls.Add(lblImageInfo, 0, 0);
            imageFooter.Controls.Add(lblCursorPosition, 1, 0);
            imageLayout.Controls.Add(imageSplit, 0, 0);
            imageLayout.Controls.Add(imageFooter, 0, 1);
            workspaceSplit.Panel1.Controls.Add(imageLayout);

            tabWork = new BufferedTabControl();
            tabWork.Dock = DockStyle.Fill;
            tabWork.DrawMode = TabDrawMode.OwnerDrawFixed;
            tabWork.Font = new Font("Microsoft YaHei UI", 9.0F, FontStyle.Regular, GraphicsUnit.Point, 134);
            tabWork.ItemSize = new Size(76, 36);
            tabWork.Multiline = false;
            tabWork.Name = "tabWork";
            tabWork.Padding = new Point(8, 6);
            tabWork.SelectedIndex = 0;
            tabWork.SizeMode = TabSizeMode.Fixed;
            tabWork.TabPages.Add(BuildCalibrationTab());
            tabWork.TabPages.Add(BuildTeachingTab());
            tabWork.TabPages.Add(BuildTemplateTab());
            tabWork.TabPages.Add(BuildDetectionTab());
            tabWork.TabPages.Add(BuildCameraTab());
            tabWork.TabPages.Add(BuildCommunicationTab());
            workspaceSplit.Panel2.BackColor = PageBackgroundColor;
            workspaceSplit.Panel2.Controls.Add(tabWork);

            return workspaceSplit;
        }

        // 构建 9+5 标定页（1-9 平移点 + 10-14 旋转点及采样表）
        private TabPage BuildCalibrationTab()
        {
            tabCalibration = CreateTabPage("tabCalibration", "9+5 标定");
            TabPage page = tabCalibration;
            page.AutoScroll = true;
            page.AutoScrollMinSize = new Size(0, 540);

            TableLayoutPanel layout = new TableLayoutPanel();
            layout.BackColor = PageBackgroundColor;
            layout.ColumnCount = 1;
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100.0F));
            layout.Dock = DockStyle.Fill;
            layout.MinimumSize = new Size(0, 520);
            layout.Padding = new Padding(10);
            layout.RowCount = 4;
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 306.0F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44.0F));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100.0F));

            TableLayoutPanel editor = new TableLayoutPanel();
            editor.ColumnCount = 2;
            editor.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 53.0F));
            editor.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 47.0F));
            editor.Dock = DockStyle.Fill;
            editor.Margin = Padding.Empty;
            editor.RowCount = 1;
            editor.RowStyles.Add(new RowStyle(SizeType.Percent, 100.0F));

            TableLayoutPanel pointLayout = new TableLayoutPanel();
            pointLayout.ColumnCount = 3;
            pointLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.333F));
            pointLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.333F));
            pointLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.334F));
            pointLayout.Dock = DockStyle.Fill;
            pointLayout.Margin = Padding.Empty;
            pointLayout.RowCount = 4;
            pointLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 25.0F));
            pointLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 25.0F));
            pointLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 25.0F));
            pointLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 25.0F));

            Button point1 = CreatePointButton(1);
            Button point2 = CreatePointButton(2);
            Button point3 = CreatePointButton(3);
            Button point4 = CreatePointButton(4);
            Button point5 = CreatePointButton(5);
            Button point6 = CreatePointButton(6);
            Button point7 = CreatePointButton(7);
            Button point8 = CreatePointButton(8);
            Button point9 = CreatePointButton(9);
            Button point10 = CreatePointButton(10);
            Button point11 = CreatePointButton(11);
            Button point12 = CreatePointButton(12);
            Button point13 = CreatePointButton(13);
            Button point14 = CreatePointButton(14);

            pointLayout.Controls.Add(point1, 0, 0);
            pointLayout.Controls.Add(point2, 1, 0);
            pointLayout.Controls.Add(point3, 2, 0);
            pointLayout.Controls.Add(point6, 0, 1);
            pointLayout.Controls.Add(point5, 1, 1);
            pointLayout.Controls.Add(point4, 2, 1);
            pointLayout.Controls.Add(point7, 0, 2);
            pointLayout.Controls.Add(point8, 1, 2);
            pointLayout.Controls.Add(point9, 2, 2);

            TableLayoutPanel rotationLayout = new TableLayoutPanel();
            rotationLayout.ColumnCount = 5;
            rotationLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 20.0F));
            rotationLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 20.0F));
            rotationLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 20.0F));
            rotationLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 20.0F));
            rotationLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 20.0F));
            rotationLayout.Dock = DockStyle.Fill;
            rotationLayout.Margin = Padding.Empty;
            rotationLayout.Controls.Add(point10, 0, 0);
            rotationLayout.Controls.Add(point11, 1, 0);
            rotationLayout.Controls.Add(point12, 2, 0);
            rotationLayout.Controls.Add(point13, 3, 0);
            rotationLayout.Controls.Add(point14, 4, 0);
            pointLayout.Controls.Add(rotationLayout, 0, 3);
            pointLayout.SetColumnSpan(rotationLayout, 3);
            Panel pointsGroup = CreateCard("标定点位", pointLayout);
            pointsGroup.Margin = new Padding(0, 0, 6, 8);

            calibrationPointButtons = new[]
            {
                point1, point2, point3, point4, point5, point6, point7,
                point8, point9, point10, point11, point12, point13, point14
            };
            string[] pointTips =
            {
                "P1：九点平移 (-ΔX, -ΔY)", "P2：九点平移 (0, -ΔY)", "P3：九点平移 (+ΔX, -ΔY)",
                "P4：九点平移 (+ΔX, 0)", "P5：九点共享中心原点 (0, 0)", "P6：九点平移 (-ΔX, 0)",
                "P7：九点平移 (-ΔX, +ΔY)", "P8：九点平移 (0, +ΔY)", "P9：九点平移 (+ΔX, +ΔY)",
                "R10：旋转点 -2ΔR", "R11：旋转点 -ΔR", "R12：五点共享原点 0°",
                "R13：旋转点 +ΔR", "R14：旋转点 +2ΔR"
            };
            for (int i = 0; i < calibrationPointButtons.Length; i++)
            {
                toolTip.SetToolTip(calibrationPointButtons[i], pointTips[i]);
            }

            TableLayoutPanel moveFields = CreateFieldTable(2);
            moveFields.AutoScroll = false;
            moveFields.ColumnStyles[0].Width = 96.0F;
            cmbCalibrationNozzle = CreateComboBox("cmbCalibrationNozzle");
            cmbCalibrationNozzle.Items.AddRange(new object[] { "左吸嘴 (Left / R1)", "右吸嘴 (Right / R2)" });
            cmbCalibrationNozzle.SelectedIndex = 0;
            cmbCalibrationNozzle.Tag = "calibration.nozzleChanged";
            toolTip.SetToolTip(cmbCalibrationNozzle, "选择当前执行 9+5 标定的独立吸嘴与 R 轴（左吸嘴对应 R1，右吸嘴对应 R2）");
            NumericUpDown stepX = CreateDecimalInput("nudCalibrationStepX", 10.0M, 0.1M, 500.0M, 3, 1.0M, false);
            NumericUpDown stepY = CreateDecimalInput("nudCalibrationStepY", 10.0M, 0.1M, 500.0M, 3, 1.0M, false);
            NumericUpDown stepR = CreateDecimalInput("nudCalibrationStepR", 6.0M, 0.5M, 90.0M, 2, 0.5M, false);
            NumericUpDown motionTimeout = CreateDecimalInput("nudMotionTimeout", 30000.0M, 1000.0M, 600000.0M, 0, 1000.0M, false);
            NumericUpDown minScore = CreateDecimalInput("nudCalibrationMinScore", 0.500M, 0.000M, 1.000M, 3, 0.05M, false);
            minScore.Tag = "calibration.minScoreChanged";
            toolTip.SetToolTip(stepR, "五点旋转依次采集 -2ΔR / -ΔR / 0 / +ΔR / +2ΔR，总跨度 4×ΔR 须 ≥ 20°（推荐 6°~10°）");
            toolTip.SetToolTip(motionTimeout, "等待上位机运动到位的超时时间（毫秒），每次调节步进 1000 ms");
            toolTip.SetToolTip(minScore, "标定模板最低匹配分数阈值；任一点位匹配分数低于此值将立即中断标定并报 NG");
            AddField(moveFields, 0, "标定吸嘴", cmbCalibrationNozzle);
            AddField(moveFields, 1, "X 步距 (mm)", stepX);
            AddField(moveFields, 2, "Y 步距 (mm)", stepY);
            AddField(moveFields, 3, "R 步距 (°)", stepR);
            AddField(moveFields, 4, "到位超时 (ms)", motionTimeout);
            AddField(moveFields, 5, "最低分数", minScore);
            moveFields.RowCount = 7;
            moveFields.RowStyles.Add(new RowStyle(SizeType.Percent, 100.0F));
            lblCalibrationSpanHint = new Label();
            lblCalibrationSpanHint.AutoEllipsis = true;
            lblCalibrationSpanHint.Dock = DockStyle.Fill;
            lblCalibrationSpanHint.Font = new Font("Microsoft YaHei UI", 8.0F, FontStyle.Regular, GraphicsUnit.Point, 134);
            lblCalibrationSpanHint.ForeColor = TextMutedColor;
            lblCalibrationSpanHint.Margin = new Padding(0, 4, 0, 0);
            lblCalibrationSpanHint.Name = "lblCalibrationSpanHint";
            lblCalibrationSpanHint.Text = "5/12 原点 · 旋转总跨度 24.0° (要求≥20°)";
            lblCalibrationSpanHint.TextAlign = ContentAlignment.MiddleLeft;
            moveFields.Controls.Add(lblCalibrationSpanHint, 0, 6);
            moveFields.SetColumnSpan(lblCalibrationSpanHint, 2);
            Panel moveGroup = CreateCard("点位参数", moveFields);
            moveGroup.Margin = new Padding(4, 0, 0, 8);

            editor.Controls.Add(pointsGroup, 0, 0);
            editor.Controls.Add(moveGroup, 1, 0);

            FlowLayoutPanel actions = new FlowLayoutPanel();
            actions.Dock = DockStyle.Fill;
            actions.FlowDirection = FlowDirection.LeftToRight;
            actions.Margin = Padding.Empty;
            actions.Padding = new Padding(0, 4, 0, 4);
            actions.WrapContents = false;
            actions.Controls.Add(CreateButton("btnAutoCalibration", "自动标定", 94, 34, true, "calibration.auto"));
            actions.Controls.Add(CreateButton("btnManualCalibration", "手动标定", 92, 34, false, "calibration.manual"));
            actions.Controls.Add(CreateButton("btnCalculateCalibration", "计算并保存", 104, 34, false, "calibration.calculate"));
            actions.Controls.Add(CreateButton("btnCancelCalibration", "停止标定", 82, 34, false, "calibration.cancel"));

            lblCalibrationRmsSummary = new Label();
            lblCalibrationRmsSummary.AutoSize = true;
            lblCalibrationRmsSummary.BackColor = Color.FromArgb(241, 245, 249);
            lblCalibrationRmsSummary.Dock = DockStyle.Fill;
            lblCalibrationRmsSummary.Font = new Font("Microsoft YaHei UI", 8.25F, FontStyle.Regular, GraphicsUnit.Point, 134);
            lblCalibrationRmsSummary.ForeColor = TextSecondaryColor;
            lblCalibrationRmsSummary.Margin = new Padding(0, 2, 0, 8);
            lblCalibrationRmsSummary.Name = "lblCalibrationRmsSummary";
            lblCalibrationRmsSummary.Padding = new Padding(10, 7, 10, 7);
            lblCalibrationRmsSummary.Text =
                "RMS 评估：待计算（采点后点击「计算并保存」）\r\n" +
                "合理标准：≤0.20 px 优(90-100) | ≤0.50 良(75-89) | ≤1.00 合格(60-74)\r\n" +
                "旋转参考：旋中 RMS ≤ 1.00 px（≤0.30 优） | 角度 RMS ≤ 0.50°（≤0.05° 优）";
            lblCalibrationRmsSummary.TextAlign = ContentAlignment.MiddleLeft;

            dgvCalibration = new DataGridView();
            ConfigureGrid(dgvCalibration, "dgvCalibration");
            dgvCalibration.MinimumSize = new Size(0, 140);
            dgvCalibration.MultiSelect = false;
            dgvCalibration.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            AddTextColumn(dgvCalibration, "Point", "点", 34);
            AddTextColumn(dgvCalibration, "Kind", "类型", 40);
            AddTextColumn(dgvCalibration, "MachineX", "机械 X(mm)", 76);
            AddTextColumn(dgvCalibration, "MachineY", "机械 Y(mm)", 76);
            AddTextColumn(dgvCalibration, "MachineR", "机械 R(°)", 68);
            AddTextColumn(dgvCalibration, "ImageRow", "图像 Row", 70);
            AddTextColumn(dgvCalibration, "ImageColumn", "图像 Col", 70);
            AddTextColumn(dgvCalibration, "ImageAngle", "图像角(°)", 66);
            AddTextColumn(dgvCalibration, "Score", "分数", 54);
            AddTextColumn(dgvCalibration, "Residual", "残差(px)", 62);
            AddTextColumn(dgvCalibration, "Status", "状态", 50);

            layout.Controls.Add(editor, 0, 0);
            layout.Controls.Add(actions, 0, 1);
            layout.Controls.Add(lblCalibrationRmsSummary, 0, 2);
            layout.Controls.Add(WrapGridCard(dgvCalibration), 0, 3);
            page.Controls.Add(layout);
            return page;
        }

        // 构建双吸嘴左右示教页与盲摆策略摘要卡片
        private TabPage BuildTeachingTab()
        {
            TabPage page = CreateTabPage("tabTeaching", "左右示教");
            TableLayoutPanel layout = CreateScrollableStack(page);
            layout.RowCount = 4;
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 246.0F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 246.0F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 62.0F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 64.0F));

            TextBox[] leftValues;
            TextBox[] rightValues;
            Panel leftGroup = BuildTeachingGroup("左侧吸嘴示教 (Left)", "left", out leftValues, out lblLeftTeachingStatus);
            Panel rightGroup = BuildTeachingGroup("右侧吸嘴示教 (Right)", "right", out rightValues, out lblRightTeachingStatus);
            leftTeachingValues = leftValues;
            rightTeachingValues = rightValues;

            lblDualNozzleSummary = new Label();
            lblDualNozzleSummary.AutoEllipsis = true;
            lblDualNozzleSummary.BackColor = Color.FromArgb(241, 245, 249);
            lblDualNozzleSummary.Dock = DockStyle.Fill;
            lblDualNozzleSummary.Font = new Font("Microsoft YaHei UI", 8.5F, FontStyle.Bold, GraphicsUnit.Point, 134);
            lblDualNozzleSummary.ForeColor = TextPrimaryColor;
            lblDualNozzleSummary.Margin = new Padding(0, 2, 0, 8);
            lblDualNozzleSummary.Name = "lblDualNozzleSummary";
            lblDualNozzleSummary.Padding = new Padding(12, 7, 12, 7);
            lblDualNozzleSummary.Text = "双吸嘴状态：等待左右吸嘴完成示教\r\n工艺模式：吸取时旋转 R 轴对齐来料板角 → 盲摆转回固定示教 R 角";
            lblDualNozzleSummary.TextAlign = ContentAlignment.MiddleLeft;

            Label hint = CreateHintLabel("示教步骤：① 对位并填入机械 X/Y/R 后点「记录轴坐标」（不拍照，防止轴遮挡）；② 移走轴后点「模板匹配定位」触发拍照并定位第 1 号基准板图像坐标，自动合并保存。");
            layout.Controls.Add(leftGroup, 0, 0);
            layout.Controls.Add(rightGroup, 0, 1);
            layout.Controls.Add(lblDualNozzleSummary, 0, 2);
            layout.Controls.Add(hint, 0, 3);
            return page;
        }

        // 构建标定模板与定位模板管理页
        private TabPage BuildTemplateTab()
        {
            TabPage page = CreateTabPage("tabTemplates", "模板");
            TableLayoutPanel layout = CreateScrollableStack(page);
            layout.RowCount = 4;
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44.0F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 206.0F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 206.0F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48.0F));

            FlowLayoutPanel folderBar = new FlowLayoutPanel();
            folderBar.Dock = DockStyle.Fill;
            folderBar.FlowDirection = FlowDirection.LeftToRight;
            folderBar.Margin = Padding.Empty;
            folderBar.Padding = new Padding(0, 2, 0, 4);
            folderBar.WrapContents = false;
            Button importFolderButton = CreateButton("btnTemplateImportFolder", "导入配置文件夹", 124, 34, true, "files.folder.import");
            Label folderHint = new Label();
            folderHint.AutoSize = true;
            folderHint.Font = new Font("Microsoft YaHei UI", 8.25F, FontStyle.Regular, GraphicsUnit.Point, 134);
            folderHint.ForeColor = TextMutedColor;
            folderHint.Margin = new Padding(8, 9, 0, 0);
            folderHint.Text = "一键统一加载/绑定同一目录的标定、定位与检测配置";
            folderBar.Controls.Add(importFolderButton);
            folderBar.Controls.Add(folderHint);

            Panel calibration = BuildTemplateGroup(
                "标定模板",
                "txtCalibrationTemplatePath",
                "template.calibration",
                "用于 9+5 标定点的特征定位");
            Panel positioning = BuildTemplateGroup(
                "定位模板",
                "txtPositioningTemplatePath",
                "template.positioning",
                "用于 PCB 板定位、角度与中心计算");
            Label hint = CreateHintLabel("所有配置文件统一存放在同一配置文件夹；也可在「通信 / 文件」页查看或统一切换。");

            layout.Controls.Add(folderBar, 0, 0);
            layout.Controls.Add(calibration, 0, 1);
            layout.Controls.Add(positioning, 0, 2);
            layout.Controls.Add(hint, 0, 3);
            return page;
        }

        // 构建板件与标记检测参数页、调试入口及检测结果表
        private TabPage BuildDetectionTab()
        {
            tabDetection = CreateTabPage("tabDetection", "检测");
            TabPage page = tabDetection;
            TableLayoutPanel layout = new TableLayoutPanel();
            layout.BackColor = PageBackgroundColor;
            layout.ColumnCount = 1;
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100.0F));
            layout.Dock = DockStyle.Fill;
            layout.Padding = new Padding(10);
            layout.RowCount = 4;
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 316.0F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44.0F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28.0F));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100.0F));

            TableLayoutPanel settingFields = CreatePairedFieldTable();
            settingFields.RowCount = 8;
            settingFields.RowStyles.Clear();
            settingFields.RowStyles.Add(new RowStyle(SizeType.Absolute, 34.0F));
            settingFields.RowStyles.Add(new RowStyle(SizeType.Absolute, 32.0F));
            settingFields.RowStyles.Add(new RowStyle(SizeType.Absolute, 32.0F));
            settingFields.RowStyles.Add(new RowStyle(SizeType.Absolute, 32.0F));
            settingFields.RowStyles.Add(new RowStyle(SizeType.Absolute, 32.0F));
            settingFields.RowStyles.Add(new RowStyle(SizeType.Absolute, 28.0F));
            settingFields.RowStyles.Add(new RowStyle(SizeType.Absolute, 28.0F));
            settingFields.RowStyles.Add(new RowStyle(SizeType.Absolute, 28.0F));

            cmbDetectionPolarity = CreateComboBox("cmbDetectionPolarity");
            cmbDetectionPolarity.Items.AddRange(new object[] { "黑底白点 (亮)", "白底黑点 (暗)" });
            cmbDetectionPolarity.SelectedIndex = 0;
            cmbDetectionPolarity.Tag = "detection.previewParameters";
            toolTip.SetToolTip(cmbDetectionPolarity, "选择不良标记相对背景的明暗极性：黑底白点检测亮标记，白底黑点检测暗标记");

            cmbHoleFillMode = CreateComboBox("cmbHoleFillMode");
            cmbHoleFillMode.Items.AddRange(new object[] { "填充内部空洞", "不填充空洞" });
            cmbHoleFillMode.SelectedIndex = 0;
            cmbHoleFillMode.Tag = "detection.previewParameters";
            toolTip.SetToolTip(cmbHoleFillMode, "选择是否填充检出目标内部的封闭空洞（自动按当前检测极性生效）");

            nudExpectedCount = CreateDecimalInput("nudExpectedCount", 6.0M, 1.0M, 100.0M, 0, 1.0M, false);
            NumericUpDown brightThreshold = CreateDecimalInput("nudBrightThreshold", 128.0M, 0.0M, 255.0M, 0, 1.0M, false);
            NumericUpDown minimumArea = CreateDecimalInput("nudMinimumBlobArea", 20.0M, 0.0M, 10000000.0M, 0, 10.0M, true);
            NumericUpDown maximumArea = CreateDecimalInput("nudMaximumBlobArea", 100000.0M, 0.0M, 10000000.0M, 0, 100.0M, true);
            NumericUpDown rectangularity = CreateDecimalInput("nudRectangularity", 0.92M, 0.0M, 1.0M, 2, 0.01M, false);
            NumericUpDown morphologyRadius = CreateDecimalInput("nudMorphologyRadius", 1.0M, 0.0M, 20.0M, 0, 1.0M, false);
            nudExpectedCount.Tag = "detection.previewParameters";
            brightThreshold.Tag = "detection.previewParameters";
            minimumArea.Tag = "detection.previewParameters";
            maximumArea.Tag = "detection.previewParameters";
            rectangularity.Tag = "detection.previewParameters";
            morphologyRadius.Tag = "detection.previewParameters";
            toolTip.SetToolTip(nudExpectedCount, "启用模板匹配时用于核对单帧实际检出的子板数量");
            toolTip.SetToolTip(brightThreshold, "手动固定二值化阈值 (0-255)；勾选「ROI 自动阈值」后自动接管");
            toolTip.SetToolTip(rectangularity, "矩形度上限 (0.00-1.00)，大于等于该值的规整矩形将被判定为标签干扰并过滤");
            toolTip.SetToolTip(morphologyRadius, "形态学开运算去噪半径（像素），设为 0 表示关闭去噪");

            AddPairedField(settingFields, 0, 0, "检测极性", cmbDetectionPolarity);
            AddPairedField(settingFields, 0, 2, "空洞填充", cmbHoleFillMode);
            AddPairedField(settingFields, 1, 0, "生产目标板数", nudExpectedCount);
            AddPairedField(settingFields, 1, 2, "手动阈值", brightThreshold);
            AddPairedField(settingFields, 2, 0, "最小面积(px²)", minimumArea);
            AddPairedField(settingFields, 2, 2, "最大面积(px²)", maximumArea);
            AddPairedField(settingFields, 3, 0, "矩形度阈值", rectangularity);
            chkRectangleFilter = CreateCheckBox("chkRectangleFilter", "启用矩形标签过滤", true, "detection.previewParameters");
            settingFields.Controls.Add(chkRectangleFilter, 2, 3);
            settingFields.SetColumnSpan(chkRectangleFilter, 2);
            AddPairedField(settingFields, 4, 0, "噪点半径(px)", morphologyRadius);
            CheckBox autoThreshold = CreateCheckBox("chkAutoThreshold", "ROI 自动阈值 (Otsu)", true, "detection.previewParameters");
            settingFields.Controls.Add(autoThreshold, 2, 4);
            settingFields.SetColumnSpan(autoThreshold, 2);
            chkTemplateMatchingEnabled = CreateCheckBox("chkTemplateMatchingEnabled", "启用模板匹配（生产检测）", true, "detection.previewParameters");
            settingFields.Controls.Add(chkTemplateMatchingEnabled, 0, 5);
            settingFields.SetColumnSpan(chkTemplateMatchingEnabled, 4);
            CheckBox affineWithTemplate = CreateCheckBox("chkRoiAffineWithTemplate", "使用模板中的检测 ROI（随板旋转）", true, "detection.previewParameters");
            settingFields.Controls.Add(affineWithTemplate, 0, 6);
            settingFields.SetColumnSpan(affineWithTemplate, 4);
            chkBinaryPreview = CreateCheckBox("chkBinaryPreview", "检测效果图显示局部二值分割", true, "detection.binaryPreview");
            settingFields.Controls.Add(chkBinaryPreview, 0, 7);
            settingFields.SetColumnSpan(chkBinaryPreview, 4);
            Panel settings = CreateCard("板件与标记检测参数", settingFields);

            FlowLayoutPanel actions = new FlowLayoutPanel();
            actions.Dock = DockStyle.Fill;
            actions.Margin = Padding.Empty;
            actions.Padding = new Padding(0, 5, 0, 3);
            Label currentRLabel = new Label();
            currentRLabel.AutoSize = false;
            currentRLabel.ForeColor = TextSecondaryColor;
            currentRLabel.Margin = new Padding(0, 0, 2, 0);
            currentRLabel.Size = new Size(34, 32);
            currentRLabel.Text = "R1(°)";
            currentRLabel.TextAlign = ContentAlignment.MiddleLeft;
            NumericUpDown currentR = CreateDecimalInput("nudInspectionCurrentR", 0.0M, -3600.0M, 3600.0M, 3, 1.0M, false);
            currentR.Margin = new Padding(0, 4, 6, 0);
            currentR.Size = new Size(64, 27);
            toolTip.SetToolTip(currentR, "手动测试检测时使用的左吸嘴 R1 当前角度（度，3 位小数）");
            Label currentRRightLabel = new Label();
            currentRRightLabel.AutoSize = false;
            currentRRightLabel.ForeColor = TextSecondaryColor;
            currentRRightLabel.Margin = new Padding(0, 0, 2, 0);
            currentRRightLabel.Size = new Size(34, 32);
            currentRRightLabel.Text = "R2(°)";
            currentRRightLabel.TextAlign = ContentAlignment.MiddleLeft;
            NumericUpDown currentRRight = CreateDecimalInput("nudInspectionCurrentRRight", 0.0M, -3600.0M, 3600.0M, 3, 1.0M, false);
            currentRRight.Margin = new Padding(0, 4, 8, 0);
            currentRRight.Size = new Size(64, 27);
            toolTip.SetToolTip(currentRRight, "手动测试检测时使用的右吸嘴 R2 当前角度（度，3 位小数）");
            actions.Controls.Add(currentRLabel);
            actions.Controls.Add(currentR);
            actions.Controls.Add(currentRRightLabel);
            actions.Controls.Add(currentRRight);
            actions.Controls.Add(CreateButton("btnTestDetection", "测试检测", 78, 34, true, "detection.test"));
            actions.Controls.Add(CreateButton("btnDebugDetection", "Blob 调试", 82, 34, false, "detection.debug"));
            actions.Controls.Add(CreateButton("btnSaveDetection", "保存参数", 76, 34, false, "detection.save"));

            lblDetectionSummary = new Label();
            lblDetectionSummary.AutoEllipsis = true;
            lblDetectionSummary.Dock = DockStyle.Fill;
            lblDetectionSummary.Font = new Font("Microsoft YaHei UI", 9.0F, FontStyle.Bold, GraphicsUnit.Point, 134);
            lblDetectionSummary.ForeColor = TextSecondaryColor;
            lblDetectionSummary.Text = "目标 --  /  实际 --  /  等待检测";
            lblDetectionSummary.TextAlign = ContentAlignment.MiddleLeft;

            dgvDetectionResults = new DataGridView();
            ConfigureGrid(dgvDetectionResults, "dgvDetectionResults");
            AddTextColumn(dgvDetectionResults, "Index", "板号", 42);
            AddTextColumn(dgvDetectionResults, "Score", "分数", 52);
            AddTextColumn(dgvDetectionResults, "ImageX", "图像 X", 64);
            AddTextColumn(dgvDetectionResults, "ImageY", "图像 Y", 64);
            AddTextColumn(dgvDetectionResults, "Angle", "板角°", 54);
            AddTextColumn(dgvDetectionResults, "LeftTarget", "左吸嘴 R1 (X,Y,R)", 142);
            AddTextColumn(dgvDetectionResults, "RightTarget", "右吸嘴 R2 (X,Y,R)", 142);
            AddTextColumn(dgvDetectionResults, "Mark", "标记", 46);
            AddTextColumn(dgvDetectionResults, "Status", "状态", 48);

            layout.Controls.Add(settings, 0, 0);
            layout.Controls.Add(actions, 0, 1);
            layout.Controls.Add(lblDetectionSummary, 0, 2);
            layout.Controls.Add(WrapGridCard(dgvDetectionResults), 0, 3);
            page.Controls.Add(layout);
            return page;
        }

        // 构建重新设计的现代工业相机控制台：状态横幅卡 + 独立功能开关 + 真实设备连接 + 采集与核心参数
        private TabPage BuildCameraTab()
        {
            TabPage page = CreateTabPage("tabCamera", "相机");
            TableLayoutPanel layout = CreateScrollableStack(page);
            layout.RowCount = 4;
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 84.0F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 134.0F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 156.0F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 232.0F));

            // 1. 顶部相机连接状态横幅卡（带彩色左侧指示条与实时采集遥测徽章）
            pnlCameraHeroCard = new Panel();
            pnlCameraHeroCard.BackColor = CardBackgroundColor;
            pnlCameraHeroCard.Dock = DockStyle.Fill;
            pnlCameraHeroCard.Margin = new Padding(0, 0, 0, 10);
            pnlCameraHeroCard.Name = "pnlCameraHeroCard";
            pnlCameraHeroCard.Padding = new Padding(16, 10, 12, 10);
            pnlCameraHeroCard.Paint += PnlCameraHeroCard_Paint;

            TableLayoutPanel heroGrid = new TableLayoutPanel();
            heroGrid.BackColor = CardBackgroundColor;
            heroGrid.ColumnCount = 2;
            heroGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 60.0F));
            heroGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40.0F));
            heroGrid.Dock = DockStyle.Fill;
            heroGrid.Margin = Padding.Empty;
            heroGrid.RowCount = 2;
            heroGrid.RowStyles.Add(new RowStyle(SizeType.Absolute, 28.0F));
            heroGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 100.0F));

            lblCameraHeroState = new Label();
            lblCameraHeroState.AutoEllipsis = true;
            lblCameraHeroState.Dock = DockStyle.Fill;
            lblCameraHeroState.Font = new Font("Microsoft YaHei UI", 10.5F, FontStyle.Bold, GraphicsUnit.Point, 134);
            lblCameraHeroState.ForeColor = TextSecondaryColor;
            lblCameraHeroState.Name = "lblCameraHeroState";
            lblCameraHeroState.Text = "○ 未连接";
            lblCameraHeroState.TextAlign = ContentAlignment.MiddleLeft;

            lblCameraHeroDetail = new Label();
            lblCameraHeroDetail.AutoEllipsis = true;
            lblCameraHeroDetail.Dock = DockStyle.Fill;
            lblCameraHeroDetail.Font = new Font("Microsoft YaHei UI", 8.25F, FontStyle.Regular, GraphicsUnit.Point, 134);
            lblCameraHeroDetail.ForeColor = TextMutedColor;
            lblCameraHeroDetail.Name = "lblCameraHeroDetail";
            lblCameraHeroDetail.Text = "大华 (CameraDH) · 未检测到在线相机";
            lblCameraHeroDetail.TextAlign = ContentAlignment.MiddleLeft;

            lblCameraLiveStatus = new Label();
            lblCameraLiveStatus.AutoEllipsis = true;
            lblCameraLiveStatus.BackColor = Color.FromArgb(241, 245, 249);
            lblCameraLiveStatus.Dock = DockStyle.Fill;
            lblCameraLiveStatus.Font = new Font("Microsoft YaHei UI", 8.0F, FontStyle.Bold, GraphicsUnit.Point, 134);
            lblCameraLiveStatus.ForeColor = TextMutedColor;
            lblCameraLiveStatus.Margin = new Padding(8, 2, 0, 2);
            lblCameraLiveStatus.Name = "lblCameraLiveStatus";
            lblCameraLiveStatus.Padding = new Padding(8, 4, 8, 4);
            lblCameraLiveStatus.Text = "未连接\r\n未采集";
            lblCameraLiveStatus.TextAlign = ContentAlignment.MiddleCenter;

            heroGrid.Controls.Add(lblCameraHeroState, 0, 0);
            heroGrid.Controls.Add(lblCameraHeroDetail, 0, 1);
            heroGrid.Controls.Add(lblCameraLiveStatus, 1, 0);
            heroGrid.SetRowSpan(lblCameraLiveStatus, 2);
            pnlCameraHeroCard.Controls.Add(heroGrid);

            // 2. 独立功能开关卡（相机连接 / 标定 / 检测 相互独立，外加预览与归档选项）
            TableLayoutPanel switchesRoot = new TableLayoutPanel();
            switchesRoot.BackColor = CardBackgroundColor;
            switchesRoot.ColumnCount = 1;
            switchesRoot.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100.0F));
            switchesRoot.Dock = DockStyle.Fill;
            switchesRoot.Margin = Padding.Empty;
            switchesRoot.RowCount = 2;
            switchesRoot.RowStyles.Add(new RowStyle(SizeType.Absolute, 42.0F));
            switchesRoot.RowStyles.Add(new RowStyle(SizeType.Percent, 100.0F));

            Panel coreSwitchesStrip = new Panel();
            coreSwitchesStrip.BackColor = PageBackgroundColor;
            coreSwitchesStrip.Dock = DockStyle.Fill;
            coreSwitchesStrip.Margin = new Padding(0, 0, 0, 6);
            coreSwitchesStrip.Padding = new Padding(10, 4, 10, 4);
            coreSwitchesStrip.Paint += DrawCardBorder;

            TableLayoutPanel coreSwitchGrid = new TableLayoutPanel();
            coreSwitchGrid.BackColor = PageBackgroundColor;
            coreSwitchGrid.ColumnCount = 3;
            coreSwitchGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.333F));
            coreSwitchGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.333F));
            coreSwitchGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.334F));
            coreSwitchGrid.Dock = DockStyle.Fill;
            coreSwitchGrid.Margin = Padding.Empty;
            coreSwitchGrid.RowCount = 1;
            coreSwitchGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 100.0F));

            chkCameraEnabled = CreateCheckBox("chkCameraEnabled", "启用相机", true, null);
            chkCameraEnabled.BackColor = PageBackgroundColor;
            chkCameraEnabled.Font = new Font("Microsoft YaHei UI", 9.0F, FontStyle.Bold, GraphicsUnit.Point, 134);
            chkCameraEnabled.UseVisualStyleBackColor = false;
            toolTip.SetToolTip(chkCameraEnabled, "独立控制相机硬件连接与采集；关闭后仍可使用本地导入图像执行检测与标定计算");

            chkCalibrationEnabled = CreateCheckBox("chkCalibrationEnabled", "启用标定", true, null);
            chkCalibrationEnabled.BackColor = PageBackgroundColor;
            chkCalibrationEnabled.Font = new Font("Microsoft YaHei UI", 9.0F, FontStyle.Bold, GraphicsUnit.Point, 134);
            chkCalibrationEnabled.UseVisualStyleBackColor = false;
            toolTip.SetToolTip(chkCalibrationEnabled, "独立控制 9+5 标定、左右示教与坐标映射；关闭时仍可正常单独执行视觉检测");

            chkInspectionEnabled = CreateCheckBox("chkInspectionEnabled", "启用检测", true, null);
            chkInspectionEnabled.BackColor = PageBackgroundColor;
            chkInspectionEnabled.Font = new Font("Microsoft YaHei UI", 9.0F, FontStyle.Bold, GraphicsUnit.Point, 134);
            chkInspectionEnabled.UseVisualStyleBackColor = false;
            toolTip.SetToolTip(chkInspectionEnabled, "独立控制板件定位与瑕疵检测（测试检测、检测当前图及 TCP 生产触发），不依赖标定");

            coreSwitchGrid.Controls.Add(chkCameraEnabled, 0, 0);
            coreSwitchGrid.Controls.Add(chkCalibrationEnabled, 1, 0);
            coreSwitchGrid.Controls.Add(chkInspectionEnabled, 2, 0);
            coreSwitchesStrip.Controls.Add(coreSwitchGrid);

            TableLayoutPanel auxSwitchGrid = new TableLayoutPanel();
            auxSwitchGrid.BackColor = CardBackgroundColor;
            auxSwitchGrid.ColumnCount = 2;
            auxSwitchGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 54.0F));
            auxSwitchGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 46.0F));
            auxSwitchGrid.Dock = DockStyle.Fill;
            auxSwitchGrid.Margin = Padding.Empty;
            auxSwitchGrid.Padding = new Padding(10, 2, 10, 0);
            auxSwitchGrid.RowCount = 1;
            auxSwitchGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 100.0F));

            chkLiveAcquisition = CreateCheckBox("chkLiveAcquisition", "连接后自动实时预览", true, "camera.liveToggle");
            toolTip.SetToolTip(chkLiveAcquisition, "开启后连接大华/海康相机时自动开启主视口实时预览，生产抓拍时自动无冲突让出相机");

            chkArchiveImages = CreateCheckBox("chkArchiveImages", "保存生产原图", false, "camera.archiveImages");
            toolTip.SetToolTip(chkArchiveImages, "开启后自动保存每次生产检测原图到归档目录（默认关闭以节省磁盘空间）");

            auxSwitchGrid.Controls.Add(chkLiveAcquisition, 0, 0);
            auxSwitchGrid.Controls.Add(chkArchiveImages, 1, 0);

            switchesRoot.Controls.Add(coreSwitchesStrip, 0, 0);
            switchesRoot.Controls.Add(auxSwitchGrid, 0, 1);
            Panel mode = CreateCard("独立功能开关", switchesRoot);

            // 3. 真实工业相机连接卡（仅支持大华/海康真实硬件，等宽三键操作栏）
            TableLayoutPanel connectionFields = CreateFieldTable(2);
            connectionFields.AutoScroll = false;
            connectionFields.ColumnStyles[0].Width = 78.0F;
            cmbCameraProvider = CreateComboBox("cmbCameraProvider");
            cmbCameraProvider.Items.AddRange(new object[] { "大华 (CameraDH)", "海康 (MVS)" });
            cmbCameraProvider.SelectedIndex = 0;
            cmbCameraProvider.Tag = "camera.providerChanged";
            toolTip.SetToolTip(cmbCameraProvider, "切换真实相机驱动（大华 / 海康）后即时生效");
            cmbCameraDevice = CreateComboBox("cmbCameraDevice");
            toolTip.SetToolTip(cmbCameraDevice, "点击「枚举设备」刷新当前在线的真实工业相机列表");
            AddField(connectionFields, 0, "相机驱动", cmbCameraProvider);
            AddField(connectionFields, 1, "在线设备", cmbCameraDevice);
            connectionFields.RowCount = 3;
            connectionFields.RowStyles.Add(new RowStyle(SizeType.Percent, 100.0F));

            TableLayoutPanel connectButtons = new TableLayoutPanel();
            connectButtons.BackColor = CardBackgroundColor;
            connectButtons.ColumnCount = 3;
            connectButtons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.333F));
            connectButtons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.333F));
            connectButtons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.334F));
            connectButtons.Dock = DockStyle.Fill;
            connectButtons.Margin = new Padding(0, 6, 0, 0);
            connectButtons.RowCount = 1;
            connectButtons.RowStyles.Add(new RowStyle(SizeType.Percent, 100.0F));

            Button btnEnumCamera = CreateButton("btnEnumerateCamera", "枚举设备", 90, 32, false, "camera.enumerate");
            btnEnumCamera.Dock = DockStyle.Fill;
            btnEnumCamera.Margin = new Padding(0, 0, 5, 0);
            Button btnConnCamera = CreateButton("btnConnectCamera", "连接相机", 90, 32, true, "camera.connect");
            btnConnCamera.Dock = DockStyle.Fill;
            btnConnCamera.Margin = new Padding(3, 0, 3, 0);
            Button btnDiscCamera = CreateButton("btnDisconnectCamera", "断开连接", 90, 32, false, "camera.disconnect");
            btnDiscCamera.Dock = DockStyle.Fill;
            btnDiscCamera.Margin = new Padding(5, 0, 0, 0);
            connectButtons.Controls.Add(btnEnumCamera, 0, 0);
            connectButtons.Controls.Add(btnConnCamera, 1, 0);
            connectButtons.Controls.Add(btnDiscCamera, 2, 0);
            connectionFields.Controls.Add(connectButtons, 0, 2);
            connectionFields.SetColumnSpan(connectButtons, 2);
            Panel connection = CreateCard("设备连接", connectionFields);

            // 4. 采集控制与核心参数卡（实时/拍照三键 + 曝光内联控制 + 三格遥测磁贴）
            TableLayoutPanel acquisitionLayout = new TableLayoutPanel();
            acquisitionLayout.BackColor = CardBackgroundColor;
            acquisitionLayout.ColumnCount = 1;
            acquisitionLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100.0F));
            acquisitionLayout.Dock = DockStyle.Fill;
            acquisitionLayout.Margin = Padding.Empty;
            acquisitionLayout.RowCount = 4;
            acquisitionLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36.0F));
            acquisitionLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 14.0F));
            acquisitionLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36.0F));
            acquisitionLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100.0F));

            TableLayoutPanel captureButtons = new TableLayoutPanel();
            captureButtons.BackColor = CardBackgroundColor;
            captureButtons.ColumnCount = 3;
            captureButtons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.333F));
            captureButtons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.333F));
            captureButtons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.334F));
            captureButtons.Dock = DockStyle.Fill;
            captureButtons.Margin = Padding.Empty;
            captureButtons.RowCount = 1;
            captureButtons.RowStyles.Add(new RowStyle(SizeType.Percent, 100.0F));

            btnToggleLiveStream = CreateButton("btnToggleLiveStream", "开始实时", 90, 32, true, "camera.liveStreamToggle");
            btnToggleLiveStream.Dock = DockStyle.Fill;
            btnToggleLiveStream.Margin = new Padding(0, 0, 5, 0);
            btnSyncLiveToMain = CreateButton("btnSyncLiveToMain", "主窗实时", 90, 32, false, "camera.resumeMainLive");
            btnSyncLiveToMain.Dock = DockStyle.Fill;
            btnSyncLiveToMain.Margin = new Padding(3, 0, 3, 0);
            Button btnTestCam = CreateButton("btnTestCamera", "单次拍照", 90, 32, false, "camera.test");
            btnTestCam.Dock = DockStyle.Fill;
            btnTestCam.Margin = new Padding(5, 0, 0, 0);
            toolTip.SetToolTip(btnToggleLiveStream, "启动或停止已连接相机的实时采集流（显示于左侧主图像视口）");
            toolTip.SetToolTip(btnSyncLiveToMain, "解除主视口画面锁定，切回相机实时预览流");
            toolTip.SetToolTip(btnTestCam, "触发已连接相机采集一帧真实图像并显示到主视口");
            captureButtons.Controls.Add(btnToggleLiveStream, 0, 0);
            captureButtons.Controls.Add(btnSyncLiveToMain, 1, 0);
            captureButtons.Controls.Add(btnTestCam, 2, 0);

            Panel divider = new Panel();
            divider.BackColor = CardBorderColor;
            divider.Dock = DockStyle.Top;
            divider.Height = 1;
            divider.Margin = new Padding(0, 7, 0, 6);

            TableLayoutPanel exposureBar = new TableLayoutPanel();
            exposureBar.BackColor = CardBackgroundColor;
            exposureBar.ColumnCount = 4;
            exposureBar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 78.0F));
            exposureBar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100.0F));
            exposureBar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 86.0F));
            exposureBar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 64.0F));
            exposureBar.Dock = DockStyle.Fill;
            exposureBar.Margin = Padding.Empty;
            exposureBar.RowCount = 1;
            exposureBar.RowStyles.Add(new RowStyle(SizeType.Percent, 100.0F));

            Label lblExposureTitle = new Label();
            lblExposureTitle.Dock = DockStyle.Fill;
            lblExposureTitle.ForeColor = TextSecondaryColor;
            lblExposureTitle.Text = "曝光 (μs)";
            lblExposureTitle.TextAlign = ContentAlignment.MiddleLeft;

            NumericUpDown cameraExposure = CreateDecimalInput("nudCameraExposure", 10000M, 10M, 10000000M, 1, 100M, false);
            cameraExposure.Dock = DockStyle.Fill;
            cameraExposure.Margin = new Padding(0, 4, 8, 4);
            toolTip.SetToolTip(cameraExposure, "相机曝光时间（微秒 μs），范围 10 ~ 10,000,000，每次调节步进 100 μs");

            Button btnApplyExp = CreateButton("btnApplyCamera", "应用曝光", 80, 30, true, "camera.apply");
            btnApplyExp.Dock = DockStyle.Fill;
            btnApplyExp.Margin = new Padding(0, 2, 6, 2);
            Button btnReadParam = CreateButton("btnReadCamera", "读取", 58, 30, false, "camera.read");
            btnReadParam.Dock = DockStyle.Fill;
            btnReadParam.Margin = new Padding(0, 2, 0, 2);
            toolTip.SetToolTip(btnApplyExp, "将当前曝光时间立即下发至已连接相机");
            toolTip.SetToolTip(btnReadParam, "从已连接相机读取当前曝光、增益和触发模式");

            exposureBar.Controls.Add(lblExposureTitle, 0, 0);
            exposureBar.Controls.Add(cameraExposure, 1, 0);
            exposureBar.Controls.Add(btnApplyExp, 2, 0);
            exposureBar.Controls.Add(btnReadParam, 3, 0);

            TableLayoutPanel metricGrid = new TableLayoutPanel();
            metricGrid.BackColor = CardBackgroundColor;
            metricGrid.ColumnCount = 3;
            metricGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.333F));
            metricGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.333F));
            metricGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.334F));
            metricGrid.Dock = DockStyle.Fill;
            metricGrid.Margin = new Padding(0, 10, 0, 0);
            metricGrid.RowCount = 1;
            metricGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 100.0F));

            Label gainValue;
            Label triggerValue;
            Panel gainTile = CreateCameraMetricTile("当前增益", "lblCameraGain", "--", new Padding(0, 0, 5, 0), out gainValue);
            Panel triggerTile = CreateCameraMetricTile("触发模式", "lblCameraTrigger", "--", new Padding(3, 0, 3, 0), out triggerValue);
            Panel resolutionTile = CreateCameraMetricTile("画面分辨率", "lblCameraResolution", "--", new Padding(5, 0, 0, 0), out lblCameraResolution);
            metricGrid.Controls.Add(gainTile, 0, 0);
            metricGrid.Controls.Add(triggerTile, 1, 0);
            metricGrid.Controls.Add(resolutionTile, 2, 0);

            acquisitionLayout.Controls.Add(captureButtons, 0, 0);
            acquisitionLayout.Controls.Add(divider, 0, 1);
            acquisitionLayout.Controls.Add(exposureBar, 0, 2);
            acquisitionLayout.Controls.Add(metricGrid, 0, 3);
            Panel acquisition = CreateCard("采集控制与参数", acquisitionLayout);

            layout.Controls.Add(pnlCameraHeroCard, 0, 0);
            layout.Controls.Add(mode, 0, 1);
            layout.Controls.Add(connection, 0, 2);
            layout.Controls.Add(acquisition, 0, 3);
            return page;
        }

        private static Panel CreateCameraMetricTile(
            string caption,
            string valueName,
            string defaultValue,
            Padding margin,
            out Label valueLabel)
        {
            Panel tile = new Panel();
            tile.BackColor = PageBackgroundColor;
            tile.Dock = DockStyle.Fill;
            tile.Margin = margin;
            tile.Padding = new Padding(10, 8, 10, 8);
            tile.Paint += DrawCardBorder;

            TableLayoutPanel inner = new TableLayoutPanel();
            inner.BackColor = PageBackgroundColor;
            inner.ColumnCount = 1;
            inner.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100.0F));
            inner.Dock = DockStyle.Fill;
            inner.Margin = Padding.Empty;
            inner.RowCount = 2;
            inner.RowStyles.Add(new RowStyle(SizeType.Absolute, 20.0F));
            inner.RowStyles.Add(new RowStyle(SizeType.Percent, 100.0F));

            Label lblCaption = new Label();
            lblCaption.AutoEllipsis = true;
            lblCaption.Dock = DockStyle.Fill;
            lblCaption.Font = new Font("Microsoft YaHei UI", 8.0F, FontStyle.Regular, GraphicsUnit.Point, 134);
            lblCaption.ForeColor = TextMutedColor;
            lblCaption.Text = caption;
            lblCaption.TextAlign = ContentAlignment.MiddleLeft;

            valueLabel = new Label();
            valueLabel.AutoEllipsis = true;
            valueLabel.Dock = DockStyle.Fill;
            valueLabel.Font = new Font("Microsoft YaHei UI", 9.5F, FontStyle.Bold, GraphicsUnit.Point, 134);
            valueLabel.ForeColor = TextPrimaryColor;
            valueLabel.Name = valueName;
            valueLabel.Text = defaultValue;
            valueLabel.TextAlign = ContentAlignment.MiddleLeft;

            inner.Controls.Add(lblCaption, 0, 0);
            inner.Controls.Add(valueLabel, 0, 1);
            tile.Controls.Add(inner);
            return tile;
        }

        // 构建 TCP 通信监听与配置/数据文件路径页
        private TabPage BuildCommunicationTab()
        {
            TabPage page = CreateTabPage("tabCommunication", "通信 / 文件");
            TableLayoutPanel layout = CreateScrollableStack(page);
            layout.RowCount = 3;
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 162.0F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 358.0F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48.0F));

            TableLayoutPanel tcpFields = CreateFieldTable(2);
            tcpFields.AutoScroll = false;
            txtTcpBindAddress = CreateTextBox("txtTcpBindAddress", "0.0.0.0");
            nudTcpPort = CreateDecimalInput("nudTcpPort", 9100.0M, 1.0M, 65535.0M, 0, 1.0M, false);
            toolTip.SetToolTip(txtTcpBindAddress, "监听网卡 IP（0.0.0.0 表示监听本机所有网卡）");
            toolTip.SetToolTip(nudTcpPort, "TCP 服务监听端口 (1-65535)，修改后点击「启动监听」立即生效");
            AddField(tcpFields, 0, "监听地址", txtTcpBindAddress);
            AddField(tcpFields, 1, "监听端口", nudTcpPort);
            tcpFields.RowCount = 3;
            tcpFields.RowStyles.Add(new RowStyle(SizeType.Absolute, 38.0F));
            FlowLayoutPanel tcpCommands = new FlowLayoutPanel();
            tcpCommands.AutoSize = false;
            tcpCommands.Dock = DockStyle.Fill;
            tcpCommands.Margin = Padding.Empty;
            tcpCommands.Padding = new Padding(0, 4, 0, 0);
            Button startTcpButton = CreateButton("btnStartTcp", "启动监听", 88, 30, true, "tcp.start");
            toolTip.SetToolTip(startTcpButton, "按当前监听地址与端口启动或热重启 TCP 监听");
            tcpCommands.Controls.Add(startTcpButton);
            tcpCommands.Controls.Add(CreateButton("btnStopTcp", "停止监听", 88, 30, false, "tcp.stop"));
            tcpFields.Controls.Add(tcpCommands, 0, 2);
            tcpFields.SetColumnSpan(tcpCommands, 2);
            Panel tcp = CreateCard("TCP 服务端", tcpFields);

            TableLayoutPanel fileFields = new TableLayoutPanel();
            fileFields.AutoScroll = false;
            fileFields.ColumnCount = 3;
            fileFields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92.0F));
            fileFields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100.0F));
            fileFields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92.0F));
            fileFields.Dock = DockStyle.Fill;
            fileFields.Margin = Padding.Empty;
            fileFields.Padding = Padding.Empty;
            fileFields.RowCount = 7;
            fileFields.RowStyles.Add(new RowStyle(SizeType.Absolute, 42.0F));
            fileFields.RowStyles.Add(new RowStyle(SizeType.Absolute, 42.0F));
            fileFields.RowStyles.Add(new RowStyle(SizeType.Absolute, 42.0F));
            fileFields.RowStyles.Add(new RowStyle(SizeType.Absolute, 42.0F));
            fileFields.RowStyles.Add(new RowStyle(SizeType.Absolute, 42.0F));
            fileFields.RowStyles.Add(new RowStyle(SizeType.Absolute, 42.0F));
            fileFields.RowStyles.Add(new RowStyle(SizeType.Absolute, 42.0F));
            AddPathField(fileFields, 0, "配置文件夹", "txtConfigDirectory", "files.folder.import", "导入文件夹");
            AddPathField(fileFields, 1, "左标定(R1)", "txtCalibrationFile", "files.calibration.browse");
            AddPathField(fileFields, 2, "右标定(R2)", "txtRightCalibrationFile", "files.calibrationRight.browse");
            AddPathField(fileFields, 3, "定位文件", "txtPositioningFile", "files.positioning.browse");
            AddPathField(fileFields, 4, "示教文件", "txtTeachingFile", "files.teaching.browse");
            AddPathField(fileFields, 5, "检测配置", "txtDetectionProfile", "files.detection.browse");
            AddPathField(fileFields, 6, "图像归档", "txtImageArchive", "files.archive.browse");
            Panel files = CreateCard("配置与数据文件（统一配置目录）", fileFields);

            FlowLayoutPanel savePanel = new FlowLayoutPanel();
            savePanel.Dock = DockStyle.Fill;
            savePanel.Margin = Padding.Empty;
            savePanel.Padding = new Padding(0, 6, 0, 0);
            savePanel.Controls.Add(CreateButton("btnSaveSettings", "保存全部设置", 116, 34, true, "settings.save"));

            layout.Controls.Add(tcp, 0, 0);
            layout.Controls.Add(files, 0, 1);
            layout.Controls.Add(savePanel, 0, 2);
            return page;
        }

        // 构建底部运行日志输出面板（支持一键折叠以释放双视口显示高度）
        private Control BuildLogPanel()
        {
            logLayout = new TableLayoutPanel();
            logLayout.BackColor = HeaderDarkColor;
            logLayout.ColumnCount = 1;
            logLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100.0F));
            logLayout.Dock = DockStyle.Fill;
            logLayout.Margin = Padding.Empty;
            logLayout.Padding = new Padding(12, 3, 12, 4);
            logLayout.RowCount = 2;
            logLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 26.0F));
            logLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100.0F));

            TableLayoutPanel header = new TableLayoutPanel();
            header.ColumnCount = 3;
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100.0F));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 80.0F));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 68.0F));
            header.Dock = DockStyle.Fill;
            header.Margin = Padding.Empty;

            Label title = new Label();
            title.Dock = DockStyle.Fill;
            title.Font = new Font("Microsoft YaHei UI", 8.5F, FontStyle.Bold, GraphicsUnit.Point, 134);
            title.ForeColor = Color.FromArgb(203, 213, 225);
            title.Text = "运行日志";
            title.TextAlign = ContentAlignment.MiddleLeft;

            btnToggleLog = CreateHeaderButton("btnToggleLog", "▼ 收起", 74, 22, false, null);
            btnToggleLog.Dock = DockStyle.Fill;
            btnToggleLog.Font = new Font("Microsoft YaHei UI", 8.0F, FontStyle.Regular, GraphicsUnit.Point, 134);
            btnToggleLog.Margin = new Padding(0, 1, 6, 1);

            btnClearLog = CreateHeaderButton("btnClearLog", "清空", 62, 22, false, null);
            btnClearLog.Dock = DockStyle.Fill;
            btnClearLog.Font = new Font("Microsoft YaHei UI", 8.0F, FontStyle.Regular, GraphicsUnit.Point, 134);
            btnClearLog.Margin = new Padding(0, 1, 0, 1);
            header.Controls.Add(title, 0, 0);
            header.Controls.Add(btnToggleLog, 1, 0);
            header.Controls.Add(btnClearLog, 2, 0);

            rtbLog = new RichTextBox();
            rtbLog.BackColor = ViewportHeaderColor;
            rtbLog.BorderStyle = BorderStyle.None;
            rtbLog.DetectUrls = false;
            rtbLog.Dock = DockStyle.Fill;
            rtbLog.Font = new Font("Consolas", 8.75F, FontStyle.Regular);
            rtbLog.ForeColor = Color.FromArgb(203, 213, 225);
            rtbLog.Name = "rtbLog";
            rtbLog.ReadOnly = true;
            rtbLog.WordWrap = false;

            logLayout.Controls.Add(header, 0, 0);
            logLayout.Controls.Add(rtbLog, 0, 1);
            return logLayout;
        }

        private Panel BuildTeachingGroup(string title, string sideKey, out TextBox[] values, out Label statusLabel)
        {
            TableLayoutPanel fields = new TableLayoutPanel();
            fields.ColumnCount = 4;
            fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 84.0F));
            fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50.0F));
            fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 78.0F));
            fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50.0F));
            fields.Dock = DockStyle.Fill;
            fields.Margin = Padding.Empty;
            fields.Padding = Padding.Empty;
            fields.RowCount = 5;
            fields.RowStyles.Add(new RowStyle(SizeType.Absolute, 34.0F));
            fields.RowStyles.Add(new RowStyle(SizeType.Absolute, 34.0F));
            fields.RowStyles.Add(new RowStyle(SizeType.Absolute, 34.0F));
            fields.RowStyles.Add(new RowStyle(SizeType.Absolute, 26.0F));
            fields.RowStyles.Add(new RowStyle(SizeType.Absolute, 40.0F));

            TextBox imageRow = CreateReadOnlyValue(sideKey + "ImageRow");
            TextBox imageColumn = CreateReadOnlyValue(sideKey + "ImageColumn");
            TextBox imageAngle = CreateReadOnlyValue(sideKey + "ImageAngle");
            TextBox machineX = CreateMachinePoseInput(sideKey + "MachineX");
            TextBox machineY = CreateMachinePoseInput(sideKey + "MachineY");
            TextBox machineR = CreateMachinePoseInput(sideKey + "MachineR");
            values = new[] { imageRow, imageColumn, imageAngle, machineX, machineY, machineR };

            AddTeachingField(fields, 0, 0, "图像 Row(px)", imageRow);
            AddTeachingField(fields, 0, 2, "机械 X(mm)", machineX);
            AddTeachingField(fields, 1, 0, "图像 Col(px)", imageColumn);
            AddTeachingField(fields, 1, 2, "机械 Y(mm)", machineY);
            AddTeachingField(fields, 2, 0, "图像角(°)", imageAngle);
            AddTeachingField(fields, 2, 2, "机械 R(°)", machineR);

            statusLabel = new Label();
            statusLabel.AutoEllipsis = true;
            statusLabel.Dock = DockStyle.Fill;
            statusLabel.Font = new Font("Microsoft YaHei UI", 8.5F, FontStyle.Bold, GraphicsUnit.Point, 134);
            statusLabel.ForeColor = Color.FromArgb(185, 28, 28);
            statusLabel.Name = sideKey == "left" ? "lblLeftTeachingStatus" : "lblRightTeachingStatus";
            statusLabel.Text = "○ 未示教";
            statusLabel.TextAlign = ContentAlignment.MiddleLeft;
            fields.Controls.Add(statusLabel, 0, 3);
            fields.SetColumnSpan(statusLabel, 4);

            FlowLayoutPanel actions = new FlowLayoutPanel();
            actions.AutoSize = true;
            actions.Dock = DockStyle.Fill;
            actions.FlowDirection = FlowDirection.RightToLeft;
            actions.Margin = Padding.Empty;
            actions.Padding = new Padding(0, 4, 0, 0);
            actions.WrapContents = false;
            Button clearBtn = CreateButton("btnClear" + sideKey + "Teaching", "清除", 62, 30, false, "teaching." + sideKey + ".clear");
            Button locateBtn = CreateButton("btnLocate" + sideKey + "Teaching", "模板匹配定位", 108, 30, true, "teaching." + sideKey + ".locate");
            Button recordBtn = CreateButton("btnCapture" + sideKey + "Teaching", "记录轴坐标", 94, 30, false, "teaching." + sideKey + ".capture");
            toolTip.SetToolTip(recordBtn, "步骤 1：仅保存当前填写的机械 X/Y/R 轴坐标（不拍照，避免轴遮挡相机）");
            toolTip.SetToolTip(locateBtn, "步骤 2：轴移走后点击，触发拍照并用检测定位模板匹配当前基准板图像坐标");
            actions.Controls.Add(clearBtn);
            actions.Controls.Add(locateBtn);
            actions.Controls.Add(recordBtn);
            fields.Controls.Add(actions, 0, 4);
            fields.SetColumnSpan(actions, 4);
            return CreateCard(title, fields);
        }

        private Panel BuildTemplateGroup(string title, string pathName, string actionPrefix, string description)
        {
            TableLayoutPanel fields = new TableLayoutPanel();
            fields.ColumnCount = 1;
            fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100.0F));
            fields.Dock = DockStyle.Fill;
            fields.Margin = Padding.Empty;
            fields.Padding = Padding.Empty;
            fields.RowCount = 4;
            fields.RowStyles.Add(new RowStyle(SizeType.Absolute, 26.0F));
            fields.RowStyles.Add(new RowStyle(SizeType.Absolute, 38.0F));
            fields.RowStyles.Add(new RowStyle(SizeType.Absolute, 42.0F));
            fields.RowStyles.Add(new RowStyle(SizeType.Percent, 100.0F));

            Label descriptionLabel = CreateHintLabel(description);
            descriptionLabel.Padding = Padding.Empty;
            TextBox path = CreateTextBox(pathName, string.Empty);
            path.Dock = DockStyle.Fill;
            path.Margin = new Padding(0, 4, 0, 6);

            FlowLayoutPanel commands = new FlowLayoutPanel();
            commands.Dock = DockStyle.Fill;
            commands.Margin = Padding.Empty;
            commands.Padding = new Padding(0, 2, 0, 0);
            commands.WrapContents = false;
            commands.Controls.Add(CreateButton("btnCreate" + pathName, "创建 / 编辑", 94, 30, true, actionPrefix + ".create"));
            commands.Controls.Add(CreateButton("btnBrowse" + pathName, "浏览", 66, 30, false, actionPrefix + ".browse"));
            commands.Controls.Add(CreateButton("btnLoad" + pathName, "加载", 66, 30, false, actionPrefix + ".load"));
            commands.Controls.Add(CreateButton("btnTest" + pathName, "测试", 66, 30, false, actionPrefix + ".test"));

            Label status = new Label();
            status.AutoEllipsis = true;
            status.Dock = DockStyle.Fill;
            status.Font = new Font("Microsoft YaHei UI", 8.5F, FontStyle.Regular, GraphicsUnit.Point, 134);
            status.ForeColor = TextMutedColor;
            status.Name = "lblStatus" + pathName;
            status.Text = "未加载";
            status.TextAlign = ContentAlignment.MiddleLeft;

            fields.Controls.Add(descriptionLabel, 0, 0);
            fields.Controls.Add(path, 0, 1);
            fields.Controls.Add(commands, 0, 2);
            fields.Controls.Add(status, 0, 3);
            return CreateCard(title, fields);
        }

        // 通用极简白色卡片与现代控件构建辅助方法
        private static Panel CreateCard(string title, Control body)
        {
            Panel card = new Panel();
            card.BackColor = CardBackgroundColor;
            card.Dock = DockStyle.Fill;
            card.Margin = new Padding(0, 0, 0, 10);
            card.Padding = new Padding(12, 9, 12, 10);
            card.Paint += DrawCardBorder;

            TableLayoutPanel frame = new TableLayoutPanel();
            frame.BackColor = CardBackgroundColor;
            frame.ColumnCount = 1;
            frame.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100.0F));
            frame.Dock = DockStyle.Fill;
            frame.Margin = Padding.Empty;
            frame.RowCount = 2;
            frame.RowStyles.Add(new RowStyle(SizeType.Absolute, 26.0F));
            frame.RowStyles.Add(new RowStyle(SizeType.Percent, 100.0F));

            Label header = new Label();
            header.AutoEllipsis = true;
            header.Dock = DockStyle.Fill;
            header.Font = new Font("Microsoft YaHei UI", 9.0F, FontStyle.Bold, GraphicsUnit.Point, 134);
            header.ForeColor = TextPrimaryColor;
            header.Margin = new Padding(0, 0, 0, 4);
            header.Text = title;
            header.TextAlign = ContentAlignment.MiddleLeft;

            body.Dock = DockStyle.Fill;
            frame.Controls.Add(header, 0, 0);
            frame.Controls.Add(body, 0, 1);
            card.Controls.Add(frame);
            return card;
        }

        private static Panel WrapGridCard(DataGridView grid)
        {
            Panel card = new Panel();
            card.BackColor = CardBackgroundColor;
            card.Dock = DockStyle.Fill;
            card.Margin = Padding.Empty;
            card.Padding = new Padding(1);
            card.Paint += DrawCardBorder;
            card.Controls.Add(grid);
            return card;
        }

        private static void DrawCardBorder(object sender, PaintEventArgs e)
        {
            Control control = sender as Control;
            if (control == null || control.Width <= 1 || control.Height <= 1) return;
            using (Pen pen = new Pen(CardBorderColor, 1.0F))
            {
                e.Graphics.DrawRectangle(pen, 0, 0, control.Width - 1, control.Height - 1);
            }
        }

        private static TableLayoutPanel CreateScrollableStack(TabPage page)
        {
            page.AutoScroll = true;
            TableLayoutPanel layout = new TableLayoutPanel();
            layout.AutoSize = true;
            layout.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            layout.BackColor = PageBackgroundColor;
            layout.ColumnCount = 1;
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100.0F));
            layout.Dock = DockStyle.Top;
            layout.Padding = new Padding(10);
            page.Controls.Add(layout);
            return layout;
        }

        private static TabPage CreateTabPage(string name, string text)
        {
            TabPage page = new TabPage();
            page.BackColor = PageBackgroundColor;
            page.Name = name;
            page.Padding = Padding.Empty;
            page.Text = text;
            page.UseVisualStyleBackColor = false;
            return page;
        }

        private static TableLayoutPanel CreateFieldTable(int columns)
        {
            TableLayoutPanel fields = new TableLayoutPanel();
            fields.AutoScroll = true;
            fields.BackColor = CardBackgroundColor;
            fields.ColumnCount = columns;
            fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 104.0F));
            fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100.0F));
            fields.Dock = DockStyle.Fill;
            fields.Font = new Font("Microsoft YaHei UI", 9.0F, FontStyle.Regular, GraphicsUnit.Point, 134);
            fields.Margin = Padding.Empty;
            fields.Padding = Padding.Empty;
            return fields;
        }

        private static TableLayoutPanel CreatePairedFieldTable()
        {
            TableLayoutPanel fields = new TableLayoutPanel();
            fields.BackColor = CardBackgroundColor;
            fields.ColumnCount = 4;
            fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96.0F));
            fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50.0F));
            fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92.0F));
            fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50.0F));
            fields.Dock = DockStyle.Fill;
            fields.Font = new Font("Microsoft YaHei UI", 9.0F, FontStyle.Regular, GraphicsUnit.Point, 134);
            fields.Margin = Padding.Empty;
            fields.Padding = Padding.Empty;
            fields.RowCount = 3;
            fields.RowStyles.Add(new RowStyle(SizeType.Percent, 33.333F));
            fields.RowStyles.Add(new RowStyle(SizeType.Percent, 33.333F));
            fields.RowStyles.Add(new RowStyle(SizeType.Percent, 33.334F));
            return fields;
        }

        private static CheckBox CreateCheckBox(string name, string text, bool isChecked, string actionTag)
        {
            CheckBox checkBox = new CheckBox();
            checkBox.AutoSize = true;
            checkBox.Checked = isChecked;
            checkBox.Cursor = Cursors.Hand;
            checkBox.Dock = DockStyle.Fill;
            checkBox.ForeColor = TextPrimaryColor;
            checkBox.Name = name;
            checkBox.Tag = actionTag;
            checkBox.Text = text;
            checkBox.UseVisualStyleBackColor = true;
            return checkBox;
        }

        private static void AddField(TableLayoutPanel table, int row, string labelText, Control editor)
        {
            while (table.RowCount <= row)
            {
                table.RowCount++;
                table.RowStyles.Add(new RowStyle(SizeType.Absolute, 34.0F));
            }

            Label label = new Label();
            label.AutoEllipsis = true;
            label.Dock = DockStyle.Fill;
            label.ForeColor = TextSecondaryColor;
            label.Margin = new Padding(0, 2, 8, 2);
            label.Text = labelText;
            label.TextAlign = ContentAlignment.MiddleLeft;
            editor.Dock = DockStyle.Fill;
            editor.Margin = new Padding(0, 3, 0, 3);
            table.Controls.Add(label, 0, row);
            table.Controls.Add(editor, 1, row);
        }

        private static void AddPairedField(TableLayoutPanel table, int row, int column, string labelText, Control editor)
        {
            Label label = new Label();
            label.AutoEllipsis = true;
            label.Dock = DockStyle.Fill;
            label.ForeColor = TextSecondaryColor;
            label.Margin = new Padding(0, 2, 4, 2);
            label.Text = labelText;
            label.TextAlign = ContentAlignment.MiddleLeft;
            editor.Dock = DockStyle.Fill;
            editor.Margin = new Padding(0, 3, 10, 3);
            table.Controls.Add(label, column, row);
            table.Controls.Add(editor, column + 1, row);
        }

        private static void AddTeachingField(TableLayoutPanel table, int row, int column, string labelText, Control editor)
        {
            Label label = new Label();
            label.AutoEllipsis = true;
            label.Dock = DockStyle.Fill;
            label.ForeColor = TextSecondaryColor;
            label.Margin = new Padding(0, 2, 4, 2);
            label.Text = labelText;
            label.TextAlign = ContentAlignment.MiddleLeft;
            editor.Dock = DockStyle.Fill;
            editor.Margin = new Padding(0, 4, 10, 4);
            table.Controls.Add(label, column, row);
            table.Controls.Add(editor, column + 1, row);
        }

        private void AddPathField(
            TableLayoutPanel table,
            int row,
            string labelText,
            string textBoxName,
            string browseAction,
            string buttonText = "浏览")
        {
            Label label = new Label();
            label.AutoEllipsis = true;
            label.Dock = DockStyle.Fill;
            label.ForeColor = TextSecondaryColor;
            label.Text = labelText;
            label.TextAlign = ContentAlignment.MiddleLeft;
            TextBox path = CreateTextBox(textBoxName, string.Empty);
            path.Dock = DockStyle.Fill;
            path.Margin = new Padding(0, 7, 8, 7);
            Button browse = CreateButton("btnBrowse" + textBoxName, buttonText, 84, 28, false, browseAction);
            browse.Dock = DockStyle.Fill;
            browse.Margin = new Padding(0, 6, 0, 6);
            table.Controls.Add(label, 0, row);
            table.Controls.Add(path, 1, row);
            table.Controls.Add(browse, 2, row);
        }

        private static Label CreateStatusLabel(string name, string text, int width)
        {
            Label label = new Label();
            label.AutoEllipsis = true;
            label.BackColor = HeaderSurfaceColor;
            label.Font = new Font("Microsoft YaHei UI", 8.75F, FontStyle.Bold, GraphicsUnit.Point, 134);
            label.ForeColor = Color.FromArgb(226, 232, 240);
            label.Margin = new Padding(0, 1, 8, 1);
            label.Name = name;
            label.Padding = new Padding(10, 0, 10, 0);
            label.Size = new Size(width, 34);
            label.Text = text;
            label.TextAlign = ContentAlignment.MiddleLeft;
            return label;
        }

        // 深色顶栏与日志栏专用按钮（主按钮为钴蓝，次按钮为轻透暗灰描边）
        private static Button CreateHeaderButton(string name, string text, int width, int height, bool primary, string actionKey)
        {
            Button button = new Button();
            button.BackColor = primary ? AccentPrimaryColor : HeaderSurfaceColor;
            button.Cursor = Cursors.Hand;
            button.FlatAppearance.BorderColor = primary ? AccentPrimaryColor : Color.FromArgb(51, 65, 85);
            button.FlatAppearance.BorderSize = 1;
            button.FlatAppearance.MouseOverBackColor = primary ? AccentHoverColor : Color.FromArgb(51, 65, 85);
            button.FlatAppearance.MouseDownBackColor = primary ? AccentDownColor : Color.FromArgb(15, 23, 42);
            button.FlatStyle = FlatStyle.Flat;
            button.Font = new Font("Microsoft YaHei UI", 8.75F, primary ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Point, 134);
            button.ForeColor = primary ? Color.White : Color.FromArgb(226, 232, 240);
            button.Margin = new Padding(0, 0, 6, 0);
            button.Name = name;
            button.Size = new Size(width, height);
            button.Tag = actionKey;
            button.Text = text;
            button.UseVisualStyleBackColor = false;
            button.EnabledChanged += delegate
            {
                if (button.Enabled)
                {
                    button.BackColor = primary ? AccentPrimaryColor : HeaderSurfaceColor;
                    button.ForeColor = primary ? Color.White : Color.FromArgb(226, 232, 240);
                    button.FlatAppearance.BorderColor = primary ? AccentPrimaryColor : Color.FromArgb(51, 65, 85);
                    button.Cursor = Cursors.Hand;
                }
                else
                {
                    button.BackColor = Color.FromArgb(26, 36, 54);
                    button.ForeColor = Color.FromArgb(148, 163, 184);
                    button.FlatAppearance.BorderColor = Color.FromArgb(51, 65, 85);
                    button.Cursor = Cursors.Default;
                }
                button.Invalidate();
            };
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

        // 浅色工作区按钮（主操作钴蓝实心、常规操作极简白底细灰框、危险操作柔和红底红字）
        private static Button CreateButton(string name, string text, int width, int height, bool primary, string actionKey)
        {
            bool danger = !string.IsNullOrWhiteSpace(actionKey) &&
                (actionKey.IndexOf("cancel", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 actionKey.IndexOf("stop", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 actionKey.IndexOf("disconnect", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 actionKey.IndexOf("clear", StringComparison.OrdinalIgnoreCase) >= 0);

            Button button = new Button();
            button.BackColor = primary
                ? AccentPrimaryColor
                : danger ? Color.FromArgb(254, 242, 242) : Color.White;
            button.Cursor = Cursors.Hand;
            button.FlatAppearance.BorderColor = primary
                ? AccentPrimaryColor
                : danger ? Color.FromArgb(254, 202, 202) : Color.FromArgb(203, 213, 225);
            button.FlatAppearance.BorderSize = 1;
            button.FlatAppearance.MouseOverBackColor = primary
                ? AccentHoverColor
                : danger ? Color.FromArgb(254, 226, 226) : Color.FromArgb(241, 245, 249);
            button.FlatAppearance.MouseDownBackColor = primary
                ? AccentDownColor
                : danger ? Color.FromArgb(254, 202, 202) : Color.FromArgb(226, 232, 240);
            button.FlatStyle = FlatStyle.Flat;
            button.Font = new Font("Microsoft YaHei UI", 8.75F, primary ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Point, 134);
            button.ForeColor = primary
                ? Color.White
                : danger ? Color.FromArgb(185, 28, 28) : TextPrimaryColor;
            button.Margin = new Padding(0, 0, 7, 0);
            button.Name = name;
            button.Size = new Size(width, height);
            button.Tag = actionKey;
            button.Text = text;
            button.UseVisualStyleBackColor = false;
            button.EnabledChanged += delegate
            {
                if (button.Enabled)
                {
                    button.BackColor = primary
                        ? AccentPrimaryColor
                        : danger ? Color.FromArgb(254, 242, 242) : Color.White;
                    button.ForeColor = primary
                        ? Color.White
                        : danger ? Color.FromArgb(185, 28, 28) : TextPrimaryColor;
                    button.FlatAppearance.BorderColor = primary
                        ? AccentPrimaryColor
                        : danger ? Color.FromArgb(254, 202, 202) : Color.FromArgb(203, 213, 225);
                    button.Cursor = Cursors.Hand;
                }
                else
                {
                    button.BackColor = primary
                        ? Color.FromArgb(235, 240, 247)
                        : Color.FromArgb(248, 250, 252);
                    button.ForeColor = Color.FromArgb(148, 163, 184);
                    button.FlatAppearance.BorderColor = Color.FromArgb(203, 213, 225);
                    button.Cursor = Cursors.Default;
                }
                button.Invalidate();
            };
            button.Paint += delegate(object sender, PaintEventArgs e)
            {
                if (button.Enabled) return;
                Rectangle bounds = new Rectangle(0, 0, button.Width, button.Height);
                Color bgColor = primary ? Color.FromArgb(235, 240, 247) : Color.FromArgb(248, 250, 252);
                using (SolidBrush bg = new SolidBrush(bgColor))
                    e.Graphics.FillRectangle(bg, bounds);
                using (Pen border = new Pen(Color.FromArgb(203, 213, 225), 1.0F))
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

        private static Button CreatePointButton(int point)
        {
            bool isOrigin = point == 5 || point == 12;
            Button button = CreateButton("btnCalibrationPoint" + point, point.ToString(), 44, 34, false, null);
            button.Dock = DockStyle.Fill;
            button.BackColor = isOrigin ? Color.FromArgb(239, 246, 255) : Color.White;
            button.ForeColor = isOrigin ? AccentPrimaryColor : TextPrimaryColor;
            button.FlatAppearance.BorderColor = isOrigin ? Color.FromArgb(147, 197, 253) : Color.FromArgb(203, 213, 225);
            button.Font = new Font("Microsoft YaHei UI", point == 5 ? 9.5F : 8.75F, FontStyle.Bold, GraphicsUnit.Point, 134);
            button.Margin = new Padding(3);
            button.Tag = point;
            return button;
        }

        private static NumericUpDown CreateDecimalInput(
            string name,
            decimal value,
            decimal minimum,
            decimal maximum,
            int decimalPlaces,
            decimal? increment = null,
            bool thousandsSeparator = false)
        {
            NumericUpDown input = new NumericUpDown();
            input.BackColor = Color.White;
            input.BorderStyle = BorderStyle.FixedSingle;
            input.DecimalPlaces = decimalPlaces;
            input.ForeColor = TextPrimaryColor;
            input.Increment = increment.HasValue
                ? increment.Value
                : (decimalPlaces == 0 ? 1.0M : decimalPlaces == 2 ? 0.01M : 0.1M);
            input.Maximum = maximum;
            input.Minimum = minimum;
            input.Name = name;
            input.TextAlign = HorizontalAlignment.Right;
            input.ThousandsSeparator = thousandsSeparator;
            input.Value = Math.Max(minimum, Math.Min(maximum, value));
            return input;
        }

        private static TextBox CreateTextBox(string name, string value)
        {
            TextBox textBox = new TextBox();
            textBox.BackColor = Color.White;
            textBox.BorderStyle = BorderStyle.FixedSingle;
            textBox.ForeColor = TextPrimaryColor;
            textBox.Name = name;
            textBox.Text = value;
            return textBox;
        }

        private static TextBox CreateReadOnlyValue(string name)
        {
            TextBox textBox = CreateTextBox(name, string.Empty);
            textBox.BackColor = PageBackgroundColor;
            textBox.ForeColor = TextSecondaryColor;
            textBox.ReadOnly = true;
            textBox.TextAlign = HorizontalAlignment.Right;
            return textBox;
        }

        private static TextBox CreateMachinePoseInput(string name)
        {
            TextBox textBox = CreateTextBox(name, "0");
            textBox.TextAlign = HorizontalAlignment.Right;
            return textBox;
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

        private static Label CreateHintLabel(string text)
        {
            Label label = new Label();
            label.AutoEllipsis = true;
            label.Dock = DockStyle.Fill;
            label.Font = new Font("Microsoft YaHei UI", 8.25F, FontStyle.Regular, GraphicsUnit.Point, 134);
            label.ForeColor = TextMutedColor;
            label.Padding = new Padding(2, 4, 2, 2);
            label.Text = text;
            label.TextAlign = ContentAlignment.TopLeft;
            return label;
        }

        private static void ConfigureGrid(DataGridView grid, string name)
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
            grid.ColumnHeadersDefaultCellStyle.Padding = new Padding(4, 0, 4, 0);
            grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = PageBackgroundColor;
            grid.ColumnHeadersDefaultCellStyle.SelectionForeColor = TextSecondaryColor;
            grid.ColumnHeadersHeight = 32;
            grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
            grid.DefaultCellStyle.BackColor = Color.White;
            grid.DefaultCellStyle.ForeColor = TextPrimaryColor;
            grid.DefaultCellStyle.Padding = new Padding(4, 0, 4, 0);
            grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(239, 246, 255);
            grid.DefaultCellStyle.SelectionForeColor = AccentHoverColor;
            grid.Dock = DockStyle.Fill;
            grid.EnableHeadersVisualStyles = false;
            grid.GridColor = Color.FromArgb(241, 245, 249);
            grid.MultiSelect = false;
            grid.Name = name;
            grid.ReadOnly = true;
            grid.RowHeadersVisible = false;
            grid.RowTemplate.Height = 29;
            grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        }

        private static void AddTextColumn(DataGridView grid, string name, string header, int width)
        {
            DataGridViewTextBoxColumn column = new DataGridViewTextBoxColumn();
            column.HeaderText = header;
            column.MinimumWidth = 35;
            column.Name = name;
            column.ReadOnly = true;
            column.SortMode = DataGridViewColumnSortMode.NotSortable;
            column.Width = width;
            grid.Columns.Add(column);
        }
    }
}
