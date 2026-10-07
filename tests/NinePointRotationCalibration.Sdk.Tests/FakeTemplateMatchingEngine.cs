using System.Drawing;
using NinePointRotationCalibration.Halcon;

namespace NinePointRotationCalibration.Sdk.Tests
{
    internal sealed class FakeTemplateMatchingEngine : ITemplateMatchingEngine
    {
        public TemplateMatchResult LocateResult { get; set; }
        public bool MutateLocatedTemplate { get; set; }
        public int LocateCallCount { get; private set; }
        public int ClearCacheCallCount { get; private set; }
        public int DisposeCallCount { get; private set; }
        public Bitmap LastLocateImage { get; private set; }
        public TemplateDefinition LastDefinition { get; private set; }
        public bool LastIncludeContour { get; private set; }

        public TemplateBuildResult BuildTemplate(Bitmap referenceImage, TemplateDefinition definition)
        {
            return TemplateBuildResult.Failure(
                CalibrationErrorCode.InternalError,
                "BuildTemplate is not used by this fake.");
        }

        public TemplateMatchResult Locate(Bitmap image, TemplateDefinition definition, bool includeContour = true)
        {
            LocateCallCount++;
            LastLocateImage = image;
            LastDefinition = definition;
            LastIncludeContour = includeContour;
            if (MutateLocatedTemplate && definition != null)
            {
                definition.Revision += 1000;
                definition.ReferenceAnchor = new ImagePoint(-999, -888);
                if (definition.ModelData != null && definition.ModelData.Length > 0)
                    definition.ModelData[0] ^= 0xff;
            }

            return LocateResult ?? TemplateMatchResult.Failure(
                CalibrationErrorCode.TemplateMatchFailed,
                "No fake match result was configured.");
        }

        public TemplateMatchCollectionResult LocateAll(Bitmap image, TemplateDefinition definition, bool includeContour = true)
        {
            return TemplateMatchCollectionResult.Failure(
                CalibrationErrorCode.InternalError,
                "LocateAll is not used by this fake.");
        }

        public TemplateRegionPreviewResult PreviewEffectiveRegion(TemplateDefinition definition, int imageWidth, int imageHeight)
        {
            return new TemplateRegionPreviewResult
            {
                Success = false,
                ErrorCode = CalibrationErrorCode.InternalError,
                Message = "PreviewEffectiveRegion is not used by this fake."
            };
        }

        public void ClearCache()
        {
            ClearCacheCallCount++;
        }

        public void Dispose()
        {
            DisposeCallCount++;
        }
    }
}
