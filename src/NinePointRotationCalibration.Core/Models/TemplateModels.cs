using System;
using System.Collections.Generic;
using System.Security.Cryptography;

namespace NinePointRotationCalibration
{
    [Serializable]
    public sealed class RegionDefinition
    {
        public RegionDefinition()
        {
            Points = new List<ImagePoint>();
        }

        public RegionKind Kind { get; set; }
        public double CenterRow { get; set; }
        public double CenterColumn { get; set; }
        public double PhiRadians { get; set; }
        public double Length1 { get; set; }
        public double Length2 { get; set; }
        public double Radius { get; set; }
        public List<ImagePoint> Points { get; set; }

        public RegionDefinition DeepClone()
        {
            RegionDefinition clone = (RegionDefinition)MemberwiseClone();
            clone.Points = Points == null ? new List<ImagePoint>() : new List<ImagePoint>(Points);
            return clone;
        }
    }

    [Serializable]
    public sealed class TemplateMaskShape
    {
        public TemplateMaskShape()
        {
            Region = new RegionDefinition();
        }

        public MaskOperation Operation { get; set; }
        public RegionDefinition Region { get; set; }

        public TemplateMaskShape DeepClone()
        {
            return new TemplateMaskShape
            {
                Operation = Operation,
                Region = Region == null ? null : Region.DeepClone()
            };
        }
    }

    [Serializable]
    public sealed class TemplateEraseStroke
    {
        public TemplateEraseStroke()
        {
            Operation = MaskOperation.Exclude;
            Radius = 8.0;
            Points = new List<ImagePoint>();
        }

        /// <summary>
        /// Exclude erases pixels from the effective domain; Include restores them.
        /// Strokes are applied in list order.
        /// </summary>
        public MaskOperation Operation { get; set; }
        public double Radius { get; set; }
        public List<ImagePoint> Points { get; set; }

        public TemplateEraseStroke DeepClone()
        {
            return new TemplateEraseStroke
            {
                Operation = Operation,
                Radius = Radius,
                Points = Points == null ? new List<ImagePoint>() : new List<ImagePoint>(Points)
            };
        }
    }

    [Serializable]
    public sealed class ShapeModelParameters
    {
        public ShapeModelParameters()
        {
            NumLevels = 0;
            AngleStartRad = -Math.PI;
            AngleExtentRad = Math.PI * 2.0;
            AngleStepRad = 0.0;
            Optimization = "auto";
            Metric = "use_polarity";
            Contrast = 30;
            MinContrast = 10;
        }

        public int NumLevels { get; set; }
        public double AngleStartRad { get; set; }
        public double AngleExtentRad { get; set; }
        public double AngleStepRad { get; set; }
        public string Optimization { get; set; }
        public string Metric { get; set; }
        public int Contrast { get; set; }
        public int MinContrast { get; set; }

        public ShapeModelParameters DeepClone() { return (ShapeModelParameters)MemberwiseClone(); }
    }

    [Serializable]
    public sealed class NccModelParameters
    {
        public NccModelParameters()
        {
            NumLevels = 0;
            AngleStartRad = -Math.PI;
            AngleExtentRad = Math.PI * 2.0;
            AngleStepRad = 0.0;
            Metric = "use_polarity";
        }

        public int NumLevels { get; set; }
        public double AngleStartRad { get; set; }
        public double AngleExtentRad { get; set; }
        public double AngleStepRad { get; set; }
        public string Metric { get; set; }

        public NccModelParameters DeepClone() { return (NccModelParameters)MemberwiseClone(); }
    }

    [Serializable]
    public sealed class MatchParameters
    {
        public MatchParameters()
        {
            AngleStartRad = -Math.PI;
            AngleExtentRad = Math.PI * 2.0;
            MinScore = 0.5;
            NumMatches = 1;
            MaxOverlap = 0.5;
            SubPixel = "least_squares";
            NumLevels = 0;
            Greediness = 0.9;
            TimeoutMilliseconds = 0;
            ContourPointSpacingPixels = 3.0;
        }

