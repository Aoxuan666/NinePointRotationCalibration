using System;

namespace NinePointRotationCalibration
{
    /// <summary>完整配置窗口的返回结果。</summary>
    [Serializable]
    public sealed class CalibrationSetupResult
    {
        /// <summary>用户是否确认配置。</summary>
        public bool Accepted { get; set; }
        /// <summary>错误码；用户取消时为 None。</summary>
        public CalibrationErrorCode ErrorCode { get; set; }
        /// <summary>结果说明。</summary>
        public string Message { get; set; }
        /// <summary>确认后的新 Job；调用方应保存并继续复用。</summary>
        public CalibrationJob Job { get; set; }

        /// <summary>创建用户取消结果，并返回原 Job 的副本。</summary>
        public static CalibrationSetupResult Cancelled(CalibrationJob original)
        {
            return new CalibrationSetupResult
            {
                Accepted = false,
                ErrorCode = CalibrationErrorCode.None,
                Message = "用户取消了标定配置。",
                Job = original == null ? null : original.DeepClone()
            };
        }

        /// <summary>创建配置失败结果。</summary>
        public static CalibrationSetupResult Failure(CalibrationErrorCode code, string message)
        {
            return new CalibrationSetupResult
            {
                Accepted = false,
                ErrorCode = code,
                Message = message
            };
        }
    }

    /// <summary>Job 保存或加载结果。</summary>
    [Serializable]
    public sealed class JobPersistenceResult
    {
        /// <summary>操作是否成功。</summary>
        public bool Success { get; set; }
        /// <summary>错误码。</summary>
        public CalibrationErrorCode ErrorCode { get; set; }
        /// <summary>结果说明。</summary>
        public string Message { get; set; }
        /// <summary>实际文件路径。</summary>
        public string Path { get; set; }
        /// <summary>加载结果，或保存时返回的 Job 快照。</summary>
        public CalibrationJob Job { get; set; }
    }

    /// <summary>模板编辑窗口的返回结果。</summary>
    [Serializable]
    public sealed class TemplateSetupResult
    {
        /// <summary>用户是否确认模板。</summary>
        public bool Accepted { get; set; }
        /// <summary>错误码；用户取消时为 None。</summary>
        public CalibrationErrorCode ErrorCode { get; set; }
        /// <summary>结果说明。</summary>
        public string Message { get; set; }
        /// <summary>确认后的模板。</summary>
        public TemplateDefinition Template { get; set; }

        /// <summary>创建用户取消结果，并返回原模板的副本。</summary>
        public static TemplateSetupResult Cancelled(TemplateDefinition original)
        {
            return new TemplateSetupResult
            {
                Accepted = false,
                ErrorCode = CalibrationErrorCode.None,
                Message = "用户取消了模板配置。",
                Template = original == null ? null : original.DeepClone()
            };
        }

        /// <summary>创建模板配置失败结果。</summary>
        public static TemplateSetupResult Failure(CalibrationErrorCode code, string message)
        {
            return new TemplateSetupResult
            {
                Accepted = false,
                ErrorCode = code,
                Message = message
            };
        }
    }
}
