using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using NinePointRotationCalibration.Halcon;
using NinePointRotationCalibration.Station.Controls;
using NinePointRotationCalibration.Station.Models;
using NinePointRotationCalibration.Station.Services;
using NinePointRotationCalibration.Station.Services.Inspection;

namespace NinePointRotationCalibration.Station.Views
{
    /// <summary>主窗口图像显示状态管理：实时预览、锁定帧及主画面局部二值预览。</summary>
    public sealed partial class MainFormRuntimeBinder
    {
        private bool IsBinaryPreviewEnabled()
        {
            CheckBox toggle = FindControl<CheckBox>("chkBinaryPreview");
            return toggle != null && toggle.Checked;
        }

        private void RenderDetectionDualViewOnTabEnter()
        {
            Bitmap sourceCopy;
            bool isLive;
            bool hasCurrentOverlay;
            List<ViewportRoiOverlay> cachedRois;
            List<ViewportContourOverlay> cachedContours;
            List<ViewportContourOverlay> matchedContours;
            List<int> boardIndices;
            int selectedBoard;
            lock (_displaySync)
            {
                isLive = _displayMode == DisplayMode.LivePreview;
                sourceCopy = _displayMode == DisplayMode.LockedFrame && _lockedDisplayImage != null
                    ? new Bitmap(_lockedDisplayImage)
                    : null;
                hasCurrentOverlay = _lastInspectionOverlayGeneration == _displayGeneration &&
                                    _lastDetectionRegions.Count > 0;
                cachedRois = hasCurrentOverlay ? CloneRoiList(_lastInspectionRois) : new List<ViewportRoiOverlay>();
                cachedContours = hasCurrentOverlay ? CloneContourList(_lastInspectionContours) : new List<ViewportContourOverlay>();
                matchedContours = hasCurrentOverlay ? CloneContourList(_lastMatchedBoardContours) : new List<ViewportContourOverlay>();
                boardIndices = hasCurrentOverlay ? new List<int>(_lastInspectionBoardIndices) : new List<int>();
                selectedBoard = _selectedInspectionBoardIndex;
            }

            if (sourceCopy == null)
            {
                _view.EffectViewport.ClearImage();
                _view.EffectViewport.ClearOverlays();
                _view.SetEffectViewportInformation(isLive ? "实时预览中   等待抓拍或点击检测" : "等待拍照或导入图像");
                return;
            }

            // 先秒速同步显示当前帧，避免切页瞬间底部视口空白，随后异步刷新最新分割效果与叠加层
            _view.EffectViewport.SetImageOwned(sourceCopy);
            if (hasCurrentOverlay)
            {
                List<ViewportContourOverlay> combinedContours = new List<ViewportContourOverlay>(matchedContours.Count + cachedContours.Count);
                combinedContours.AddRange(matchedContours);
                combinedContours.AddRange(cachedContours);
                _view.EffectViewport.SetPointOverlays(null);
                _view.EffectViewport.SetContourOverlays(combinedContours);
                _view.EffectViewport.SetRoiOverlays(ApplySelectedRoiHighlight(cachedRois, boardIndices, selectedBoard));
            }
            else
            {
                _view.EffectViewport.ClearOverlays();
            }
            SyncEffectViewportViewDeferred();
            _view.SetEffectViewportInformation("检测效果图刷新中...");
            ScheduleBinaryPreview(false);
        }

        private void RestoreSingleViewportOverlays()
        {
            List<ViewportContourOverlay> contours;
            lock (_displaySync)
            {
                bool current = _displayMode == DisplayMode.LockedFrame &&
                               _lastInspectionOverlayGeneration == _displayGeneration;
                contours = current ? CloneContourList(_lastInspectionContours) : new List<ViewportContourOverlay>();
            }

            _view.Viewport.SetPointOverlays(null);
            _view.Viewport.SetRoiOverlays(null);
            _view.Viewport.SetContourOverlays(contours);
        }

