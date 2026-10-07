using System;
using System.Collections.Generic;
using HalconDotNet;

namespace NinePointRotationCalibration.Halcon
{
    internal static class HalconRegionBuilder
    {
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

                if (definition.MaskRegions != null)
                {
                    foreach (TemplateMaskShape mask in definition.MaskRegions)
                    {
                        if (mask == null || mask.Region == null)
                        {
                            continue;
                        }

                        using (HRegion maskRegion = BuildRegion(mask.Region, "Template mask"))
                        using (HRegion clippedMask = maskRegion.Intersection(imageBounds))
                        {
                            HRegion next;
                            if (mask.Operation == MaskOperation.Include)
                            {
                                using (HRegion restored = current.Union2(clippedMask))
                                {
                                    next = restored.Intersection(templateDomain);
                                }
                            }
                            else if (mask.Operation == MaskOperation.Exclude)
                            {
                                next = current.Difference(clippedMask);
                            }
                            else
                            {
                                throw new ArgumentOutOfRangeException("Mask operation", "Unsupported mask operation: " + mask.Operation + ".");
                            }

                            Replace(ref current, next);
                        }
                    }
                }

                if (definition.EraseStrokes != null)
                {
                    foreach (TemplateEraseStroke stroke in definition.EraseStrokes)
                    {
                        if (stroke == null || stroke.Points == null || stroke.Points.Count == 0)
                        {
                            continue;
                        }

                        using (HRegion strokeRegion = BuildStroke(stroke.Points, stroke.Radius, "Erase stroke"))
                        using (HRegion clippedStroke = strokeRegion.Intersection(imageBounds))
                        {
                            if (stroke.Operation == MaskOperation.Include)
                            {
                                using (HRegion restored = current.Union2(clippedStroke))
                                {
                                    Replace(ref current, restored.Intersection(templateDomain));
                                }
                            }
                            else if (stroke.Operation == MaskOperation.Exclude)
                            {
                                Replace(ref current, current.Difference(clippedStroke));
                            }
                            else
                            {
                                throw new ArgumentOutOfRangeException("Stroke operation", "Unsupported stroke operation: " + stroke.Operation + ".");
                            }
                        }
                    }
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

        public static void GetAreaCenter(HRegion region, out double area, out double row, out double column)
        {
            if (region == null) throw new ArgumentNullException(nameof(region));
            area = region.AreaCenter(out row, out column);
            if (!IsFinite(area) || !IsFinite(row) || !IsFinite(column) || area <= 0.0)
            {
                throw new ArgumentException("The region is empty or invalid.", nameof(region));
            }
        }

        public static List<List<ImagePoint>> ExtractRegionContours(HRegion region)
        {
            if (region == null) throw new ArgumentNullException(nameof(region));
            List<List<ImagePoint>> result = new List<List<ImagePoint>>();
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

            if (points.Count == 1)
            {
                HRegion circle = new HRegion();
                circle.GenCircle(points[0].Row, points[0].Column, radius);
                return circle;
            }

            HRegion combined = new HRegion();
            combined.GenEmptyRegion();
            try
            {
                for (int index = 1; index < points.Count; index++)
                {
                    using (HRegion line = new HRegion())
                    {
                        line.GenRegionLine(
                            (int)Math.Round(points[index - 1].Row),
                            (int)Math.Round(points[index - 1].Column),
                            (int)Math.Round(points[index].Row),
                            (int)Math.Round(points[index].Column));
                        using (HRegion widened = line.DilationCircle(radius))
                        {
                            Replace(ref combined, combined.Union2(widened));
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
            HRegion previous = current;
            current = next;
            if (previous != null) previous.Dispose();
        }
    }
}
