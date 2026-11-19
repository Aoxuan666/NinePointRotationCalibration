using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using NinePointRotationCalibration.WinForms.Geometry;

namespace NinePointRotationCalibration.WinForms.Models
{
    /// <summary>
    /// 模板编辑器中的示教建模与运行匹配参数配置。
    /// </summary>
    [Serializable]
    public sealed class TemplateEditorParameters
    {
        public TemplateEditorParameters()
        {
            ModelType = TemplateModelType.Shape;
            NumLevels = 5;
            MatchNumLevels = 0;
            AngleStartDegrees = -0.3 * 180.0 / Math.PI;
            AngleExtentDegrees = 0.6 * 180.0 / Math.PI;
            MatchAngleStartDegrees = -5.0;
            MatchAngleExtentDegrees = 10.0;
            _hasSeparateMatchParameters = true;
            AngleStepDegrees = 0.0;
            Optimization = "auto";
            Metric = "use_polarity";
            ShapeMetric = "use_polarity";
            NccMetric = "use_polarity";
            AutoThreshold = true;
            Contrast = 30.0;
            MinimumContrast = 10.0;
            FeatureExtractionMode = ShapeFeatureExtractionMode.EdgeContours;
            EdgeSigma = 1.2;
            EdgeLowThreshold = 20.0;
            EdgeHighThreshold = 45.0;
            MinimumContourLengthPixels = 9.0;
            FeaturePointSpacingPixels = 6.0;
            MaximumFeaturePointCount = 20000;
            MinimumScore = 0.5;
            Greediness = 0.85;
            MaximumOverlap = 0.3;
            MatchCount = 1;
            SubPixel = "interpolation";
            TimeoutMilliseconds = 0;
            ContourPointSpacingPixels = 3.0;
            AllowPartialMatch = false;
            BrushRadius = 12.0;
        }

        public TemplateModelType ModelType { get; set; }

        public int NumLevels { get; set; }

        [field: OptionalField]
        public int MatchNumLevels { get; set; }

        public double AngleStartDegrees { get; set; }

        public double AngleExtentDegrees { get; set; }

        [field: OptionalField]
        public double MatchAngleStartDegrees { get; set; }

        [field: OptionalField]
        public double MatchAngleExtentDegrees { get; set; }

        public double AngleStepDegrees { get; set; }

        public string Optimization { get; set; }

        public string Metric { get; set; }

        public string ShapeMetric { get; set; }

        public string NccMetric { get; set; }

        /// <summary>启用 HALCON 自适应阈值与对比度估计（默认开启）。</summary>
        [field: OptionalField]
        public bool AutoThreshold { get; set; }

        public double Contrast { get; set; }

        public double MinimumContrast { get; set; }

        public ShapeFeatureExtractionMode FeatureExtractionMode { get; set; }

        public double EdgeSigma { get; set; }

        public double EdgeLowThreshold { get; set; }

        public double EdgeHighThreshold { get; set; }

        public double MinimumContourLengthPixels { get; set; }

        public double FeaturePointSpacingPixels { get; set; }

        public int MaximumFeaturePointCount { get; set; }

        public double MinimumScore { get; set; }

        public double Greediness { get; set; }

        public double MaximumOverlap { get; set; }

        public int MatchCount { get; set; }

        public string SubPixel { get; set; }

        public int TimeoutMilliseconds { get; set; }

        public double ContourPointSpacingPixels { get; set; }

        public bool AllowPartialMatch { get; set; }

        public double BrushRadius { get; set; }

        [OptionalField]
        private bool _hasSeparateMatchParameters;

        [OnDeserializing]
        private void OnDeserializing(StreamingContext context)
        {
            AutoThreshold = true;
        }

        [OnDeserialized]
        private void OnDeserialized(StreamingContext context)
        {
            // 兼容旧版序列化数据：未拆分匹配参数时回退沿用示教金字塔层数与角度范围
            if (_hasSeparateMatchParameters) return;
            MatchNumLevels = NumLevels;
            MatchAngleStartDegrees = AngleStartDegrees;
            MatchAngleExtentDegrees = AngleExtentDegrees;
            _hasSeparateMatchParameters = true;
        }

        /// <summary>
        /// 创建参数配置的副本。
        /// </summary>
        public TemplateEditorParameters DeepClone()
        {
            return (TemplateEditorParameters)MemberwiseClone();
        }
    }

    /// <summary>
    /// 模板编辑器的完整状态，包含各 ROI、基准中心、掩膜笔画、参数及 HALCON 模型数据。
    /// </summary>
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

        public RotatedRectangle DetectionRoi { get; set; }

        /// <summary>
        /// 上位机使用的定位基准中心，可独立于 HALCON 模型域中心进行平移。
        /// </summary>
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

        /// <summary>
        /// 模板结构或参数已修改、需要重新创建模型时为 true。
        /// </summary>
        public bool ModelDirty { get; set; }

        public bool HasModel
        {
            get { return ModelData != null && ModelData.Length > 0; }
        }

        /// <summary>
        /// 创建模板编辑器状态的深拷贝。
        /// </summary>
        public TemplateEditorState DeepClone()
        {
            TemplateEditorState clone = new TemplateEditorState
            {
                TemplateRoi = TemplateRoi == null ? null : TemplateRoi.DeepClone(),
                SearchRoi = SearchRoi == null ? null : SearchRoi.DeepClone(),
                DetectionRoi = DetectionRoi == null ? null : DetectionRoi.DeepClone(),
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

    /// <summary>
    /// 模板编辑相关后台操作的通用返回结果基类。
    /// </summary>
    public class TemplateOperationResult
    {
        public bool Success { get; set; }

        public string Message { get; set; }

        public Exception Exception { get; set; }

        /// <summary>
        /// 构造失败状态的操作结果。
        /// </summary>
        public static TemplateOperationResult Failed(string message, Exception exception = null)
        {
            return new TemplateOperationResult { Success = false, Message = message, Exception = exception };
        }
    }

    /// <summary>
    /// 特征提取操作返回结果，包含预览特征点、轮廓及推荐参数。
    /// </summary>
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

    /// <summary>
    /// 模型创建操作返回结果，包含序列化的 HALCON 模型数据与参考元数据。
    /// </summary>
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

        /// <summary>建模完成后实际采用并回填的参数（含自适应算出的有效阈值）。</summary>
        public TemplateEditorParameters ResolvedParameters { get; set; }
    }

    /// <summary>
    /// 模板测试匹配操作返回结果。
    /// </summary>
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
