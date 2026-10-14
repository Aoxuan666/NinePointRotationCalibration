using System.ComponentModel;
using System.Drawing;
using System.Windows.Forms;
using NinePointRotationCalibration.Station.Controls;

namespace NinePointRotationCalibration.Station.Views
{
    partial class MainForm
    {
        private IContainer components;

        private ImageViewport imageViewport;
        private DataGridView dgvCalibration;
        private DataGridView dgvDetectionResults;
        private RichTextBox rtbLog;
        private TabControl tabWork;
        private NumericUpDown nudExpectedCount;
        private CheckBox chkRectangleFilter;
        private CheckBox chkTemplateMatchingEnabled;
        private CheckBox chkBinaryPreview;
        private ComboBox cmbCameraProvider;
        private ComboBox cmbCameraDevice;
        private CheckBox chkCameraEnabled;
        private CheckBox chkCalibrationEnabled;
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
        private TabPage tabCalibration;
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
            BackColor = Color.FromArgb(232, 235, 238);
            ClientSize = new Size(1500, 900);
            Font = new Font("Microsoft YaHei UI", 9.0F, FontStyle.Regular, GraphicsUnit.Point, 134);
            ForeColor = Color.FromArgb(39, 43, 48);
            MinimumSize = new Size(1280, 720);
            Name = "MainForm";
            StartPosition = FormStartPosition.CenterScreen;
            Text = "视觉工作站";

