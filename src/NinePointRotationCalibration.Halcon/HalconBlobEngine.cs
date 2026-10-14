using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Threading;
using HalconDotNet;

namespace NinePointRotationCalibration.Halcon
{
    public sealed class HalconBlobRegion
    {
        public double Row { get; set; }
        public double Column { get; set; }
        public double PhiDegrees { get; set; }
        public double HalfWidth { get; set; }
        public double HalfHeight { get; set; }
        public Rectangle ImageBounds { get; set; }
    }

    public sealed class HalconBlobParameters
    {
        public bool AutoThreshold { get; set; }
        public bool DetectDarkForeground { get; set; }
        public int Threshold { get; set; }
        public double MinimumArea { get; set; }
        public double MaximumArea { get; set; }
        public bool RectangleFilterEnabled { get; set; }
        public double RectangleFillThreshold { get; set; }
        public bool FillHoles { get; set; }
        public int MorphologyRadius { get; set; }
    }

    public sealed class HalconBlobResult : IDisposable
    {
        public HalconBlobResult()
        {
            Contour = new List<ImagePoint>();
        }

        public Rectangle ImageBounds { get; set; }
        public Bitmap BinaryImage { get; set; }
        public bool HasFinding { get; set; }
        public double Row { get; set; }
        public double Column { get; set; }
        public double Area { get; set; }
        public double Rectangularity { get; set; }
        public bool RejectedAsRectangle { get; set; }
        public List<ImagePoint> Contour { get; private set; }
        public int ActivePixelCount { get; set; }
        public int EffectiveThreshold { get; set; }
        public double ImageConversionElapsedMilliseconds { get; set; }
        public double ThresholdElapsedMilliseconds { get; set; }
        public double MorphologyElapsedMilliseconds { get; set; }
        public double HoleFillElapsedMilliseconds { get; set; }
        public double BinaryImageElapsedMilliseconds { get; set; }
        public double ComponentElapsedMilliseconds { get; set; }
        public double ElapsedMilliseconds { get; set; }

        public void Dispose()
        {
            Bitmap image = BinaryImage;
            BinaryImage = null;
            if (image != null) image.Dispose();
        }
    }

    public static class HalconBlobEngine
    {
        public static List<HalconBlobResult> Analyze(
            Bitmap source,
            IList<HalconBlobRegion> regions,
            HalconBlobParameters parameters,
            bool includeBinary,
            CancellationToken cancellationToken)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (regions == null) throw new ArgumentNullException(nameof(regions));
            if (parameters == null) throw new ArgumentNullException(nameof(parameters));

            List<HalconBlobResult> results = new List<HalconBlobResult>(regions.Count);
            Stopwatch conversionWatch = Stopwatch.StartNew();
            try
            {
                using (HImage gray = HalconImageConverter.ToGrayHImage(source))
                {
                    conversionWatch.Stop();
                    for (int index = 0; index < regions.Count; index++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        HalconBlobResult result = AnalyzeRegion(
                            gray,
                            regions[index],
                            parameters,
                            includeBinary,
                            cancellationToken);
                        if (index == 0)
                            result.ImageConversionElapsedMilliseconds = conversionWatch.Elapsed.TotalMilliseconds;
                        results.Add(result);
                    }
                }
                return results;
            }
            catch
            {
                foreach (HalconBlobResult result in results) result.Dispose();
                throw;
            }
        }

