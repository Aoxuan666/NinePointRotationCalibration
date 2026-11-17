using System.Collections.Generic;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using NinePointRotationCalibration.WinForms.Models;

namespace NinePointRotationCalibration.WinForms.Services
{
    /// <summary>
    /// 模板编辑器依赖的后台视觉服务契约，负责特征提取、建模与匹配测试。
    /// </summary>
    public interface ITemplateEditorService
    {
        /// <summary>
        /// 异步提取模板特征点与轮廓预览。
        /// </summary>
        Task<TemplateFeatureResult> ExtractFeaturesAsync(
            Bitmap image,
            TemplateEditorState state,
            CancellationToken cancellationToken);

        /// <summary>
        /// 异步创建并序列化 HALCON 模板模型。
        /// </summary>
        Task<TemplateBuildResult> CreateModelAsync(
            Bitmap image,
            TemplateEditorState state,
            CancellationToken cancellationToken);

        /// <summary>
        /// 异步执行单次模板匹配测试。
        /// </summary>
        Task<TemplateTestResult> TestMatchAsync(
            Bitmap image,
            TemplateEditorState state,
            CancellationToken cancellationToken);
    }

    /// <summary>
    /// 标定配置窗体依赖的后台工作流服务契约，负责单图定位与标定矩阵求解。
    /// </summary>
    public interface ICalibrationWorkflow
    {
        ITemplateEditorService TemplateEditorService { get; }

        /// <summary>
        /// 使用当前模板对采样图像执行定位。
        /// </summary>
        Task<CalibrationLocateUiResult> LocateAsync(
            Bitmap image,
            TemplateEditorState template,
            CancellationToken cancellationToken);

        /// <summary>
        /// 根据已采集样本求解九点仿射变换与旋转中心。
        /// </summary>
        Task<CalibrationCalculationUiResult> CalculateAsync(
            IReadOnlyList<CalibrationSampleRow> samples,
            TemplateEditorState template,
            CancellationToken cancellationToken);
    }
}
