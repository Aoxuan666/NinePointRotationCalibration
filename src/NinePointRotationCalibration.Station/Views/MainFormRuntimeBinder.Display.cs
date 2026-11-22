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
            if (!_runtime.IsInspectionEnabled)
            {
                _view.EffectViewport.ClearOverlays();
                _view.SetEffectViewportInformation("检测功能已关闭 · 请在「相机」页开启「启用检测」");
                return;
            }

            long currentGeneration;
            long overlayGeneration;
            bool isLive;
            bool hasCurrentOverlay;
            bool hasLockedImage;
            List<DetectionRoi> cachedRegions;
            List<ViewportRoiOverlay> cachedRois;
            List<ViewportContourOverlay> cachedContours;
            List<ViewportContourOverlay> matchedContours;
            List<int> boardIndices;
            int selectedBoard;
            lock (_displaySync)
            {
                currentGeneration = _displayGeneration;
                overlayGeneration = _lastInspectionOverlayGeneration;
                isLive = _displayMode == DisplayMode.LivePreview;
                hasLockedImage = _displayMode == DisplayMode.LockedFrame && _lockedDisplayImage != null;
                hasCurrentOverlay = hasLockedImage &&
                                    _lastInspectionOverlayGeneration == _displayGeneration &&
                                    _lastDetectionRegions.Count > 0;
                cachedRegions = hasCurrentOverlay ? _lastDetectionRegions : null;
                cachedRois = hasCurrentOverlay ? CloneRoiList(_lastInspectionRois) : new List<ViewportRoiOverlay>();
                cachedContours = hasCurrentOverlay ? CloneContourList(_lastInspectionContours) : new List<ViewportContourOverlay>();
                matchedContours = hasCurrentOverlay ? CloneContourList(_lastMatchedBoardContours) : new List<ViewportContourOverlay>();
                boardIndices = hasCurrentOverlay ? new List<int>(_lastInspectionBoardIndices) : new List<int>();
                selectedBoard = _selectedInspectionBoardIndex;
            }

            if (!hasLockedImage)
            {
                _view.EffectViewport.ClearImage();
                _view.EffectViewport.ClearOverlays();
                _view.SetEffectViewportInformation(isLive ? "实时预览中   等待抓拍或点击检测" : "等待拍照或导入图像");
                return;
            }

            DetectionProfile profile = ReadDetectionProfileFromView();
            bool binaryEnabled = IsBinaryPreviewEnabled();
            int expectedCount = Decimal.ToInt32(_view.ExpectedBoardCountInput.Value);
            CalibrationJob job = _runtime.GetPositioningJobSnapshot();
            string currentKey = BuildEffectPreviewKey(
                currentGeneration,
                hasCurrentOverlay ? overlayGeneration : -1,
                hasCurrentOverlay && cachedRegions != null ? cachedRegions.Count : 0,
                profile,
                binaryEnabled,
                expectedCount,
                job);

            // 若当前图像、检测参数、模板和叠加层与已渲染效果图完全一致，直接复用底图与叠加层，实现零重绘秒切
            if (_view.EffectViewport.HasImage &&
                _renderedEffectGeneration == currentGeneration &&
                string.Equals(_renderedEffectKey, currentKey, StringComparison.Ordinal))
            {
                if (hasCurrentOverlay)
                {
                    _view.EffectViewport.SetRoiOverlays(ApplySelectedRoiHighlight(cachedRois, boardIndices, selectedBoard));
                }
                _view.SetEffectViewportInformation(string.IsNullOrWhiteSpace(_renderedEffectInfoText)
                    ? "检测效果图已就绪"
                    : _renderedEffectInfoText);
                return;
            }

            Bitmap sourceCopy;
            Bitmap sourcePreview;
            lock (_displaySync)
            {
                if (_displayMode != DisplayMode.LockedFrame ||
                    _lockedDisplayImage == null ||
                    _displayGeneration != currentGeneration)
                {
                    return;
                }
                sourceCopy = ImageViewport.CloneToPArgbFast(_lockedDisplayImage);
                sourcePreview = ImageViewport.CreateFastPreview(sourceCopy);
            }

            // 缓存未命中时先秒速同步显示当前帧，切页绘制完成后再在后台生成最新分割效果与叠加层
            _view.EffectViewport.SetImageOwned(sourceCopy, sourcePreview);
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
            _view.SetEffectViewportInformation("检测效果图刷新中...");
            TriggerEffectPreviewOnUi(false);
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
            if (_view.IsHandleCreated)
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
            if (!_runtime.IsInspectionEnabled)
            {
                _view.SetEffectViewportInformation("检测功能已关闭 · 请在「相机」页开启「启用检测」");
                return;
            }

            // 取消上一次尚未完成的效果图计算，并记录当前 _displayGeneration 防止旧帧覆盖新锁定图像
            CancellationTokenSource cancellation =
                CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            lock (_binaryPreviewSync)
            {
                if (_binaryPreviewCancellation != null)
                {
                    try { _binaryPreviewCancellation.Cancel(); }
                    catch (ObjectDisposedException) { }
                }
                _binaryPreviewCancellation = cancellation;
            }

            Bitmap source = null;
            try
            {
                // 先执行 25ms 防抖合并，再从锁定帧克隆位图，避免连续滚轮调参期间在 UI 线程反复复制高分辨率原图
                if (debounce)
                {
                    await Task.Delay(25).ConfigureAwait(true);
                    if (cancellation.IsCancellationRequested || _disposed || !_view.IsDetectionDualViewActive)
                        return;
                }

                LockedFrameGrayCache grayCache = null;
                List<DetectionRoi> regions;
                List<ImagePose> boardPoses;
                List<ViewportContourOverlay> matchedContours;
                List<int> boardIndices;
                int selectedBoardIndex;
                long generation;
                bool isLiveMode;
                lock (_displaySync)
                {
                    generation = _displayGeneration;
                    isLiveMode = _displayMode == DisplayMode.LivePreview;
                    if (_lockedDisplayImage != null) source = ImageViewport.CloneToPArgbFast(_lockedDisplayImage);
                    grayCache = _lockedGray;
                    bool hasOverlay = _lastInspectionOverlayGeneration == generation;
                    regions = hasOverlay
                        ? _lastDetectionRegions.Select(CloneDetectionRoi).ToList()
                        : new List<DetectionRoi>();
                    boardPoses = hasOverlay
                        ? _lastMatchedBoardPoses.Select(p => p == null ? null : new ImagePose(p.Row, p.Column, p.AngleDegrees)).ToList()
                        : new List<ImagePose>();
                    matchedContours = hasOverlay
                        ? CloneContourList(_lastMatchedBoardContours)
                        : new List<ViewportContourOverlay>();
                    boardIndices = hasOverlay
                        ? new List<int>(_lastInspectionBoardIndices)
                        : new List<int>();
                    selectedBoardIndex = _selectedInspectionBoardIndex;
                }

                if (source == null)
                {
                    _view.EffectViewport.ClearImage();
                    _view.EffectViewport.ClearOverlays();
                    _view.SetEffectViewportInformation(isLiveMode ? "实时预览中" : "等待拍照或导入图像");
                    _view.SetOperationStatus("效果图预览需要已拍摄或已导入的图像", false, true);
                    return;
                }

                DetectionProfile profile = ReadDetectionProfileFromView();
                if (profile.MinimumBlobArea > profile.MaximumBlobArea)
                {
                    _view.SetEffectViewportInformation("参数无效：最小面积不能大于最大面积");
                    _view.SetOperationStatus("最小面积不能大于最大面积", false, true);
                    return;
                }
                bool binaryEnabled = IsBinaryPreviewEnabled();
                int expectedCount = Decimal.ToInt32(_view.ExpectedBoardCountInput.Value);
                CalibrationJob job = _runtime.GetPositioningJobSnapshot();
                if (profile.TemplateMatchingEnabled && regions.Count == 0)
                {
                    bool alreadyInspectedCurrentFrame;
                    lock (_displaySync) alreadyInspectedCurrentFrame = _lastInspectionCompletedGeneration == generation;
                    if (alreadyInspectedCurrentFrame || job == null || job.Template == null || !job.Template.HasUsableModel)
                    {
                        bool stillCurrent;
                        lock (_displaySync) stillCurrent = generation == _displayGeneration;
                        if (stillCurrent && !cancellation.IsCancellationRequested && _view.IsDetectionDualViewActive)
                        {
                            InvalidateEffectRenderCache();
                            Bitmap fastPreview = ImageViewport.CreateFastPreview(source);
                            _view.EffectViewport.SetImageOwned(source, fastPreview);
                            source = null;
                            _view.EffectViewport.ClearOverlays();
                            _view.SetEffectViewportInformation(job == null || job.Template == null || !job.Template.HasUsableModel
                                ? "尚未加载定位模板 · 当前显示原图"
                                : "未匹配到目标板 · 当前显示原图");
                        }
                        return;
                    }

                    _view.SetEffectViewportInformation("正在定位检测区域...");
                    _view.SetOperationStatus("正在定位检测 ROI...", true, false);
                    int maximumMatches = expectedCount + 1;
                    TemplateMatchCollectionResult matchResult = await Task
                        .Run(() => _runtime.LocatePositioningForPreview(source, grayCache, maximumMatches))
                        .ConfigureAwait(true);
                    if (cancellation.IsCancellationRequested || _disposed || !_view.IsDetectionDualViewActive)
                        return;

                    if (matchResult != null && matchResult.Success)
                    {
                        // 与 BoardInspectionService 使用完全相同的行列聚类排序，避免预览与正式检测板号跳变
                        List<TemplateMatchResult> validMatches = BoardInspectionService.OrderMatches(
                            matchResult.Matches,
                            job.Template,
                            profile);
                        regions = new List<DetectionRoi>(validMatches.Count);
                        boardPoses = new List<ImagePose>(validMatches.Count);
                        foreach (TemplateMatchResult match in validMatches)
                        {
                            DetectionRoi roi = _binaryAnalyzer.ResolveDetectionRoi(job.Template, match, profile);
                            if (roi == null) continue;
                            regions.Add(roi);
                            boardPoses.Add(new ImagePose(match.Row, match.Column, match.AngleDegrees));
                        }
                        matchedContours = BuildMatchedContours(validMatches);
                        boardIndices = Enumerable.Range(1, regions.Count).ToList();
                    }

                    if (regions.Count == 0)
                    {
                        bool stillCurrent;
                        lock (_displaySync) stillCurrent = generation == _displayGeneration;
                        if (stillCurrent && !cancellation.IsCancellationRequested && _view.IsDetectionDualViewActive)
                        {
                            InvalidateEffectRenderCache();
                            Bitmap fastPreview = ImageViewport.CreateFastPreview(source);
                            _view.EffectViewport.SetImageOwned(source, fastPreview);
                            source = null;
                            _view.EffectViewport.ClearOverlays();
                            _view.SetEffectViewportInformation("未匹配到模板目标 · 当前显示原图");
                            _view.SetOperationStatus("当前图像未匹配到定位模板目标", false, true);
                            _view.SyncDetectionBoardStatuses(null, null, null, null, null);
                            _view.SetDetectionSummary(expectedCount, 0, false, "未匹配到定位模板目标");
                            _view.SetInspectionHud(
                                matchResult == null ? 0.0 : matchResult.ElapsedMilliseconds,
                                matchResult == null ? 0.0 : matchResult.ElapsedMilliseconds,
                                0.0, 0.0, 0.0, 0.0, 0.0, 0.0, expectedCount, 0, false, false, 0, 0, "未匹配到目标");
                        }
                        return;
                    }

                    lock (_displaySync)
                    {
                        if (generation == _displayGeneration)
                        {
                            _lastDetectionRegions = regions.Select(CloneDetectionRoi).ToList();
                            _lastMatchedBoardPoses = boardPoses.Select(p => p == null ? null : new ImagePose(p.Row, p.Column, p.AngleDegrees)).ToList();
                            _lastMatchedBoardContours = CloneContourList(matchedContours);
                            _lastInspectionBoardIndices = new List<int>(boardIndices);
                            _lastInspectionOverlayGeneration = generation;
                            _lastInspectionTimingGeneration = generation;
                            _lastInspectionMatchElapsedMs = matchResult == null ? 0.0 : matchResult.ElapsedMilliseconds;
                        }
                    }
                }

                EffectPreviewPayload preview = await Task.Run(
                    () => BuildEffectPreview(
                        source,
                        grayCache,
                        regions,
                        boardPoses,
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
                    InvalidateEffectRenderCache();
                    _view.SetEffectViewportInformation("检测效果图更新失败");
                    _view.SetOperationStatus("检测效果图更新失败：" + preview.ErrorMessage, false, true);
                    preview.Dispose();
                    return;
                }

                long overlayGenAfter;
                int regionCountAfter;
                double cachedMatchMs;
                lock (_displaySync)
                {
                    if (generation == _displayGeneration)
                    {
                        _lastInspectionRois = CloneRoiList(preview.Rois);
                        _lastInspectionContours = CloneContourList(preview.BlobContours);
                        _lastInspectionOverlayGeneration = generation;
                    }
                    overlayGenAfter = _lastInspectionOverlayGeneration == generation ? generation : -1;
                    regionCountAfter = overlayGenAfter == generation ? _lastDetectionRegions.Count : 0;
                    cachedMatchMs = profile.TemplateMatchingEnabled && _lastInspectionTimingGeneration == generation
                        ? _lastInspectionMatchElapsedMs
                        : 0.0;
                }

                // 统一同步效果图、检测结果表、汇总栏和左上角 HUD 的 OK/NG 状态，杜绝四处显示不一致
                int actualCount = preview.BoardStatuses.Count;
                int ngCount = 0;
                int[] statusIndices = new int[actualCount];
                bool[] statusMarks = new bool[actualCount];
                double[] statusCols = new double[actualCount];
                double[] statusRows = new double[actualCount];
                double[] statusAngles = new double[actualCount];
                for (int i = 0; i < actualCount; i++)
                {
                    PreviewBoardStatus bs = preview.BoardStatuses[i];
                    statusIndices[i] = bs.BoardIndex;
                    statusMarks[i] = bs.HasManualMark;
                    statusCols[i] = bs.Column;
                    statusRows[i] = bs.Row;
                    statusAngles[i] = bs.AngleDegrees;
                    if (bs.HasManualMark) ngCount++;
                }
                int okCount = actualCount - ngCount;
                bool hasManualMarks = ngCount > 0;
                bool countMatches = !profile.TemplateMatchingEnabled || actualCount == expectedCount;
                bool pass = countMatches && !hasManualMarks;
                string ngReason = !countMatches && hasManualMarks
                    ? "板数不符+有标记"
                    : !countMatches ? "板数不符" : (hasManualMarks ? "有标记" : null);
                string summaryMsg = pass
                    ? string.Format(CultureInfo.InvariantCulture, "检测通过（OK {0} / NG 0）", okCount)
                    : !countMatches
                        ? string.Format(CultureInfo.InvariantCulture, "板数不符（目标 {0} / 实际 {1}，OK {2} / NG {3}）", expectedCount, actualCount, okCount, ngCount)
                        : string.Format(CultureInfo.InvariantCulture, "检出不良标记（OK {0} / NG {1}）", okCount, ngCount);
                _view.SyncDetectionBoardStatuses(statusIndices, statusMarks, statusCols, statusRows, statusAngles);
                _view.SetDetectionSummary(expectedCount, actualCount, pass, summaryMsg);
                _view.SetInspectionHud(
                    cachedMatchMs + preview.BlobElapsedMilliseconds, cachedMatchMs, preview.BlobElapsedMilliseconds,
                    preview.ImageConversionMilliseconds, preview.ThresholdMilliseconds,
                    preview.MorphologyMilliseconds, preview.HoleFillMilliseconds, preview.ComponentMilliseconds,
                    expectedCount, actualCount, pass, hasManualMarks, okCount, ngCount, ngReason);

                string effectInfo = BuildEffectInfoText(profile, binaryEnabled, preview.BlobCount);
                _view.EffectViewport.SetImageOwned(preview.TakeImage(), preview.TakePreviewImage());
                _view.EffectViewport.SetPointOverlays(null);
                _view.EffectViewport.SetContourOverlays(preview.EffectContours);
                _view.EffectViewport.SetRoiOverlays(ApplySelectedRoiHighlight(
                    preview.Rois,
                    boardIndices,
                    selectedBoardIndex));
                _view.SetEffectViewportInformation(effectInfo);
                _view.SetOperationStatus(binaryEnabled ? "检测效果图（局部二值）已更新" : "检测效果图（原图叠加）已更新", false, false);
                _renderedEffectGeneration = generation;
                _renderedEffectKey = BuildEffectPreviewKey(
                    generation,
                    overlayGenAfter,
                    regionCountAfter,
                    profile,
                    binaryEnabled,
                    expectedCount,
                    job);
                _renderedEffectInfoText = effectInfo;
                preview.Dispose();
            }
            catch (Exception exception)
            {
                LocalDebugLog.WriteException("WARN", "ui.preview", "ScheduleBinaryPreview failed", exception);
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
            LockedFrameGrayCache grayCache,
            IList<DetectionRoi> regions,
            IList<ImagePose> boardPoses,
            IList<int> boardIndices,
            IList<ViewportContourOverlay> matchedContours,
            DetectionProfile profile,
            bool binaryEnabled,
            int selectedBoardIndex,
            CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested) return null;

            EffectPreviewPayload payload = new EffectPreviewPayload();
            Bitmap combined = ImageViewport.CloneToPArgbFast(source);
            payload.Image = combined;
            List<BlobAnalysisResult> analyses = null;
            try
            {
                IList<DetectionRoi> targetList = regions ?? new List<DetectionRoi>();
                bool hasExplicitRegions = targetList.Count > 0;
                IList<DetectionRoi> targets = hasExplicitRegions
                    ? targetList
                    : new List<DetectionRoi>
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

                // 全链路单次灰度图复用 + 批量 ROI 分析：无论板数多少均仅转换 0~1 次图像，彻底消除逐板重复转换千万像素导致的卡顿
                using (HalconGrayImage gray = grayCache == null
                    ? HalconGrayImage.FromBitmap(source)
                    : grayCache.Acquire(source))
                {
                    analyses = _binaryAnalyzer.AnalyzeRoisDetailedNoThrow(
                        gray, targets, profile, binaryEnabled, cancellationToken);
                }
                if (cancellationToken.IsCancellationRequested)
                {
                    payload.Dispose();
                    return null;
                }

                Graphics graphics = binaryEnabled ? Graphics.FromImage(combined) : null;
                try
                {
                    if (graphics != null)
                    {
                        graphics.CompositingMode = CompositingMode.SourceCopy;
                        graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
                        graphics.PixelOffsetMode = PixelOffsetMode.Half;
                    }

                    for (int index = 0; index < targets.Count; index++)
                    {
                        if (cancellationToken.IsCancellationRequested)
                        {
                            payload.Dispose();
                            return null;
                        }
                        DetectionRoi roi = targets[index];
                        BlobAnalysisResult analysis = analyses != null && index < analyses.Count
                            ? analyses[index]
                            : null;
                        bool roiHasDefect = false;
                        if (analysis != null)
                        {
                            payload.BlobElapsedMilliseconds += analysis.ElapsedMilliseconds;
                            payload.ImageConversionMilliseconds += analysis.ImageConversionElapsedMilliseconds;
                            payload.ThresholdMilliseconds += analysis.ThresholdElapsedMilliseconds;
                            payload.MorphologyMilliseconds += analysis.MorphologyElapsedMilliseconds;
                            payload.HoleFillMilliseconds += analysis.HoleFillElapsedMilliseconds;
                            payload.ComponentMilliseconds += analysis.ComponentElapsedMilliseconds;

                            if (graphics != null &&
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
                                // 单板 NG 判定与 BoardInspectionService 保持完全一致：只要存在未被矩形过滤的 Blob 即判 NG
                                if (finding == null || finding.RejectedAsRectangle) continue;
                                roiHasDefect = true;
                                payload.BlobCount++;
                                if (finding.Contour != null && finding.Contour.Count >= 2)
                                {
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
                        }

                        int boardNumber = boardIndices != null && index < boardIndices.Count
                            ? boardIndices[index]
                            : index + 1;
                        ImagePose matchedPose = boardPoses != null && index < boardPoses.Count ? boardPoses[index] : null;
                        payload.BoardStatuses.Add(new PreviewBoardStatus
                        {
                            BoardIndex = boardNumber,
                            HasManualMark = roiHasDefect,
                            Column = matchedPose != null ? matchedPose.Column : (roi == null ? 0.0 : roi.ReferenceCenterColumn),
                            Row = matchedPose != null ? matchedPose.Row : (roi == null ? 0.0 : roi.ReferenceCenterRow),
                            AngleDegrees = matchedPose != null ? matchedPose.AngleDegrees : (roi == null ? 0.0 : -roi.PhiDegrees)
                        });

                        if (hasExplicitRegions && roi != null)
                        {
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
                    }
                }
                finally
                {
                    if (graphics != null) graphics.Dispose();
                }
                // 在后台线程预先生成 1600px 快速预览帧，彻底消除效果图首次上屏时的 UI 主线程重采样卡顿
                payload.PreviewImage = ImageViewport.CreateFastPreview(combined);
                return payload;
            }
            catch (Exception exception)
            {
                LocalDebugLog.WriteException("WARN", "ui.preview", "BuildEffectPreview failed", exception);
                payload.Dispose();
                if (cancellationToken.IsCancellationRequested) return null;
                return new EffectPreviewPayload { ErrorMessage = exception.Message };
            }
            finally
            {
                if (analyses != null)
                {
                    for (int i = 0; i < analyses.Count; i++)
                    {
                        if (analyses[i] != null) analyses[i].Dispose();
                    }
                }
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
                bool labelIsNg = rawLabel.EndsWith(" NG", StringComparison.OrdinalIgnoreCase);
                result.Add(new ViewportRoiOverlay
                {
                    ImageBounds = roi.ImageBounds,
                    AngleDegrees = roi.AngleDegrees,
                    Label = selected ? "★ " + rawLabel : rawLabel,
                    Color = selected
                        ? Color.Gold
                        : (roi.Color == Color.Gold ? (labelIsNg ? Color.OrangeRed : Color.DeepSkyBlue) : roi.Color),
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

        private static string BuildEffectInfoText(DetectionProfile profile, bool binaryEnabled, int blobCount)
        {
            bool fillEnabled = profile.DetectDarkForeground ? profile.FillDarkHoles : profile.FillHoles;
            return string.Format(
                CultureInfo.InvariantCulture,
                "{0}   {1}   {2}   {3}   去噪 {4}px   检出 {5}",
                binaryEnabled ? "局部二值分割（ROI 外保留原图）" : "原图叠加",
                profile.DetectDarkForeground ? "白底黑点" : "黑底白点",
                fillEnabled ? "填孔" : "不填孔",
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

        private Bitmap SnapshotForInspection(out LockedFrameGrayCache cache)
        {
            lock (_displaySync)
            {
                Bitmap snapshot = SnapshotAndLockCurrentImage();
                cache = _lockedGray;
                return snapshot;
            }
        }

        private Bitmap TrySnapshotLockedImage(out LockedFrameGrayCache cache)
        {
            lock (_displaySync)
            {
                if (_displayMode == DisplayMode.LockedFrame && _lockedDisplayImage != null)
                {
                    cache = _lockedGray;
                    return (Bitmap)_lockedDisplayImage.Clone();
                }
                cache = null;
                return null;
            }
        }

        private Bitmap SnapshotAndLockCurrentImage()
        {
            // 已处于锁定帧模式时直接克隆返回，避免重复截屏并保持当前基准帧不变
            lock (_displaySync)
            {
                if (_lockedDisplayImage != null) return (Bitmap)_lockedDisplayImage.Clone();
            }

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
            int imageWidth = image.Width;
            int imageHeight = image.Height;
            Bitmap displayCopy = ImageViewport.CloneToPArgbFast(image);
            Bitmap displayPreview = ImageViewport.CreateFastPreview(displayCopy);
            Bitmap effectCopy = _view.IsDetectionDualViewActive ? ImageViewport.CloneToPArgbFast(displayCopy) : null;
            Bitmap effectPreview = effectCopy != null && displayPreview != null ? ImageViewport.CloneToPArgbFast(displayPreview) : null;
            Bitmap previous = null;
            bool scheduleEffect = false;
            long generation;
            lock (_displaySync)
            {
                if (_disposed)
                {
                    image.Dispose();
                    displayCopy.Dispose();
                    if (displayPreview != null) displayPreview.Dispose();
                    if (effectCopy != null) effectCopy.Dispose();
                    if (effectPreview != null) effectPreview.Dispose();
                    return;
                }

                previous = _lockedDisplayImage;
                _lockedDisplayImage = image;
                if (_lockedGray != null) _lockedGray.Dispose();
                _lockedGray = new LockedFrameGrayCache();
                _displayMode = DisplayMode.LockedFrame;
                _displayGeneration++;
                generation = _displayGeneration;
                _inspectionFrameGeneration = inspectionFrame ? _displayGeneration : -1;
                ClearInspectionPreviewCache();
                _view.Viewport.ClearOverlays();
                DisplayImageCore(displayCopy, displayPreview, source, capturedAtUtc ?? DateTime.UtcNow);
                if (effectCopy != null)
                {
                    _view.EffectViewport.SetImageOwned(effectCopy, effectPreview);
                    _view.EffectViewport.ClearOverlays();
                    _view.SetEffectViewportInformation(inspectionFrame ? "检测效果图计算中..." : "已载入图像，正在生成效果图...");
                    scheduleEffect = !inspectionFrame;
                }
            }
            if (previous != null) previous.Dispose();
            LocalDebugLog.Write(
                "DEBUG",
                "ui.display",
                "Pinned frame: source='" + source + "', size=" + imageWidth + "x" + imageHeight +
                ", inspectionFrame=" + inspectionFrame + ", gen=" + generation);
            if (scheduleEffect) TriggerEffectPreviewOnUi(false);
        }

        // 退出锁定状态与二值预览，恢复接收相机实时预览流
        private void ResumeLivePreview()
        {
            CancelBinaryPreview();
            LocalDebugLog.Write("INFO", "ui.display", "Resumed live preview mode.");
            Bitmap previous;
            lock (_displaySync)
            {
                previous = _lockedDisplayImage;
                _lockedDisplayImage = null;
                if (_lockedGray != null) _lockedGray.Dispose();
                _lockedGray = null;
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
            UpdateCameraLiveUiStatus();
        }

        private void ClearInspectionPreviewCache()
        {
            _lastDetectionRegions = new List<DetectionRoi>();
            _lastMatchedBoardPoses = new List<ImagePose>();
            _lastInspectionPoints = new List<ViewportPointOverlay>();
            _lastInspectionRois = new List<ViewportRoiOverlay>();
            _lastInspectionContours = new List<ViewportContourOverlay>();
            _lastMatchedBoardContours = new List<ViewportContourOverlay>();
            _lastInspectionBoardIndices = new List<int>();
            _selectedInspectionBoardIndex = -1;
            _lastInspectionOverlayGeneration = -1;
            _lastInspectionCompletedGeneration = -1;
            InvalidateEffectRenderCache();
        }

        private void InvalidateEffectRenderCache()
        {
            _renderedEffectGeneration = -1;
            _renderedEffectKey = null;
            _renderedEffectInfoText = null;
        }

        private static string BuildEffectPreviewKey(
            long generation,
            long overlayGeneration,
            int regionCount,
            DetectionProfile profile,
            bool binaryEnabled,
            int expectedCount,
            CalibrationJob positioningJob)
        {
            DetectionProfile safe = profile ?? new DetectionProfile();
            TemplateDefinition template = positioningJob == null ? null : positioningJob.Template;
            long templateRevision = template == null ? 0L : template.Revision;
            string modelHash = template == null ? string.Empty : (template.ModelHash ?? string.Empty);
            return string.Format(
                CultureInfo.InvariantCulture,
                "{0}|{1}|{2}|{3}|{4}|{5}|{6}|{7}|{8}|{9}|{10:0.####}|{11:0.####}|{12:0.####}|{13}|{14}|{15}|{16}|{17}",
                generation,
                overlayGeneration,
                regionCount,
                binaryEnabled ? 1 : 0,
                expectedCount,
                safe.TemplateMatchingEnabled ? 1 : 0,
                safe.FollowTemplateTransform ? 1 : 0,
                safe.AutoThreshold ? 1 : 0,
                safe.DetectDarkForeground ? 1 : 0,
                safe.BrightThreshold,
                safe.MinimumBlobArea,
                safe.MaximumBlobArea,
                safe.RectangleFillThreshold,
                safe.RectangleFilterEnabled ? 1 : 0,
                safe.FillHoles ? 1 : 0,
                safe.FillDarkHoles ? 1 : 0,
                safe.MorphologyRadius,
                templateRevision + ":" + modelHash);
        }

        private void DisplayImageCore(Bitmap image, Bitmap preview, string source, DateTime capturedAtUtc)
        {
            _view.Viewport.SetImageOwned(image, preview);
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
                BoardStatuses = new List<PreviewBoardStatus>();
            }

            public Bitmap Image { get; set; }
            public Bitmap PreviewImage { get; set; }
            public string ErrorMessage { get; set; }
            public int BlobCount { get; set; }
            public double BlobElapsedMilliseconds { get; set; }
            public double ImageConversionMilliseconds { get; set; }
            public double ThresholdMilliseconds { get; set; }
            public double MorphologyMilliseconds { get; set; }
            public double HoleFillMilliseconds { get; set; }
            public double ComponentMilliseconds { get; set; }
            public List<ViewportContourOverlay> BlobContours { get; private set; }
            public List<ViewportContourOverlay> EffectContours { get; private set; }
            public List<ViewportRoiOverlay> Rois { get; private set; }
            public List<PreviewBoardStatus> BoardStatuses { get; private set; }

            public Bitmap TakeImage()
            {
                Bitmap image = Image;
                Image = null;
                return image;
            }

            public Bitmap TakePreviewImage()
            {
                Bitmap preview = PreviewImage;
                PreviewImage = null;
                return preview;
            }

            public void Dispose()
            {
                Bitmap image = Image;
                Image = null;
                if (image != null) image.Dispose();
                Bitmap preview = PreviewImage;
                PreviewImage = null;
                if (preview != null) preview.Dispose();
            }
        }

        private sealed class PreviewBoardStatus
        {
            public int BoardIndex { get; set; }
            public bool HasManualMark { get; set; }
            public double Column { get; set; }
            public double Row { get; set; }
            public double AngleDegrees { get; set; }
        }
    }
}
