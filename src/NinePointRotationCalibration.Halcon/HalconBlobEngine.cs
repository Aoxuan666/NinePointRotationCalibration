using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Runtime.InteropServices;
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
        public HalconBlobParameters()
        {
            FillHoles = true;
            FillDarkHoles = true;
        }

        public bool AutoThreshold { get; set; }
        public bool DetectDarkForeground { get; set; }
        public int Threshold { get; set; }
        public double MinimumArea { get; set; }
        public double MaximumArea { get; set; }
        public bool RectangleFilterEnabled { get; set; }
        public double RectangleFillThreshold { get; set; }
        public bool FillHoles { get; set; }
        /// <summary>检测暗目标（黑色叉/黑点）时是否填充黑色目标内部空洞。</summary>
        public bool FillDarkHoles { get; set; }
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
                    using (HRegion reducedDomain = reduced.GetDomain())
                    {
                        double domainRow, domainCol;
                        int domainArea = reducedDomain.AreaCenter(out domainRow, out domainCol);
                        if (domainArea <= 0)
                        {
                            result.EffectiveThreshold = Math.Max(0, Math.Min(255, parameters.Threshold));
                            return result;
                        }

                        Stopwatch stageWatch = Stopwatch.StartNew();
                        if (parameters.AutoThreshold)
                        {
                            try
                            {
                                int usedThreshold;
                                thresholded = reduced.BinaryThreshold(
                                    "max_separability",
                                    parameters.DetectDarkForeground ? "dark" : "light",
                                    out usedThreshold);
                                result.EffectiveThreshold = Math.Max(0, Math.Min(255, usedThreshold));
                            }
                            catch (HalconException)
                            {
                                // 纯色或无方差区域可能无法计算自动分割阈值，安全退化为空区域
                                thresholded = new HRegion();
                                thresholded.GenEmptyRegion();
                                result.EffectiveThreshold = Math.Max(0, Math.Min(255, parameters.Threshold));
                            }
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
                bool shouldFillHoles = parameters.DetectDarkForeground
                    ? parameters.FillDarkHoles
                    : parameters.FillHoles;
                filled = shouldFillHoles ? morphed.FillUp() : new HRegion(morphed);
                fillWatch.Stop();
                result.HoleFillElapsedMilliseconds = fillWatch.Elapsed.TotalMilliseconds;

                if (CancellationRequested(cancellationToken, throwOnCancellation))
                    return null;
                Stopwatch componentWatch = Stopwatch.StartNew();
                connected = filled.Connection();
                int connectedCount = connected.CountObj();
                double minArea = Math.Max(0.0, parameters.MinimumArea);
                double maxArea = Math.Max(minArea, parameters.MaximumArea);
                bool hasValidCandidate = false;
                bool rejectedAsRectangle = false;

                if (connectedCount > 0)
                {
                    // 先按面积范围筛选候选连通域，避免大面积背景亮斑或超大矩形干扰遮蔽真实的叉号缺陷（漏检）。
                    using (HRegion areaFiltered = connected.SelectShape("area", "and", minArea, maxArea))
                    {
                        if (areaFiltered.CountObj() > 0)
                        {
                            hasValidCandidate = true;
                            if (parameters.RectangleFilterEnabled)
                            {
                                double rectThreshold = Math.Max(0.0, Math.Min(1.0, parameters.RectangleFillThreshold));
                                double nonRectMax = Math.Max(0.0, rectThreshold - 1e-6);
                                using (HRegion nonRect = nonRectMax > 0.0
                                    ? areaFiltered.SelectShape("rectangularity", "and", 0.0, nonRectMax)
                                    : CreateEmptyRegion())
                                {
                                    if (nonRect.CountObj() > 0)
                                    {
                                        // 优先选择面积最大的非矩形合法目标，杜绝同 ROI 内较大矩形标签掩盖小面积 X 标记。
                                        selected = SelectSingleLargest(nonRect);
                                        rejectedAsRectangle = false;
                                    }
                                    else
                                    {
                                        selected = SelectSingleLargest(areaFiltered);
                                        rejectedAsRectangle = true;
                                    }
                                }
                            }
                            else
                            {
                                selected = SelectSingleLargest(areaFiltered);
                                rejectedAsRectangle = false;
                            }
                        }
                        else
                        {
                            // 无满足面积区间的目标时回退到最大连通域供调试界面展示诊断面积，HasFinding 置 false。
                            selected = SelectSingleLargest(connected);
                        }
                    }
                }

                if (selected != null && selected.CountObj() > 0)
                {
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
                    result.RejectedAsRectangle = hasValidCandidate
                        ? rejectedAsRectangle
                        : (parameters.RectangleFilterEnabled && result.Rectangularity >= parameters.RectangleFillThreshold);
                    result.HasFinding = hasValidCandidate && area >= minArea && area <= maxArea;
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
            // 直接按 HALCON 区域行程（Runs）写入 8bpp 灰度位图，省去临时 HImage 构造、PaintRegion 与千万级字节中间数组分配
            int width = Math.Max(1, bounds.Width);
            int height = Math.Max(1, bounds.Height);
            byte bgByte = detectDarkForeground ? (byte)255 : (byte)0;
            byte fgByte = detectDarkForeground ? (byte)0 : (byte)255;

            Bitmap bitmap = new Bitmap(width, height, PixelFormat.Format8bppIndexed);
            ColorPalette palette = bitmap.Palette;
            for (int index = 0; index < palette.Entries.Length; index++)
                palette.Entries[index] = Color.FromArgb(index, index, index);
            bitmap.Palette = palette;

            byte[] bgRow = new byte[width];
            byte[] fgRow = new byte[width];
            if (bgByte != 0)
                for (int i = 0; i < width; i++) bgRow[i] = bgByte;
            if (fgByte != 0)
                for (int i = 0; i < width; i++) fgRow[i] = fgByte;

            BitmapData data = null;
            HTuple runRows = null;
            HTuple colBegins = null;
            HTuple colEnds = null;
            try
            {
                data = bitmap.LockBits(
                    new Rectangle(0, 0, width, height),
                    ImageLockMode.WriteOnly,
                    PixelFormat.Format8bppIndexed);
                int stride = Math.Abs(data.Stride);
                for (int y = 0; y < height; y++)
                {
                    IntPtr rowPtr = data.Stride >= 0
                        ? IntPtr.Add(data.Scan0, y * data.Stride)
                        : IntPtr.Add(data.Scan0, (height - 1 - y) * stride);
                    Marshal.Copy(bgRow, 0, rowPtr, width);
                }

                foreground.GetRegionRuns(out runRows, out colBegins, out colEnds);
                int count = Math.Min(runRows.Length, Math.Min(colBegins.Length, colEnds.Length));
                if (count > 0)
                {
                    int[] rowsArr = runRows.ToIArr();
                    int[] beginArr = colBegins.ToIArr();
                    int[] endArr = colEnds.ToIArr();
                    int top = bounds.Top;
                    int left = bounds.Left;
                    for (int i = 0; i < count; i++)
                    {
                        int localY = rowsArr[i] - top;
                        if (localY < 0 || localY >= height) continue;
                        int startX = beginArr[i] - left;
                        int endX = endArr[i] - left;
                        if (endX < 0 || startX >= width) continue;
                        if (startX < 0) startX = 0;
                        if (endX >= width) endX = width - 1;
                        int len = endX - startX + 1;
                        if (len <= 0) continue;
                        IntPtr rowPtr = data.Stride >= 0
                            ? IntPtr.Add(data.Scan0, localY * data.Stride + startX)
                            : IntPtr.Add(data.Scan0, (height - 1 - localY) * stride + startX);
                        Marshal.Copy(fgRow, 0, rowPtr, len);
                    }
                }
                return bitmap;
            }
            catch
            {
                if (data != null)
                {
                    bitmap.UnlockBits(data);
                    data = null;
                }
                bitmap.Dispose();
                throw;
            }
            finally
            {
                if (runRows != null) runRows.Dispose();
                if (colBegins != null) colBegins.Dispose();
                if (colEnds != null) colEnds.Dispose();
                if (data != null) bitmap.UnlockBits(data);
            }
        }

        private static HRegion SelectSingleLargest(HRegion candidates)
        {
            if (candidates == null || candidates.CountObj() <= 0)
                return CreateEmptyRegion();

            using (HRegion maxAreaRegions = candidates.SelectShapeStd("max_area", 70.0))
            {
                if (maxAreaRegions.CountObj() <= 0)
                    return CreateEmptyRegion();
                // 两个连通域面积完全相等时 SelectShapeStd 会返回多对象元组，必须 SelectObj(1) 避免标量 AreaCenter 抛出异常。
                return maxAreaRegions.SelectObj(1);
            }
        }

        private static HRegion CreateEmptyRegion()
        {
            HRegion empty = new HRegion();
            empty.GenEmptyRegion();
            return empty;
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
