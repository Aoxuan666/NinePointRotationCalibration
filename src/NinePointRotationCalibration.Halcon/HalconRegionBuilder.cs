using System;
using System.Collections.Generic;
using HalconDotNet;

namespace NinePointRotationCalibration.Halcon
{
    /// <summary>将核心区域的几何定义（ROI、掩膜、擦除笔迹、搜索区）构造为 HALCON HRegion。</summary>
    internal static class HalconRegionBuilder
    {
        static HalconRegionBuilder()
        {
            // 关闭 HALCON 全局 128x128 默认区域裁剪，避免在首次加载大图前构造 ROI/掩膜时被静默截断；
            // 本类所有区域构建流程均显式与 imageBounds 求交。
            try
            {
                HSystem.SetSystem("clip_region", "false");
            }
            catch
            {
            }
        }

        /// <summary>生成模板有效区域：主 ROI 裁剪至图像边界后依次应用掩膜、笔迹与反选。</summary>
        public static HRegion BuildEffectiveRegion(TemplateDefinition definition, int imageWidth, int imageHeight)
        {
            if (definition == null)
            {
                throw new ArgumentNullException(nameof(definition));
            }

            if (definition.TemplateRoi == null)
            {
                throw new ArgumentException("Template ROI is not configured.", nameof(definition));
            }

            ValidateImageSize(imageWidth, imageHeight);

            HRegion imageBounds = null;
            HRegion templateDomain = null;
            HRegion current = null;
            try
            {
                imageBounds = new HRegion();
                imageBounds.GenRectangle1(0.0, 0.0, imageHeight - 1.0, imageWidth - 1.0);
                using (HRegion baseRegion = BuildRegion(definition.TemplateRoi, "Template ROI"))
                {
                    templateDomain = baseRegion.Intersection(imageBounds);
                    current = new HRegion(templateDomain);
                }

                int roiR1, roiC1, roiR2, roiC2;
                templateDomain.SmallestRectangle1(out roiR1, out roiC1, out roiR2, out roiC2);

                // 将所有掩膜（结构化区域 + 画笔）按先后顺序收集，并对连续同极性（连续擦除或连续恢复）做批量合并，
                // 同时将连续同半径画笔合并为单次 DilationCircle，把 N 笔串行膨胀/差集耗时降低一个数量级。
                List<MaskEntry> entries = new List<MaskEntry>();
                if (definition.MaskRegions != null)
                {
                    for (int i = 0; i < definition.MaskRegions.Count; i++)
                    {
                        TemplateMaskShape mask = definition.MaskRegions[i];
                        if (mask == null || mask.Region == null) continue;
                        entries.Add(new MaskEntry(mask.Operation, mask.Region, null, 0.0));
                    }
                }
                if (definition.EraseStrokes != null)
                {
                    for (int i = 0; i < definition.EraseStrokes.Count; i++)
                    {
                        TemplateEraseStroke stroke = definition.EraseStrokes[i];
                        if (stroke == null || stroke.Points == null || stroke.Points.Count == 0) continue;
                        entries.Add(new MaskEntry(stroke.Operation, null, stroke.Points, stroke.Radius));
                    }
                }

                int idx = 0;
                while (idx < entries.Count)
                {
                    MaskOperation op = entries[idx].Operation;
                    if (op != MaskOperation.Include && op != MaskOperation.Exclude)
                    {
                        throw new ArgumentOutOfRangeException("Mask operation", "Unsupported mask operation: " + op + ".");
                    }
                    int batchEnd = idx + 1;
                    while (batchEnd < entries.Count && entries[batchEnd].Operation == op)
                    {
                        batchEnd++;
                    }
                    ApplyMaskBatch(ref current, templateDomain, imageBounds, entries, idx, batchEnd, op, roiR1, roiC1, roiR2, roiC2);
                    idx = batchEnd;
                }

                if (definition.IsMaskInverted)
                {
                    Replace(ref current, templateDomain.Difference(current));
                }

                EnsureNonEmpty(current, "The effective template region is empty after applying masks.");
                HRegion result = current;
                current = null;
                return result;
            }
            finally
            {
                if (current != null) current.Dispose();
                if (templateDomain != null) templateDomain.Dispose();
                if (imageBounds != null) imageBounds.Dispose();
            }
        }

