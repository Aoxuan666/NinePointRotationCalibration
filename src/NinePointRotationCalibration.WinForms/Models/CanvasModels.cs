using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.Serialization;
using NinePointRotationCalibration.WinForms.Geometry;

namespace NinePointRotationCalibration.WinForms.Models
{
    /// <summary>
    /// 画布当前激活的交互编辑工具。
    /// </summary>
    public enum CanvasEditTool
    {
        Select,
        DrawTemplateRoi,
        DrawSearchRoi,
        DrawDetectionRoi,
        MoveAnchor,
        EraseMask,
        RestoreMask,
        MaskRectangle,
        MaskCircle,
        MaskPolygon
    }

    /// <summary>
    /// 画布中可被选中或编辑的 ROI 目标类型。
    /// </summary>
    public enum CanvasRoiTarget
    {
        None,
        Template,
        Search,
        Detection
    }

    /// <summary>
    /// 模板掩膜项的几何形态。
    /// </summary>
    public enum MaskShapeKind
    {
        Brush = 0,
        Rectangle = 1,
        Circle = 2,
        Polygon = 3
    }

    /// <summary>
    /// 模板掩膜单次操作记录（支持画笔涂抹、矩形区域、圆形区域与多边形区域）。
    /// </summary>
    [Serializable]
    public sealed class MaskStroke
    {
        public MaskStroke()
        {
            Kind = MaskShapeKind.Brush;
            Points = new List<ImageCoordinate>();
            Radius = 12.0;
            IsErase = true;
        }

        [field: OptionalField]
        public MaskShapeKind Kind { get; set; }

        [field: OptionalField]
        public RotatedRectangle Rectangle { get; set; }

        [field: OptionalField]
        public ImageCoordinate Center { get; set; }

        public List<ImageCoordinate> Points { get; set; }

        public double Radius { get; set; }

        /// <summary>
        /// true 表示从模板有效域擦除该区域，false 表示恢复该区域。
        /// </summary>
        public bool IsErase { get; set; }

        /// <summary>
        /// 指示当前掩膜项是否具备有效几何数据。
        /// </summary>
        public bool IsValid
        {
            get
            {
                switch (Kind)
                {
                    case MaskShapeKind.Rectangle:
                        return Rectangle != null && Rectangle.HalfWidth >= 1.0 && Rectangle.HalfHeight >= 1.0;
                    case MaskShapeKind.Circle:
                        return Radius >= 1.0;
                    case MaskShapeKind.Polygon:
                        return Points != null && Points.Count >= 3;
                    default:
                        return Points != null && Points.Count > 0 && Radius > 0.0;
                }
            }
        }

        /// <summary>
        /// 创建掩膜项的深拷贝。
        /// </summary>
        public MaskStroke DeepClone()
        {
            return new MaskStroke
            {
                Kind = Kind,
                Rectangle = Rectangle == null ? null : Rectangle.DeepClone(),
                Center = Center,
                Radius = Radius,
                IsErase = IsErase,
                Points = new List<ImageCoordinate>(Points ?? new List<ImageCoordinate>())
            };
        }
    }

    /// <summary>
    /// UI 端快速掩膜几何命中判断工具，用于涂抹或框选掩膜时 0ms 即时隐藏/恢复特征点。
    /// </summary>
    public static class MaskGeometry
    {
        public static bool IsPointExcluded(
            ImageCoordinate point,
            IList<MaskStroke> masks,
            bool inverted,
            MaskStroke activePreview = null)
        {
            bool included = true;
            if (masks != null)
            {
                for (int i = 0; i < masks.Count; i++)
                {
                    MaskStroke mask = masks[i];
                    if (mask != null && mask.IsValid && IsPointInsideMask(point, mask))
                    {
                        included = !mask.IsErase;
                    }
                }
            }

            if (activePreview != null && activePreview.IsValid && IsPointInsideMask(point, activePreview))
            {
                included = !activePreview.IsErase;
            }

            return inverted ? included : !included;
        }

