using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using NinePointRotationCalibration.Halcon;
using NinePointRotationCalibration.Station.Controls;
using NinePointRotationCalibration.Station.Models;
using NinePointRotationCalibration.Station.Services.Inspection;

namespace NinePointRotationCalibration.Station.Views
{
    public partial class BlobDebugForm : Form
    {
        private readonly Bitmap _sourceImage;
        private readonly CalibrationJob _positioningJob;
        private readonly NinePointRotationCalibrationSdk _sdk;
        private readonly BoardBlobAnalyzer _blobAnalyzer;
        private readonly int _maximumMatches;
        private readonly SemaphoreSlim _analysisGate = new SemaphoreSlim(1, 1);
        private readonly CancellationTokenSource _lifetime = new CancellationTokenSource();
        private readonly object _previewCancellationSync = new object();
        private readonly object _runningTasksSync = new object();
        private readonly HashSet<Task> _runningTasks = new HashSet<Task>();

        private CancellationTokenSource _previewCancellation;
        private TemplateMatchCollectionResult _cachedMatches;
        private DetectionProfile _appliedProfile;
        private int _previewGeneration;
        private int _releaseStarted;
        private int _resourcesDisposed;
        private bool _loadingControls;
        private volatile bool _closing;

        public BlobDebugForm(
            Bitmap sourceImage,
            CalibrationJob positioningJob,
            DetectionProfile profile,
            int maximumMatches = 0)
        {
            if (sourceImage == null) throw new ArgumentNullException(nameof(sourceImage));

            _sourceImage = CloneTo24Bpp(sourceImage);
            _positioningJob = positioningJob == null ? null : positioningJob.DeepClone();
            _maximumMatches = Math.Max(0, maximumMatches);
            _appliedProfile = CloneProfile(profile);
            _sdk = new NinePointRotationCalibrationSdk();
            _blobAnalyzer = new BoardBlobAnalyzer();

            InitializeComponent();
            ApplyProfileToControls(_appliedProfile);
            WireEvents();
            sourceViewport.SetImage(_sourceImage);
            tabPreview.SelectedTab = tabBinaryImage;
            UpdateDependentControls();

            AcceptButton = btnApply;
            CancelButton = btnCancel;
            toolTip.SetToolTip(
                chkTemplateMatchingEnabled,
                "取消勾选后跳过定位模板，对整幅图直接执行二值化和 Blob 分析。");
            toolTip.SetToolTip(btnRefresh, "立即使用当前参数刷新预览。");
            toolTip.SetToolTip(btnFit, "使当前图像适应窗口。");
            toolTip.SetToolTip(btnActualPixels, "以一个图像像素对应一个屏幕像素显示。");
        }

        public DetectionProfile AppliedProfile
        {
            get { return CloneProfile(_appliedProfile); }
        }

        private void WireEvents()
        {
            Shown += BlobDebugForm_Shown;
            FormClosing += BlobDebugForm_FormClosing;
            KeyDown += BlobDebugForm_KeyDown;
            previewTimer.Tick += PreviewTimer_Tick;

            chkTemplateMatchingEnabled.CheckedChanged += ParameterControl_Changed;
            chkFollowTemplateTransform.CheckedChanged += ParameterControl_Changed;
            chkRectangleFilter.CheckedChanged += ParameterControl_Changed;
            nudBrightThreshold.ValueChanged += ParameterControl_Changed;
            nudMinimumBlobArea.ValueChanged += ParameterControl_Changed;
            nudMaximumBlobArea.ValueChanged += ParameterControl_Changed;
            nudRectangularity.ValueChanged += ParameterControl_Changed;
            nudMorphologyRadius.ValueChanged += ParameterControl_Changed;
            chkFillHoles.CheckedChanged += ParameterControl_Changed;
            chkAutoThreshold.CheckedChanged += ParameterControl_Changed;
            chkDarkForeground.CheckedChanged += ParameterControl_Changed;
            foreach (NumericUpDown input in new[]
            {
                nudBrightThreshold,
                nudMinimumBlobArea,
                nudMaximumBlobArea,
                nudRectangularity,
                nudMorphologyRadius
            })
            {
                input.MouseWheel += NumericInput_MouseWheel;
            }

            btnRefresh.Click += delegate { SchedulePreview(true); };
            btnFit.Click += delegate { GetActiveViewport().FitImage(); };
            btnActualPixels.Click += delegate { GetActiveViewport().ShowActualPixels(); };
            btnApply.Click += BtnApply_Click;
            tabPreview.SelectedIndexChanged += delegate { UpdatePreviewToolbar(); };
        }

