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
            ClosingRadius = 8;
            SmoothEdges = true;
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
        /// <summary>形态学开运算去噪与外凸边缘光滑半径（像素）。</summary>
        public int MorphologyRadius { get; set; }
        /// <summary>形态学闭运算断笔桥接与内凹边缘光滑半径（像素，可桥接约 2 倍半径的丝印/焊盘断裂）。</summary>
        public int ClosingRadius { get; set; }
        /// <summary>是否启用 ROI 局部轻度灰度预平滑与 XLD 亚像素轮廓平滑以消除边缘锯齿。</summary>
        public bool SmoothEdges { get; set; }
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

                int openRadius = Math.Max(0, Math.Min(100, parameters.MorphologyRadius));
                int closeRadius = Math.Max(0, Math.Min(100, parameters.ClosingRadius));
                double roiShortHalf = Math.Min(
                    Math.Max(1.0, input.HalfWidth),
                    Math.Max(1.0, input.HalfHeight));
                // 在极小 ROI（如合成单测 36x36）中保护闭运算半径不过度填满整框；正常 20MP PCB ROI（半宽通常 > 120px）不受限。
                int effectiveCloseRadius = closeRadius > 0
                    ? Math.Max(1, Math.Min(closeRadius, Math.Max(1, (int)Math.Floor(roiShortHalf * 0.25))))
                    : 0;
                double minArea = Math.Max(0.0, parameters.MinimumArea);
                double maxArea = Math.Max(minArea, parameters.MaximumArea);

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
                        // 启用边缘平滑且开启去噪或桥接时，仅在 ROI 局部执行 3x3 轻度高斯平滑（保持原图坐标系不变），
                        // 提前压制高频像素噪点与微弱反光毛刺，大幅减轻二值化边缘锯齿与细微断墨。
                        bool applyGraySmooth = parameters.SmoothEdges && (openRadius > 0 || effectiveCloseRadius > 0);
                        HImage smoothedImage = null;
                        try
                        {
                            HImage thresholdSource = reduced;
                            if (applyGraySmooth)
                            {
                                smoothedImage = reduced.GaussFilter(3);
                                thresholdSource = smoothedImage;
                            }

                            if (parameters.AutoThreshold)
                            {
                                try
                                {
                                    int usedThreshold;
                                    thresholded = thresholdSource.BinaryThreshold(
                                        "max_separability",
                                        parameters.DetectDarkForeground ? "dark" : "light",
                                        out usedThreshold);
                                    result.EffectiveThreshold = Math.Max(0, Math.Min(255, usedThreshold));
                                }
                                catch (HalconException)
                                {
                                    // 纯色或无方差区域可能无法计算自动分割阈值，安全退化为空区域
                                    thresholded = CreateEmptyRegion();
                                    result.EffectiveThreshold = Math.Max(0, Math.Min(255, parameters.Threshold));
                                }
                            }
                            else
                            {
                                int threshold = Math.Max(0, Math.Min(255, parameters.Threshold));
                                thresholded = parameters.DetectDarkForeground
                                    ? thresholdSource.Threshold(0.0, threshold)
                                    : thresholdSource.Threshold(threshold, 255.0);
                                result.EffectiveThreshold = threshold;
                            }
                        }
                        finally
                        {
                            if (smoothedImage != null) smoothedImage.Dispose();
                        }
                        stageWatch.Stop();
                        result.ThresholdElapsedMilliseconds = stageWatch.Elapsed.TotalMilliseconds;
                    }
                }

                if (CancellationRequested(cancellationToken, throwOnCancellation))
                    return null;
                Stopwatch morphologyWatch = Stopwatch.StartNew();
                morphed = ApplyMorphology(
                    thresholded,
                    openRadius,
                    effectiveCloseRadius,
                    minArea);
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
                bool hasValidCandidate = false;
                bool rejectedAsRectangle = false;
                double contourBridgeRadius = 0.0;

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
                    }

                    // 若单连通域未找到合法非矩形标记（例如白丝印/大焊盘造成超宽断裂把整条叉切成两段，
                    // 导致每段各自低于最小面积），对邻近的实质性非矩形碎片执行受限距离聚簇恢复，防止坏板漏检。
                    if ((!hasValidCandidate || rejectedAsRectangle) && effectiveCloseRadius > 0 && connectedCount >= 2)
                    {
                        double clusterBridgeRadius;
                        HRegion recovered = TryRecoverBrokenCrossCluster(
                            connected,
                            minArea,
                            maxArea,
                            parameters.RectangleFilterEnabled,
                            parameters.RectangleFillThreshold,
                            effectiveCloseRadius,
                            out clusterBridgeRadius);
                        if (recovered != null)
                        {
                            if (selected != null) selected.Dispose();
                            selected = recovered;
                            hasValidCandidate = true;
                            rejectedAsRectangle = false;
                            contourBridgeRadius = clusterBridgeRadius;
                        }
                    }

                    if (selected == null)
                    {
                        // 无满足面积区间的目标时回退到最大连通域供调试界面展示诊断面积，HasFinding 置 false。
                        selected = SelectSingleLargest(connected);
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
                        List<ImagePoint> smoothContour = ExtractSmoothPrimaryContour(
                            selected,
                            parameters.SmoothEdges,
                            Math.Max(openRadius, effectiveCloseRadius),
                            contourBridgeRadius);
                        result.Contour.AddRange(smoothContour);
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

        /// <summary>
        /// 打叉板抗断裂与双侧边缘光滑形态学链路：
        /// 1. 闭运算前按面积剔除孤立细碎噪点（不腐蚀主笔画细颈，避免把浮动黑点粘连成边缘凸瘤）；
        /// 2. 圆形闭运算（ClosingCircle）桥接白丝印/焊盘/反光引起的笔画断裂并填平内凹锯齿；
        /// 3. 圆形开运算（OpeningCircle）在笔画加固后削平外凸毛刺，形成 Close-Open 交替平滑滤波器。
        /// </summary>
        private static HRegion ApplyMorphology(
            HRegion thresholded,
            int openRadius,
            int closeRadius,
            double minArea)
        {
            if (thresholded == null || thresholded.CountObj() <= 0)
                return CreateEmptyRegion();

            double dummyRow, dummyCol;
            if (thresholded.AreaCenter(out dummyRow, out dummyCol) <= 0)
                return CreateEmptyRegion();

            // 未启用桥接闭运算时保持纯开运算去噪路径，兼容小圆点/精细斑点调试场景
            if (closeRadius <= 0)
            {
                return openRadius > 0
                    ? thresholded.OpeningCircle((double)openRadius)
                    : new HRegion(thresholded);
            }

            HRegion cleaned = null;
            HRegion closed = null;
            try
            {
                if (openRadius > 0)
                {
                    double speckByRadius = Math.PI * openRadius * openRadius * 2.0;
                    double speckLimit = minArea > 0.0
                        ? Math.Min(speckByRadius, minArea * 0.05)
                        : 0.0;
                    if (speckLimit >= 2.0)
                    {
                        using (HRegion rawConn = thresholded.Connection())
                        using (HRegion kept = rawConn.SelectShape("area", "and", speckLimit, 1.0e12))
                        {
                            cleaned = kept.CountObj() > 0 ? kept.Union1() : CreateEmptyRegion();
                        }
                    }
                    else
                    {
                        cleaned = new HRegion(thresholded);
                    }
                }
                else
                {
                    cleaned = new HRegion(thresholded);
                }

                if (cleaned.AreaCenter(out dummyRow, out dummyCol) <= 0)
                    return CreateEmptyRegion();

                // HALCON 推荐半整数圆半径以获得更对称的离散圆盘结构元素；+0.5 可稳定桥接约 2*closeRadius px 间隙
                closed = cleaned.ClosingCircle(closeRadius + 0.5);
                if (openRadius > 0)
                {
                    return closed.OpeningCircle(openRadius + 0.5);
                }

                HRegion result = closed;
                closed = null;
                return result;
            }
            finally
            {
                if (closed != null) closed.Dispose();
                if (cleaned != null) cleaned.Dispose();
            }
        }

        /// <summary>
        /// 针对宽丝印/大焊盘导致主叉被截成多段（每段各自小于最小面积）的兜底恢复逻辑：
        /// 仅在非矩形的实质性笔画碎片之间做局部距离聚簇，按未膨胀的真实像素并集核验面积与矩形度，
        /// 杜绝零散细小过孔/噪点累加或规整矩形标签混入造成误报。
        /// </summary>
        private static HRegion TryRecoverBrokenCrossCluster(
            HRegion connected,
            double minArea,
            double maxArea,
            bool rectangleFilterEnabled,
            double rectangleFillThreshold,
            int effectiveCloseRadius,
            out double displayBridgeRadius)
        {
            displayBridgeRadius = 0.0;
            if (connected == null || connected.CountObj() < 2 || minArea <= 0.0)
                return null;

            // 仅聚簇严格小于 minArea 的实质性碎片：既排除细小噪点/过孔，也防止已被拒识的大型矩形标签混入合并。
            double minFragmentArea = Math.Max(40.0, minArea * 0.10);
            double maxFragmentArea = Math.Max(minFragmentArea, minArea - 1e-3);
            double rectThreshold = Math.Max(0.0, Math.Min(1.0, rectangleFillThreshold));

            HRegion fragments = null;
            HRegion fragmentUnion = null;
            HRegion dilated = null;
            HRegion mergedDilated = null;
            HRegion clusters = null;
            HRegion bestCluster = null;
            try
            {
                fragments = connected.SelectShape("area", "and", minFragmentArea, maxFragmentArea);
                if (fragments.CountObj() < 2) return null;

                // 单根被截断的直叉臂本身可能是细长条形（局部矩形度偏高），因此先按局部邻近距离聚簇，
                // 再对合并后的整体簇判定真实面积与整体矩形度（整叉矩形度极低，规整矩形即便被切开整体矩形度仍高）。
                fragmentUnion = fragments.Union1();
                double clusterRadius = Math.Min(60.0, Math.Max(effectiveCloseRadius * 3.0, 18.0));
                dilated = fragments.DilationCircle(clusterRadius + 0.5);
                mergedDilated = dilated.Union1();
                clusters = mergedDilated.Connection();

                int clusterCount = clusters.CountObj();
                double bestArea = -1.0;
                for (int i = 1; i <= clusterCount; i++)
                {
                    using (HRegion clusterMask = clusters.SelectObj(i))
                    using (HRegion trueCluster = clusterMask.Intersection(fragmentUnion))
                    {
                        double row, col;
                        int trueArea = trueCluster.AreaCenter(out row, out col);
                        if (trueArea < minArea || trueArea > maxArea) continue;

                        if (rectangleFilterEnabled)
                        {
                            using (HTuple rect = trueCluster.Rectangularity())
                            {
                                double rectangularity = rect.Length == 0 ? 0.0 : rect[0].D;
                                if (rectangularity >= rectThreshold) continue;
                            }
                        }

                        if (trueArea > bestArea)
                        {
                            bestArea = trueArea;
                            if (bestCluster != null) bestCluster.Dispose();
                            bestCluster = new HRegion(trueCluster);
                        }
                    }
                }

                if (bestCluster != null)
                {
                    displayBridgeRadius = clusterRadius + 1.5;
                }
                HRegion output = bestCluster;
                bestCluster = null;
                return output;
            }
            catch (HalconException)
            {
                if (bestCluster != null) bestCluster.Dispose();
                return null;
            }
            finally
            {
                if (bestCluster != null) bestCluster.Dispose();
                if (clusters != null) clusters.Dispose();
                if (mergedDilated != null) mergedDilated.Dispose();
                if (dilated != null) dilated.Dispose();
                if (fragmentUnion != null) fragmentUnion.Dispose();
                if (fragments != null) fragments.Dispose();
            }
        }

        /// <summary>提取目标主外边界并通过 HALCON SmoothContoursXld 消除离散像素阶梯锯齿。</summary>
        private static List<ImagePoint> ExtractSmoothPrimaryContour(
            HRegion selected,
            bool smoothEdges,
            int smoothHintRadius,
            double contourBridgeRadius)
        {
            List<ImagePoint> output = new List<ImagePoint>();
            if (selected == null || selected.CountObj() <= 0) return output;

            HRegion contourRegion = null;
            HXLDCont rawContours = null;
            HXLDCont longest = null;
            HXLDCont smoothed = null;
            try
            {
                contourRegion = contourBridgeRadius > 0.0
                    ? selected.ClosingCircle(contourBridgeRadius)
                    : new HRegion(selected);

                rawContours = contourRegion.GenContourRegionXld("border");
                int count = rawContours.CountObj();
                if (count <= 0) return output;

                int bestIndex = 1;
                int bestLength = -1;
                for (int i = 1; i <= count; i++)
                {
                    using (HXLDCont candidate = rawContours.SelectObj(i))
                    {
                        HTuple rows = null;
                        HTuple cols = null;
                        try
                        {
                            candidate.GetContourXld(out rows, out cols);
                            int len = Math.Min(rows.Length, cols.Length);
                            if (len > bestLength)
                            {
                                bestLength = len;
                                bestIndex = i;
                            }
                        }
                        finally
                        {
                            if (rows != null) rows.Dispose();
                            if (cols != null) cols.Dispose();
                        }
                    }
                }

                if (bestLength < 2) return output;
                longest = rawContours.SelectObj(bestIndex);

                HXLDCont activeContour = longest;
                if (smoothEdges && bestLength >= 9)
                {
                    // SmoothContoursXld 要求回归点数为 >= 5 的奇数，按形态学尺度自适应选取 9..17
                    int desired = Math.Max(9, Math.Min(17, (smoothHintRadius * 2 + 5) | 1));
                    int maxAllowed = bestLength - 2;
                    if ((maxAllowed & 1) == 0) maxAllowed--;
                    int smoothPoints = Math.Min(desired, maxAllowed);
                    if (smoothPoints >= 5)
                    {
                        try
                        {
                            smoothed = longest.SmoothContoursXld(smoothPoints);
                            activeContour = smoothed;
                        }
                        catch (HOperatorException)
                        {
                            activeContour = longest;
                        }
                    }
                }

                List<List<ImagePoint>> segments = HalconContourConverter.Extract(activeContour);
                List<ImagePoint> largest = segments.OrderByDescending(segment => segment.Count).FirstOrDefault();
                if (largest != null && largest.Count > 0)
                {
                    output.AddRange(largest);
                    // 确保边界轮廓首尾闭合以供绘制。
                    ImagePoint first = largest[0];
                    ImagePoint last = largest[largest.Count - 1];
                    if (first.Row != last.Row || first.Column != last.Column)
                        output.Add(first);
                }

                return output;
            }
            finally
            {
                if (smoothed != null) smoothed.Dispose();
                if (longest != null) longest.Dispose();
                if (rawContours != null) rawContours.Dispose();
                if (contourRegion != null) contourRegion.Dispose();
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
