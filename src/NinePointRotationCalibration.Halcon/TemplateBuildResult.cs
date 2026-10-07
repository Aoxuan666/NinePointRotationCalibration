using System;
using System.Collections.Generic;

namespace NinePointRotationCalibration.Halcon
{
    [Serializable]
    public sealed class TemplateBuildResult
    {
        public TemplateBuildResult()
        {
            ContourSegments = new List<List<ImagePoint>>();
        }

        public bool Success { get; set; }
        public CalibrationErrorCode ErrorCode { get; set; }
        public string Message { get; set; }
        public TemplateDefinition Template { get; set; }
        public double EffectiveArea { get; set; }
        public List<List<ImagePoint>> ContourSegments { get; set; }
        public double ElapsedMilliseconds { get; set; }

        public static TemplateBuildResult Failure(CalibrationErrorCode code, string message)
        {
            return new TemplateBuildResult
            {
                Success = false,
                ErrorCode = code,
                Message = message
            };
        }
    }

    [Serializable]
    public sealed class TemplateRegionPreviewResult
    {
        public TemplateRegionPreviewResult()
        {
            ContourSegments = new List<List<ImagePoint>>();
        }

        public bool Success { get; set; }
        public CalibrationErrorCode ErrorCode { get; set; }
        public string Message { get; set; }
        public double EffectiveArea { get; set; }
        public ImagePoint DomainCenter { get; set; }
        public List<List<ImagePoint>> ContourSegments { get; set; }
    }

    [Serializable]
    public sealed class TemplateMatchCollectionResult
    {
        public TemplateMatchCollectionResult()
        {
            Matches = new List<TemplateMatchResult>();
        }

        public bool Success { get; set; }
        public CalibrationErrorCode ErrorCode { get; set; }
        public string Message { get; set; }
        public List<TemplateMatchResult> Matches { get; set; }
        public double ElapsedMilliseconds { get; set; }

        public static TemplateMatchCollectionResult Failure(CalibrationErrorCode code, string message)
        {
            return new TemplateMatchCollectionResult
            {
                Success = false,
                ErrorCode = code,
                Message = message
            };
        }
    }
}
