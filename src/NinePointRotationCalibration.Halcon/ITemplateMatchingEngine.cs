using System;
using System.Drawing;

namespace NinePointRotationCalibration.Halcon
{
    /// <summary>定义模板创建、多目标定位与有效区域预览的匹配引擎接口。</summary>
    public interface ITemplateMatchingEngine : IDisposable
    {
        /// <summary>根据参考图像和模板定义创建 HALCON 模板。</summary>
        TemplateBuildResult BuildTemplate(Bitmap referenceImage, TemplateDefinition definition);
        /// <summary>在图像中定位得分最高的目标。</summary>
        TemplateMatchResult Locate(Bitmap image, TemplateDefinition definition, bool includeContour = true);
        /// <summary>在图像中定位所有满足条件的匹配目标。</summary>
        TemplateMatchCollectionResult LocateAll(Bitmap image, TemplateDefinition definition, bool includeContour = true);
        /// <summary>生成模板有效区域（ROI 叠加掩膜和擦除笔迹）的预览轮廓。</summary>
        TemplateRegionPreviewResult PreviewEffectiveRegion(TemplateDefinition definition, int imageWidth, int imageHeight);
        /// <summary>清除已缓存的原生 HALCON 模板句柄。</summary>
        void ClearCache();
    }
}
