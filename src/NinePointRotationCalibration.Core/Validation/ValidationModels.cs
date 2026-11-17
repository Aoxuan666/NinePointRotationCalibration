using System;
using System.Collections.Generic;

namespace NinePointRotationCalibration
{
    /// <summary>单条标定任务校验问题记录。</summary>
    [Serializable]
    public sealed class ValidationIssue
    {
        public ValidationSeverity Severity { get; set; }
        public string Code { get; set; }
        public string Message { get; set; }
        public string MemberName { get; set; }
        public string SampleId { get; set; }
    }

    /// <summary>标定任务校验结果集合，汇总错误与警告信息。</summary>
    [Serializable]
    public sealed class JobValidationResult
    {
        public JobValidationResult()
        {
            Issues = new List<ValidationIssue>();
        }

        public List<ValidationIssue> Issues { get; set; }

        /// <summary>是否存在任意错误级别的问题（仅包含警告时仍为 true）。</summary>
        public bool IsValid
        {
            get
            {
                if (Issues == null) return true;
                foreach (ValidationIssue issue in Issues)
                {
                    if (issue != null && issue.Severity == ValidationSeverity.Error) return false;
                }

                return true;
            }
        }

        public int ErrorCount { get { return Count(ValidationSeverity.Error); } }
        public int WarningCount { get { return Count(ValidationSeverity.Warning); } }

        /// <summary>向结果中添加一条校验问题。</summary>
        public void Add(ValidationSeverity severity, string code, string message, string memberName = null, string sampleId = null)
        {
            if (Issues == null) Issues = new List<ValidationIssue>();
            Issues.Add(new ValidationIssue
            {
                Severity = severity,
                Code = code,
                Message = message,
                MemberName = memberName,
                SampleId = sampleId
            });
        }

        private int Count(ValidationSeverity severity)
        {
            int count = 0;
            if (Issues == null) return count;
            foreach (ValidationIssue issue in Issues)
            {
                if (issue != null && issue.Severity == severity) count++;
            }

            return count;
        }
    }
}
