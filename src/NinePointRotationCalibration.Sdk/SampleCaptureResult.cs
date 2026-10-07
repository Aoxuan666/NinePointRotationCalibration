using System;

namespace NinePointRotationCalibration
{
    /// <summary>模板定位并追加标定点的返回结果。</summary>
    [Serializable]
    public sealed class SampleCaptureResult
    {
        /// <summary>定位和采点是否成功。</summary>
        public bool Success { get; set; }
        /// <summary>错误码。</summary>
        public CalibrationErrorCode ErrorCode { get; set; }
        /// <summary>结果说明。</summary>
        public string Message { get; set; }
        /// <summary>操作后的 Job；成功时调用方必须用它替换当前 Job。</summary>
        public CalibrationJob Job { get; set; }
        /// <summary>本次追加的样本。</summary>
        public CalibrationSample Sample { get; set; }
        /// <summary>本次模板匹配结果。</summary>
        public TemplateMatchResult Match { get; set; }

        /// <summary>创建采点失败结果，并保留输入快照。</summary>
        public static SampleCaptureResult Failure(
            CalibrationErrorCode errorCode,
            string message,
            CalibrationJob job = null,
            TemplateMatchResult match = null)
        {
            return new SampleCaptureResult
            {
                Success = false,
                ErrorCode = errorCode,
                Message = message,
                Job = job == null ? null : job.DeepClone(),
                Match = match == null ? null : match.DeepClone()
            };
        }
    }
}