        private void BlobDebugForm_Shown(object sender, EventArgs e)
        {
            ClampToWorkingArea();
            SchedulePreview(true);
        }

        private void BlobDebugForm_FormClosing(object sender, FormClosingEventArgs e)
        {
            _closing = true;
            previewTimer.Stop();
            CancelCurrentPreview();
        }

        private void BlobDebugForm_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode != Keys.F5) return;
            e.Handled = true;
            SchedulePreview(true);
        }

        private void ParameterControl_Changed(object sender, EventArgs e)
        {
            if (_loadingControls || _closing) return;
            UpdateDependentControls();
            SchedulePreview(false);
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

        private void PreviewTimer_Tick(object sender, EventArgs e)
        {
            previewTimer.Stop();
            StartPreview(_previewGeneration);
        }

        private void BtnApply_Click(object sender, EventArgs e)
        {
            DetectionProfile profile;
            string validationMessage;
            if (!TryReadProfile(out profile, out validationMessage))
            {
                SetPreviewStatus(validationMessage, true);
                return;
            }

            _appliedProfile = CloneProfile(profile);
            DialogResult = DialogResult.OK;
            Close();
        }

        private void SchedulePreview(bool immediate)
        {
            if (_closing || IsDisposed) return;

            previewTimer.Stop();
            _previewGeneration++;
            CancelCurrentPreview();

            if (immediate)
            {
                StartPreview(_previewGeneration);
            }
            else
            {
                SetPreviewStatus("参数已更改，等待更新", false);
                previewTimer.Start();
            }
        }

        private void StartPreview(int generation)
        {
            if (_closing || generation != _previewGeneration) return;

            DetectionProfile profile;
            string validationMessage;
            if (!TryReadProfile(out profile, out validationMessage))
            {
                SetPreviewStatus(validationMessage, true);
                return;
            }

            CancellationTokenSource cancellation =
                CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            lock (_previewCancellationSync)
            {
                if (_previewCancellation != null)
                {
                    try { _previewCancellation.Cancel(); }
                    catch (ObjectDisposedException) { }
                }
                _previewCancellation = cancellation;
            }

            SetPreviewStatus(
                profile.TemplateMatchingEnabled && _cachedMatches == null
                    ? "正在定位模板并生成二值预览..."
                    : "正在更新二值预览...",
                false);

            Task previewTask = RunPreviewAsync(profile, generation, cancellation);
            lock (_runningTasksSync) _runningTasks.Add(previewTask);
            previewTask.ContinueWith(
                completed =>
                {
                    lock (_runningTasksSync) _runningTasks.Remove(completed);
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        private async Task RunPreviewAsync(
            DetectionProfile profile,
            int generation,
            CancellationTokenSource cancellation)
        {
            PreviewPayload payload = null;
            Exception error = null;
            bool cancelled = false;
            try
            {
                payload = await Task.Run(
                    () => AnalyzePreview(profile, cancellation.Token),
                    cancellation.Token).ConfigureAwait(false);
                cancelled = cancellation.IsCancellationRequested;
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
            }
            catch (Exception exception)
            {
                cancelled = cancellation.IsCancellationRequested;
                if (!cancelled) error = exception;
            }
            finally
            {
                lock (_previewCancellationSync)
                {
                    if (ReferenceEquals(_previewCancellation, cancellation))
                        _previewCancellation = null;
                }
                cancellation.Dispose();
            }

            if (cancelled || _closing || generation != _previewGeneration)
            {
                if (payload != null) payload.Dispose();
                return;
            }

            PostPreviewCompletion(generation, payload, error);
        }

        private PreviewPayload AnalyzePreview(
            DetectionProfile profile,
            CancellationToken cancellationToken)
        {
            _analysisGate.Wait(cancellationToken);
            try
            {
                Stopwatch totalWatch = Stopwatch.StartNew();
                List<BlobAnalysisResult> analyses = new List<BlobAnalysisResult>();
                List<TemplateMatchResult> matches = new List<TemplateMatchResult>();
                try
                {
                    if (profile.TemplateMatchingEnabled)
                    {
                        TemplateMatchCollectionResult located = EnsureTemplateMatches(cancellationToken);
                        if (located.Matches != null)
                        {
                            matches.AddRange(located.Matches.Where(match => match != null && match.Success));
                        }
                        if (matches.Count == 0)
                            throw new InvalidOperationException("定位模板未匹配到目标。");

                        if (profile.FollowTemplateTransform &&
                            (_positioningJob == null || _positioningJob.Template == null ||
                             _positioningJob.Template.DetectionRoi == null))
                        {
                            throw new InvalidOperationException(
                                "定位模板尚未配置检测 ROI，请先在定位模板编辑器中创建检测区。");
                        }

                        List<DetectionRoi> rois = matches.Select(match =>
                            _blobAnalyzer.ResolveDetectionRoi(
                                _positioningJob.Template,
                                match,
                                profile)).ToList();
                        analyses.AddRange(_blobAnalyzer.AnalyzeRoisDetailed(
                            _sourceImage,
                            rois,
                            profile,
                            true,
                            cancellationToken));
                    }
                    else
                    {
                        TemplateDefinition template = _positioningJob == null
                            ? null
                            : _positioningJob.Template;
                        analyses.Add(_blobAnalyzer.AnalyzeDirectDetailed(
                            _sourceImage,
                            template,
                            profile,
                            true,
                            cancellationToken));
                    }

                    cancellationToken.ThrowIfCancellationRequested();
                    totalWatch.Stop();
                    return BuildPreviewPayload(
                        analyses,
                        matches,
                        profile.TemplateMatchingEnabled,
                        totalWatch.Elapsed.TotalMilliseconds,
                        cancellationToken);
                }
                finally
                {
                    foreach (BlobAnalysisResult analysis in analyses)
                    {
                        if (analysis != null) analysis.Dispose();
                    }
                }
            }
            finally
            {
                _analysisGate.Release();
            }
        }

        private TemplateMatchCollectionResult EnsureTemplateMatches(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_cachedMatches != null) return _cachedMatches;
            if (_positioningJob == null || _positioningJob.Template == null)
                throw new InvalidOperationException("未加载定位模板，无法启用模板匹配。");

            CalibrationJob locateJob = _positioningJob.DeepClone();
            if (locateJob.Template.MatchParameters != null)
                locateJob.Template.MatchParameters.NumMatches = _maximumMatches;

            TemplateMatchCollectionResult result = _sdk.LocateAll(_sourceImage, locateJob, false);
            if (result == null || !result.Success)
            {
                throw new InvalidOperationException(
                    result == null || string.IsNullOrWhiteSpace(result.Message)
                        ? "定位模板未返回有效结果。"
                        : result.Message);
            }

            // HALCON locating cannot be interrupted. Preserve a successful result even when
            // this preview became stale while LocateAll was running, so the next preview only
            // needs to rerun thresholding and Blob analysis.
            _cachedMatches = result;
            cancellationToken.ThrowIfCancellationRequested();
            return _cachedMatches;
        }

        private PreviewPayload BuildPreviewPayload(
            IList<BlobAnalysisResult> analyses,
            IList<TemplateMatchResult> matches,
            bool templateMatchingEnabled,
            double totalMilliseconds,
            CancellationToken cancellationToken)
        {
            PreviewPayload payload = new PreviewPayload();
            try
            {
                if (cancellationToken.IsCancellationRequested)
                    return null;
                payload.BinaryImage = ComposeBinaryImage(analyses, cancellationToken);
                if (payload.BinaryImage == null || cancellationToken.IsCancellationRequested)
                {
                    payload.Dispose();
                    return null;
                }
                payload.TemplateMatchingEnabled = templateMatchingEnabled;
                payload.MatchCount = matches == null ? 0 : matches.Count;
                payload.TotalMilliseconds = totalMilliseconds;

                int findingIndex = 1;
                foreach (BlobAnalysisResult analysis in analyses ?? new List<BlobAnalysisResult>())
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        payload.Dispose();
                        return null;
                    }
                    if (analysis == null) continue;
                    payload.ActivePixelCount += analysis.ActivePixelCount;
                    payload.EffectiveThresholds.Add(analysis.EffectiveThreshold);
                    payload.ImageConversionMilliseconds += analysis.ImageConversionElapsedMilliseconds;
                    payload.ThresholdMilliseconds += analysis.ThresholdElapsedMilliseconds;
                    payload.MorphologyMilliseconds += analysis.MorphologyElapsedMilliseconds;
                    payload.HoleFillMilliseconds += analysis.HoleFillElapsedMilliseconds;
                    payload.ComponentMilliseconds += analysis.ComponentElapsedMilliseconds;
                    AddRoiOverlay(payload, analysis.Roi);

                    foreach (BlobFinding finding in analysis.Findings ?? new List<BlobFinding>())
                    {
                        if (finding == null) continue;
                        payload.Rows.Add(new BlobRow
                        {
                            Index = findingIndex,
                            Column = finding.Column,
                            Row = finding.Row,
                            Area = finding.Area,
                            Rectangularity = finding.Rectangularity,
                            RejectedAsRectangle = finding.RejectedAsRectangle
                        });
                        if (!finding.RejectedAsRectangle && finding.Contour != null && finding.Contour.Count > 1)
                        {
                            payload.Contours.Add(new ViewportContourOverlay
                            {
                                Points = finding.Contour.ToList(),
                                Color = Color.LimeGreen,
                                LineWidth = 2.0F
                            });
                        }
                        if (finding.RejectedAsRectangle) payload.FilteredCount++;
                        else payload.AcceptedCount++;
                        findingIndex++;
                    }
                }
                return payload;
            }
            catch (OperationCanceledException)
            {
                payload.Dispose();
                return null;
            }
            catch
            {
                payload.Dispose();
                throw;
            }
        }

        private Bitmap ComposeBinaryImage(
            IEnumerable<BlobAnalysisResult> analyses,
            CancellationToken cancellationToken)
        {
            Bitmap combined = new Bitmap(_sourceImage);
            try
            {
                using (Graphics graphics = Graphics.FromImage(combined))
                {
                    graphics.CompositingMode = CompositingMode.SourceCopy;
                    graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
                    graphics.PixelOffsetMode = PixelOffsetMode.Half;

                    foreach (BlobAnalysisResult analysis in analyses ?? Enumerable.Empty<BlobAnalysisResult>())
                    {
                        if (cancellationToken.IsCancellationRequested)
                        {
                            combined.Dispose();
                            return null;
                        }
                        if (analysis == null || analysis.BinaryImage == null ||
                            analysis.ImageBounds.Width <= 0 || analysis.ImageBounds.Height <= 0)
                        {
                            continue;
                        }

                        GraphicsState state = graphics.Save();
                        try
                        {
                            using (GraphicsPath clip = CreateRoiClipPath(analysis.Roi))
                            {
                                graphics.SetClip(clip, CombineMode.Replace);
                                graphics.DrawImage(
                                    analysis.BinaryImage,
                                    analysis.ImageBounds,
                                    0,
                                    0,
                                    analysis.BinaryImage.Width,
                                    analysis.BinaryImage.Height,
                                    GraphicsUnit.Pixel);
                            }
                        }
                        finally
                        {
                            graphics.Restore(state);
                        }
                    }
                }
                return combined;
            }
            catch
            {
                combined.Dispose();
                throw;
            }
        }

        private static GraphicsPath CreateRoiClipPath(DetectionRoi roi)
        {
            GraphicsPath path = new GraphicsPath();
            if (roi == null || roi.HalfWidth <= 0.0 || roi.HalfHeight <= 0.0)
            {
                path.AddRectangle(RectangleF.Empty);
                return path;
            }

            double radians = roi.PhiDegrees * Math.PI / 180.0;
            double cosine = Math.Cos(radians);
            double sine = Math.Sin(radians);
            PointF[] corners = new PointF[4];
            double[] x = { -roi.HalfWidth, roi.HalfWidth, roi.HalfWidth, -roi.HalfWidth };
            double[] y = { -roi.HalfHeight, -roi.HalfHeight, roi.HalfHeight, roi.HalfHeight };
            for (int index = 0; index < corners.Length; index++)
            {
                corners[index] = new PointF(
                    (float)(roi.ReferenceCenterColumn + (x[index] * cosine) - (y[index] * sine)),
                    (float)(roi.ReferenceCenterRow + (x[index] * sine) + (y[index] * cosine)));
            }
            path.AddPolygon(corners);
            return path;
        }

        private static void AddRoiOverlay(PreviewPayload payload, DetectionRoi roi)
        {
            if (roi == null || roi.HalfWidth <= 0.0 || roi.HalfHeight <= 0.0) return;
            payload.Regions.Add(new ViewportRoiOverlay
            {
                ImageBounds = new RectangleF(
                    (float)(roi.ReferenceCenterColumn - roi.HalfWidth),
                    (float)(roi.ReferenceCenterRow - roi.HalfHeight),
                    (float)(roi.HalfWidth * 2.0),
                    (float)(roi.HalfHeight * 2.0)),
                AngleDegrees = (float)roi.PhiDegrees,
                Label = "检测 ROI",
                Color = Color.DeepSkyBlue,
                LineWidth = 2.0F
            });
        }

        private void PostPreviewCompletion(int generation, PreviewPayload payload, Exception error)
        {
            if (_closing || IsDisposed || !IsHandleCreated)
            {
                if (payload != null) payload.Dispose();
                return;
            }

            try
            {
                BeginInvoke(new MethodInvoker(delegate
                {
                    if (_closing || IsDisposed || generation != _previewGeneration)
                    {
                        if (payload != null) payload.Dispose();
                        return;
                    }

                    if (error != null)
                    {
                        if (payload != null) payload.Dispose();
                        ApplyPreviewError(error.Message);
                        return;
                    }

                    ApplyPreviewPayload(payload);
                }));
            }
            catch (ObjectDisposedException)
            {
                if (payload != null) payload.Dispose();
            }
            catch (InvalidOperationException)
            {
                if (payload != null) payload.Dispose();
            }
        }

        private void ApplyPreviewPayload(PreviewPayload payload)
        {
            if (payload == null)
            {
                ApplyPreviewError("分析未返回结果。");
                return;
            }

            Bitmap binaryImage = payload.TakeBinaryImage();
            binaryViewport.SetImageOwned(binaryImage);
            sourceViewport.SetPointOverlays(null);
            sourceViewport.SetRoiOverlays(payload.Regions);
            sourceViewport.SetContourOverlays(payload.Contours);
            binaryViewport.SetPointOverlays(null);
            binaryViewport.SetRoiOverlays(null);
            binaryViewport.SetContourOverlays(payload.Contours);

            dgvBlobs.Rows.Clear();
            foreach (BlobRow item in payload.Rows)
            {
                int index = dgvBlobs.Rows.Add(
                    item.Index,
                    item.Column.ToString("0.0", CultureInfo.InvariantCulture),
                    item.Row.ToString("0.0", CultureInfo.InvariantCulture),
                    item.Area.ToString("0", CultureInfo.InvariantCulture),
                    item.Rectangularity.ToString("0.000", CultureInfo.InvariantCulture),
                    item.RejectedAsRectangle ? "矩形过滤" : "保留");
                dgvBlobs.Rows[index].DefaultCellStyle.ForeColor = item.RejectedAsRectangle
                    ? Color.FromArgb(116, 122, 129)
                    : Color.FromArgb(178, 55, 55);
            }

            lblModeStatus.Text = payload.TemplateMatchingEnabled
                ? "模板匹配：" + payload.MatchCount + " 个目标，检测 ROI 已变换"
                : "直接二值化：整幅图像";
            lblModeStatus.Text += chkDarkForeground.Checked ? "  /  白底黑点" : "  /  黑底白点";
            lblModeStatus.Text += "  /  ROI 外保留原图";
            lblSummary.Text = string.Format(
                CultureInfo.InvariantCulture,
                "保留 {0}  /  矩形过滤 {1}  /  目标像素 {2:N0}",
                payload.AcceptedCount,
                payload.FilteredCount,
                payload.ActivePixelCount);
            string thresholds = payload.EffectiveThresholds.Count == 0
                ? "--"
                : string.Join("/", payload.EffectiveThresholds.OrderBy(value => value));
            SetPreviewStatus(string.Format(
                CultureInfo.InvariantCulture,
                "预览 {0:0} ms  |  图转 {2:0}  阈值 {3:0}  形态 {4:0}  填孔 {5:0}  连通 {6:0}  |  T {1}",
                payload.TotalMilliseconds,
                thresholds,
                payload.ImageConversionMilliseconds,
                payload.ThresholdMilliseconds,
                payload.MorphologyMilliseconds,
                payload.HoleFillMilliseconds,
                payload.ComponentMilliseconds), false);
            payload.Dispose();
        }

        private void ApplyPreviewError(string message)
        {
            binaryViewport.ClearImage();
            binaryViewport.ClearOverlays();
            sourceViewport.ClearOverlays();
            dgvBlobs.Rows.Clear();
            lblSummary.Text = "Blob --";
            SetPreviewStatus(string.IsNullOrWhiteSpace(message) ? "分析失败" : message, true);
        }

        private void ApplyProfileToControls(DetectionProfile profile)
        {
            DetectionProfile value = profile ?? new DetectionProfile();
            _loadingControls = true;
            try
            {
                chkTemplateMatchingEnabled.Checked = value.TemplateMatchingEnabled;
                chkFollowTemplateTransform.Checked = value.FollowTemplateTransform;
                chkRectangleFilter.Checked = value.RectangleFilterEnabled;
                chkFillHoles.Checked = value.FillHoles;
                chkAutoThreshold.Checked = value.AutoThreshold;
                chkDarkForeground.Checked = value.DetectDarkForeground;
                SetNumericValue(nudBrightThreshold, value.BrightThreshold);
                SetNumericValue(nudMinimumBlobArea, value.MinimumBlobArea);
                SetNumericValue(nudMaximumBlobArea, value.MaximumBlobArea);
                SetNumericValue(nudRectangularity, value.RectangleFillThreshold);
                SetNumericValue(nudMorphologyRadius, value.MorphologyRadius);
            }
            finally
            {
                _loadingControls = false;
            }
        }

        private bool TryReadProfile(out DetectionProfile profile, out string validationMessage)
        {
            profile = null;
            validationMessage = string.Empty;
            if (nudMinimumBlobArea.Value > nudMaximumBlobArea.Value)
            {
                validationMessage = "最小面积不能大于最大面积。";
                return false;
            }

            profile = new DetectionProfile
            {
                Roi = CloneRoi(_appliedProfile == null ? null : _appliedProfile.Roi),
                TemplateMatchingEnabled = chkTemplateMatchingEnabled.Checked,
                FollowTemplateTransform = chkFollowTemplateTransform.Checked,
                AutoThreshold = chkAutoThreshold.Checked,
                DetectDarkForeground = chkDarkForeground.Checked,
                BrightThreshold = Decimal.ToInt32(nudBrightThreshold.Value),
                MinimumBlobArea = Decimal.ToDouble(nudMinimumBlobArea.Value),
                MaximumBlobArea = Decimal.ToDouble(nudMaximumBlobArea.Value),
                RectangleFilterEnabled = chkRectangleFilter.Checked,
                RectangleFillThreshold = Decimal.ToDouble(nudRectangularity.Value),
                FillHoles = chkFillHoles.Checked,
                MorphologyRadius = Decimal.ToInt32(nudMorphologyRadius.Value)
            };
            return true;
        }

        private void UpdateDependentControls()
        {
            chkFollowTemplateTransform.Enabled = chkTemplateMatchingEnabled.Checked;
            nudRectangularity.Enabled = chkRectangleFilter.Checked;
            nudBrightThreshold.Enabled = !chkAutoThreshold.Checked;
            string polarity = chkDarkForeground.Checked ? "白底黑点" : "黑底白点";
            lblModeStatus.Text = (chkTemplateMatchingEnabled.Checked
                ? "模板匹配 + 检测 ROI"
                : "关闭模板匹配，整图直接二值化") + "  /  " + polarity;
        }

        private void UpdatePreviewToolbar()
        {
            ImageViewport active = GetActiveViewport();
            btnFit.Enabled = active != null && active.HasImage;
            btnActualPixels.Enabled = active != null && active.HasImage;
        }

        private ImageViewport GetActiveViewport()
        {
            return tabPreview.SelectedTab == tabSourceImage ? sourceViewport : binaryViewport;
        }

        private void SetPreviewStatus(string text, bool isError)
        {
            lblPreviewStatus.Text = text ?? string.Empty;
            lblPreviewStatus.ForeColor = isError
                ? Color.FromArgb(255, 154, 154)
                : Color.FromArgb(205, 211, 218);
            UpdatePreviewToolbar();
        }

        private void CancelCurrentPreview()
        {
            lock (_previewCancellationSync)
            {
                if (_previewCancellation == null) return;
                try { _previewCancellation.Cancel(); }
                catch (ObjectDisposedException) { }
            }
        }

        private void ClampToWorkingArea()
        {
            Rectangle workingArea = Screen.FromControl(this).WorkingArea;
            MinimumSize = new Size(
                Math.Max(1, Math.Min(MinimumSize.Width, workingArea.Width)),
                Math.Max(1, Math.Min(MinimumSize.Height, workingArea.Height)));
            int width = Math.Max(MinimumSize.Width, Math.Min(Width, workingArea.Width));
            int height = Math.Max(MinimumSize.Height, Math.Min(Height, workingArea.Height));
            Bounds = new Rectangle(
                workingArea.Left + Math.Max(0, (workingArea.Width - width) / 2),
                workingArea.Top + Math.Max(0, (workingArea.Height - height) / 2),
                width,
                height);
        }

        private static void SetNumericValue(NumericUpDown control, double value)
        {
            decimal converted;
            try { converted = Convert.ToDecimal(value, CultureInfo.InvariantCulture); }
            catch (OverflowException) { converted = control.Minimum; }
            converted = Math.Max(control.Minimum, Math.Min(control.Maximum, converted));
            control.Value = converted;
        }

        private static Bitmap CloneTo24Bpp(Bitmap source)
        {
            Bitmap result = new Bitmap(
                Math.Max(1, source.Width),
                Math.Max(1, source.Height),
                PixelFormat.Format24bppRgb);
            using (Graphics graphics = Graphics.FromImage(result))
                graphics.DrawImageUnscaled(source, 0, 0);
            return result;
        }

        private static DetectionProfile CloneProfile(DetectionProfile source)
        {
            DetectionProfile value = source ?? new DetectionProfile();
            return new DetectionProfile
            {
                Roi = CloneRoi(value.Roi),
                TemplateMatchingEnabled = value.TemplateMatchingEnabled,
                FollowTemplateTransform = value.FollowTemplateTransform,
                AutoThreshold = value.AutoThreshold,
                DetectDarkForeground = value.DetectDarkForeground,
                BrightThreshold = value.BrightThreshold,
                MinimumBlobArea = value.MinimumBlobArea,
                MaximumBlobArea = value.MaximumBlobArea,
                RectangleFilterEnabled = value.RectangleFilterEnabled,
                RectangleFillThreshold = value.RectangleFillThreshold,
                FillHoles = value.FillHoles,
                MorphologyRadius = value.MorphologyRadius
            };
        }

        private static DetectionRoi CloneRoi(DetectionRoi source)
        {
            if (source == null) return null;
            return new DetectionRoi
            {
                ReferenceCenterRow = source.ReferenceCenterRow,
                ReferenceCenterColumn = source.ReferenceCenterColumn,
                PhiDegrees = source.PhiDegrees,
                HalfWidth = source.HalfWidth,
                HalfHeight = source.HalfHeight
            };
        }

        private void ReleaseAnalysisResources()
        {
            if (Interlocked.Exchange(ref _releaseStarted, 1) != 0) return;
            _closing = true;
            if (previewTimer != null) previewTimer.Stop();
            CancelCurrentPreview();
            try { _lifetime.Cancel(); }
            catch (ObjectDisposedException) { }

            Task[] running;
            lock (_runningTasksSync) running = _runningTasks.ToArray();
            if (running.Length == 0)
            {
                DisposeAnalysisResources();
                return;
            }

            Task.WhenAll(running).ContinueWith(
                ignored => DisposeAnalysisResources(),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        private void DisposeAnalysisResources()
        {
            if (Interlocked.Exchange(ref _resourcesDisposed, 1) != 0) return;
            _sdk.Dispose();
            _sourceImage.Dispose();
            _analysisGate.Dispose();
            _lifetime.Dispose();
        }

        private sealed class PreviewPayload : IDisposable
        {
            public PreviewPayload()
            {
                Regions = new List<ViewportRoiOverlay>();
                Contours = new List<ViewportContourOverlay>();
                Rows = new List<BlobRow>();
                EffectiveThresholds = new HashSet<int>();
            }

            public Bitmap BinaryImage { get; set; }
            public List<ViewportRoiOverlay> Regions { get; private set; }
            public List<ViewportContourOverlay> Contours { get; private set; }
            public List<BlobRow> Rows { get; private set; }
            public HashSet<int> EffectiveThresholds { get; private set; }
            public bool TemplateMatchingEnabled { get; set; }
            public int MatchCount { get; set; }
            public int AcceptedCount { get; set; }
            public int FilteredCount { get; set; }
            public long ActivePixelCount { get; set; }
            public double TotalMilliseconds { get; set; }
            public double ImageConversionMilliseconds { get; set; }
            public double ThresholdMilliseconds { get; set; }
            public double MorphologyMilliseconds { get; set; }
            public double HoleFillMilliseconds { get; set; }
            public double ComponentMilliseconds { get; set; }

            public Bitmap TakeBinaryImage()
            {
                Bitmap value = BinaryImage;
                BinaryImage = null;
                return value;
            }

            public void Dispose()
            {
                Bitmap image = BinaryImage;
                BinaryImage = null;
                if (image != null) image.Dispose();
            }
        }

        private sealed class BlobRow
        {
            public int Index { get; set; }
            public double Column { get; set; }
            public double Row { get; set; }
            public double Area { get; set; }
            public double Rectangularity { get; set; }
            public bool RejectedAsRectangle { get; set; }
        }
    }
}
