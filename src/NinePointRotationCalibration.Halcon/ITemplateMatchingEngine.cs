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

    /// <summary>快速提取模板特征预览轮廓的扩展接口，无需创建或序列化完整匹配模型。</summary>
    public interface IFeaturePreviewTemplateMatchingEngine
    {
        /// <summary>使用已转换的灰度图快速提取模板边缘特征与推荐阈值。</summary>
        TemplateBuildResult PreviewFeatures(HalconGrayImage grayImage, TemplateDefinition definition);
    }
}