        private struct MaskEntry
        {
            public MaskEntry(MaskOperation op, RegionDefinition region, IList<ImagePoint> strokePoints, double strokeRadius)
            {
                Operation = op;
                Region = region;
                StrokePoints = strokePoints;
                StrokeRadius = strokeRadius;
            }

            public MaskOperation Operation;
            public RegionDefinition Region;
            public IList<ImagePoint> StrokePoints;
            public double StrokeRadius;
        }

        private static void ApplyMaskBatch(
            ref HRegion current,
            HRegion templateDomain,
            HRegion imageBounds,
            List<MaskEntry> entries,
            int startIndex,
            int endIndex,
            MaskOperation operation,
            int roiR1,
            int roiC1,
            int roiR2,
            int roiC2)
        {
            HRegion accumulated = null;
            Dictionary<int, List<IList<ImagePoint>>> brushGroups = null;
            Dictionary<int, double> brushRadii = null;
            try
            {
                for (int i = startIndex; i < endIndex; i++)
                {
                    MaskEntry entry = entries[i];
                    IList<ImagePoint> brushPoints = entry.StrokePoints;
                    double brushRadius = entry.StrokeRadius;
                    if (entry.Region != null && entry.Region.Kind == RegionKind.Freehand && entry.Region.Radius > 0.0)
                    {
                        brushPoints = entry.Region.Points;
                        brushRadius = entry.Region.Radius;
                    }

                    if (brushPoints != null)
                    {
                        if (brushPoints.Count == 0) continue;
                        ValidatePositive(brushRadius, "Mask stroke radius");
                        ValidatePoints(brushPoints, "Mask stroke");
                        int radiusKey = (int)Math.Round(brushRadius * 10.0);
                        if (brushGroups == null)
                        {
                            brushGroups = new Dictionary<int, List<IList<ImagePoint>>>();
                            brushRadii = new Dictionary<int, double>();
                        }
                        List<IList<ImagePoint>> list;
                        if (!brushGroups.TryGetValue(radiusKey, out list))
                        {
                            list = new List<IList<ImagePoint>>();
                            brushGroups[radiusKey] = list;
                            brushRadii[radiusKey] = brushRadius;
                        }
                        list.Add(brushPoints);
                        continue;
                    }

                    if (entry.Region == null) continue;
                    using (HRegion shapeRegion = BuildRegion(entry.Region, "Template mask"))
                    {
                        AppendRegion(ref accumulated, shapeRegion);
                    }
                }

                if (brushGroups != null)
                {
                    foreach (KeyValuePair<int, List<IList<ImagePoint>>> group in brushGroups)
                    {
                        double radius = brushRadii[group.Key];
                        using (HRegion batchedBrush = BuildBatchedStrokes(group.Value, radius, roiR1, roiC1, roiR2, roiC2))
                        {
                            if (batchedBrush != null) AppendRegion(ref accumulated, batchedBrush);
                        }
                    }
                }

                if (accumulated == null) return;
                using (HRegion unioned = accumulated.Union1())
                using (HRegion clipped = unioned.Intersection(imageBounds))
                {
                    if (operation == MaskOperation.Include)
                    {
                        // 恢复区域时与主模板域求交，保证掩膜不会扩展到主 ROI 外部。
                        using (HRegion restored = current.Union2(clipped))
                        {
                            Replace(ref current, restored.Intersection(templateDomain));
                        }
                    }
                    else
                    {
                        Replace(ref current, current.Difference(clipped));
                    }
                }
            }
            finally
            {
                if (accumulated != null) accumulated.Dispose();
            }
        }

        private static void AppendRegion(ref HRegion target, HRegion item)
        {
            if (item == null) return;
            if (target == null)
            {
                target = new HRegion(item);
                return;
            }
            Replace(ref target, target.ConcatObj(item));
        }