        public static bool IsPointInsideMask(ImageCoordinate point, MaskStroke mask)
        {
            if (mask == null) return false;
            switch (mask.Kind)
            {
                case MaskShapeKind.Rectangle:
                    return mask.Rectangle != null && mask.Rectangle.Contains(point);
                case MaskShapeKind.Circle:
                {
                    double dr = point.Row - mask.Center.Row;
                    double dc = point.Column - mask.Center.Column;
                    return (dr * dr) + (dc * dc) <= mask.Radius * mask.Radius;
                }
                case MaskShapeKind.Polygon:
                    return IsPointInPolygon(point, mask.Points);
                default:
                    return IsPointInBrush(point, mask.Points, mask.Radius);
            }
        }

        public static bool IsPointNearSegment(
            ImageCoordinate point,
            ImageCoordinate start,
            ImageCoordinate end,
            double radius)
        {
            double r2 = radius * radius;
            double dx = end.Column - start.Column;
            double dy = end.Row - start.Row;
            double len2 = (dx * dx) + (dy * dy);
            if (len2 <= 1e-9)
            {
                double px = point.Column - start.Column;
                double py = point.Row - start.Row;
                return (px * px) + (py * py) <= r2;
            }

            double t = (((point.Column - start.Column) * dx) + ((point.Row - start.Row) * dy)) / len2;
            if (t < 0.0) t = 0.0;
            else if (t > 1.0) t = 1.0;
            double projX = start.Column + (t * dx);
            double projY = start.Row + (t * dy);
            double ex = point.Column - projX;
            double ey = point.Row - projY;
            return (ex * ex) + (ey * ey) <= r2;
        }

        private static bool IsPointInBrush(ImageCoordinate point, IList<ImageCoordinate> points, double radius)
        {
            if (points == null || points.Count == 0 || radius <= 0.0) return false;
            if (points.Count == 1)
            {
                double dr = point.Row - points[0].Row;
                double dc = point.Column - points[0].Column;
                return (dr * dr) + (dc * dc) <= radius * radius;
            }

            for (int i = 1; i < points.Count; i++)
            {
                if (IsPointNearSegment(point, points[i - 1], points[i], radius))
                {
                    return true;
                }
            }
            return false;
        }

        private static bool IsPointInPolygon(ImageCoordinate point, IList<ImageCoordinate> polygon)
        {
            if (polygon == null || polygon.Count < 3) return false;
            bool inside = false;
            int count = polygon.Count;
            for (int i = 0, j = count - 1; i < count; j = i++)
            {
                double yi = polygon[i].Row;
                double xi = polygon[i].Column;
                double yj = polygon[j].Row;
                double xj = polygon[j].Column;
                bool intersect = ((yi > point.Row) != (yj > point.Row)) &&
                                 (point.Column < ((xj - xi) * (point.Row - yi) / ((yj - yi) + 1e-12)) + xi);
                if (intersect)
                {
                    inside = !inside;
                }
            }
            return inside;
        }
    }

    /// <summary>
    /// 画布点标记叠加项。
    /// </summary>
    public sealed class CanvasPointOverlay
    {
        public ImageCoordinate Position { get; set; }

        public string Label { get; set; }

        public Color Color { get; set; } = Color.Lime;

        public bool IsSelected { get; set; }

        public float RadiusPixels { get; set; } = 5.0f;
    }

    /// <summary>
    /// 画布多段线或闭合轮廓叠加项。
    /// </summary>
    public sealed class CanvasPolylineOverlay
    {
        public CanvasPolylineOverlay()
        {
            Points = new List<ImageCoordinate>();
            Color = Color.Lime;
            WidthPixels = 1.5f;
        }

        public List<ImageCoordinate> Points { get; set; }

        public Color Color { get; set; }

        public float WidthPixels { get; set; }

        public bool Closed { get; set; }
    }

