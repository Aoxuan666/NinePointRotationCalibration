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
    /// <summary>Image display state: locked/live frames and the binary preview.</summary>
    public sealed partial class MainFormRuntimeBinder
    {
        private bool IsBinaryPreviewEnabled()
        {
            CheckBox toggle = FindControl<CheckBox>("chkBinaryPreview");
            return toggle != null && toggle.Checked;
        }

        private async void ScheduleBinaryPreview(bool debounce)
        {
            if (_disposed || !IsBinaryPreviewEnabled()) return;

            CancellationTokenSource cancellation =
                CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            lock (_binaryPreviewSync)
            {
                if (_binaryPreviewCancellation != null) _binaryPreviewCancellation.Cancel();
                _binaryPreviewCancellation = cancellation;
            }

            Bitmap source = null;
            List<DetectionRoi> regions;
            long generation;
            lock (_displaySync)
            {
                generation = _displayGeneration;
                if (_lockedDisplayImage != null) source = new Bitmap(_lockedDisplayImage);
                regions = _lastInspectionOverlayGeneration == generation
                    ? _lastDetectionRegions.Select(CloneDetectionRoi).ToList()
                    : new List<DetectionRoi>();
            }

            if (source == null)
            {
                lock (_binaryPreviewSync)
                    if (ReferenceEquals(_binaryPreviewCancellation, cancellation))
                        _binaryPreviewCancellation = null;
                cancellation.Dispose();
                _view.SetOperationStatus("二值预览需要已拍摄或已导入的图像", false, true);
                return;
            }

            try
            {
                DetectionProfile profile = ReadDetectionProfileFromView();
                if (profile.TemplateMatchingEnabled && regions.Count == 0)
                {
                    _view.SetOperationStatus("正在定位检测 ROI...", true, false);
                    int maximumMatches = Decimal.ToInt32(_view.ExpectedBoardCountInput.Value) + 1;
                    TemplateMatchCollectionResult matchResult = await _runtime
                        .TestPositioningTemplateAsync(
                            source,
                            maximumMatches,
                            false,
                            cancellation.Token)
                        .ConfigureAwait(true);
                    CalibrationJob job = _runtime.GetPositioningJobSnapshot();
                    if (matchResult != null && matchResult.Success && job != null && job.Template != null)
                    {
                        regions = (matchResult.Matches ?? new List<TemplateMatchResult>())
                            .Where(match => match != null && match.Success)
                            .Select(match => _binaryAnalyzer.ResolveDetectionRoi(job.Template, match, profile))
                            .Where(region => region != null)
                            .ToList();
                    }
                    if (regions.Count == 0)
                        throw new InvalidOperationException("模板定位后没有得到有效检测 ROI。请检查模板和检测 ROI。");

                    lock (_displaySync)
                    {
                        if (generation == _displayGeneration)
                        {
                            _lastDetectionRegions = regions.Select(CloneDetectionRoi).ToList();
                            _lastInspectionOverlayGeneration = generation;
                        }
                    }
                }

                _view.SetOperationStatus("正在更新二值预览...", true, false);
                if (debounce) await Task.Delay(180, cancellation.Token).ConfigureAwait(true);
                BinaryPreviewPayload preview = await Task.Run(
                    () => BuildBinaryPreview(source, regions, profile, cancellation.Token),
                    cancellation.Token).ConfigureAwait(true);
                source.Dispose();
                source = null;

                bool current;
                lock (_displaySync) current = generation == _displayGeneration;
                if (!current || cancellation.IsCancellationRequested || !IsBinaryPreviewEnabled())
                {
                    preview.Dispose();
                    return;
                }

                _view.DisplayOwnedImage(preview.TakeImage());
                _view.Viewport.SetPointOverlays(null);
                _view.Viewport.SetContourOverlays(preview.Contours);
                _view.Viewport.SetRoiOverlays(null);
                _view.SetImageInformation(string.Format(
                    CultureInfo.InvariantCulture,
                    "局部二值   {0}   {1}   形态学 {2}px   ROI 外保留原图",
                    profile.DetectDarkForeground ? "白底黑点" : "黑底白点",
                    profile.AutoThreshold ? "自动阈值" : "阈值 " + profile.BrightThreshold,
                    profile.MorphologyRadius));
                _view.SetOperationStatus("二值预览已更新", false, false);
                preview.Dispose();
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exception)
            {
                if (!_disposed) _view.SetOperationStatus("二值预览失败：" + exception.Message, false, true);
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

        private BinaryPreviewPayload BuildBinaryPreview(
            Bitmap source,
            IList<DetectionRoi> regions,
            DetectionProfile profile,
            CancellationToken cancellationToken)
        {
            BinaryPreviewPayload payload = new BinaryPreviewPayload();
            Bitmap combined = new Bitmap(source);
            payload.Image = combined;
            try
            {
                using (Graphics graphics = Graphics.FromImage(combined))
                {
                    graphics.CompositingMode = CompositingMode.SourceCopy;
                    graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
                    graphics.PixelOffsetMode = PixelOffsetMode.Half;

                    IEnumerable<DetectionRoi> targets = regions != null && regions.Count > 0
                        ? regions
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
                    foreach (DetectionRoi roi in targets)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        using (BlobAnalysisResult analysis = _binaryAnalyzer.AnalyzeRoiDetailed(
                            source, roi, profile, true, cancellationToken))
                        {
                            if (analysis.BinaryImage == null || analysis.ImageBounds.Width <= 0 ||
                                analysis.ImageBounds.Height <= 0) continue;
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
                            foreach (BlobFinding finding in analysis.Findings ?? new List<BlobFinding>())
                            {
                                if (finding == null || finding.RejectedAsRectangle ||
                                    finding.Contour == null || finding.Contour.Count < 2) continue;
                                payload.Contours.Add(new ViewportContourOverlay
                                {
                                    Points = finding.Contour.ToList(),
                                    Color = Color.LimeGreen,
                                    LineWidth = 2.0F
                                });
                            }
                        }
                    }
                }
                return payload;
            }
            catch
            {
                payload.Dispose();
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

        private void RestoreSourceDisplay()
        {
            CancelBinaryPreview();
            Bitmap source;
            List<ViewportPointOverlay> points;
            List<ViewportRoiOverlay> rois;
            List<ViewportContourOverlay> contours;
            lock (_displaySync)
            {
                source = _lockedDisplayImage == null ? null : new Bitmap(_lockedDisplayImage);
                bool current = _lastInspectionOverlayGeneration == _displayGeneration;
                points = current ? _lastInspectionPoints : new List<ViewportPointOverlay>();
                rois = current ? _lastInspectionRois : new List<ViewportRoiOverlay>();
                contours = current ? _lastInspectionContours : new List<ViewportContourOverlay>();
            }
            if (source == null) return;
            _view.DisplayOwnedImage(source);
            _view.Viewport.SetPointOverlays(null);
            _view.Viewport.SetRoiOverlays(null);
            _view.Viewport.SetContourOverlays(contours);
            _view.SetImageInformation("原图   检测输入");
            _view.SetOperationStatus("已切换为原图", false, false);
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
            lock (_displaySync)
            {
                if (_lockedDisplayImage != null) return (Bitmap)_lockedDisplayImage.Clone();
            }

            Bitmap snapshot = _view.Viewport.Snapshot();
            if (snapshot == null) return null;

            Bitmap lockedCopy = (Bitmap)snapshot.Clone();
            PinOwnedDisplayImage(lockedCopy, "检测输入", false);
            return snapshot;
        }

        private void PinOwnedDisplayImage(
            Bitmap image,
            string source,
            bool inspectionFrame,
            DateTime? capturedAtUtc = null)
        {
            if (image == null) return;
            CancelBinaryPreview();
            Bitmap displayCopy = new Bitmap(image);
            Bitmap previous = null;
            lock (_displaySync)
            {
                if (_disposed)
                {
                    image.Dispose();
                    displayCopy.Dispose();
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
            }
            if (previous != null) previous.Dispose();
        }

        private void ResumeLivePreview()
        {
            CancelBinaryPreview();
            CheckBox binaryToggle = FindControl<CheckBox>("chkBinaryPreview");
            if (binaryToggle != null && binaryToggle.Checked) binaryToggle.Checked = false;
            Bitmap previous;
            lock (_displaySync)
            {
                previous = _lockedDisplayImage;
                _lockedDisplayImage = null;
                _displayMode = DisplayMode.LivePreview;
                _displayGeneration++;
                _inspectionFrameGeneration = -1;
                ClearInspectionPreviewCache();
                if (!_disposed) _view.Viewport.ClearOverlays();
            }
            if (previous != null) previous.Dispose();
        }

        private void ClearInspectionPreviewCache()
        {
            _lastDetectionRegions = new List<DetectionRoi>();
            _lastInspectionPoints = new List<ViewportPointOverlay>();
            _lastInspectionRois = new List<ViewportRoiOverlay>();
            _lastInspectionContours = new List<ViewportContourOverlay>();
            _lastInspectionOverlayGeneration = -1;
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

        private sealed class BinaryPreviewPayload : IDisposable
        {
            public BinaryPreviewPayload()
            {
                Contours = new List<ViewportContourOverlay>();
            }

            public Bitmap Image { get; set; }
            public List<ViewportContourOverlay> Contours { get; private set; }

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