        private void HighlightSelectedDetectionRow()
        {
            DataGridView grid = _view.DetectionResultGrid;
            int boardIndex = -1;
            if (grid != null && grid.SelectedRows.Count > 0)
            {
                object raw = grid.SelectedRows[0].Cells[0].Value;
                int parsed;
                if (raw != null && int.TryParse(Convert.ToString(raw, CultureInfo.InvariantCulture), out parsed))
                {
                    boardIndex = parsed;
                }
            }

            List<ViewportRoiOverlay> rois;
            List<int> indices;
            bool hasCurrent;
            lock (_displaySync)
            {
                _selectedInspectionBoardIndex = boardIndex;
                hasCurrent = _lastInspectionOverlayGeneration == _displayGeneration && _lastInspectionRois.Count > 0;
                rois = hasCurrent ? CloneRoiList(_lastInspectionRois) : new List<ViewportRoiOverlay>();
                indices = hasCurrent ? new List<int>(_lastInspectionBoardIndices) : new List<int>();
            }

            if (!hasCurrent) return;
            _view.EffectViewport.SetRoiOverlays(ApplySelectedRoiHighlight(rois, indices, boardIndex));
        }

        private void TriggerEffectPreviewOnUi(bool debounce)
        {
            if (_disposed || !_view.IsDetectionDualViewActive) return;
            if (_view.IsHandleCreated && _view.InvokeRequired)
            {
                try
                {
                    _view.BeginInvoke(new MethodInvoker(delegate { ScheduleBinaryPreview(debounce); }));
                }
                catch (ObjectDisposedException)
                {
                }
                catch (InvalidOperationException)
                {
                }
                return;
            }
            ScheduleBinaryPreview(debounce);
        }

