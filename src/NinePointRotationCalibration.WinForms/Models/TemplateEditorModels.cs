using System;
using System.Collections.Generic;
using System.Linq;
using NinePointRotationCalibration.WinForms.Geometry;

namespace NinePointRotationCalibration.WinForms.Models
{
    [Serializable]
    public sealed class TemplateEditorParameters
    {
        public TemplateEditorParameters()
        {
            ModelType = TemplateModelType.Shape;
            NumLevels = 0;
            AngleStartDegrees = -30.0;
            AngleExtentDegrees = 60.0;
            AngleStepDegrees = 0.0;
            Optimization = "auto";
            Metric = "use_polarity";
            Contrast = 30.0;
            MinimumContrast = 10.0;
            MinimumScore = 0.6;
            Greediness = 0.8;
            MaximumOverlap = 0.5;
            MatchCount = 1;
            SubPixel = "least_squares";
            TimeoutMilliseconds = 2000;
            ContourPointSpacingPixels = 3.0;
            AllowPartialMatch = false;
            BrushRadius = 12.0;
        }

        public TemplateModelType ModelType { get; set; }

        public int NumLevels { get; set; }

        public double AngleStartDegrees { get; set; }

        public double AngleExtentDegrees { get; set; }

        public double AngleStepDegrees { get; set; }

        public string Optimization { get; set; }

        public string Metric { get; set; }

        public double Contrast { get; set; }

        public double MinimumContrast { get; set; }

        public double MinimumScore { get; set; }

        public double Greediness { get; set; }

        public double MaximumOverlap { get; set; }

        public int MatchCount { get; set; }

        public string SubPixel { get; set; }

        public int TimeoutMilliseconds { get; set; }

        public double ContourPointSpacingPixels { get; set; }

        public bool AllowPartialMatch { get; set; }

        public double BrushRadius { get; set; }

        public TemplateEditorParameters DeepClone()
        {
            return (TemplateEditorParameters)MemberwiseClone();
        }
    }

    [Serializable]
    public sealed class TemplateEditorState
    {
        public TemplateEditorState()
        {
            Parameters = new TemplateEditorParameters();
            MaskStrokes = new List<MaskStroke>();
            FeaturePoints = new List<ImageCoordinate>();
            ModelContours = new List<List<ImageCoordinate>>();
            ModelDirty = true;
            AnchorLocked = false;
            TemplateRoiLocked = false;
            SearchRoiLocked = false;
            MaskVisible = true;
        }

        public RotatedRectangle TemplateRoi { get; set; }

        public RotatedRectangle SearchRoi { get; set; }

        public ImageCoordinate ReferenceAnchor { get; set; }

        public bool AnchorLocked { get; set; }

        public bool TemplateRoiLocked { get; set; }

        public bool SearchRoiLocked { get; set; }

        public bool MaskVisible { get; set; }

        public bool MaskInverted { get; set; }

        public List<MaskStroke> MaskStrokes { get; set; }

        public TemplateEditorParameters Parameters { get; set; }

        public List<ImageCoordinate> FeaturePoints { get; set; }

        public List<List<ImageCoordinate>> ModelContours { get; set; }

        public byte[] ModelData { get; set; }

        public string ModelFormat { get; set; }

        public string ModelHash { get; set; }

        public ImageCoordinate DomainCenter { get; set; }

        public ImageCoordinate ModelReferencePosition { get; set; }

        public double ModelReferenceAngleDegrees { get; set; }

        public int ReferenceImageWidth { get; set; }

        public int ReferenceImageHeight { get; set; }

        public string HalconVersion { get; set; }

        public long Revision { get; set; }

        public bool ModelDirty { get; set; }

        public bool HasModel
        {
            get { return ModelData != null && ModelData.Length > 0; }
        }

        public TemplateEditorState DeepClone()
        {
            TemplateEditorState clone = new TemplateEditorState
            {
                TemplateRoi = TemplateRoi == null ? null : TemplateRoi.DeepClone(),
                SearchRoi = SearchRoi == null ? null : SearchRoi.DeepClone(),
                ReferenceAnchor = ReferenceAnchor,
                AnchorLocked = AnchorLocked,
                TemplateRoiLocked = TemplateRoiLocked,
                SearchRoiLocked = SearchRoiLocked,
                MaskVisible = MaskVisible,
                MaskInverted = MaskInverted,
                Parameters = Parameters == null ? new TemplateEditorParameters() : Parameters.DeepClone(),
                ModelData = ModelData == null ? null : (byte[])ModelData.Clone(),
                ModelFormat = ModelFormat,
                ModelHash = ModelHash,
                DomainCenter = DomainCenter,
                ModelReferencePosition = ModelReferencePosition,
                ModelReferenceAngleDegrees = ModelReferenceAngleDegrees,
                ReferenceImageWidth = ReferenceImageWidth,
                ReferenceImageHeight = ReferenceImageHeight,
                HalconVersion = HalconVersion,
                Revision = Revision,
                ModelDirty = ModelDirty
            };
            clone.MaskStrokes = (MaskStrokes ?? new List<MaskStroke>())
                .Where(item => item != null)
                .Select(item => item.DeepClone())
                .ToList();
            clone.FeaturePoints = new List<ImageCoordinate>(FeaturePoints ?? new List<ImageCoordinate>());
            clone.ModelContours = (ModelContours ?? new List<List<ImageCoordinate>>())
                .Where(item => item != null)
                .Select(item => new List<ImageCoordinate>(item))
                .ToList();
            return clone;
        }
    }

    public class TemplateOperationResult
    {
        public bool Success { get; set; }

        public string Message { get; set; }

        public Exception Exception { get; set; }

        public static TemplateOperationResult Failed(string message, Exception exception = null)
        {
            return new TemplateOperationResult { Success = false, Message = message, Exception = exception };
        }
    }

    public sealed class TemplateFeatureResult : TemplateOperationResult
    {
        public TemplateFeatureResult()
        {
            FeaturePoints = new List<ImageCoordinate>();
            Contours = new List<List<ImageCoordinate>>();
        }

        public List<ImageCoordinate> FeaturePoints { get; set; }

        public List<List<ImageCoordinate>> Contours { get; set; }

        public TemplateEditorParameters RecommendedParameters { get; set; }
    }

    public sealed class TemplateBuildResult : TemplateOperationResult
    {
        public TemplateBuildResult()
        {
            Contours = new List<List<ImageCoordinate>>();
        }

        public byte[] ModelData { get; set; }

        public string ModelFormat { get; set; }

        public string ModelHash { get; set; }

        public ImageCoordinate DomainCenter { get; set; }

        public ImageCoordinate ModelReferencePosition { get; set; }

        public double ModelReferenceAngleDegrees { get; set; }

        public int ReferenceImageWidth { get; set; }

        public int ReferenceImageHeight { get; set; }

        public string HalconVersion { get; set; }

        public List<List<ImageCoordinate>> Contours { get; set; }
    }

    public sealed class TemplateTestResult : TemplateOperationResult
    {
        public TemplateTestResult()
        {
            MatchedContours = new List<List<ImageCoordinate>>();
        }

        public ImageCoordinate Position { get; set; }

        public double AngleDegrees { get; set; }

        public double Score { get; set; }

        public double ElapsedMilliseconds { get; set; }

        public List<List<ImageCoordinate>> MatchedContours { get; set; }
    }
}
