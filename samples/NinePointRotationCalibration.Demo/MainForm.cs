using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using NinePointRotationCalibration;

namespace NinePointRotationCalibration.Demo
{
    internal sealed class MainForm : Form
    {
        private readonly NinePointRotationCalibrationSdk _sdk = new NinePointRotationCalibrationSdk();
        private readonly ZoomImageView _imageView;
        private readonly ToolStripButton _openImageButton;
        private readonly ToolStripButton _setupButton;
        private readonly ToolStripButton _locateButton;
        private readonly ToolStripButton _loadJobButton;
        private readonly ToolStripButton _saveJobButton;
        private readonly ToolStripButton _fitButton;
        private readonly ToolStripButton _actualSizeButton;
        private readonly ToolStripButton _zoomOutButton;
        private readonly ToolStripButton _zoomInButton;
        private readonly Label _imageSummaryLabel;
        private readonly Label _jobSummaryLabel;
        private readonly Label _jobPathLabel;
        private readonly Label _sampleCountLabel;
        private readonly NumericUpDown _machineXInput;
        private readonly NumericUpDown _machineYInput;
        private readonly NumericUpDown _machineThetaInput;
        private readonly ComboBox _sampleKindCombo;
        private readonly TextBox _sampleTagInput;
        private readonly Button _captureButton;
        private readonly Button _calculateButton;
        private readonly DataGridView _sampleGrid;
        private readonly TextBox _resultSummary;
        private readonly ToolStripStatusLabel _statusLabel;
        private readonly ToolStripStatusLabel _imageSizeLabel;
        private readonly ToolStripStatusLabel _zoomLabel;
        private readonly ToolStripProgressBar _progressBar;

        private CalibrationJob _job = new CalibrationJob();
        private CalibrationResult _lastResult;
        private Bitmap _currentImage;
        private string _currentImagePath;
        private string _currentJobPath;
        private bool _busy;
        private bool _initialBoundsConstrained;

        public MainForm()
        {
            Text = "九点加旋转标定 - 上位机示例";
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(1120, 720);
            Size = new Size(1480, 880);
            Font = new Font("Microsoft YaHei UI", 9.0f, FontStyle.Regular, GraphicsUnit.Point);
            BackColor = Color.FromArgb(244, 246, 248);

            ToolStrip mainTools = new ToolStrip
            {
                GripStyle = ToolStripGripStyle.Hidden,
                AutoSize = false,
                Height = 42,
                Padding = new Padding(8, 5, 8, 5),
                BackColor = Color.White,
                RenderMode = ToolStripRenderMode.System
            };
            _openImageButton = CreateToolButton("加载图像...", OpenImage);
            _setupButton = CreateToolButton("完整配置...", OpenFullSetup);
            _locateButton = CreateToolButton("定位", LocateCurrentImage);
            _loadJobButton = CreateToolButton("加载 Job...", LoadJob);
            _saveJobButton = CreateToolButton("保存 Job...", SaveJob);
            _fitButton = CreateToolButton("适应窗口", (sender, args) => _imageView.FitToWindow());
            _actualSizeButton = CreateToolButton("1:1", (sender, args) => _imageView.ShowActualSize());
            _zoomOutButton = CreateToolButton("-", (sender, args) => _imageView.ZoomOut());
            _zoomOutButton.ToolTipText = "缩小";
            _zoomInButton = CreateToolButton("+", (sender, args) => _imageView.ZoomIn());
            _zoomInButton.ToolTipText = "放大";
            mainTools.Items.AddRange(new ToolStripItem[]
            {
                _openImageButton,
                new ToolStripSeparator(),
                _setupButton,
                _locateButton,
                new ToolStripSeparator(),
                _loadJobButton,
                _saveJobButton,
                new ToolStripSeparator(),
                _fitButton,
                _actualSizeButton,
                _zoomOutButton,
                _zoomInButton
            });

            _imageView = new ZoomImageView { Dock = DockStyle.Fill };
            _imageView.ViewChanged += (sender, args) => UpdateZoomStatus();
            _imageSummaryLabel = new Label
            {
                Dock = DockStyle.Fill,
                AutoEllipsis = true,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(12, 0, 12, 0),
                ForeColor = Color.FromArgb(62, 70, 80),
                Text = "尚未加载图像"
            };

            Panel previewPanel = new Panel { Dock = DockStyle.Fill, BackColor = _imageView.BackColor };
            Panel imageHeader = new Panel { Dock = DockStyle.Top, Height = 36, BackColor = Color.White };
            imageHeader.Controls.Add(_imageSummaryLabel);
            previewPanel.Controls.Add(_imageView);
            previewPanel.Controls.Add(imageHeader);

            _jobSummaryLabel = new Label
            {
                Dock = DockStyle.Top,
                Height = 43,
                AutoEllipsis = true,
                Padding = new Padding(0, 3, 0, 0),
                ForeColor = Color.FromArgb(38, 45, 54)
            };
            _jobPathLabel = new Label
            {
                Dock = DockStyle.Top,
                Height = 22,
                AutoEllipsis = true,
                ForeColor = Color.DimGray
            };
            _machineXInput = CreatePoseInput();
            _machineYInput = CreatePoseInput();
            _machineThetaInput = CreatePoseInput();
            _sampleKindCombo = new ComboBox
            {
                Dock = DockStyle.Fill,
                DropDownStyle = ComboBoxStyle.DropDownList
            };
            _sampleKindCombo.Items.AddRange(new object[] { "平移", "旋转" });
            _sampleKindCombo.SelectedIndex = 0;
            _sampleTagInput = new TextBox { Dock = DockStyle.Fill };
            _captureButton = CreateActionButton("采集样本", CaptureSample);
            _calculateButton = CreateActionButton("计算标定", CalculateCalibration);
            _sampleCountLabel = new Label
            {
                Dock = DockStyle.Top,
                Height = 29,
                Padding = new Padding(7, 5, 7, 0),
                ForeColor = Color.FromArgb(62, 70, 80)
            };
            _sampleGrid = CreateSampleGrid();
            _resultSummary = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                BackColor = Color.White,
                BorderStyle = BorderStyle.FixedSingle,
                Font = new Font("Microsoft YaHei UI", 9.0f, FontStyle.Regular, GraphicsUnit.Point),
                Text = "尚未计算标定结果。"
            };

