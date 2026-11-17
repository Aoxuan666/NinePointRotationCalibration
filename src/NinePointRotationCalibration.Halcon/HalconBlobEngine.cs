using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Threading;
using HalconDotNet;

namespace NinePointRotationCalibration.Halcon
{
    /// <summary>Blob 检测使用的旋转矩形 ROI 及其所在图像裁剪范围。</summary>
    public sealed class HalconBlobRegion
    {
        public double Row { get; set; }
        public double Column { get; set; }
        /// <summary>屏幕坐标系下的旋转角度（度，行向下坐标中顺时针为正）。</summary>
        public double PhiDegrees { get; set; }
        public double HalfWidth { get; set; }
        public double HalfHeight { get; set; }
        public Rectangle ImageBounds { get; set; }
    }

    /// <summary>Blob 分割、形态学与形状过滤参数。</summary>
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

    /// <summary>单个 ROI 的 Blob 检测结果及各阶段耗时。</summary>
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
        /// <summary>最大连通域的矩形度（面积与最小外接矩形面积之比）。</summary>
        public double Rectangularity { get; set; }
        /// <summary>是否因矩形度达到阈值而被判定为矩形干扰并拒识。</summary>
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

        /// <summary>释放结果持有的二值预览 Bitmap。</summary>
        public void Dispose()
        {
            Bitmap image = BinaryImage;
            BinaryImage = null;
            if (image != null) image.Dispose();
        }
    }

    /// <summary>基于 HALCON 的 Blob 区域检测引擎。</summary>
    public static class HalconBlobEngine
    {
        /// <summary>使用 HALCON 对每个 ROI 执行阈值、形态学、连通域和轮廓分析。</summary>
        public static List<HalconBlobResult> Analyze(
            Bitmap source,
            IList<HalconBlobRegion> regions,
            HalconBlobParameters parameters,
            bool includeBinary,
            CancellationToken cancellationToken)
        {
            return Analyze(source, regions, parameters, includeBinary, cancellationToken, true);
        }

        /// <summary>执行 Blob 分析，并支持在取消时选择抛出异常或提前返回。</summary>
        // 预览调用方可以取消过期任务，避免正常刷新被误报为未处理的 HALCON 异常。
        public static List<HalconBlobResult> Analyze(
            Bitmap source,
            IList<HalconBlobRegion> regions,
            HalconBlobParameters parameters,
            bool includeBinary,
            CancellationToken cancellationToken,
            bool throwOnCancellation)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (regions == null) throw new ArgumentNullException(nameof(regions));
            if (parameters == null) throw new ArgumentNullException(nameof(parameters));

            using (HalconGrayImage gray = HalconGrayImage.FromBitmap(source))
            {
                List<HalconBlobResult> results = Analyze(
                    gray,
                    regions,
                    parameters,
                    includeBinary,
                    cancellationToken,
                    throwOnCancellation);
                if (results.Count > 0)
                    results[0].ImageConversionElapsedMilliseconds = gray.ConversionElapsedMilliseconds;
                return results;
            }
        }

        /// <summary>
        /// 在调用方已经转换好的灰度图上分析区域（例如模板匹配使用的图像），不会重复转换图像。
        /// </summary>
        public static List<HalconBlobResult> Analyze(
            HalconGrayImage gray,
            IList<HalconBlobRegion> regions,
            HalconBlobParameters parameters,
            bool includeBinary,
            CancellationToken cancellationToken,
            bool throwOnCancellation)
        {
            if (gray == null) throw new ArgumentNullException(nameof(gray));
            if (regions == null) throw new ArgumentNullException(nameof(regions));
            if (parameters == null) throw new ArgumentNullException(nameof(parameters));

            List<HalconBlobResult> results = new List<HalconBlobResult>(regions.Count);
            try
            {
                for (int index = 0; index < regions.Count; index++)
                {
                    if (CancellationRequested(cancellationToken, throwOnCancellation))
                        break;
                    HalconBlobResult result = AnalyzeRegion(
                        gray.Image,
                        regions[index],
                        parameters,
                        includeBinary,
                        cancellationToken,
                        throwOnCancellation);
                    if (result == null)
                        break;
                    results.Add(result);
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
            CancellationToken cancellationToken,
            bool throwOnCancellation)
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
                    // PhiDegrees 在行向下屏幕坐标系中顺时针为正，HALCON GenRectangle2 以逆时针为正，需取反。
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

                if (CancellationRequested(cancellationToken, throwOnCancellation))
                    return null;
                Stopwatch morphologyWatch = Stopwatch.StartNew();
                int radius = Math.Max(0, Math.Min(100, parameters.MorphologyRadius));
                morphed = radius > 0
                    ? thresholded.OpeningCircle((double)radius)
                    : new HRegion(thresholded);
                morphologyWatch.Stop();
                result.MorphologyElapsedMilliseconds = morphologyWatch.Elapsed.TotalMilliseconds;

                if (CancellationRequested(cancellationToken, throwOnCancellation))
                    return null;
                Stopwatch fillWatch = Stopwatch.StartNew();
                filled = parameters.FillHoles ? morphed.FillUp() : new HRegion(morphed);
                fillWatch.Stop();
                result.HoleFillElapsedMilliseconds = fillWatch.Elapsed.TotalMilliseconds;

                if (CancellationRequested(cancellationToken, throwOnCancellation))
                    return null;
                Stopwatch componentWatch = Stopwatch.StartNew();
                connected = filled.Connection();
                // 仅保留面积最大的单个连通域作为候选目标，再判断面积与矩形度筛选条件。
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
                            // 确保边界轮廓首尾闭合以供绘制。
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
                    if (CancellationRequested(cancellationToken, throwOnCancellation))
                        return null;
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
                    // 将整图坐标下的前景区域平移到 ROI 局部裁剪坐标系后再填充绘制二值图。
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

        private static bool CancellationRequested(
            CancellationToken cancellationToken,
            bool throwOnCancellation)
        {
            if (!cancellationToken.IsCancellationRequested) return false;
            if (throwOnCancellation) cancellationToken.ThrowIfCancellationRequested();
            return true;
        }
    }
}