        private static HRegion BuildBatchedStrokes(
            List<IList<ImagePoint>> strokes,
            double radius,
            int roiR1,
            int roiC1,
            int roiR2,
            int roiC2)
        {
            double pad = radius + 2.0;
            double minR = roiR1 - pad;
            double maxR = roiR2 + pad;
            double minC = roiC1 - pad;
            double maxC = roiC2 + pad;
            List<int> r1 = new List<int>();
            List<int> c1 = new List<int>();
            List<int> r2 = new List<int>();
            List<int> c2 = new List<int>();
            for (int s = 0; s < strokes.Count; s++)
            {
                IList<ImagePoint> pts = strokes[s];
                if (pts == null || pts.Count == 0) continue;
                if (pts.Count == 1)
                {
                    double pr = pts[0].Row;
                    double pc = pts[0].Column;
                    if (pr < minR || pr > maxR || pc < minC || pc > maxC) continue;
                    int ir = (int)Math.Round(pr);
                    int ic = (int)Math.Round(pc);
                    r1.Add(ir); c1.Add(ic); r2.Add(ir); c2.Add(ic);
                    continue;
                }
                for (int i = 0; i < pts.Count - 1; i++)
                {
                    double aR = pts[i].Row, aC = pts[i].Column;
                    double bR = pts[i + 1].Row, bC = pts[i + 1].Column;
                    if ((aR < minR && bR < minR) || (aR > maxR && bR > maxR) ||
                        (aC < minC && bC < minC) || (aC > maxC && bC > maxC)) continue;
                    r1.Add((int)Math.Round(aR));
                    c1.Add((int)Math.Round(aC));
                    r2.Add((int)Math.Round(bR));
                    c2.Add((int)Math.Round(bC));
                }
            }
            if (r1.Count == 0) return null;
            using (HTuple tr1 = new HTuple(r1.ToArray()))
            using (HTuple tc1 = new HTuple(c1.ToArray()))
            using (HTuple tr2 = new HTuple(r2.ToArray()))
            using (HTuple tc2 = new HTuple(c2.ToArray()))
            using (HRegion lines = new HRegion())
            {
                lines.GenRegionLine(tr1, tc1, tr2, tc2);
                using (HRegion centerline = lines.Union1())
                    return centerline.DilationCircle(Math.Max(0.5, radius));
            }
        }

        /// <summary>构造并裁剪搜索区域；未指定时默认使用整幅图像范围。</summary>
        public static HRegion BuildSearchRegion(RegionDefinition definition, int imageWidth, int imageHeight)
        {
            ValidateImageSize(imageWidth, imageHeight);

            HRegion imageBounds = new HRegion();
            try
            {
                imageBounds.GenRectangle1(0.0, 0.0, imageHeight - 1.0, imageWidth - 1.0);
                if (definition == null)
                {
                    return new HRegion(imageBounds);
                }

                using (HRegion search = BuildRegion(definition, "Search region"))
                {
                    HRegion clipped = search.Intersection(imageBounds);
                    try
                    {
                        EnsureNonEmpty(clipped, "The search region does not intersect the input image.");
                        return clipped;
                    }
                    catch
                    {
                        clipped.Dispose();
                        throw;
                    }
                }
            }
            finally
            {
                imageBounds.Dispose();
            }
        }

        /// <summary>计算区域的面积和质心位置，并校验区域非空且有限。</summary>
        public static void GetAreaCenter(HRegion region, out double area, out double row, out double column)
        {
            if (region == null) throw new ArgumentNullException(nameof(region));
            area = region.AreaCenter(out row, out column);
            if (!IsFinite(area) || !IsFinite(row) || !IsFinite(column) || area <= 0.0)
            {
                throw new ArgumentException("The region is empty or invalid.", nameof(region));
            }
        }