        private async void ScheduleBinaryPreview(bool debounce)
        {
            if (_disposed || !_view.IsDetectionDualViewActive) return;

            // 取消上一次尚未完成的效果图计算，并记录当前 _displayGeneration 防止旧帧覆盖新锁定图像
            CancellationTokenSource cancellation =
                CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            lock (_binaryPreviewSync)
            {
                if (_binaryPreviewCancellation != null) _binaryPreviewCancellation.Cancel();
                _binaryPreviewCancellation = cancellation;
            }

            Bitmap source = null;
            List<DetectionRoi> regions;
            List<ViewportContourOverlay> matchedContours;
            List<int> boardIndices;
            int selectedBoardIndex;
            long generation;
            lock (_displaySync)
            {
                generation = _displayGeneration;
                if (_lockedDisplayImage != null) source = new Bitmap(_lockedDisplayImage);
                bool current = _lastInspectionOverlayGeneration == generation;
                regions = current
                    ? _lastDetectionRegions.Select(CloneDetectionRoi).ToList()
                    : new List<DetectionRoi>();
                matchedContours = current
                    ? CloneContourList(_lastMatchedBoardContours)
                    : new List<ViewportContourOverlay>();
                boardIndices = current
                    ? new List<int>(_lastInspectionBoardIndices)
                    : new List<int>();
                selectedBoardIndex = _selectedInspectionBoardIndex;
            }

            if (source == null)
            {
                lock (_binaryPreviewSync)
                    if (ReferenceEquals(_binaryPreviewCancellation, cancellation))
                        _binaryPreviewCancellation = null;
                cancellation.Dispose();
                _view.EffectViewport.ClearImage();
                _view.EffectViewport.ClearOverlays();
                _view.SetEffectViewportInformation(_displayMode == DisplayMode.LivePreview ? "实时预览中" : "等待拍照或导入图像");
                _view.SetOperationStatus("效果图预览需要已拍摄或已导入的图像", false, true);
                return;
            }

            try
            {
                // 连续拖动/微调参数时先进行 25ms 合并窗口防抖，不向 Task.Delay 传 CancellationToken 以避免调试器首次概率异常中断
                if (debounce)
                {
                    await Task.Delay(25).ConfigureAwait(true);
                    if (cancellation.IsCancellationRequested || _disposed || !_view.IsDetectionDualViewActive)
                        return;
                }

                DetectionProfile profile = ReadDetectionProfileFromView();
                bool binaryEnabled = IsBinaryPreviewEnabled();
                if (profile.TemplateMatchingEnabled && regions.Count == 0)
                {
                    CalibrationJob job = _runtime.GetPositioningJobSnapshot();
                    bool alreadyInspectedCurrentFrame;
                    lock (_displaySync) alreadyInspectedCurrentFrame = _lastInspectionCompletedGeneration == generation;
                    if (alreadyInspectedCurrentFrame || job == null || job.Template == null || !job.Template.HasUsableModel)
                    {
                        bool stillCurrent;
                        lock (_displaySync) stillCurrent = generation == _displayGeneration;
                        if (stillCurrent && !cancellation.IsCancellationRequested && _view.IsDetectionDualViewActive)
                        {
                            _view.EffectViewport.SetImageOwned(source);
                            source = null;
                            _view.EffectViewport.ClearOverlays();
                            SyncEffectViewportViewDeferred();
                            _view.SetEffectViewportInformation(job == null || job.Template == null || !job.Template.HasUsableModel
                                ? "尚未加载定位模板 · 当前显示原图"
                                : "未匹配到目标板 · 当前显示原图");
                        }
                        return;
                    }

                    _view.SetEffectViewportInformation("正在定位检测区域...");
                    _view.SetOperationStatus("正在定位检测 ROI...", true, false);
                    int maximumMatches = Decimal.ToInt32(_view.ExpectedBoardCountInput.Value) + 1;
                    TemplateMatchCollectionResult matchResult = await Task
                        .Run(() => _runtime.LocatePositioningForPreview(source, maximumMatches))
                        .ConfigureAwait(true);
                    if (cancellation.IsCancellationRequested || _disposed || !_view.IsDetectionDualViewActive)
                        return;

                    if (matchResult != null && matchResult.Success)
                    {
                        List<TemplateMatchResult> validMatches = (matchResult.Matches ?? new List<TemplateMatchResult>())
                            .Where(match => match != null && match.Success)
                            .ToList();
                        regions = validMatches
                            .Select(match => _binaryAnalyzer.ResolveDetectionRoi(job.Template, match, profile))
                            .Where(region => region != null)
                            .ToList();
                        matchedContours = BuildMatchedContours(validMatches);
                        boardIndices = Enumerable.Range(1, regions.Count).ToList();
                    }

                    if (regions.Count == 0)
                    {
                        bool stillCurrent;
                        lock (_displaySync) stillCurrent = generation == _displayGeneration;
                        if (stillCurrent && !cancellation.IsCancellationRequested && _view.IsDetectionDualViewActive)
                        {
                            _view.EffectViewport.SetImageOwned(source);
                            source = null;
                            _view.EffectViewport.ClearOverlays();
                            SyncEffectViewportViewDeferred();
                            _view.SetEffectViewportInformation("未匹配到模板目标 · 当前显示原图");
                            _view.SetOperationStatus("当前图像未匹配到定位模板目标", false, true);
                        }
                        return;
                    }

                    lock (_displaySync)
                    {
                        if (generation == _displayGeneration)
                        {
                            _lastDetectionRegions = regions.Select(CloneDetectionRoi).ToList();
                            _lastMatchedBoardContours = CloneContourList(matchedContours);
                            _lastInspectionBoardIndices = new List<int>(boardIndices);
                            _lastInspectionOverlayGeneration = generation;
                        }
                    }
                }

                EffectPreviewPayload preview = await Task.Run(
                    () => BuildEffectPreview(
                        source,
                        regions,
                        boardIndices,
                        matchedContours,
                        profile,
                        binaryEnabled,
                        selectedBoardIndex,
                        cancellation.Token)).ConfigureAwait(true);
                source.Dispose();
                source = null;

                bool current;
                lock (_displaySync) current = generation == _displayGeneration;
                if (preview == null || !current || cancellation.IsCancellationRequested || !_view.IsDetectionDualViewActive)
                {
                    if (preview != null) preview.Dispose();
                    return;
                }
                if (!string.IsNullOrWhiteSpace(preview.ErrorMessage))
                {
                    _view.SetEffectViewportInformation("检测效果图更新失败");
                    _view.SetOperationStatus("检测效果图更新失败：" + preview.ErrorMessage, false, true);
                    preview.Dispose();
                    return;
                }

                lock (_displaySync)
                {
                    if (generation == _displayGeneration)
                    {
                        _lastInspectionRois = CloneRoiList(preview.Rois);
                        _lastInspectionContours = CloneContourList(preview.BlobContours);
                        _lastInspectionOverlayGeneration = generation;
                    }
                }

                _view.EffectViewport.SetImageOwned(preview.TakeImage());
                _view.EffectViewport.SetPointOverlays(null);
                _view.EffectViewport.SetContourOverlays(preview.EffectContours);
                _view.EffectViewport.SetRoiOverlays(ApplySelectedRoiHighlight(
                    preview.Rois,
                    boardIndices,
                    selectedBoardIndex));
                SyncEffectViewportViewDeferred();
                _view.SetEffectViewportInformation(BuildEffectInfoText(profile, binaryEnabled, preview.BlobCount));
                _view.SetOperationStatus(binaryEnabled ? "检测效果图（局部二值）已更新" : "检测效果图（原图叠加）已更新", false, false);
                preview.Dispose();
            }
            catch (Exception exception)
            {
                if (!_disposed && !cancellation.IsCancellationRequested)
                {
                    _view.SetEffectViewportInformation("检测效果图更新失败");
                    _view.SetOperationStatus("检测效果图更新失败：" + exception.Message, false, true);
                }
            }
            finally
            {
                if (source != null) source.Dispose();
                lock (_binaryPreviewSync)
                {
                    if (ReferenceEquals(_binaryPreviewCancellation, cancellation))
                        _binaryPreviewCancellation = null;
                }
                cancellation.Dispose();
            }
        }