        public double AngleStartRad { get; set; }
        public double AngleExtentRad { get; set; }
        public double MinScore { get; set; }
        public int NumMatches { get; set; }
        public double MaxOverlap { get; set; }
        public string SubPixel { get; set; }
        public int NumLevels { get; set; }
        public double Greediness { get; set; }
        public int TimeoutMilliseconds { get; set; }
        public double ContourPointSpacingPixels { get; set; }

        public MatchParameters DeepClone() { return (MatchParameters)MemberwiseClone(); }
    }

    [Serializable]
    public sealed class TemplateDefinition
    {
        public TemplateDefinition()
        {
            ModelType = TemplateModelType.Shape;
            MaskRegions = new List<TemplateMaskShape>();
            EraseStrokes = new List<TemplateEraseStroke>();
            ShapeParameters = new ShapeModelParameters();
            NccParameters = new NccModelParameters();
            MatchParameters = new MatchParameters();
            ModelReference = new ImagePose();
            Revision = 1;
            IsModelDirty = true;
        }

        public TemplateModelType ModelType { get; set; }
        public RegionDefinition TemplateRoi { get; set; }
        public RegionDefinition SearchRegion { get; set; }
        public List<TemplateMaskShape> MaskRegions { get; set; }
        public List<TemplateEraseStroke> EraseStrokes { get; set; }
        public ShapeModelParameters ShapeParameters { get; set; }
        public NccModelParameters NccParameters { get; set; }
        public MatchParameters MatchParameters { get; set; }
        public ImagePoint ReferenceAnchor { get; set; }
        public bool IsReferenceAnchorLocked { get; set; }
        public bool IsMaskInverted { get; set; }
        public ImagePoint DomainCenter { get; set; }
        public ImagePose ModelReference { get; set; }
        public int ReferenceImageWidth { get; set; }
        public int ReferenceImageHeight { get; set; }
        public byte[] ModelData { get; set; }
        public string ModelHash { get; set; }
        public string HalconVersion { get; set; }
        public long Revision { get; set; }
        public bool IsModelDirty { get; set; }

        public void MarkChanged(bool requiresModelRebuild = true)
        {
            Revision = Revision < 1 ? 1 : Revision + 1;
            if (requiresModelRebuild) IsModelDirty = true;
        }

        public void SetModelData(byte[] modelData, string halconVersion = null)
        {
            ModelData = modelData == null ? null : (byte[])modelData.Clone();
            ModelHash = ComputeModelHash(ModelData);
            HalconVersion = halconVersion;
            IsModelDirty = ModelData == null || ModelData.Length == 0;
        }

        public bool HasUsableModel
        {
            get { return !IsModelDirty && ModelData != null && ModelData.Length > 0; }
        }

        public static string ComputeModelHash(byte[] data)
        {
            if (data == null || data.Length == 0) return null;
            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(data);
                return BitConverter.ToString(hash).Replace("-", string.Empty).ToLowerInvariant();
            }
        }

        public TemplateDefinition DeepClone()
        {
            TemplateDefinition clone = (TemplateDefinition)MemberwiseClone();
            clone.TemplateRoi = TemplateRoi == null ? null : TemplateRoi.DeepClone();
            clone.SearchRegion = SearchRegion == null ? null : SearchRegion.DeepClone();
            clone.ShapeParameters = ShapeParameters == null ? null : ShapeParameters.DeepClone();
            clone.NccParameters = NccParameters == null ? null : NccParameters.DeepClone();
            clone.MatchParameters = MatchParameters == null ? null : MatchParameters.DeepClone();
            clone.ModelReference = ModelReference == null ? null : ModelReference.DeepClone();
            clone.ModelData = ModelData == null ? null : (byte[])ModelData.Clone();
            clone.MaskRegions = new List<TemplateMaskShape>();
            if (MaskRegions != null)
            {
                foreach (TemplateMaskShape shape in MaskRegions)
                    clone.MaskRegions.Add(shape == null ? null : shape.DeepClone());
            }

            clone.EraseStrokes = new List<TemplateEraseStroke>();
            if (EraseStrokes != null)
            {
                foreach (TemplateEraseStroke stroke in EraseStrokes)
                    clone.EraseStrokes.Add(stroke == null ? null : stroke.DeepClone());
            }

            return clone;
        }
    }
}
