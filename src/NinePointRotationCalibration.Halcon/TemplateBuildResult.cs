using System;
using System.Collections.Generic;

namespace NinePointRotationCalibration.Halcon
{
    /// <summary>模板创建结果，包含生成的模板定义、有效面积和参考轮廓。</summary>
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

        /// <summary>创建模板构建失败结果。</summary>
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

    /// <summary>模板有效区域（ROI 应用掩膜和擦除笔迹后）的预览结果。</summary>
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

    /// <summary>多目标模板匹配结果集合。</summary>
    [Serializable]
    public sealed class TemplateMatchCollectionResult
    {
        public TemplateMatchCollectionResult()
        {
            Matches = new List<TemplateMatchResult>();
        }

        public bool Success { get; set; }
        public CalibrationErrorCode ErrorCode { get; set; }
        /// <summary>匹配引擎正常执行完成但视野内未发现任何达到阈值目标时为 true（用于区分空料与引擎异常）。</summary>
        public bool IsNoMatch { get; set; }
        public string Message { get; set; }
        public List<TemplateMatchResult> Matches { get; set; }
        public double ElapsedMilliseconds { get; set; }

        /// <summary>创建模板匹配失败结果。</summary>
        public static TemplateMatchCollectionResult Failure(CalibrationErrorCode code, string message)
        {
            return new TemplateMatchCollectionResult
            {
                Success = false,
                ErrorCode = code,
                IsNoMatch = false,
                Message = message
            };
        }

        /// <summary>创建匹配引擎正常完成但结果为 0 个目标的空料/无匹配结果。</summary>
        public static TemplateMatchCollectionResult NoMatch(string message)
        {
            return new TemplateMatchCollectionResult
            {
                Success = false,
                ErrorCode = CalibrationErrorCode.TemplateMatchFailed,
                IsNoMatch = true,
                Message = message
            };
        }
    }
}