        private EffectPreviewPayload BuildEffectPreview(
            Bitmap source,
            IList<DetectionRoi> regions,
            IList<int> boardIndices,
            IList<ViewportContourOverlay> matchedContours,
            DetectionProfile profile,
            bool binaryEnabled,
            int selectedBoardIndex,
            CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested) return null;

            EffectPreviewPayload payload = new EffectPreviewPayload();
            Bitmap combined = new Bitmap(source);
            payload.Image = combined;
            try
            {
                IList<DetectionRoi> targetList = regions ?? new List<DetectionRoi>();
                bool hasExplicitRegions = targetList.Count > 0;
                IEnumerable<DetectionRoi> targets = hasExplicitRegions
                    ? (IEnumerable<DetectionRoi>)targetList
                    : new[]
                    {
                        new DetectionRoi
                        {
                            ReferenceCenterRow = source.Height / 2.0,
                            ReferenceCenterColumn = source.Width / 2.0,
                            HalfWidth = source.Width / 2.0,
                            HalfHeight = source.Height / 2.0
                        }
                    };

                if (matchedContours != null && matchedContours.Count > 0)
                {
                    payload.EffectContours.AddRange(CloneContourList(matchedContours));
                }

                using (Graphics graphics = Graphics.FromImage(combined))
                {
                    graphics.CompositingMode = CompositingMode.SourceCopy;
                    graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
                    graphics.PixelOffsetMode = PixelOffsetMode.Half;

                    int index = 0;
                    foreach (DetectionRoi roi in targets)
                    {
                        if (cancellationToken.IsCancellationRequested)
                        {
                            payload.Dispose();
                            return null;
                        }
                        bool roiHasDefect = false;
                        List<BlobAnalysisResult> analyses = _binaryAnalyzer.AnalyzeRoisDetailedNoThrow(
                            source, new[] { roi }, profile, binaryEnabled, cancellationToken);
                        if (cancellationToken.IsCancellationRequested)
                        {
                            foreach (BlobAnalysisResult item in analyses)
                            {
                                if (item != null) item.Dispose();
                            }
                            payload.Dispose();
                            return null;
                        }
                        using (BlobAnalysisResult analysis = analyses.Count > 0 ? analyses[0] : new BlobAnalysisResult { Roi = roi })
                        {
                            if (binaryEnabled &&
                                analysis.BinaryImage != null &&
                                analysis.ImageBounds.Width > 0 &&
                                analysis.ImageBounds.Height > 0)
                            {
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

                            foreach (BlobFinding finding in analysis.Findings ?? new List<BlobFinding>())
                            {
                                if (finding == null || finding.RejectedAsRectangle ||
                                    finding.Contour == null || finding.Contour.Count < 2) continue;
                                roiHasDefect = true;
                                payload.BlobCount++;
                                ViewportContourOverlay contour = new ViewportContourOverlay
                                {
                                    Points = finding.Contour.ToList(),
                                    Color = Color.LimeGreen,
                                    LineWidth = 2.0F
                                };
                                payload.BlobContours.Add(contour);
                                payload.EffectContours.Add(contour);
                            }
                        }

                        if (hasExplicitRegions && roi != null)
                        {
                            int boardNumber = boardIndices != null && index < boardIndices.Count
                                ? boardIndices[index]
                                : index + 1;
                            bool selected = selectedBoardIndex > 0 && boardNumber == selectedBoardIndex;
                            payload.Rois.Add(new ViewportRoiOverlay
                            {
                                ImageBounds = new RectangleF(
                                    (float)(roi.ReferenceCenterColumn - roi.HalfWidth),
                                    (float)(roi.ReferenceCenterRow - roi.HalfHeight),
                                    (float)(roi.HalfWidth * 2.0),
                                    (float)(roi.HalfHeight * 2.0)),
                                AngleDegrees = (float)roi.PhiDegrees,
                                Label = string.Format(
                                    CultureInfo.InvariantCulture,
                                    "{0}板 {1} {2}",
                                    selected ? "★ " : string.Empty,
                                    boardNumber,
                                    roiHasDefect ? "NG" : "OK"),
                                Color = selected
                                    ? Color.Gold
                                    : (roiHasDefect ? Color.OrangeRed : Color.DeepSkyBlue),
                                LineWidth = selected ? 3.0F : 2.0F
                            });
                        }

                        index++;
                    }
                }
                return payload;
            }
            catch (Exception exception)
            {
                payload.Dispose();
                if (cancellationToken.IsCancellationRequested) return null;
                return new EffectPreviewPayload { ErrorMessage = exception.Message };
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

        private static List<ViewportContourOverlay> BuildMatchedContours(IList<TemplateMatchResult> matches)
        {
            List<ViewportContourOverlay> contours = new List<ViewportContourOverlay>();
            if (matches == null) return contours;

            foreach (TemplateMatchResult match in matches)
            {
                if (match == null || !match.Success) continue;
                if (match.ContourSegments != null && match.ContourSegments.Count > 0)
                {
                    foreach (List<ImagePoint> segment in match.ContourSegments)
                    {
                        if (segment == null || segment.Count < 2) continue;
                        contours.Add(new ViewportContourOverlay
                        {
                            Points = segment.Select(p => new PointF((float)p.Column, (float)p.Row)).ToList(),
                            Color = Color.FromArgb(190, 0, 210, 255),
                            LineWidth = 1.25F
                        });
                    }
                }
                else if (match.Contour != null && match.Contour.Count >= 2)
                {
                    contours.Add(new ViewportContourOverlay
                    {
                        Points = match.Contour.Select(p => new PointF((float)p.Column, (float)p.Row)).ToList(),
                        Color = Color.FromArgb(190, 0, 210, 255),
                        LineWidth = 1.25F
                    });
                }
            }
            return contours;
        }

        private static List<ViewportRoiOverlay> ApplySelectedRoiHighlight(
            IList<ViewportRoiOverlay> rois,
            IList<int> boardIndices,
            int selectedBoardIndex)
        {
            List<ViewportRoiOverlay> result = new List<ViewportRoiOverlay>();
            if (rois == null) return result;

            for (int index = 0; index < rois.Count; index++)
            {
                ViewportRoiOverlay roi = rois[index];
                if (roi == null) continue;
                int boardNumber = boardIndices != null && index < boardIndices.Count
                    ? boardIndices[index]
                    : index + 1;
                bool selected = selectedBoardIndex > 0 && boardNumber == selectedBoardIndex;
                string rawLabel = (roi.Label ?? string.Empty).TrimStart('★', ' ');
                result.Add(new ViewportRoiOverlay
                {
                    ImageBounds = roi.ImageBounds,
                    AngleDegrees = roi.AngleDegrees,
                    Label = selected ? "★ " + rawLabel : rawLabel,
                    Color = selected
                        ? Color.Gold
                        : (roi.Color == Color.Gold ? Color.DeepSkyBlue : roi.Color),
                    LineWidth = selected ? 3.0F : 2.0F
                });
            }
            return result;
        }

        private static List<ViewportContourOverlay> CloneContourList(IEnumerable<ViewportContourOverlay> source)
        {
            if (source == null) return new List<ViewportContourOverlay>();
            return source
                .Where(item => item != null && item.Points != null)
                .Select(item => new ViewportContourOverlay
                {
                    Points = item.Points.ToList(),
                    Label = item.Label,
                    Color = item.Color,
                    LineWidth = item.LineWidth
                })
                .ToList();
        }

        private static List<ViewportRoiOverlay> CloneRoiList(IEnumerable<ViewportRoiOverlay> source)
        {
            if (source == null) return new List<ViewportRoiOverlay>();
            return source
                .Where(item => item != null)
                .Select(item => new ViewportRoiOverlay
                {
                    ImageBounds = item.ImageBounds,
                    AngleDegrees = item.AngleDegrees,
                    Label = item.Label,
                    Color = item.Color,
                    LineWidth = item.LineWidth
                })
                .ToList();
        }

        private void SyncEffectViewportViewDeferred()
        {
            if (_disposed || !_view.IsDetectionDualViewActive) return;
            if (_view.EffectViewport.IsHandleCreated)
            {
                try
                {
                    _view.EffectViewport.BeginInvoke(new MethodInvoker(delegate
                    {
                        if (!_disposed &&
                            _view.IsDetectionDualViewActive &&
                            _view.Viewport.HasImage &&
                            _view.EffectViewport.HasImage)
                        {
                            _view.EffectViewport.SyncViewFrom(_view.Viewport);
                        }
                    }));
                }
                catch (ObjectDisposedException)
                {
                }
                catch (InvalidOperationException)
                {
                }
            }
            else if (_view.Viewport.HasImage && _view.EffectViewport.HasImage)
            {
                _view.EffectViewport.SyncViewFrom(_view.Viewport);
            }
        }

        private static string BuildEffectInfoText(DetectionProfile profile, bool binaryEnabled, int blobCount)
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "{0}   {1}   {2}   形态学 {3}px   区域 {4}",
                binaryEnabled ? "局部二值分割（ROI 外保留原图）" : "原图叠加",
                profile.DetectDarkForeground ? "白底黑点" : "黑底白点",
                profile.AutoThreshold ? "自动阈值" : "阈值 " + profile.BrightThreshold,
                profile.MorphologyRadius,
                blobCount);
        }