        /// <summary>获取区域外接矩形并按指定像素外扩（钳位在图像边界内），供局部裁剪加速算子。</summary>
        public static void GetRegionBoundingBox(
            HRegion region,
            int imageWidth,
            int imageHeight,
            int padding,
            out int cropRow,
            out int cropColumn,
            out int cropWidth,
            out int cropHeight)
        {
            int r1, c1, r2, c2;
            region.SmallestRectangle1(out r1, out c1, out r2, out c2);
            int pad = Math.Max(0, padding);
            cropRow = Math.Max(0, r1 - pad);
            cropColumn = Math.Max(0, c1 - pad);
            int maxRow = Math.Min(imageHeight - 1, r2 + pad);
            int maxCol = Math.Min(imageWidth - 1, c2 + pad);
            cropWidth = Math.Max(1, maxCol - cropColumn + 1);
            cropHeight = Math.Max(1, maxRow - cropRow + 1);
        }

        /// <summary>提取区域内外边界的有序 XLD 轮廓点段。</summary>
        public static List<List<ImagePoint>> ExtractRegionContours(HRegion region)
        {
            if (region == null) throw new ArgumentNullException(nameof(region));
            List<List<ImagePoint>> result = new List<List<ImagePoint>>();
            // 先求内边界并拆分连通域再转 XLD 中心轮廓，确保含孔洞或分离区域各自独立成段。
            using (HRegion boundary = region.Boundary("inner"))
            using (HRegion components = boundary.Connection())
            {
                int count = components.CountObj();
                for (int index = 1; index <= count; index++)
                {
                    using (HRegion component = components.SelectObj(index))
                    using (HXLDCont contours = component.GenContourRegionXld("center"))
                    {
                        result.AddRange(HalconContourConverter.Extract(contours));
                    }
                }
            }

            return result;
        }

        private static HRegion BuildRegion(RegionDefinition definition, string name)
        {
            if (definition == null) throw new ArgumentNullException(nameof(definition));

            HRegion region = new HRegion();
            try
            {
                switch (definition.Kind)
                {
                    case RegionKind.Rectangle1:
                        ValidateCenterAndLengths(definition, name);
                        region.GenRectangle1(
                            definition.CenterRow - definition.Length2,
                            definition.CenterColumn - definition.Length1,
                            definition.CenterRow + definition.Length2,
                            definition.CenterColumn + definition.Length1);
                        break;

                    case RegionKind.Rectangle2:
                        ValidateCenterAndLengths(definition, name);
                        if (!IsFinite(definition.PhiRadians)) throw Invalid(name, "angle");
                        // RegionDefinition.PhiRadians 已采用 HALCON 逆时针弧度约定，直接传入。
                        region.GenRectangle2(
                            definition.CenterRow,
                            definition.CenterColumn,
                            definition.PhiRadians,
                            definition.Length1,
                            definition.Length2);
                        break;

                    case RegionKind.Circle:
                        ValidateCenter(definition, name);
                        ValidatePositive(definition.Radius, name + " radius");
                        region.GenCircle(definition.CenterRow, definition.CenterColumn, definition.Radius);
                        break;

                    case RegionKind.Polygon:
                        BuildPolygon(region, definition.Points, name);
                        break;

                    case RegionKind.Freehand:
                        region.Dispose();
                        return BuildFreehand(definition, name);

                    default:
                        throw new ArgumentOutOfRangeException(nameof(definition), "Unsupported region kind: " + definition.Kind + ".");
                }

                return region;
            }
            catch
            {
                region.Dispose();
                throw;
            }
        }

        private static HRegion BuildFreehand(RegionDefinition definition, string name)
        {
            if (definition.Points == null || definition.Points.Count == 0)
            {
                throw new ArgumentException(name + " has no points.");
            }

            if (definition.Radius > 0.0)
            {
                return BuildStroke(definition.Points, definition.Radius, name);
            }

            if (definition.Points.Count >= 3)
            {
                HRegion region = new HRegion();
                try
                {
                    BuildPolygon(region, definition.Points, name);
                    return region;
                }
                catch
                {
                    region.Dispose();
                    throw;
                }
            }

            throw new ArgumentException(name + " requires a positive radius for an open freehand stroke.");
        }

