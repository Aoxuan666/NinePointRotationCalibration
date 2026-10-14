using System.Drawing;
using NinePointRotationCalibration.Halcon;

namespace NinePointRotationCalibration.Station.Tests
{
    internal sealed class FakeTemplateMatchingEngine : ITemplateMatchingEngine
    {
        public TemplateMatchResult LocateResult { get; set; }
        public TemplateMatchCollectionResult LocateAllResult { get; set; }
        public int LocateAllCallCount { get; private set; }
        public bool? LastLocateAllIncludeContour { get; private set; }
        public int? LastLocateAllNumMatches { get; private set; }

        public TemplateBuildResult BuildTemplate(Bitmap referenceImage, TemplateDefinition definition)
        {
            return TemplateBuildResult.Failure(CalibrationErrorCode.InternalError, "Not used by this test fake.");
        }

        public TemplateMatchResult Locate(Bitmap image, TemplateDefinition definition, bool includeContour = true)
        {
            return LocateResult == null
                ? TemplateMatchResult.Failure(CalibrationErrorCode.TemplateMatchFailed, "No fake match configured.")
                : LocateResult.DeepClone();
        }

        public TemplateMatchCollectionResult LocateAll(
            Bitmap image,
            TemplateDefinition definition,
            bool includeContour = true)
        {
            LocateAllCallCount++;
            LastLocateAllIncludeContour = includeContour;
            LastLocateAllNumMatches = definition == null || definition.MatchParameters == null
                ? (int?)null
                : definition.MatchParameters.NumMatches;
            return LocateAllResult ?? TemplateMatchCollectionResult.Failure(
                CalibrationErrorCode.TemplateMatchFailed,
                "No fake match collection configured.");
        }

        public TemplateRegionPreviewResult PreviewEffectiveRegion(
            TemplateDefinition definition,
            int imageWidth,
            int imageHeight)
        {
            return new TemplateRegionPreviewResult
            {
                Success = false,
                ErrorCode = CalibrationErrorCode.InternalError,
                Message = "Not used by this test fake."
            };
        }

        public void ClearCache()
        {
        }

        public void Dispose()
        {
        }
    }
}