    /// <summary>
    /// 画布位姿指示箭头叠加项（用于显示匹配位置与角度）。
    /// </summary>
    public sealed class CanvasPoseOverlay
    {
        public ImageCoordinate Position { get; set; }

        public double AngleDegrees { get; set; }

        public double Score { get; set; }

        public string Label { get; set; }

        public Color Color { get; set; } = Color.Lime;
    }

    /// <summary>
    /// 画布残差矢量叠加项（从预期位置指向实测位置）。
    /// </summary>
    public sealed class CanvasResidualOverlay
    {
        public ImageCoordinate Actual { get; set; }

        public ImageCoordinate Expected { get; set; }

        public Color Color { get; set; } = Color.OrangeRed;
    }

    /// <summary>
    /// 画布图形与状态 HUD 的叠加图层集合。
    /// </summary>
    public sealed class CanvasRuntimeOverlay
    {
        public CanvasRuntimeOverlay()
        {
            Points = new List<CanvasPointOverlay>();
            Polylines = new List<CanvasPolylineOverlay>();
            Poses = new List<CanvasPoseOverlay>();
            Residuals = new List<CanvasResidualOverlay>();
            HudLines = new List<string>();
        }

        public List<CanvasPointOverlay> Points { get; set; }

        public List<CanvasPolylineOverlay> Polylines { get; set; }

        public List<CanvasPoseOverlay> Poses { get; set; }

        public List<CanvasResidualOverlay> Residuals { get; set; }

        public ImageCoordinate? RotationCenter { get; set; }

        public List<string> HudLines { get; set; }

        public bool? IsOk { get; set; }

        public string StatusText { get; set; }
    }

    /// <summary>
    /// ROI 变更事件参数。
    /// </summary>
    public sealed class RoiChangedEventArgs : EventArgs
    {
        public RoiChangedEventArgs(CanvasRoiTarget target, RotatedRectangle roi, bool isFinal)
        {
            Target = target;
            Roi = roi == null ? null : roi.DeepClone();
            IsFinal = isFinal;
        }

        public CanvasRoiTarget Target { get; private set; }

        public RotatedRectangle Roi { get; private set; }

        /// <summary>
        /// 是否为拖拽结束或键盘微调后的最终提交事件。
        /// </summary>
        public bool IsFinal { get; private set; }
    }

    /// <summary>
    /// 基准中心变更事件参数。
    /// </summary>
    public sealed class AnchorChangedEventArgs : EventArgs
    {
        public AnchorChangedEventArgs(ImageCoordinate anchor, bool isFinal)
        {
            Anchor = anchor;
            IsFinal = isFinal;
        }

        public ImageCoordinate Anchor { get; private set; }

        public bool IsFinal { get; private set; }
    }

    /// <summary>
    /// 掩膜笔画绘制完成事件参数。
    /// </summary>
    public sealed class MaskStrokeEventArgs : EventArgs
    {
        public MaskStrokeEventArgs(MaskStroke stroke)
        {
            Stroke = stroke == null ? null : stroke.DeepClone();
        }

        public MaskStroke Stroke { get; private set; }
    }

    /// <summary>
    /// 掩膜绘制过程中（拖拽画笔/矩形/圆形/多边形）的实时预览变更事件参数。
    /// </summary>
    public sealed class MaskPreviewEventArgs : EventArgs
    {
        public MaskPreviewEventArgs(
            MaskStroke activeMask,
            bool isIncrementalEraseSegment = false,
            ImageCoordinate segmentStart = default(ImageCoordinate),
            ImageCoordinate segmentEnd = default(ImageCoordinate))
        {
            ActiveMask = activeMask;
            IsIncrementalEraseSegment = isIncrementalEraseSegment;
            SegmentStart = segmentStart;
            SegmentEnd = segmentEnd;
        }

        public MaskStroke ActiveMask { get; private set; }
        public bool IsIncrementalEraseSegment { get; private set; }
        public ImageCoordinate SegmentStart { get; private set; }
        public ImageCoordinate SegmentEnd { get; private set; }
    }
}