        private void CancelBinaryPreview()
        {
            lock (_binaryPreviewSync)
            {
                if (_binaryPreviewCancellation == null) return;
                try { _binaryPreviewCancellation.Cancel(); }
                catch (ObjectDisposedException) { }
            }
        }

        private static DetectionRoi CloneDetectionRoi(DetectionRoi source)
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

        private Bitmap SnapshotAndLockCurrentImage()
        {
            // 已处于锁定帧模式时直接克隆返回，避免重复截屏并保持当前基准帧不变
            lock (_displaySync)
            {
                if (_lockedDisplayImage != null) return (Bitmap)_lockedDisplayImage.Clone();
            }

            // 启用了模拟相机时禁止从仿真预览流截屏，强制要求先导入真实图像
            if (_runtime.IsMockCamera) return null;

            Bitmap snapshot = _view.Viewport.Snapshot();
            if (snapshot == null) return null;

            // 实时预览状态下抓取一帧并切入 LockedFrame 模式，防止后续预览帧冲掉调试画面
            Bitmap lockedCopy = (Bitmap)snapshot.Clone();
            PinOwnedDisplayImage(lockedCopy, "检测输入", false);
            return snapshot;
        }

        // 接管传入位图所有权并切换为 LockedFrame 模式，递增 _displayGeneration 使旧预览失效
        private void PinOwnedDisplayImage(
            Bitmap image,
            string source,
            bool inspectionFrame,
            DateTime? capturedAtUtc = null)
        {
            if (image == null) return;
            CancelBinaryPreview();
            Bitmap displayCopy = new Bitmap(image);
            Bitmap effectCopy = _view.IsDetectionDualViewActive ? new Bitmap(image) : null;
            Bitmap previous = null;
            bool scheduleEffect = false;
            lock (_displaySync)
            {
                if (_disposed)
                {
                    image.Dispose();
                    displayCopy.Dispose();
                    if (effectCopy != null) effectCopy.Dispose();
                    return;
                }

                previous = _lockedDisplayImage;
                _lockedDisplayImage = image;
                _displayMode = DisplayMode.LockedFrame;
                _displayGeneration++;
                _inspectionFrameGeneration = inspectionFrame ? _displayGeneration : -1;
                ClearInspectionPreviewCache();
                _view.Viewport.ClearOverlays();
                DisplayImageCore(displayCopy, source, capturedAtUtc ?? DateTime.UtcNow);
                if (effectCopy != null)
                {
                    _view.EffectViewport.SetImageOwned(effectCopy);
                    _view.EffectViewport.ClearOverlays();
                    _view.SetEffectViewportInformation(inspectionFrame ? "检测效果图计算中..." : "已载入图像，正在生成效果图...");
                    SyncEffectViewportViewDeferred();
                    scheduleEffect = !inspectionFrame;
                }
            }
            if (previous != null) previous.Dispose();
            if (scheduleEffect) TriggerEffectPreviewOnUi(false);
        }