            Control sidePanel = BuildSidePanel();
            SplitContainer workspace = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Size = new Size(1400, 780),
                Orientation = Orientation.Vertical,
                FixedPanel = FixedPanel.Panel2,
                Panel1MinSize = 500,
                Panel2MinSize = 430,
                SplitterDistance = 955,
                SplitterWidth = 5,
                BackColor = Color.FromArgb(218, 222, 227)
            };
            workspace.Panel1.Controls.Add(previewPanel);
            workspace.Panel2.Controls.Add(sidePanel);
            workspace.Resize += (sender, args) =>
            {
                int desired = Math.Max(workspace.Panel1MinSize, workspace.Width - 445);
                if (desired > 0 && desired < workspace.Width - workspace.Panel2MinSize)
                    workspace.SplitterDistance = desired;
            };

            StatusStrip statusStrip = new StatusStrip { SizingGrip = false };
            _statusLabel = new ToolStripStatusLabel
            {
                Spring = true,
                TextAlign = ContentAlignment.MiddleLeft,
                Text = "就绪"
            };
            _imageSizeLabel = new ToolStripStatusLabel { Text = "无图像" };
            _zoomLabel = new ToolStripStatusLabel { Text = "缩放 --" };
            _progressBar = new ToolStripProgressBar
            {
                Style = ProgressBarStyle.Marquee,
                MarqueeAnimationSpeed = 30,
                Width = 110,
                Visible = false
            };
            statusStrip.Items.AddRange(new ToolStripItem[]
            {
                _statusLabel,
                _progressBar,
                _imageSizeLabel,
                _zoomLabel
            });

            Controls.Add(workspace);
            Controls.Add(statusStrip);
            Controls.Add(mainTools);
            FormClosing += HandleFormClosing;
            FormClosed += HandleFormClosed;
            Shown += HandleFirstShown;

