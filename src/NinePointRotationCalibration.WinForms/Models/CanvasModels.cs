using System;
using System.Collections.Generic;
using System.Drawing;
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
        RestoreMask
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
    /// 模板掩膜画笔单次涂抹的轨迹记录。
    /// </summary>
    [Serializable]
    public sealed class MaskStroke
    {
        public MaskStroke()
        {
            Points = new List<ImageCoordinate>();
            Radius = 12.0;
            IsErase = true;
        }

        public List<ImageCoordinate> Points { get; set; }

        public double Radius { get; set; }

        /// <summary>
        /// true 表示从模板有效域擦除该区域，false 表示恢复该区域。
        /// </summary>
        public bool IsErase { get; set; }

        /// <summary>
        /// 创建掩膜笔画的深拷贝。
        /// </summary>
        public MaskStroke DeepClone()
        {
            return new MaskStroke
            {
                Radius = Radius,
                IsErase = IsErase,
                Points = new List<ImageCoordinate>(Points ?? new List<ImageCoordinate>())
            };
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
}