        private static HRegion BuildStroke(IList<ImagePoint> points, double radius, string name)
        {
            if (points == null || points.Count == 0) throw new ArgumentException(name + " has no points.");
            ValidatePositive(radius, name + " radius");
            ValidatePoints(points, name);

            // 单点退化为圆；多点折线逐段生成像素线段并按画笔半径做圆形膨胀后合并。
            if (points.Count == 1)
            {
                HRegion circle = new HRegion();
                circle.GenCircle(points[0].Row, points[0].Column, radius);
                return circle;
            }

            int segments = points.Count - 1;
            int[] startRows = new int[segments], startColumns = new int[segments];
            int[] endRows = new int[segments], endColumns = new int[segments];
            for (int i = 0; i < segments; i++)
            {
                startRows[i] = (int)Math.Round(points[i].Row);
                startColumns[i] = (int)Math.Round(points[i].Column);
                endRows[i] = (int)Math.Round(points[i + 1].Row);
                endColumns[i] = (int)Math.Round(points[i + 1].Column);
            }
            using (HTuple r1 = new HTuple(startRows))
            using (HTuple c1 = new HTuple(startColumns))
            using (HTuple r2 = new HTuple(endRows))
            using (HTuple c2 = new HTuple(endColumns))
            using (HRegion lines = new HRegion())
            {
                lines.GenRegionLine(r1, c1, r2, c2);
                using (HRegion centerline = lines.Union1())
                    return centerline.DilationCircle(Math.Max(0.5, radius));
            }
        }

        private static void BuildPolygon(HRegion region, IList<ImagePoint> points, string name)
        {
            if (points == null || points.Count < 3) throw new ArgumentException(name + " requires at least three points.");
            ValidatePoints(points, name);

            double[] rows = new double[points.Count];
            double[] columns = new double[points.Count];
            for (int index = 0; index < points.Count; index++)
            {
                rows[index] = points[index].Row;
                columns[index] = points[index].Column;
            }

            using (HTuple rowTuple = new HTuple(rows))
            using (HTuple columnTuple = new HTuple(columns))
            {
                region.GenRegionPolygonFilled(rowTuple, columnTuple);
            }
        }

        private static void EnsureNonEmpty(HRegion region, string message)
        {
            double row;
            double column;
            double area = region.AreaCenter(out row, out column);
            if (!IsFinite(area) || area <= 0.0) throw new ArgumentException(message);
        }

        private static void ValidateCenterAndLengths(RegionDefinition definition, string name)
        {
            ValidateCenter(definition, name);
            ValidatePositive(definition.Length1, name + " length1");
            ValidatePositive(definition.Length2, name + " length2");
        }

        private static void ValidateCenter(RegionDefinition definition, string name)
        {
            if (!IsFinite(definition.CenterRow) || !IsFinite(definition.CenterColumn)) throw Invalid(name, "center");
        }

        private static void ValidatePoints(IList<ImagePoint> points, string name)
        {
            for (int index = 0; index < points.Count; index++)
            {
                if (!IsFinite(points[index].Row) || !IsFinite(points[index].Column))
                {
                    throw new ArgumentException(name + " point " + index + " is not finite.");
                }
            }
        }

        private static void ValidatePositive(double value, string name)
        {
            if (!IsFinite(value) || value <= 0.0) throw new ArgumentOutOfRangeException(name, "The value must be finite and greater than zero.");
        }

        private static void ValidateImageSize(int width, int height)
        {
            if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
            if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));
        }

        private static ArgumentException Invalid(string name, string part)
        {
            return new ArgumentException(name + " " + part + " is not finite.");
        }

        private static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }

        private static void Replace(ref HRegion current, HRegion next)
        {
            // 切换区域引用时立即释放旧的 HALCON 区域句柄。
            HRegion previous = current;
            current = next;
            if (previous != null) previous.Dispose();
        }
    }
}