            UpdateAllViews();
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
            // requested size together so the result panel remains reachable.
            MinimumSize = new Size(
                Math.Max(1, Math.Min(MinimumSize.Width, workingArea.Width)),
                Math.Max(1, Math.Min(MinimumSize.Height, workingArea.Height)));
            int width = Math.Max(MinimumSize.Width, Math.Min(Width, workingArea.Width));
            int height = Math.Max(MinimumSize.Height, Math.Min(Height, workingArea.Height));
            int left = workingArea.Left + Math.Max(0, (workingArea.Width - width) / 2);
            int top = workingArea.Top + Math.Max(0, (workingArea.Height - height) / 2);
            Bounds = new Rectangle(left, top, width, height);
        }

        private Control BuildSidePanel()
        {
            Panel host = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(244, 246, 248),
                Padding = new Padding(10)
            };
            TableLayoutPanel layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 4,
                Margin = Padding.Empty
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100.0f));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 92.0f));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 190.0f));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100.0f));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 224.0f));

            Panel jobPanel = new Panel { Dock = DockStyle.Fill, Margin = new Padding(3, 0, 3, 7) };
            Label jobTitle = CreateSectionTitle("当前 Job");
            jobPanel.Controls.Add(_jobPathLabel);
            jobPanel.Controls.Add(_jobSummaryLabel);
            jobPanel.Controls.Add(jobTitle);

            GroupBox captureGroup = new GroupBox
            {
                Text = "样本采集",
                Dock = DockStyle.Fill,
                Padding = new Padding(10, 8, 10, 9),
                Margin = new Padding(3, 0, 3, 8)
            };
            TableLayoutPanel inputs = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 4,
                RowCount = 4
            };
            inputs.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 46.0f));
            inputs.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50.0f));
            inputs.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 54.0f));
            inputs.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50.0f));
            for (int index = 0; index < 3; index++) inputs.RowStyles.Add(new RowStyle(SizeType.Absolute, 34.0f));
            inputs.RowStyles.Add(new RowStyle(SizeType.Percent, 100.0f));
            inputs.Controls.Add(CreateFieldLabel("X"), 0, 0);
            inputs.Controls.Add(_machineXInput, 1, 0);
            inputs.Controls.Add(CreateFieldLabel("Y"), 2, 0);
            inputs.Controls.Add(_machineYInput, 3, 0);
            inputs.Controls.Add(CreateFieldLabel("Theta"), 0, 1);
            inputs.Controls.Add(_machineThetaInput, 1, 1);
            inputs.Controls.Add(CreateFieldLabel("类型"), 2, 1);
            inputs.Controls.Add(_sampleKindCombo, 3, 1);
            inputs.Controls.Add(CreateFieldLabel("标记"), 0, 2);
            inputs.Controls.Add(_sampleTagInput, 1, 2);
            inputs.SetColumnSpan(_sampleTagInput, 3);
            FlowLayoutPanel captureActions = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                Padding = new Padding(0, 5, 0, 0),
                Margin = Padding.Empty
            };
            Button locateSideButton = CreateActionButton("定位", LocateCurrentImage);
            locateSideButton.Width = 86;
            _captureButton.Width = 104;
            _calculateButton.Width = 104;
            captureActions.Controls.Add(locateSideButton);
            captureActions.Controls.Add(_captureButton);
            captureActions.Controls.Add(_calculateButton);
            inputs.Controls.Add(captureActions, 0, 3);
            inputs.SetColumnSpan(captureActions, 4);
            captureGroup.Controls.Add(inputs);

            GroupBox sampleGroup = new GroupBox
            {
                Text = "样本",
                Dock = DockStyle.Fill,
                Padding = new Padding(7, 6, 7, 7),
                Margin = new Padding(3, 0, 3, 8)
            };
            sampleGroup.Controls.Add(_sampleGrid);
            sampleGroup.Controls.Add(_sampleCountLabel);

            GroupBox resultGroup = new GroupBox
            {
                Text = "结果摘要",
                Dock = DockStyle.Fill,
                Padding = new Padding(8, 8, 8, 8),
                Margin = new Padding(3, 0, 3, 0)
            };
            resultGroup.Controls.Add(_resultSummary);

            layout.Controls.Add(jobPanel, 0, 0);
            layout.Controls.Add(captureGroup, 0, 1);
            layout.Controls.Add(sampleGroup, 0, 2);
            layout.Controls.Add(resultGroup, 0, 3);
            host.Controls.Add(layout);
            return host;
        }

        private void OpenImage(object sender, EventArgs eventArgs)
        {
            using (OpenFileDialog dialog = new OpenFileDialog
            {
                Title = "加载标定图像",
                Filter = "图像文件|*.bmp;*.png;*.jpg;*.jpeg;*.tif;*.tiff|所有文件|*.*",
                CheckFileExists = true,
                Multiselect = false
            })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    Bitmap loaded = LoadBitmapWithoutLock(dialog.FileName);
                    Bitmap previous = _currentImage;
                    _currentImage = loaded;
                    _currentImagePath = dialog.FileName;
                    _imageView.SetImage(_currentImage);
                    if (previous != null) previous.Dispose();
                    _lastResult = null;
                    _resultSummary.Text = "尚未计算标定结果。";
                    UpdateAllViews();
                    SetStatus("图像已加载：" + Path.GetFileName(dialog.FileName), false);
                }
                catch (Exception exception)
                {
                    ShowError("加载图像", exception.Message);
                }
            }
        }

        private void OpenFullSetup(object sender, EventArgs eventArgs)
        {
            if (!EnsureImage()) return;
            SetStatus("正在打开完整配置...", false);
            CalibrationSetupResult setup = _sdk.OpenSetupDialog(_currentImage, _job, this);
            if (setup.Accepted && setup.Job != null)
            {
                _job = setup.Job;
                _lastResult = null;
                _imageView.SetMatch(null);
                _resultSummary.Text = "配置已更新，尚未重新计算标定结果。";
                UpdateAllViews();
                SetStatus("完整配置已应用。", false);
                return;
            }

            if (setup.ErrorCode == CalibrationErrorCode.None)
            {
                SetStatus("已取消完整配置。", false);
            }
            else
            {
                ShowSdkFailure("完整配置", setup.ErrorCode, setup.Message);
            }
        }

        private async void LocateCurrentImage(object sender, EventArgs eventArgs)
        {
            if (!EnsureReadyForMatching()) return;
            TemplateMatchResult result = await RunBusyAsync(
                "正在定位当前图像...",
                () => _sdk.Locate(_currentImage, _job, true));
            if (result == null) return;

            _imageView.SetMatch(result.Success ? result : null);
            if (!result.Success)
            {
                ShowSdkFailure("定位", result.ErrorCode, result.Message);
                return;
            }

            SetStatus(string.Format(
                "定位成功：Row={0:0.00}, Column={1:0.00}, Angle={2:0.00} deg, Score={3:0.000}",
                result.Anchor.Row,
                result.Anchor.Column,
                result.AngleDegrees,
                result.Score), false);
        }

        private async void CaptureSample(object sender, EventArgs eventArgs)
        {
            if (!EnsureReadyForMatching()) return;
            double machineX = decimal.ToDouble(_machineXInput.Value);
            double machineY = decimal.ToDouble(_machineYInput.Value);
            double machineTheta = decimal.ToDouble(_machineThetaInput.Value);
            CalibrationSampleKind kind = _sampleKindCombo.SelectedIndex == 1
                ? CalibrationSampleKind.Rotation
                : CalibrationSampleKind.Translation;
            string tag = string.IsNullOrWhiteSpace(_sampleTagInput.Text) ? null : _sampleTagInput.Text.Trim();

            SampleCaptureResult result = await RunBusyAsync(
                "正在定位并采集样本...",
                () => _sdk.CaptureSample(
                    _job,
                    _currentImage,
                    machineX,
                    machineY,
                    machineTheta,
                    tag,
                    kind));
            if (result == null) return;
            if (!result.Success || result.Job == null)
            {
                _imageView.SetMatch(null);
                ShowSdkFailure("采集样本", result.ErrorCode, result.Message);
                return;
            }

            _job = result.Job;
            _lastResult = null;
            _imageView.SetMatch(result.Match);
            _resultSummary.Text = "样本已变化，尚未重新计算标定结果。";
            _sampleTagInput.Clear();
            UpdateAllViews();
            SetStatus(string.Format(
                "{0}样本已采集，Score={1:0.000}。",
                kind == CalibrationSampleKind.Rotation ? "旋转" : "平移",
                result.Match == null ? 0.0 : result.Match.Score), false);
        }

        private async void CalculateCalibration(object sender, EventArgs eventArgs)
        {
            if (_job == null) return;
            CalibrationResult result = await RunBusyAsync(
                "正在计算标定结果...",
                () => _sdk.Calculate(_job));
            if (result == null) return;

            _lastResult = result;
            UpdateResultSummary();
            if (result.Success)
            {
                SetStatus(string.Format(
                    "标定成功：{0} 个内点，RMS={1:0.###} px。",
                    result.InlierCount,
                    result.RmsResidualPixels), false);
            }
            else
            {
                SetStatus("标定失败：" + result.Message, true);
            }
        }

        private void SaveJob(object sender, EventArgs eventArgs)
        {
            using (SaveFileDialog dialog = new SaveFileDialog
            {
                Title = "保存标定 Job",
                Filter = "九点标定 Job|*.nprcal|所有文件|*.*",
                DefaultExt = "nprcal",
                AddExtension = true,
                FileName = string.IsNullOrWhiteSpace(_currentJobPath)
                    ? "calibration.nprcal"
                    : Path.GetFileName(_currentJobPath),
                InitialDirectory = GetExistingDirectory(_currentJobPath)
            })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                JobPersistenceResult result = _sdk.TrySaveJob(dialog.FileName, _job);
                if (!result.Success)
                {
                    ShowSdkFailure("保存 Job", result.ErrorCode, result.Message);
                    return;
                }

                _currentJobPath = result.Path;
                UpdateJobSummary();
                SetStatus("Job 已保存：" + result.Path, false);
            }
        }

        private void LoadJob(object sender, EventArgs eventArgs)
        {
            using (OpenFileDialog dialog = new OpenFileDialog
            {
                Title = "加载标定 Job",
                Filter = "九点标定 Job|*.nprcal|所有文件|*.*",
                CheckFileExists = true,
                Multiselect = false,
                InitialDirectory = GetExistingDirectory(_currentJobPath)
            })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                JobPersistenceResult result = _sdk.TryLoadJob(dialog.FileName);
                if (!result.Success || result.Job == null)
                {
                    ShowSdkFailure("加载 Job", result.ErrorCode, result.Message);
                    return;
                }

                _job = result.Job;
                _currentJobPath = result.Path;
                _lastResult = null;
                _imageView.SetMatch(null);
                _resultSummary.Text = "Job 已加载，尚未计算标定结果。";
                UpdateAllViews();
                SetStatus("Job 已加载：" + result.Path, false);
            }
        }

        private async Task<T> RunBusyAsync<T>(string message, Func<T> operation) where T : class
        {
            _busy = true;
            _progressBar.Visible = true;
            SetStatus(message, false);
            UpdateCommandState();
            try
            {
                return await Task.Run(operation);
            }
            catch (Exception exception)
            {
                ShowError("执行失败", exception.Message);
                return null;
            }
            finally
            {
                _busy = false;
                _progressBar.Visible = false;
                UpdateCommandState();
            }
        }

        private void UpdateAllViews()
        {
            UpdateImageSummary();
            UpdateJobSummary();
            UpdateSampleGrid();
            UpdateResultSummary();
            UpdateCommandState();
            UpdateZoomStatus();
        }

        private void UpdateImageSummary()
        {
            if (_currentImage == null)
            {
                _imageSummaryLabel.Text = "尚未加载图像";
                _imageSizeLabel.Text = "无图像";
                return;
            }

            string name = string.IsNullOrWhiteSpace(_currentImagePath)
                ? "当前图像"
                : Path.GetFileName(_currentImagePath);
            _imageSummaryLabel.Text = string.Format("{0}    {1} x {2}", name, _currentImage.Width, _currentImage.Height);
            _imageSummaryLabel.Tag = _currentImagePath;
            _imageSizeLabel.Text = string.Format("{0} x {1}", _currentImage.Width, _currentImage.Height);
        }

        private void UpdateJobSummary()
        {
            CalibrationJob job = _job ?? new CalibrationJob();
            TemplateDefinition template = job.Template;
            string templateState;
            if (template != null && template.HasUsableModel)
            {
                templateState = string.Format(
                    "模板已就绪 ({0})，Revision {1}",
                    template.ModelType == TemplateModelType.Shape ? "Shape" : "NCC",
                    template.Revision);
            }
            else if (template != null && template.TemplateRoi != null)
            {
                templateState = "模板需重新创建";
            }
            else
            {
                templateState = "模板未配置";
            }

            _jobSummaryLabel.Text = (string.IsNullOrWhiteSpace(job.Name) ? "未命名 Job" : job.Name) + Environment.NewLine + templateState;
            _jobPathLabel.Text = string.IsNullOrWhiteSpace(_currentJobPath)
                ? "尚未保存"
                : _currentJobPath;
        }

        private void UpdateSampleGrid()
        {
            _sampleGrid.Rows.Clear();
            CalibrationSample[] samples = (_job == null || _job.Samples == null)
                ? new CalibrationSample[0]
                : _job.Samples.Where(item => item != null).ToArray();
            int sequence = 0;
            foreach (CalibrationSample sample in samples)
            {
                sequence++;
                MachinePose machine = sample.MachinePose ?? new MachinePose();
                ImagePose image = sample.ImagePose ?? new ImagePose();
                bool stale = _job.Template != null && sample.TemplateRevision != _job.Template.Revision;
                int rowIndex = _sampleGrid.Rows.Add(
                    sequence,
                    sample.Kind == CalibrationSampleKind.Rotation ? "旋转" : "平移",
                    machine.X.ToString("0.###"),
                    machine.Y.ToString("0.###"),
                    machine.ThetaDegrees.ToString("0.###"),
                    image.Row.ToString("0.##"),
                    image.Column.ToString("0.##"),
                    sample.MatchScore.ToString("0.000"),
                    !sample.Enabled ? "禁用" : stale ? "过期" : "有效");
                if (stale)
                {
                    _sampleGrid.Rows[rowIndex].DefaultCellStyle.ForeColor = Color.FromArgb(186, 92, 25);
                }
            }

            int translationCount = samples.Count(item => item.Kind == CalibrationSampleKind.Translation && item.Enabled);
            int rotationCount = samples.Count(item => item.Kind == CalibrationSampleKind.Rotation && item.Enabled);
            int staleCount = _job != null && _job.Template != null
                ? samples.Count(item => item.Enabled && item.TemplateRevision != _job.Template.Revision)
                : 0;
            int requiredTranslation = _job != null && _job.SolverOptions != null
                ? _job.SolverOptions.MinimumTranslationSamples
                : 9;
            int requiredRotation = _job != null && _job.SolverOptions != null
                ? _job.SolverOptions.MinimumRotationSamples
                : 3;
            _sampleCountLabel.Text = string.Format(
                "平移 {0}/{1}    旋转 {2}/{3}{4}",
                translationCount,
                requiredTranslation,
                rotationCount,
                requiredRotation,
                staleCount > 0 ? "    过期 " + staleCount : string.Empty);
        }

        private void UpdateResultSummary()
        {
            if (_lastResult == null) return;
            CalibrationResult result = _lastResult;
            StringBuilder text = new StringBuilder();
            if (!result.Success)
            {
                text.AppendLine("标定失败  [" + result.ErrorCode + "]");
                text.AppendLine(result.Message ?? string.Empty);
                text.AppendFormat("候选 {0}，内点 {1}，剔除 {2}", result.CandidateCount, result.InlierCount, result.RejectedCount);
                _resultSummary.Text = text.ToString();
                return;
            }

            string unit = GetLinearUnitName(_job);
            text.AppendLine("标定成功");
            text.AppendFormat("平移：内点 {0}/{1}，RMS {2:0.###} px，Max {3:0.###} px\r\n",
                result.InlierCount,
                result.CandidateCount,
                result.RmsResidualPixels,
                result.MaxResidualPixels);
            text.AppendFormat("分辨率：X {0:0.######} {2}/px，Y {1:0.######} {2}/px\r\n",
                result.ResolutionX,
                result.ResolutionY,
                unit);
            text.AppendFormat("轴夹角 {0:0.###} deg，条件数 {1:0.###}\r\n",
                result.AxisAngleDegrees,
                result.ConditionNumber);
            text.AppendFormat("Stage -> Pixel: [{0:0.######}, {1:0.######}, {2:0.###}; {3:0.######}, {4:0.######}, {5:0.###}]\r\n",
                result.StageToPixelMatrix.M11,
                result.StageToPixelMatrix.M12,
                result.StageToPixelMatrix.OffsetX,
                result.StageToPixelMatrix.M21,
                result.StageToPixelMatrix.M22,
                result.StageToPixelMatrix.OffsetY);

            if (result.Rotation != null && result.Rotation.IsAvailable)
            {
                RotationCalibrationResult rotation = result.Rotation;
                text.AppendFormat("旋转：{0}，偏置 {1:0.###} deg，角度 RMS {2:0.###} deg\r\n",
                    rotation.Direction == RotationDirection.Same ? "同向" : rotation.Direction == RotationDirection.Opposite ? "反向" : "未知",
                    rotation.AngleOffsetDegrees,
                    rotation.AngleRmsDegrees);
                text.AppendFormat("旋转中心：图像 ({0:0.###}, {1:0.###})，机械 ({2:0.###}, {3:0.###}) {4}",
                    rotation.RotationCenterImage.Row,
                    rotation.RotationCenterImage.Column,
                    rotation.RotationCenterStage.X,
                    rotation.RotationCenterStage.Y,
                    unit);
            }

            if (result.Warnings != null && result.Warnings.Count > 0)
            {
                text.AppendLine();
                foreach (string warning in result.Warnings.Where(item => !string.IsNullOrWhiteSpace(item)))
                    text.AppendLine("警告：" + warning);
            }
            _resultSummary.Text = text.ToString().TrimEnd();
        }

        private void UpdateCommandState()
        {
            bool hasImage = _currentImage != null;
            bool hasTemplate = _job != null && _job.Template != null && _job.Template.HasUsableModel;
            bool canMatch = hasImage && hasTemplate && !_busy;
            _openImageButton.Enabled = !_busy;
            _setupButton.Enabled = hasImage && !_busy;
            _locateButton.Enabled = canMatch;
            _loadJobButton.Enabled = !_busy;
            _saveJobButton.Enabled = _job != null && !_busy;
            _fitButton.Enabled = hasImage && !_busy;
            _actualSizeButton.Enabled = hasImage && !_busy;
            _zoomOutButton.Enabled = hasImage && !_busy;
            _zoomInButton.Enabled = hasImage && !_busy;
            _captureButton.Enabled = canMatch;
            _calculateButton.Enabled = _job != null && _job.Samples != null && _job.Samples.Count > 0 && !_busy;
            _machineXInput.Enabled = !_busy;
            _machineYInput.Enabled = !_busy;
            _machineThetaInput.Enabled = !_busy;
            _sampleKindCombo.Enabled = !_busy;
            _sampleTagInput.Enabled = !_busy;
        }

        private void UpdateZoomStatus()
        {
            if (_zoomLabel == null) return;
            if (_currentImage == null)
            {
                _zoomLabel.Text = "缩放 --";
                return;
            }

            _zoomLabel.Text = _imageView.IsFitToWindow
                ? string.Format("适应 {0}%", _imageView.ZoomPercent)
                : string.Format("缩放 {0}%", _imageView.ZoomPercent);
        }

        private bool EnsureImage()
        {
            if (_currentImage != null) return true;
            MessageBox.Show(this, "请先加载当前图像。", "图像", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return false;
        }

        private bool EnsureReadyForMatching()
        {
            if (!EnsureImage()) return false;
            if (_job == null || _job.Template == null || !_job.Template.HasUsableModel)
            {
                MessageBox.Show(this, "模板尚未创建或已经失效，请先打开完整配置。", "模板", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return false;
            }
            return true;
        }

        private void SetStatus(string message, bool isError)
        {
            _statusLabel.Text = string.IsNullOrWhiteSpace(message) ? "就绪" : message;
            _statusLabel.ForeColor = isError ? Color.Firebrick : SystemColors.ControlText;
        }

        private void ShowSdkFailure(string operation, CalibrationErrorCode errorCode, string message)
        {
            string text = string.Format("{0} [{1}]\r\n{2}", operation, errorCode, message);
            SetStatus(operation + "失败：" + message, true);
            MessageBox.Show(this, text, operation, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        private void ShowError(string operation, string message)
        {
            SetStatus(operation + "失败：" + message, true);
            MessageBox.Show(this, message, operation, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        private void HandleFormClosing(object sender, FormClosingEventArgs eventArgs)
        {
            if (!_busy) return;
            eventArgs.Cancel = true;
            SetStatus("当前操作完成后才能关闭窗口。", true);
        }

        private void HandleFormClosed(object sender, FormClosedEventArgs eventArgs)
        {
            if (_currentImage != null)
            {
                _currentImage.Dispose();
                _currentImage = null;
            }
            _sdk.Dispose();
        }

        private static Bitmap LoadBitmapWithoutLock(string path)
        {
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (Image source = Image.FromStream(stream, true, true))
            {
                Bitmap bitmap = new Bitmap(source.Width, source.Height, PixelFormat.Format24bppRgb);
                try
                {
                    using (Graphics graphics = Graphics.FromImage(bitmap))
                    {
                        graphics.DrawImageUnscaled(source, 0, 0);
                    }
                    return bitmap;
                }
                catch
                {
                    bitmap.Dispose();
                    throw;
                }
            }
        }

        private static string GetExistingDirectory(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return string.Empty;
            string directory = Path.GetDirectoryName(path);
            return !string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory) ? directory : string.Empty;
        }

        private static string GetLinearUnitName(CalibrationJob job)
        {
            if (job == null) return "unit";
            switch (job.LinearUnit)
            {
                case LinearUnit.Micrometer:
                    return "um";
                case LinearUnit.Inch:
                    return "inch";
                case LinearUnit.Custom:
                    return string.IsNullOrWhiteSpace(job.CustomLinearUnitName) ? "unit" : job.CustomLinearUnitName;
                default:
                    return "mm";
            }
        }

        private static ToolStripButton CreateToolButton(string text, EventHandler handler)
        {
            ToolStripButton button = new ToolStripButton(text)
            {
                DisplayStyle = ToolStripItemDisplayStyle.Text,
                AutoSize = true,
                Margin = new Padding(2, 1, 2, 2),
                Padding = new Padding(5, 0, 5, 0)
            };
            button.Click += handler;
            return button;
        }

        private static Button CreateActionButton(string text, EventHandler handler)
        {
            Button button = new Button
            {
                Text = text,
                Width = 96,
                Height = 32,
                Margin = new Padding(0, 0, 7, 0),
                UseVisualStyleBackColor = true
            };
            button.Click += handler;
            return button;
        }

        private static NumericUpDown CreatePoseInput()
        {
            return new NumericUpDown
            {
                Dock = DockStyle.Fill,
                DecimalPlaces = 3,
                Minimum = -1000000000M,
                Maximum = 1000000000M,
                Increment = 0.1M,
                ThousandsSeparator = false
            };
        }

        private static Label CreateFieldLabel(string text)
        {
            return new Label
            {
                Text = text,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.FromArgb(62, 70, 80)
            };
        }

        private static Label CreateSectionTitle(string text)
        {
            return new Label
            {
                Text = text,
                Dock = DockStyle.Top,
                Height = 25,
                Font = new Font("Microsoft YaHei UI", 10.0f, FontStyle.Bold, GraphicsUnit.Point),
                ForeColor = Color.FromArgb(30, 38, 47)
            };
        }

        private static DataGridView CreateSampleGrid()
        {
            DataGridView grid = new DataGridView
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                AllowUserToResizeRows = false,
                MultiSelect = false,
                RowHeadersVisible = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None,
                BackgroundColor = Color.White,
                BorderStyle = BorderStyle.FixedSingle,
                EnableHeadersVisualStyles = false,
                ColumnHeadersHeight = 30,
                RowTemplate = { Height = 27 }
            };
            grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(235, 238, 242);
            grid.ColumnHeadersDefaultCellStyle.ForeColor = Color.FromArgb(42, 49, 58);
            grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(213, 230, 248);
            grid.DefaultCellStyle.SelectionForeColor = Color.FromArgb(25, 31, 38);
            grid.Columns.Add(CreateGridColumn("No", "#", 38));
            grid.Columns.Add(CreateGridColumn("Kind", "类型", 52));
            grid.Columns.Add(CreateGridColumn("X", "X", 62));
            grid.Columns.Add(CreateGridColumn("Y", "Y", 62));
            grid.Columns.Add(CreateGridColumn("Theta", "Theta", 65));
            grid.Columns.Add(CreateGridColumn("Row", "Row", 62));
            grid.Columns.Add(CreateGridColumn("Column", "Col", 62));
            grid.Columns.Add(CreateGridColumn("Score", "Score", 60));
            DataGridViewTextBoxColumn status = CreateGridColumn("Status", "状态", 65);
            status.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
            status.MinimumWidth = 58;
            grid.Columns.Add(status);
            return grid;
        }

        private static DataGridViewTextBoxColumn CreateGridColumn(string name, string header, int width)
        {
            return new DataGridViewTextBoxColumn
            {
                Name = name,
                HeaderText = header,
                Width = width,
                SortMode = DataGridViewColumnSortMode.NotSortable
            };
        }
    }
}
