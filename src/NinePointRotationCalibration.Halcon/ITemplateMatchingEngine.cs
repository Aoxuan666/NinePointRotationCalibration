using System;
using System.Drawing;

namespace NinePointRotationCalibration.Halcon
{
    public interface ITemplateMatchingEngine : IDisposable
    {
        TemplateBuildResult BuildTemplate(Bitmap referenceImage, TemplateDefinition definition);
        TemplateMatchResult Locate(Bitmap image, TemplateDefinition definition, bool includeContour = true);
        TemplateMatchCollectionResult LocateAll(Bitmap image, TemplateDefinition definition, bool includeContour = true);
        TemplateRegionPreviewResult PreviewEffectiveRegion(TemplateDefinition definition, int imageWidth, int imageHeight);
        void ClearCache();
    }
}
