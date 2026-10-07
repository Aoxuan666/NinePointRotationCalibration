using System;
using System.Collections.Generic;
using System.Drawing;
using NinePointRotationCalibration.WinForms.Geometry;

namespace NinePointRotationCalibration.WinForms.Models
{
    public enum CanvasEditTool
    {
        Select,
        DrawTemplateRoi,
        DrawSearchRoi,
        MoveAnchor,
        EraseMask,
        RestoreMask
    }

    public enum CanvasRoiTarget
    {
        None,
        Template,
        Search
    }

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

        public bool IsErase { get; set; }

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

    public sealed class CanvasPointOverlay
    {
        public ImageCoordinate Position { get; set; }

        public string Label { get; set; }

        public Color Color { get; set; } = Color.Lime;

        public bool IsSelected { get; set; }

        public float RadiusPixels { get; set; } = 5.0f;
    }

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

    public sealed class CanvasPoseOverlay
    {
        public ImageCoordinate Position { get; set; }

        public double AngleDegrees { get; set; }

        public double Score { get; set; }

        public string Label { get; set; }

        public Color Color { get; set; } = Color.Lime;
    }

    public sealed class CanvasResidualOverlay
    {
        public ImageCoordinate Actual { get; set; }

        public ImageCoordinate Expected { get; set; }

        public Color Color { get; set; } = Color.OrangeRed;
    }

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

        public bool IsFinal { get; private set; }
    }

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

    public sealed class MaskStrokeEventArgs : EventArgs
    {
        public MaskStrokeEventArgs(MaskStroke stroke)
        {
            Stroke = stroke == null ? null : stroke.DeepClone();
        }

        public MaskStroke Stroke { get; private set; }
    }
}