            TableLayoutPanel rootLayout = new TableLayoutPanel();
            rootLayout.BackColor = BackColor;
            rootLayout.ColumnCount = 1;
            rootLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100.0F));
            rootLayout.Dock = DockStyle.Fill;
            rootLayout.Margin = Padding.Empty;
            rootLayout.Name = "rootLayout";
            rootLayout.RowCount = 3;
            rootLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 58.0F));
            rootLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100.0F));
            rootLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 156.0F));

            rootLayout.Controls.Add(BuildHeader(), 0, 0);
            rootLayout.Controls.Add(BuildWorkspace(), 0, 1);
            rootLayout.Controls.Add(BuildLogPanel(), 0, 2);
            Controls.Add(rootLayout);

            ResumeLayout(false);
        }

        private Control BuildHeader()
        {
            TableLayoutPanel header = new TableLayoutPanel();
            header.BackColor = Color.FromArgb(38, 43, 49);
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
            statuses.Padding = new Padding(0, 2, 0, 0);
            statuses.WrapContents = false;

            FlowLayoutPanel cameraStatus = new FlowLayoutPanel();
            cameraStatus.AutoSize = false;
            cameraStatus.BackColor = Color.Transparent;
            cameraStatus.FlowDirection = FlowDirection.LeftToRight;
            cameraStatus.Margin = new Padding(0, 2, 8, 2);
            cameraStatus.Name = "cameraStatusPanel";
            cameraStatus.Size = new Size(200, 34);
            cameraStatus.WrapContents = false;

            lblCameraIndicator = new Label();
            lblCameraIndicator.AutoSize = false;
            lblCameraIndicator.Font = new Font("Microsoft YaHei UI", 11.0F, FontStyle.Regular, GraphicsUnit.Point, 134);
            lblCameraIndicator.ForeColor = Color.FromArgb(145, 151, 158);
            lblCameraIndicator.Margin = Padding.Empty;
            lblCameraIndicator.Name = "lblCameraIndicator";
            lblCameraIndicator.Size = new Size(18, 34);
            lblCameraIndicator.Text = "\u25CF";
            lblCameraIndicator.TextAlign = ContentAlignment.MiddleCenter;

            lblCameraStatus = new Label();
            lblCameraStatus.AutoEllipsis = true;
            lblCameraStatus.ForeColor = Color.White;
            lblCameraStatus.Margin = Padding.Empty;
            lblCameraStatus.Name = "lblCameraStatus";
            lblCameraStatus.Size = new Size(182, 34);
            lblCameraStatus.Text = "相机未连接";
            lblCameraStatus.TextAlign = ContentAlignment.MiddleLeft;
            cameraStatus.Controls.Add(lblCameraIndicator);
            cameraStatus.Controls.Add(lblCameraStatus);

            FlowLayoutPanel tcpStatus = new FlowLayoutPanel();
            tcpStatus.AutoSize = false;
            tcpStatus.BackColor = Color.Transparent;
            tcpStatus.FlowDirection = FlowDirection.LeftToRight;
            tcpStatus.Margin = new Padding(0, 2, 8, 2);
            tcpStatus.Name = "tcpStatusPanel";
            tcpStatus.Size = new Size(128, 34);
            tcpStatus.WrapContents = false;

            lblTcpIndicator = new Label();
            lblTcpIndicator.AutoSize = false;
            lblTcpIndicator.Font = new Font("Microsoft YaHei UI", 11.0F, FontStyle.Regular, GraphicsUnit.Point, 134);
            lblTcpIndicator.ForeColor = Color.FromArgb(145, 151, 158);
            lblTcpIndicator.Margin = Padding.Empty;
            lblTcpIndicator.Name = "lblTcpIndicator";
            lblTcpIndicator.Size = new Size(18, 34);
            lblTcpIndicator.Text = "\u25CF";
            lblTcpIndicator.TextAlign = ContentAlignment.MiddleCenter;

            lblTcpStatus = new Label();
            lblTcpStatus.AutoEllipsis = true;
            lblTcpStatus.ForeColor = Color.White;
            lblTcpStatus.Margin = Padding.Empty;
            lblTcpStatus.Name = "lblTcpStatus";
            lblTcpStatus.Size = new Size(110, 34);
            lblTcpStatus.Text = "TCP 未监听";
            lblTcpStatus.TextAlign = ContentAlignment.MiddleLeft;
            tcpStatus.Controls.Add(lblTcpIndicator);
            tcpStatus.Controls.Add(lblTcpStatus);

            lblOperationStatus = CreateStatusLabel("lblOperationStatus", "就绪", 150);
            lblOperationStatus.BackColor = Color.FromArgb(43, 92, 68);
            statuses.Controls.Add(cameraStatus);
            statuses.Controls.Add(tcpStatus);
            statuses.Controls.Add(lblOperationStatus);

            FlowLayoutPanel commands = new FlowLayoutPanel();
            commands.AutoSize = true;
            commands.Dock = DockStyle.Fill;
            commands.FlowDirection = FlowDirection.LeftToRight;
            commands.Margin = Padding.Empty;
            commands.WrapContents = false;

            btnSnapshot = CreateButton("btnSnapshot", "拍照", 82, 34, true, "camera.snapshot");
            btnImportImage = CreateButton("btnImportImage", "导入图片", 82, 34, false, "image.import");
            btnDetectCurrentImage = CreateButton("btnDetectCurrentImage", "检测当前图", 94, 34, true, "detection.currentImage");
            btnFit = CreateButton("btnFit", "适应", 70, 34, false, null);
            btnActualPixels = CreateButton("btnActualPixels", "1:1", 62, 34, false, null);
            toolTip.SetToolTip(btnSnapshot, "触发单次采集");
            toolTip.SetToolTip(btnImportImage, "从本地导入一张图片");
            toolTip.SetToolTip(btnDetectCurrentImage, "检测图像窗口中的当前图片");
            toolTip.SetToolTip(btnFit, "图像适应窗口");
            toolTip.SetToolTip(btnActualPixels, "按原始像素显示");
            commands.Controls.Add(btnSnapshot);
            commands.Controls.Add(btnImportImage);
            commands.Controls.Add(btnDetectCurrentImage);
            commands.Controls.Add(btnFit);
            commands.Controls.Add(btnActualPixels);

            header.Controls.Add(statuses, 0, 0);
            header.Controls.Add(commands, 1, 0);
            return header;
        }

        private Control BuildWorkspace()
        {
            SplitContainer workspace = new SplitContainer();
            workspace.BackColor = Color.FromArgb(202, 207, 212);
            workspace.Dock = DockStyle.Fill;
            workspace.FixedPanel = FixedPanel.None;
            workspace.IsSplitterFixed = false;
            workspace.Margin = Padding.Empty;
            workspace.Name = "workspaceSplit";
            workspace.Size = new Size(1484, 678);
            workspace.Panel1MinSize = 600;
            workspace.Panel2MinSize = 430;
            workspace.SplitterDistance = 930;
            workspace.SplitterWidth = 6;

            TableLayoutPanel imageLayout = new TableLayoutPanel();
            imageLayout.BackColor = Color.Black;
            imageLayout.ColumnCount = 1;
            imageLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100.0F));
            imageLayout.Dock = DockStyle.Fill;
            imageLayout.Padding = Padding.Empty;
            imageLayout.RowCount = 2;
            imageLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100.0F));
            imageLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28.0F));

            imageViewport = new ImageViewport();
            imageViewport.Dock = DockStyle.Fill;
            imageViewport.Margin = Padding.Empty;
            imageViewport.Name = "imageViewport";
            imageViewport.TabIndex = 0;

            lblInspectionHud = new Label();
            lblInspectionHud.AutoEllipsis = true;
            lblInspectionHud.BackColor = Color.FromArgb(30, 34, 39);
            lblInspectionHud.Font = new Font("Microsoft YaHei UI", 9.0F, FontStyle.Bold, GraphicsUnit.Point, 134);
            lblInspectionHud.ForeColor = Color.FromArgb(220, 226, 232);
            lblInspectionHud.Location = new Point(12, 12);
            lblInspectionHud.Name = "lblInspectionHud";
            lblInspectionHud.Padding = new Padding(10, 6, 10, 6);
            lblInspectionHud.Size = new Size(400, 76);
            lblInspectionHud.Text = "CT --\r\n检测结果 --\r\n检测分项 --";
            lblInspectionHud.TextAlign = ContentAlignment.MiddleLeft;
            lblInspectionHud.Visible = false;
            imageViewport.Controls.Add(lblInspectionHud);
            lblInspectionHud.BringToFront();

            TableLayoutPanel imageFooter = new TableLayoutPanel();
            imageFooter.ColumnCount = 2;
            imageFooter.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100.0F));
            imageFooter.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 180.0F));
            imageFooter.Dock = DockStyle.Fill;
            imageFooter.Margin = new Padding(0, 5, 0, 0);

            lblImageInfo = new Label();
            lblImageInfo.AutoEllipsis = true;
            lblImageInfo.Dock = DockStyle.Fill;
            lblImageInfo.ForeColor = Color.FromArgb(181, 188, 196);
            lblImageInfo.Text = "无图像";
            lblImageInfo.TextAlign = ContentAlignment.MiddleLeft;

            lblCursorPosition = new Label();
            lblCursorPosition.Dock = DockStyle.Fill;
            lblCursorPosition.ForeColor = Color.FromArgb(181, 188, 196);
            lblCursorPosition.Text = "X --   Y --";
            lblCursorPosition.TextAlign = ContentAlignment.MiddleRight;

            imageFooter.Controls.Add(lblImageInfo, 0, 0);
            imageFooter.Controls.Add(lblCursorPosition, 1, 0);
            imageLayout.Controls.Add(imageViewport, 0, 0);
            imageLayout.Controls.Add(imageFooter, 0, 1);
            workspace.Panel1.Controls.Add(imageLayout);

            tabWork = new TabControl();
            tabWork.Dock = DockStyle.Fill;
            tabWork.Font = new Font(Font.FontFamily, 9.0F, FontStyle.Regular);
            tabWork.Multiline = true;
            tabWork.Name = "tabWork";
            tabWork.Padding = new Point(11, 6);
            tabWork.SelectedIndex = 0;
            tabWork.TabPages.Add(BuildCalibrationTab());
            tabWork.TabPages.Add(BuildTeachingTab());
            tabWork.TabPages.Add(BuildTemplateTab());
            tabWork.TabPages.Add(BuildDetectionTab());
            tabWork.TabPages.Add(BuildCameraTab());
            tabWork.TabPages.Add(BuildCommunicationTab());
            workspace.Panel2.Controls.Add(tabWork);

            return workspace;
        }

        private TabPage BuildCalibrationTab()
        {
            tabCalibration = CreateTabPage("tabCalibration", "9+5 标定");
            TabPage page = tabCalibration;

            TableLayoutPanel layout = new TableLayoutPanel();
            layout.ColumnCount = 1;
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100.0F));
            layout.Dock = DockStyle.Fill;
            layout.Padding = new Padding(8);
            layout.RowCount = 3;
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 238.0F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 43.0F));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100.0F));

            TableLayoutPanel editor = new TableLayoutPanel();
            editor.ColumnCount = 2;
            editor.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 53.0F));
            editor.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 47.0F));
            editor.Dock = DockStyle.Fill;
            editor.RowCount = 1;
            editor.RowStyles.Add(new RowStyle(SizeType.Percent, 100.0F));

            GroupBox pointsGroup = CreateGroupBox("标定点位");
            pointsGroup.Padding = new Padding(8, 23, 8, 8);
            TableLayoutPanel pointLayout = new TableLayoutPanel();
            pointLayout.ColumnCount = 3;
            pointLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.333F));
            pointLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.333F));
            pointLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.334F));
            pointLayout.Dock = DockStyle.Fill;
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
            pointsGroup.Controls.Add(pointLayout);

            calibrationPointButtons = new[]
            {
                point1, point2, point3, point4, point5, point6, point7,
                point8, point9, point10, point11, point12, point13, point14
            };

            GroupBox moveGroup = CreateGroupBox("点位参数");
            TableLayoutPanel moveFields = CreateFieldTable(2);
            moveFields.AutoScroll = false;
            moveFields.ColumnStyles[0].Width = 98.0F;
            NumericUpDown stepX = CreateDecimalInput("nudCalibrationStepX", 10.0M, -1000.0M, 1000.0M, 3);
            NumericUpDown stepY = CreateDecimalInput("nudCalibrationStepY", 10.0M, -1000.0M, 1000.0M, 3);
            NumericUpDown stepR = CreateDecimalInput("nudCalibrationStepR", 3.0M, -360.0M, 360.0M, 3);
            NumericUpDown motionTimeout = CreateDecimalInput("nudMotionTimeout", 30000.0M, 1000.0M, 600000.0M, 0);
            AddField(moveFields, 0, "X 步距 (mm)", stepX);
            AddField(moveFields, 1, "Y 步距 (mm)", stepY);
            AddField(moveFields, 2, "R 步距 (deg)", stepR);
            AddField(moveFields, 3, "到位超时 (ms)", motionTimeout);
            moveFields.RowCount = 5;
            moveFields.RowStyles.Add(new RowStyle(SizeType.Percent, 100.0F));
            Label centerHint = new Label();
            centerHint.AutoSize = true;
            centerHint.ForeColor = Color.FromArgb(94, 101, 109);
            centerHint.Margin = new Padding(5, 5, 5, 0);
            centerHint.Text = "5 / 12 号分别为九点 / 五点原点";
            moveFields.Controls.Add(centerHint, 0, 4);
            moveFields.SetColumnSpan(centerHint, 2);
            moveGroup.Controls.Add(moveFields);

            editor.Controls.Add(pointsGroup, 0, 0);
            editor.Controls.Add(moveGroup, 1, 0);

            FlowLayoutPanel actions = new FlowLayoutPanel();
            actions.Dock = DockStyle.Fill;
            actions.FlowDirection = FlowDirection.LeftToRight;
            actions.Padding = new Padding(0, 4, 0, 3);
            actions.WrapContents = false;
            actions.Controls.Add(CreateButton("btnManualCalibration", "手动标定", 92, 32, false, "calibration.manual"));
            actions.Controls.Add(CreateButton("btnAutoCalibration", "自动标定", 92, 32, true, "calibration.auto"));
            actions.Controls.Add(CreateButton("btnCancelCalibration", "取消", 70, 32, false, "calibration.cancel"));
            actions.Controls.Add(CreateButton("btnCalculateCalibration", "计算并保存", 104, 32, false, "calibration.calculate"));

            dgvCalibration = new DataGridView();
            ConfigureGrid(dgvCalibration, "dgvCalibration");
            dgvCalibration.MultiSelect = false;
            dgvCalibration.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            AddTextColumn(dgvCalibration, "Point", "点", 42);
            AddTextColumn(dgvCalibration, "Kind", "类型", 48);
            AddTextColumn(dgvCalibration, "MachineX", "机械 X", 72);
            AddTextColumn(dgvCalibration, "MachineY", "机械 Y", 72);
            AddTextColumn(dgvCalibration, "MachineR", "机械 R", 68);
            AddTextColumn(dgvCalibration, "ImageRow", "图像 Row", 78);
            AddTextColumn(dgvCalibration, "ImageColumn", "图像 Col", 78);
            AddTextColumn(dgvCalibration, "ImageAngle", "角度", 65);
            AddTextColumn(dgvCalibration, "Status", "状态", 60);

            layout.Controls.Add(editor, 0, 0);
            layout.Controls.Add(actions, 0, 1);
            layout.Controls.Add(dgvCalibration, 0, 2);
            page.Controls.Add(layout);
            return page;
        }

        private TabPage BuildTeachingTab()
        {
            TabPage page = CreateTabPage("tabTeaching", "左右示教");
            TableLayoutPanel layout = CreateScrollableStack();
            layout.RowCount = 3;
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 246.0F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 246.0F));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100.0F));

            TextBox[] leftValues;
            TextBox[] rightValues;
            GroupBox leftGroup = BuildTeachingGroup("左侧示教", "left", out leftValues, out lblLeftTeachingStatus);
            GroupBox rightGroup = BuildTeachingGroup("右侧示教", "right", out rightValues, out lblRightTeachingStatus);
            leftTeachingValues = leftValues;
            rightTeachingValues = rightValues;

            Label hint = CreateHintLabel("记录当前会拍照并使用定位模板匹配，将模板匹配中心/角度与当前机械 X/Y/R 成对保存。");
            layout.Controls.Add(leftGroup, 0, 0);
            layout.Controls.Add(rightGroup, 0, 1);
            layout.Controls.Add(hint, 0, 2);
            page.Controls.Add(layout);
            return page;
        }

        private TabPage BuildTemplateTab()
        {
            TabPage page = CreateTabPage("tabTemplates", "模板");
            TableLayoutPanel layout = CreateScrollableStack();
            layout.RowCount = 3;
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 238.0F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 238.0F));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100.0F));

            GroupBox calibration = BuildTemplateGroup(
                "标定模板",
                "txtCalibrationTemplatePath",
                "template.calibration",
                "用于 9+5 标定点的特征定位");
            GroupBox positioning = BuildTemplateGroup(
                "定位模板",
                "txtPositioningTemplatePath",
                "template.positioning",
                "用于 PCB 板定位、角度与中心计算");
            Label hint = CreateHintLabel("标定模板和定位模板相互独立；创建模板时使用当前图像与当前 ROI。路径由配置文件持久化。");

            layout.Controls.Add(calibration, 0, 0);
            layout.Controls.Add(positioning, 0, 1);
            layout.Controls.Add(hint, 0, 2);
            page.Controls.Add(layout);
            return page;
        }

        private TabPage BuildDetectionTab()
        {
            TabPage page = CreateTabPage("tabDetection", "检测");
            TableLayoutPanel layout = new TableLayoutPanel();
            layout.ColumnCount = 1;
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100.0F));
            layout.Dock = DockStyle.Fill;
            layout.Padding = new Padding(8);
            layout.RowCount = 4;
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 310.0F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42.0F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28.0F));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100.0F));

            GroupBox settings = CreateGroupBox("板件与标记检测");
            TableLayoutPanel settingFields = CreatePairedFieldTable();
            settingFields.RowCount = 8;
            settingFields.RowStyles.Clear();
            settingFields.RowStyles.Add(new RowStyle(SizeType.Percent, 20.0F));
            settingFields.RowStyles.Add(new RowStyle(SizeType.Percent, 20.0F));
            settingFields.RowStyles.Add(new RowStyle(SizeType.Percent, 20.0F));
            settingFields.RowStyles.Add(new RowStyle(SizeType.Percent, 20.0F));
            settingFields.RowStyles.Add(new RowStyle(SizeType.Percent, 20.0F));
            settingFields.RowStyles.Add(new RowStyle(SizeType.Percent, 20.0F));
            settingFields.RowStyles.Add(new RowStyle(SizeType.Percent, 20.0F));
            settingFields.RowStyles.Add(new RowStyle(SizeType.Percent, 20.0F));
            nudExpectedCount = CreateDecimalInput("nudExpectedCount", 6.0M, 1.0M, 100.0M, 0);
            NumericUpDown brightThreshold = CreateDecimalInput("nudBrightThreshold", 128.0M, 0.0M, 255.0M, 0);
            NumericUpDown minimumArea = CreateDecimalInput("nudMinimumBlobArea", 30.0M, 0.0M, 10000000.0M, 0);
            NumericUpDown maximumArea = CreateDecimalInput("nudMaximumBlobArea", 10000.0M, 0.0M, 10000000.0M, 0);
            NumericUpDown rectangularity = CreateDecimalInput("nudRectangularity", 0.78M, 0.0M, 1.0M, 2);
            NumericUpDown morphologyRadius = CreateDecimalInput("nudMorphologyRadius", 1.0M, 0.0M, 5.0M, 0);
            brightThreshold.Tag = "detection.previewParameters";
            morphologyRadius.Tag = "detection.previewParameters";
            AddPairedField(settingFields, 0, 0, "生产目标板数", nudExpectedCount);
            AddPairedField(settingFields, 0, 2, "手动阈值", brightThreshold);
            AddPairedField(settingFields, 1, 0, "最小面积", minimumArea);
            AddPairedField(settingFields, 1, 2, "最大面积", maximumArea);
            AddPairedField(settingFields, 2, 0, "矩形度", rectangularity);
            AddPairedField(settingFields, 3, 0, "白孔去除半径", morphologyRadius);
            chkRectangleFilter = new CheckBox();
            chkRectangleFilter.AutoSize = true;
            chkRectangleFilter.Checked = true;
            chkRectangleFilter.Dock = DockStyle.Fill;
            chkRectangleFilter.Name = "chkRectangleFilter";
            chkRectangleFilter.Text = "启用矩形标签过滤";
            chkRectangleFilter.Tag = "detection.previewParameters";
            chkRectangleFilter.UseVisualStyleBackColor = true;
            settingFields.Controls.Add(chkRectangleFilter, 2, 2);
            settingFields.SetColumnSpan(chkRectangleFilter, 2);
            CheckBox fillHoles = new CheckBox();
            fillHoles.AutoSize = true;
            fillHoles.Checked = true;
            fillHoles.Dock = DockStyle.Fill;
            fillHoles.Name = "chkFillHoles";
            fillHoles.Tag = "detection.previewParameters";
            fillHoles.Text = "填充白目标内部黑洞";
            fillHoles.UseVisualStyleBackColor = true;
            settingFields.Controls.Add(fillHoles, 2, 3);
            settingFields.SetColumnSpan(fillHoles, 2);
            CheckBox autoThreshold = new CheckBox();
            autoThreshold.AutoSize = true;
            autoThreshold.Checked = true;
            autoThreshold.Dock = DockStyle.Fill;
            autoThreshold.Name = "chkAutoThreshold";
            autoThreshold.Tag = "detection.previewParameters";
            autoThreshold.Text = "ROI 自动阈值";
            autoThreshold.UseVisualStyleBackColor = true;
            settingFields.Controls.Add(autoThreshold, 0, 4);
            settingFields.SetColumnSpan(autoThreshold, 2);
            CheckBox darkForeground = new CheckBox();
            darkForeground.AutoSize = true;
            darkForeground.Dock = DockStyle.Fill;
            darkForeground.Name = "chkDarkForeground";
            darkForeground.Tag = "detection.previewParameters";
            darkForeground.Text = "白底黑点（暗目标）";
            darkForeground.UseVisualStyleBackColor = true;
            settingFields.Controls.Add(darkForeground, 2, 4);
            settingFields.SetColumnSpan(darkForeground, 2);
            chkTemplateMatchingEnabled = new CheckBox();
            chkTemplateMatchingEnabled.AutoSize = true;
            chkTemplateMatchingEnabled.Checked = true;
            chkTemplateMatchingEnabled.Dock = DockStyle.Fill;
            chkTemplateMatchingEnabled.Name = "chkTemplateMatchingEnabled";
            chkTemplateMatchingEnabled.Text = "启用模板匹配（生产检测）";
            chkTemplateMatchingEnabled.Tag = "detection.previewParameters";
            chkTemplateMatchingEnabled.UseVisualStyleBackColor = true;
            settingFields.Controls.Add(chkTemplateMatchingEnabled, 0, 5);
            settingFields.SetColumnSpan(chkTemplateMatchingEnabled, 4);
            CheckBox affineWithTemplate = new CheckBox();
            affineWithTemplate.AutoSize = true;
            affineWithTemplate.Checked = true;
            affineWithTemplate.Dock = DockStyle.Fill;
            affineWithTemplate.Name = "chkRoiAffineWithTemplate";
            affineWithTemplate.Tag = "detection.previewParameters";
            affineWithTemplate.Text = "使用模板中的检测 ROI（随板旋转）";
            settingFields.Controls.Add(affineWithTemplate, 0, 6);
            settingFields.SetColumnSpan(affineWithTemplate, 4);
            chkBinaryPreview = new CheckBox();
            chkBinaryPreview.AutoSize = true;
            chkBinaryPreview.Dock = DockStyle.Fill;
            chkBinaryPreview.Name = "chkBinaryPreview";
            chkBinaryPreview.Tag = "detection.binaryPreview";
            chkBinaryPreview.Text = "主画面显示二值图";
            chkBinaryPreview.UseVisualStyleBackColor = true;
            settingFields.Controls.Add(chkBinaryPreview, 0, 7);
            settingFields.SetColumnSpan(chkBinaryPreview, 4);
            settings.Controls.Add(settingFields);

            FlowLayoutPanel actions = new FlowLayoutPanel();
            actions.Dock = DockStyle.Fill;
            actions.Padding = new Padding(0, 4, 0, 2);
            Label currentRLabel = new Label();
            currentRLabel.AutoSize = false;
            currentRLabel.Margin = new Padding(0, 0, 5, 0);
            currentRLabel.Size = new Size(48, 32);
            currentRLabel.Text = "当前 R";
            currentRLabel.TextAlign = ContentAlignment.MiddleLeft;
            NumericUpDown currentR = CreateDecimalInput("nudInspectionCurrentR", 0.0M, -360000.0M, 360000.0M, 3);
            currentR.Margin = new Padding(0, 4, 8, 0);
            currentR.Size = new Size(64, 27);
            actions.Controls.Add(currentRLabel);
            actions.Controls.Add(currentR);
            actions.Controls.Add(CreateButton("btnTestDetection", "测试检测", 80, 32, true, "detection.test"));
            actions.Controls.Add(CreateButton("btnDebugDetection", "Blob 调试", 88, 32, true, "detection.debug"));
            actions.Controls.Add(CreateButton("btnSaveDetection", "保存参数", 80, 32, false, "detection.save"));

            lblDetectionSummary = new Label();
            lblDetectionSummary.AutoEllipsis = true;
            lblDetectionSummary.Dock = DockStyle.Fill;
            lblDetectionSummary.Font = new Font(Font.FontFamily, 9.0F, FontStyle.Bold);
            lblDetectionSummary.ForeColor = Color.FromArgb(94, 101, 109);
            lblDetectionSummary.Text = "目标 6  /  实际 --  /  等待检测";
            lblDetectionSummary.TextAlign = ContentAlignment.MiddleLeft;

            dgvDetectionResults = new DataGridView();
            ConfigureGrid(dgvDetectionResults, "dgvDetectionResults");
            AddTextColumn(dgvDetectionResults, "Index", "板号", 48);
            AddTextColumn(dgvDetectionResults, "Score", "分数", 60);
            AddTextColumn(dgvDetectionResults, "ImageX", "图像 X", 74);
            AddTextColumn(dgvDetectionResults, "ImageY", "图像 Y", 74);
            AddTextColumn(dgvDetectionResults, "Angle", "角度", 64);
            AddTextColumn(dgvDetectionResults, "Mark", "叉标记", 64);
            AddTextColumn(dgvDetectionResults, "Side", "映射", 58);
            AddTextColumn(dgvDetectionResults, "Status", "状态", 60);

            layout.Controls.Add(settings, 0, 0);
            layout.Controls.Add(actions, 0, 1);
            layout.Controls.Add(lblDetectionSummary, 0, 2);
            layout.Controls.Add(dgvDetectionResults, 0, 3);
            page.Controls.Add(layout);
            return page;
        }

        private TabPage BuildCameraTab()
        {
            TabPage page = CreateTabPage("tabCamera", "相机");
            TableLayoutPanel layout = CreateScrollableStack();
            layout.RowCount = 5;
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 82.0F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 160.0F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 132.0F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44.0F));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100.0F));

            GroupBox mode = CreateGroupBox("运行开关");
            FlowLayoutPanel modeOptions = new FlowLayoutPanel();
            modeOptions.Dock = DockStyle.Fill;
            modeOptions.FlowDirection = FlowDirection.LeftToRight;
            modeOptions.Padding = new Padding(8, 8, 8, 4);
            modeOptions.WrapContents = false;

            chkCameraEnabled = new CheckBox();
            chkCameraEnabled.AutoSize = true;
            chkCameraEnabled.Checked = true;
            chkCameraEnabled.Margin = new Padding(0, 4, 24, 0);
            chkCameraEnabled.Name = "chkCameraEnabled";
            chkCameraEnabled.Text = "启用相机";
            chkCameraEnabled.UseVisualStyleBackColor = true;

            chkCalibrationEnabled = new CheckBox();
            chkCalibrationEnabled.AutoSize = true;
            chkCalibrationEnabled.Checked = true;
            chkCalibrationEnabled.Margin = new Padding(0, 4, 0, 0);
            chkCalibrationEnabled.Name = "chkCalibrationEnabled";
            chkCalibrationEnabled.Text = "启用标定与坐标换算";
            chkCalibrationEnabled.UseVisualStyleBackColor = true;

            modeOptions.Controls.Add(chkCameraEnabled);
            modeOptions.Controls.Add(chkCalibrationEnabled);
            mode.Controls.Add(modeOptions);

            GroupBox connection = CreateGroupBox("相机连接");
            TableLayoutPanel connectionFields = CreateFieldTable(2);
            cmbCameraProvider = CreateComboBox("cmbCameraProvider");
            cmbCameraProvider.Items.AddRange(new object[] { "大华 (CameraDH)", "模拟相机" });
            cmbCameraProvider.SelectedIndex = 0;
            cmbCameraDevice = CreateComboBox("cmbCameraDevice");
            AddField(connectionFields, 0, "相机驱动", cmbCameraProvider);
            AddField(connectionFields, 1, "设备", cmbCameraDevice);
            FlowLayoutPanel cameraCommands = new FlowLayoutPanel();
            cameraCommands.AutoSize = true;
            cameraCommands.Dock = DockStyle.Fill;
            cameraCommands.Margin = Padding.Empty;
            cameraCommands.WrapContents = false;
            cameraCommands.Controls.Add(CreateButton("btnEnumerateCamera", "枚举", 68, 30, false, "camera.enumerate"));
            cameraCommands.Controls.Add(CreateButton("btnConnectCamera", "连接", 68, 30, true, "camera.connect"));
            cameraCommands.Controls.Add(CreateButton("btnDisconnectCamera", "断开", 68, 30, false, "camera.disconnect"));
            connectionFields.Controls.Add(cameraCommands, 0, 2);
            connectionFields.SetColumnSpan(cameraCommands, 2);
            connection.Controls.Add(connectionFields);

            GroupBox acquisition = CreateGroupBox("采集参数");
            TableLayoutPanel acquisitionFields = CreateFieldTable(2);
            AddField(acquisitionFields, 0, "曝光 (us)", CreateDecimalInput("nudCameraExposure", 8000.0M, 1.0M, 10000000.0M, 0));
            AddField(acquisitionFields, 1, "超时 (ms)", CreateDecimalInput("nudCameraTimeout", 2000.0M, 10.0M, 60000.0M, 0));
            acquisition.Controls.Add(acquisitionFields);

            FlowLayoutPanel actions = new FlowLayoutPanel();
            actions.Dock = DockStyle.Fill;
            actions.Padding = new Padding(0, 5, 0, 2);
            actions.Controls.Add(CreateButton("btnApplyCamera", "应用参数", 88, 32, true, "camera.apply"));
            actions.Controls.Add(CreateButton("btnTestCamera", "测试拍照", 88, 32, false, "camera.test"));

            Label hint = CreateHintLabel("相机由视觉工作站独占控制。连接、取图和参数写入应在后台执行，图像窗口只呈现最新帧。");
            layout.Controls.Add(mode, 0, 0);
            layout.Controls.Add(connection, 0, 1);
            layout.Controls.Add(acquisition, 0, 2);
            layout.Controls.Add(actions, 0, 3);
            layout.Controls.Add(hint, 0, 4);
            page.Controls.Add(layout);
            return page;
        }

        private TabPage BuildCommunicationTab()
        {
            TabPage page = CreateTabPage("tabCommunication", "通信 / 文件");
            TableLayoutPanel layout = CreateScrollableStack();
            layout.RowCount = 3;
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 174.0F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 292.0F));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100.0F));

            GroupBox tcp = CreateGroupBox("TCP 服务端");
            TableLayoutPanel tcpFields = CreateFieldTable(2);
            txtTcpBindAddress = CreateTextBox("txtTcpBindAddress", "0.0.0.0");
            nudTcpPort = CreateDecimalInput("nudTcpPort", 9100.0M, 1.0M, 65535.0M, 0);
            AddField(tcpFields, 0, "监听地址", txtTcpBindAddress);
            AddField(tcpFields, 1, "监听端口", nudTcpPort);
            FlowLayoutPanel tcpCommands = new FlowLayoutPanel();
            tcpCommands.AutoSize = true;
            tcpCommands.Dock = DockStyle.Fill;
            tcpCommands.Margin = Padding.Empty;
            tcpCommands.Controls.Add(CreateButton("btnStartTcp", "启动监听", 88, 30, true, "tcp.start"));
            tcpCommands.Controls.Add(CreateButton("btnStopTcp", "停止监听", 88, 30, false, "tcp.stop"));
            tcpFields.Controls.Add(tcpCommands, 0, 2);
            tcpFields.SetColumnSpan(tcpCommands, 2);
            tcp.Controls.Add(tcpFields);

            GroupBox files = CreateGroupBox("配置与数据文件");
            TableLayoutPanel fileFields = new TableLayoutPanel();
            fileFields.AutoScroll = true;
            fileFields.ColumnCount = 3;
            fileFields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96.0F));
            fileFields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100.0F));
            fileFields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 62.0F));
            fileFields.Dock = DockStyle.Fill;
            fileFields.Padding = new Padding(8, 6, 8, 6);
            fileFields.RowCount = 5;
            fileFields.RowStyles.Add(new RowStyle(SizeType.Absolute, 46.0F));
            fileFields.RowStyles.Add(new RowStyle(SizeType.Absolute, 46.0F));
            fileFields.RowStyles.Add(new RowStyle(SizeType.Absolute, 46.0F));
            fileFields.RowStyles.Add(new RowStyle(SizeType.Absolute, 46.0F));
            fileFields.RowStyles.Add(new RowStyle(SizeType.Absolute, 46.0F));
            AddPathField(fileFields, 0, "标定文件", "txtCalibrationFile", "files.calibration.browse");
            AddPathField(fileFields, 1, "定位文件", "txtPositioningFile", "files.positioning.browse");
            AddPathField(fileFields, 2, "示教文件", "txtTeachingFile", "files.teaching.browse");
            AddPathField(fileFields, 3, "检测配置", "txtDetectionProfile", "files.detection.browse");
            AddPathField(fileFields, 4, "图像归档", "txtImageArchive", "files.archive.browse");
            files.Controls.Add(fileFields);

            FlowLayoutPanel savePanel = new FlowLayoutPanel();
            savePanel.Dock = DockStyle.Fill;
            savePanel.Padding = new Padding(0, 6, 0, 0);
            savePanel.Controls.Add(CreateButton("btnSaveSettings", "保存全部设置", 110, 32, true, "settings.save"));

            layout.Controls.Add(tcp, 0, 0);
            layout.Controls.Add(files, 0, 1);
            layout.Controls.Add(savePanel, 0, 2);
            page.Controls.Add(layout);
            return page;
        }

        private Control BuildLogPanel()
        {
            TableLayoutPanel layout = new TableLayoutPanel();
            layout.BackColor = Color.FromArgb(29, 33, 38);
            layout.ColumnCount = 1;
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100.0F));
            layout.Dock = DockStyle.Fill;
            layout.Margin = new Padding(8, 4, 8, 8);
            layout.Padding = new Padding(10, 6, 10, 8);
            layout.RowCount = 2;
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34.0F));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100.0F));

            TableLayoutPanel header = new TableLayoutPanel();
            header.ColumnCount = 2;
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100.0F));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 82.0F));
            header.Dock = DockStyle.Fill;

            Label title = new Label();
            title.Dock = DockStyle.Fill;
            title.Font = new Font(Font.FontFamily, 9.0F, FontStyle.Bold);
            title.ForeColor = Color.FromArgb(220, 225, 230);
            title.Text = "运行日志";
            title.TextAlign = ContentAlignment.MiddleLeft;

            btnClearLog = CreateButton("btnClearLog", "清空", 70, 27, false, null);
            btnClearLog.Dock = DockStyle.Right;
            btnClearLog.Margin = new Padding(6, 1, 0, 3);
            header.Controls.Add(title, 0, 0);
            header.Controls.Add(btnClearLog, 1, 0);

            rtbLog = new RichTextBox();
            rtbLog.BackColor = Color.FromArgb(20, 23, 27);
            rtbLog.BorderStyle = BorderStyle.None;
            rtbLog.DetectUrls = false;
            rtbLog.Dock = DockStyle.Fill;
            rtbLog.Font = new Font("Consolas", 9.0F, FontStyle.Regular);
            rtbLog.ForeColor = Color.FromArgb(205, 211, 218);
            rtbLog.Name = "rtbLog";
            rtbLog.ReadOnly = true;
            rtbLog.WordWrap = false;

            layout.Controls.Add(header, 0, 0);
            layout.Controls.Add(rtbLog, 0, 1);
            return layout;
        }

        private GroupBox BuildTeachingGroup(string title, string sideKey, out TextBox[] values, out Label statusLabel)
        {
            GroupBox group = CreateGroupBox(title);
            TableLayoutPanel fields = new TableLayoutPanel();
            fields.ColumnCount = 4;
            fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 72.0F));
            fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50.0F));
            fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 72.0F));
            fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50.0F));
            fields.Dock = DockStyle.Fill;
            fields.Padding = new Padding(8, 7, 8, 7);
            fields.RowCount = 5;
            fields.RowStyles.Add(new RowStyle(SizeType.Absolute, 38.0F));
            fields.RowStyles.Add(new RowStyle(SizeType.Absolute, 38.0F));
            fields.RowStyles.Add(new RowStyle(SizeType.Absolute, 38.0F));
            fields.RowStyles.Add(new RowStyle(SizeType.Absolute, 38.0F));
            fields.RowStyles.Add(new RowStyle(SizeType.Percent, 100.0F));

            TextBox imageRow = CreateReadOnlyValue(sideKey + "ImageRow");
            TextBox imageColumn = CreateReadOnlyValue(sideKey + "ImageColumn");
            TextBox imageAngle = CreateReadOnlyValue(sideKey + "ImageAngle");
            TextBox machineX = CreateMachinePoseInput(sideKey + "MachineX");
            TextBox machineY = CreateMachinePoseInput(sideKey + "MachineY");
            TextBox machineR = CreateMachinePoseInput(sideKey + "MachineR");
            values = new[] { imageRow, imageColumn, imageAngle, machineX, machineY, machineR };

            AddTeachingField(fields, 0, 0, "图像 Row", imageRow);
            AddTeachingField(fields, 0, 2, "机械 X", machineX);
            AddTeachingField(fields, 1, 0, "图像 Col", imageColumn);
            AddTeachingField(fields, 1, 2, "机械 Y", machineY);
            AddTeachingField(fields, 2, 0, "图像角度", imageAngle);
            AddTeachingField(fields, 2, 2, "机械 R", machineR);

            statusLabel = new Label();
            statusLabel.AutoSize = true;
            statusLabel.Dock = DockStyle.Fill;
            statusLabel.ForeColor = Color.FromArgb(138, 68, 68);
            statusLabel.Name = sideKey == "left" ? "lblLeftTeachingStatus" : "lblRightTeachingStatus";
            statusLabel.Text = "未示教";
            statusLabel.TextAlign = ContentAlignment.MiddleLeft;
            fields.Controls.Add(statusLabel, 0, 3);
            fields.SetColumnSpan(statusLabel, 2);

            FlowLayoutPanel actions = new FlowLayoutPanel();
            actions.AutoSize = true;
            actions.Dock = DockStyle.Fill;
            actions.FlowDirection = FlowDirection.RightToLeft;
            actions.Margin = Padding.Empty;
            actions.WrapContents = false;
            actions.Controls.Add(CreateButton("btnClear" + sideKey + "Teaching", "清除", 70, 30, false, "teaching." + sideKey + ".clear"));
            actions.Controls.Add(CreateButton("btnCapture" + sideKey + "Teaching", "记录当前", 84, 30, true, "teaching." + sideKey + ".capture"));
            fields.Controls.Add(actions, 2, 3);
            fields.SetColumnSpan(actions, 2);
            group.Controls.Add(fields);
            return group;
        }

        private GroupBox BuildTemplateGroup(string title, string pathName, string actionPrefix, string description)
        {
            GroupBox group = CreateGroupBox(title);
            TableLayoutPanel fields = new TableLayoutPanel();
            fields.ColumnCount = 1;
            fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100.0F));
            fields.Dock = DockStyle.Fill;
            fields.Padding = new Padding(8, 8, 8, 8);
            fields.RowCount = 4;
            fields.RowStyles.Add(new RowStyle(SizeType.Absolute, 34.0F));
            fields.RowStyles.Add(new RowStyle(SizeType.Absolute, 42.0F));
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
            commands.WrapContents = false;
            commands.Controls.Add(CreateButton("btnBrowse" + pathName, "浏览", 66, 30, false, actionPrefix + ".browse"));
            commands.Controls.Add(CreateButton("btnCreate" + pathName, "创建模板", 84, 30, true, actionPrefix + ".create"));
            commands.Controls.Add(CreateButton("btnLoad" + pathName, "加载", 66, 30, false, actionPrefix + ".load"));
            commands.Controls.Add(CreateButton("btnTest" + pathName, "测试", 66, 30, false, actionPrefix + ".test"));

            Label status = new Label();
            status.AutoEllipsis = true;
            status.Dock = DockStyle.Fill;
            status.ForeColor = Color.FromArgb(94, 101, 109);
            status.Name = "lblStatus" + pathName;
            status.Text = "未加载";
            status.TextAlign = ContentAlignment.MiddleLeft;

            fields.Controls.Add(descriptionLabel, 0, 0);
            fields.Controls.Add(path, 0, 1);
            fields.Controls.Add(commands, 0, 2);
            fields.Controls.Add(status, 0, 3);
            group.Controls.Add(fields);
            return group;
        }

        private static TableLayoutPanel CreateScrollableStack()
        {
            TableLayoutPanel layout = new TableLayoutPanel();
            layout.AutoScroll = true;
            layout.ColumnCount = 1;
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100.0F));
            layout.Dock = DockStyle.Fill;
            layout.Padding = new Padding(8);
            return layout;
        }

        private static TabPage CreateTabPage(string name, string text)
        {
            TabPage page = new TabPage();
            page.BackColor = Color.FromArgb(244, 246, 248);
            page.Name = name;
            page.Padding = Padding.Empty;
            page.Text = text;
            page.UseVisualStyleBackColor = false;
            return page;
        }

        private static GroupBox CreateGroupBox(string text)
        {
            GroupBox group = new GroupBox();
            group.Dock = DockStyle.Fill;
            group.Font = new Font("Microsoft YaHei UI", 9.0F, FontStyle.Bold, GraphicsUnit.Point, 134);
            group.Margin = new Padding(3, 3, 3, 7);
            group.Padding = new Padding(6, 21, 6, 6);
            group.Text = text;
            return group;
        }

        private static TableLayoutPanel CreateFieldTable(int columns)
        {
            TableLayoutPanel fields = new TableLayoutPanel();
            fields.AutoScroll = true;
            fields.ColumnCount = columns;
            fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 112.0F));
            fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100.0F));
            fields.Dock = DockStyle.Fill;
            fields.Font = new Font("Microsoft YaHei UI", 9.0F, FontStyle.Regular, GraphicsUnit.Point, 134);
            fields.Padding = new Padding(8, 7, 8, 7);
            return fields;
        }

        private static TableLayoutPanel CreatePairedFieldTable()
        {
            TableLayoutPanel fields = new TableLayoutPanel();
            fields.ColumnCount = 4;
            fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 82.0F));
            fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50.0F));
            fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 82.0F));
            fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50.0F));
            fields.Dock = DockStyle.Fill;
            fields.Font = new Font("Microsoft YaHei UI", 9.0F, FontStyle.Regular, GraphicsUnit.Point, 134);
            fields.Padding = new Padding(8, 7, 8, 7);
            fields.RowCount = 3;
            fields.RowStyles.Add(new RowStyle(SizeType.Percent, 33.333F));
            fields.RowStyles.Add(new RowStyle(SizeType.Percent, 33.333F));
            fields.RowStyles.Add(new RowStyle(SizeType.Percent, 33.334F));
            return fields;
        }

        private static void AddField(TableLayoutPanel table, int row, string labelText, Control editor)
        {
            while (table.RowCount <= row)
            {
                table.RowCount++;
                table.RowStyles.Add(new RowStyle(SizeType.Absolute, 36.0F));
            }

            Label label = new Label();
            label.AutoEllipsis = true;
            label.Dock = DockStyle.Fill;
            label.Margin = new Padding(0, 3, 8, 3);
            label.Text = labelText;
            label.TextAlign = ContentAlignment.MiddleLeft;
            editor.Dock = DockStyle.Fill;
            editor.Margin = new Padding(0, 4, 0, 4);
            table.Controls.Add(label, 0, row);
            table.Controls.Add(editor, 1, row);
        }

        private static void AddPairedField(TableLayoutPanel table, int row, int column, string labelText, Control editor)
        {
            Label label = new Label();
            label.AutoEllipsis = true;
            label.Dock = DockStyle.Fill;
            label.Margin = new Padding(0, 3, 5, 3);
            label.Text = labelText;
            label.TextAlign = ContentAlignment.MiddleLeft;
            editor.Dock = DockStyle.Fill;
            editor.Margin = new Padding(0, 5, 8, 5);
            table.Controls.Add(label, column, row);
            table.Controls.Add(editor, column + 1, row);
        }

        private static void AddTeachingField(TableLayoutPanel table, int row, int column, string labelText, Control editor)
        {
            Label label = new Label();
            label.AutoEllipsis = true;
            label.Dock = DockStyle.Fill;
            label.Margin = new Padding(0, 3, 6, 3);
            label.Text = labelText;
            label.TextAlign = ContentAlignment.MiddleLeft;
            editor.Dock = DockStyle.Fill;
            editor.Margin = new Padding(0, 5, 8, 5);
            table.Controls.Add(label, column, row);
            table.Controls.Add(editor, column + 1, row);
        }

        private void AddPathField(TableLayoutPanel table, int row, string labelText, string textBoxName, string browseAction)
        {
            Label label = new Label();
            label.AutoEllipsis = true;
            label.Dock = DockStyle.Fill;
            label.Text = labelText;
            label.TextAlign = ContentAlignment.MiddleLeft;
            TextBox path = CreateTextBox(textBoxName, string.Empty);
            path.Dock = DockStyle.Fill;
            path.Margin = new Padding(0, 8, 7, 8);
            Button browse = CreateButton("btnBrowse" + textBoxName, "浏览", 56, 28, false, browseAction);
            browse.Dock = DockStyle.Fill;
            browse.Margin = new Padding(0, 7, 0, 7);
            table.Controls.Add(label, 0, row);
            table.Controls.Add(path, 1, row);
            table.Controls.Add(browse, 2, row);
        }

        private static Label CreateStatusLabel(string name, string text, int width)
        {
            Label label = new Label();
            label.AutoEllipsis = true;
            label.BackColor = Color.FromArgb(77, 82, 89);
            label.ForeColor = Color.White;
            label.Margin = new Padding(0, 2, 8, 2);
            label.Name = name;
            label.Padding = new Padding(9, 0, 9, 0);
            label.Size = new Size(width, 34);
            label.Text = text;
            label.TextAlign = ContentAlignment.MiddleLeft;
            return label;
        }

        private static Button CreateButton(string name, string text, int width, int height, bool primary, string actionKey)
        {
            Button button = new Button();
            button.BackColor = primary ? Color.FromArgb(17, 122, 101) : Color.FromArgb(72, 78, 86);
            button.Cursor = Cursors.Hand;
            button.FlatAppearance.BorderColor = primary ? Color.FromArgb(17, 122, 101) : Color.FromArgb(100, 107, 115);
            button.FlatStyle = FlatStyle.Flat;
            button.ForeColor = Color.White;
            button.Margin = new Padding(0, 0, 7, 0);
            button.Name = name;
            button.Size = new Size(width, height);
            button.Tag = actionKey;
            button.Text = text;
            button.UseVisualStyleBackColor = false;
            return button;
        }

        private static Button CreatePointButton(int point)
        {
            Button button = CreateButton("btnCalibrationPoint" + point, point.ToString(), 44, 36, false, null);
            button.Dock = DockStyle.Fill;
            button.Font = new Font("Microsoft YaHei UI", point == 5 ? 10.0F : 9.0F, FontStyle.Bold, GraphicsUnit.Point, 134);
            button.Margin = new Padding(3);
            button.Tag = point;
            return button;
        }

        private static NumericUpDown CreateDecimalInput(string name, decimal value, decimal minimum, decimal maximum, int decimalPlaces)
        {
            NumericUpDown input = new NumericUpDown();
            input.DecimalPlaces = decimalPlaces;
            input.Increment = decimalPlaces == 0 ? 1.0M : 0.1M;
            input.Maximum = maximum;
            input.Minimum = minimum;
            input.Name = name;
            input.TextAlign = HorizontalAlignment.Right;
            input.ThousandsSeparator = true;
            input.Value = value;
            return input;
        }

        private static TextBox CreateTextBox(string name, string value)
        {
            TextBox textBox = new TextBox();
            textBox.BorderStyle = BorderStyle.FixedSingle;
            textBox.Name = name;
            textBox.Text = value;
            return textBox;
        }

        private static TextBox CreateReadOnlyValue(string name)
        {
            TextBox textBox = CreateTextBox(name, string.Empty);
            textBox.BackColor = Color.White;
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
            comboBox.DropDownStyle = ComboBoxStyle.DropDownList;
            comboBox.FormattingEnabled = true;
            comboBox.Name = name;
            return comboBox;
        }

        private static Label CreateHintLabel(string text)
        {
            Label label = new Label();
            label.AutoEllipsis = true;
            label.Dock = DockStyle.Fill;
            label.ForeColor = Color.FromArgb(94, 101, 109);
            label.Padding = new Padding(4, 8, 4, 4);
            label.Text = text;
            label.TextAlign = ContentAlignment.TopLeft;
            return label;
        }

        private static void ConfigureGrid(DataGridView grid, string name)
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
            grid.Name = name;
            grid.ReadOnly = true;
            grid.RowHeadersVisible = false;
            grid.RowTemplate.Height = 27;
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