        private static HalconBlobResult AnalyzeRegion(
            HImage gray,
            HalconBlobRegion input,
            HalconBlobParameters parameters,
            bool includeBinary,
            CancellationToken cancellationToken)
        {
            Stopwatch totalWatch = Stopwatch.StartNew();
            HalconBlobResult result = new HalconBlobResult
            {
                ImageBounds = input == null ? Rectangle.Empty : input.ImageBounds
            };
            HRegion thresholded = null;
            HRegion morphed = null;
            HRegion filled = null;
            HRegion connected = null;
            HRegion selected = null;
            try
            {
                if (input == null || input.ImageBounds.Width <= 0 || input.ImageBounds.Height <= 0)
                    return result;

                using (HRegion roi = new HRegion())
                {
                    roi.GenRectangle2(
                        input.Row,
                        input.Column,
                        -input.PhiDegrees * Math.PI / 180.0,
                        input.HalfWidth,
                        input.HalfHeight);
                    using (HImage reduced = gray.ReduceDomain(roi))
                    {
                        Stopwatch stageWatch = Stopwatch.StartNew();
                        if (parameters.AutoThreshold)
                        {
                            int usedThreshold;
                            thresholded = reduced.BinaryThreshold(
                                "max_separability",
                                parameters.DetectDarkForeground ? "dark" : "light",
                                out usedThreshold);
                            result.EffectiveThreshold = Math.Max(0, Math.Min(255, usedThreshold));
                        }
                        else
                        {
                            int threshold = Math.Max(0, Math.Min(255, parameters.Threshold));
                            thresholded = parameters.DetectDarkForeground
                                ? reduced.Threshold(0.0, threshold)
                                : reduced.Threshold(threshold, 255.0);
                            result.EffectiveThreshold = threshold;
                        }
                        stageWatch.Stop();
                        result.ThresholdElapsedMilliseconds = stageWatch.Elapsed.TotalMilliseconds;
                    }
                }

                cancellationToken.ThrowIfCancellationRequested();
                Stopwatch morphologyWatch = Stopwatch.StartNew();
                int radius = Math.Max(0, Math.Min(100, parameters.MorphologyRadius));
                morphed = radius > 0
                    ? thresholded.OpeningCircle((double)radius)
                    : new HRegion(thresholded);
                morphologyWatch.Stop();
                result.MorphologyElapsedMilliseconds = morphologyWatch.Elapsed.TotalMilliseconds;

                cancellationToken.ThrowIfCancellationRequested();
                Stopwatch fillWatch = Stopwatch.StartNew();
                filled = parameters.FillHoles ? morphed.FillUp() : new HRegion(morphed);
                fillWatch.Stop();
                result.HoleFillElapsedMilliseconds = fillWatch.Elapsed.TotalMilliseconds;

                cancellationToken.ThrowIfCancellationRequested();
                Stopwatch componentWatch = Stopwatch.StartNew();
                connected = filled.Connection();
                selected = connected.CountObj() > 0
                    ? connected.SelectShapeStd("max_area", 70.0)
                    : new HRegion(connected);

                double row;
                double column;
                int area = selected.AreaCenter(out row, out column);
                result.ActivePixelCount = Math.Max(0, area);
                result.Area = area;
                result.Row = row;
                result.Column = column;
                if (area > 0)
                {
                    using (HTuple rectangularity = selected.Rectangularity())
                    {
                        result.Rectangularity = rectangularity.Length == 0 ? 0.0 : rectangularity[0].D;
                    }
                }
                result.RejectedAsRectangle = parameters.RectangleFilterEnabled &&
                    result.Rectangularity >= parameters.RectangleFillThreshold;
                result.HasFinding = area >= parameters.MinimumArea && area <= parameters.MaximumArea;
                if (result.HasFinding)
                {
                    using (HXLDCont contours = selected.GenContourRegionXld("border"))
                    {
                        List<List<ImagePoint>> segments = HalconContourConverter.Extract(contours);
                        List<ImagePoint> largest = segments.OrderByDescending(segment => segment.Count).FirstOrDefault();
                        if (largest != null)
                        {
                            result.Contour.AddRange(largest);
                            if (largest.Count > 0)
                            {
                                ImagePoint first = largest[0];
                                ImagePoint last = largest[largest.Count - 1];
                                if (first.Row != last.Row || first.Column != last.Column)
                                    result.Contour.Add(first);
                            }
                        }
                    }
                }
                componentWatch.Stop();
                result.ComponentElapsedMilliseconds = componentWatch.Elapsed.TotalMilliseconds;

                if (includeBinary)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Stopwatch binaryWatch = Stopwatch.StartNew();
                    result.BinaryImage = RenderBinary(
                        filled,
                        input.ImageBounds,
                        parameters.DetectDarkForeground);
                    binaryWatch.Stop();
                    result.BinaryImageElapsedMilliseconds = binaryWatch.Elapsed.TotalMilliseconds;
                }

                return result;
            }
            catch
            {
                result.Dispose();
                throw;
            }
            finally
            {
                if (selected != null) selected.Dispose();
                if (connected != null) connected.Dispose();
                if (filled != null) filled.Dispose();
                if (morphed != null) morphed.Dispose();
                if (thresholded != null) thresholded.Dispose();
                totalWatch.Stop();
                result.ElapsedMilliseconds = totalWatch.Elapsed.TotalMilliseconds;
            }
        }

        private static Bitmap RenderBinary(
            HRegion foreground,
            Rectangle bounds,
            bool detectDarkForeground)
        {
            using (HImage black = new HImage())
            {
                black.GenImageConst("byte", bounds.Width, bounds.Height);
                HImage background = null;
                HImage painted = null;
                try
                {
                    background = detectDarkForeground
                        ? black.ScaleImage(0.0, 255.0)
                        : black.CopyImage();
                    using (HRegion local = foreground.MoveRegion(-bounds.Top, -bounds.Left))
                    {
                        painted = local.PaintRegion(
                            background,
                            detectDarkForeground ? 0.0 : 255.0,
                            "fill");
                    }
                    return HalconImageConverter.ToGrayBitmap(painted);
                }
                finally
                {
                    if (painted != null) painted.Dispose();
                    if (background != null) background.Dispose();
                }
            }
        }
    }
}