        // 退出锁定状态与二值预览，恢复接收相机实时预览流
        private void ResumeLivePreview()
        {
            CancelBinaryPreview();
            Bitmap previous;
            lock (_displaySync)
            {
                previous = _lockedDisplayImage;
                _lockedDisplayImage = null;
                _displayMode = DisplayMode.LivePreview;
                _displayGeneration++;
                _inspectionFrameGeneration = -1;
                ClearInspectionPreviewCache();
                if (!_disposed)
                {
                    _view.Viewport.ClearOverlays();
                    _view.EffectViewport.ClearOverlays();
                    _view.EffectViewport.ClearImage();
                    if (_view.IsDetectionDualViewActive)
                    {
                        _view.SetEffectViewportInformation("实时预览中   同步原图");
                    }
                }
            }
            if (previous != null) previous.Dispose();
        }

        private void ClearInspectionPreviewCache()
        {
            _lastDetectionRegions = new List<DetectionRoi>();
            _lastInspectionPoints = new List<ViewportPointOverlay>();
            _lastInspectionRois = new List<ViewportRoiOverlay>();
            _lastInspectionContours = new List<ViewportContourOverlay>();
            _lastMatchedBoardContours = new List<ViewportContourOverlay>();
            _lastInspectionBoardIndices = new List<int>();
            _selectedInspectionBoardIndex = -1;
            _lastInspectionOverlayGeneration = -1;
            _lastInspectionCompletedGeneration = -1;
        }

        private void DisplayImageCore(Bitmap image, string source, DateTime capturedAtUtc)
        {
            _view.DisplayOwnedImage(image);
            _view.SetImageInformation(string.Format(
                CultureInfo.InvariantCulture,
                "{0} x {1}   {2}   {3:HH:mm:ss.fff}",
                image.Width,
                image.Height,
                source ?? "frame",
                capturedAtUtc.ToLocalTime()));
        }

        // 承载后台生成的效果图与轮廓叠加层，TakeImage 用于将位图所有权安全移交视口
        private sealed class EffectPreviewPayload : IDisposable
        {
            public EffectPreviewPayload()
            {
                BlobContours = new List<ViewportContourOverlay>();
                EffectContours = new List<ViewportContourOverlay>();
                Rois = new List<ViewportRoiOverlay>();
            }

            public Bitmap Image { get; set; }
            public string ErrorMessage { get; set; }
            public int BlobCount { get; set; }
            public List<ViewportContourOverlay> BlobContours { get; private set; }
            public List<ViewportContourOverlay> EffectContours { get; private set; }
            public List<ViewportRoiOverlay> Rois { get; private set; }

            public Bitmap TakeImage()
            {
                Bitmap image = Image;
                Image = null;
                return image;
            }

            public void Dispose()
            {
                Bitmap image = Image;
                Image = null;
                if (image != null) image.Dispose();
            }
        }
    }
}
