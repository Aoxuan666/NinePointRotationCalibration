using System;
using System.Collections.Generic;

namespace NinePointRotationCalibration
{
    /// <summary>单条标定样本，记录机械位姿、图像匹配位姿及所属模板版本。</summary>
    [Serializable]
    public sealed class CalibrationSample
    {
        public CalibrationSample()
        {
            SampleId = Guid.NewGuid().ToString("N");
            MachinePose = new MachinePose();
            ImagePose = new ImagePose();
            MatchScore = 1.0;
            Enabled = true;
            CapturedAtUtc = DateTime.UtcNow;
        }

        public string SampleId { get; set; }
        public CalibrationSampleKind Kind { get; set; }
        public MachinePose MachinePose { get; set; }
        public ImagePose ImagePose { get; set; }
        public double MatchScore { get; set; }
        public bool Enabled { get; set; }
        /// <summary>采集该样本时对应的模板修订号，用于检测样本是否过期。</summary>
        public long TemplateRevision { get; set; }
        public string Tag { get; set; }
        public DateTime CapturedAtUtc { get; set; }

        /// <summary>判断样本是否匹配指定模板修订号。</summary>
        public bool IsCurrent(long templateRevision) { return TemplateRevision == templateRevision; }

        /// <summary>创建标定样本的深拷贝。</summary>
        public CalibrationSample DeepClone()
        {
            CalibrationSample clone = (CalibrationSample)MemberwiseClone();
            clone.MachinePose = MachinePose == null ? null : MachinePose.DeepClone();
            clone.ImagePose = ImagePose == null ? null : ImagePose.DeepClone();
            return clone;
        }
    }

    /// <summary>标定求解器的样本数量、RANSAC/MAD 外点剔除与残差阈值选项。</summary>
    [Serializable]
    public sealed class CalibrationSolverOptions
    {
        public CalibrationSolverOptions()
        {
            MinimumTranslationSamples = 9;
            MinimumTranslationInliers = 6;
            MinimumAxisTravel = 0.1;
            MinimumMatchScore = 0.5;
            EnableRansac = true;
            RansacIterations = 512;
            RansacInlierThresholdPixels = 1.5;
            MadMultiplier = 3.5;
            UseMatchScoreWeights = true;
            MaxRmsResidualPixels = 1.0;
            MaxResidualPixels = 3.0;
            MaxConditionNumber = 100000.0;
            RequireRotationCalibration = true;
            MinimumRotationSamples = 3;
            MinimumRotationInliers = 3;
            MinimumRotationSpanDegrees = 20.0;
            AngleOutlierThresholdDegrees = 2.0;
            MaxAngleRmsDegrees = 0.5;
            CenterOutlierThresholdPixels = 2.0;
            MaxRotationCenterRmsPixels = 1.0;
            RandomSeed = 19790531;
        }

        public int MinimumTranslationSamples { get; set; }
        public int MinimumTranslationInliers { get; set; }
        /// <summary>平移样本在 X、Y 轴上各自需覆盖的最小行程。</summary>
        public double MinimumAxisTravel { get; set; }
        public double MinimumMatchScore { get; set; }
        public bool EnableRansac { get; set; }
        public int RansacIterations { get; set; }
        public double RansacInlierThresholdPixels { get; set; }
        /// <summary>MAD 自适应外点判别阈值倍数（阈值 = 下限 与 中位数 + 倍数 * sigma 取较大值）。</summary>
        public double MadMultiplier { get; set; }
        /// <summary>是否按模板匹配分数平方对最小二乘拟合加权。</summary>
        public bool UseMatchScoreWeights { get; set; }
        public double MaxRmsResidualPixels { get; set; }
        public double MaxResidualPixels { get; set; }
        /// <summary>仿射线性部分允许的最大条件数，防止采样共线退化。</summary>
        public double MaxConditionNumber { get; set; }
        public bool RequireRotationCalibration { get; set; }
        public int MinimumRotationSamples { get; set; }
        public int MinimumRotationInliers { get; set; }
        /// <summary>旋转样本需覆盖的最小角度跨度（度）。</summary>
        public double MinimumRotationSpanDegrees { get; set; }
        public double AngleOutlierThresholdDegrees { get; set; }
        public double MaxAngleRmsDegrees { get; set; }
        public double CenterOutlierThresholdPixels { get; set; }
        public double MaxRotationCenterRmsPixels { get; set; }
        /// <summary>RANSAC 随机抽样固定种子，保证结果可复现。</summary>
        public int RandomSeed { get; set; }

        /// <summary>创建求解选项的深拷贝。</summary>
        public CalibrationSolverOptions DeepClone() { return (CalibrationSolverOptions)MemberwiseClone(); }
    }

    /// <summary>完整的九点与旋转标定任务，包含模板定义、求解配置与采集样本集合。</summary>
    [Serializable]
    public sealed class CalibrationJob
    {
        public const int CurrentSchemaVersion = 1;

        public CalibrationJob()
        {
            SchemaVersion = CurrentSchemaVersion;
            JobId = Guid.NewGuid().ToString("N");
            Name = "Nine-point rotation calibration";
            LinearUnit = LinearUnit.Millimeter;
            RotationConvention = RotationConvention.Unknown;
            Template = new TemplateDefinition();
            SolverOptions = new CalibrationSolverOptions();
            Samples = new List<CalibrationSample>();
            CreatedAtUtc = DateTime.UtcNow;
            UpdatedAtUtc = CreatedAtUtc;
        }

        public int SchemaVersion { get; set; }
        public string JobId { get; set; }
        public string Name { get; set; }
        public LinearUnit LinearUnit { get; set; }
        public string CustomLinearUnitName { get; set; }
        public RotationConvention RotationConvention { get; set; }
        public TemplateDefinition Template { get; set; }
        public CalibrationSolverOptions SolverOptions { get; set; }
        public List<CalibrationSample> Samples { get; set; }
        public DateTime CreatedAtUtc { get; set; }
        public DateTime UpdatedAtUtc { get; set; }

        /// <summary>克隆样本并绑定当前模板修订号后加入任务。</summary>
        public CalibrationSample AddSample(CalibrationSample sample)
        {
            if (sample == null) throw new ArgumentNullException("sample");
            if (Samples == null) Samples = new List<CalibrationSample>();
            CalibrationSample copy = sample.DeepClone();
            if (string.IsNullOrWhiteSpace(copy.SampleId)) copy.SampleId = Guid.NewGuid().ToString("N");
            copy.TemplateRevision = Template == null ? 0 : Template.Revision;
            if (copy.CapturedAtUtc == default(DateTime)) copy.CapturedAtUtc = DateTime.UtcNow;
            Samples.Add(copy);
            UpdatedAtUtc = DateTime.UtcNow;
            return copy;
        }

        /// <summary>递增模板修订号，并在需要时将模板模型标记为需重建。</summary>
        public void MarkTemplateChanged(bool requiresModelRebuild = true)
        {
            if (Template == null) Template = new TemplateDefinition();
            Template.MarkChanged(requiresModelRebuild);
            UpdatedAtUtc = DateTime.UtcNow;
        }

        /// <summary>检查是否存在已启用但模板修订号过期（陈旧）的样本。</summary>
        public bool HasStaleEnabledSamples
        {
            get
            {
                if (Samples == null || Template == null) return false;
                foreach (CalibrationSample sample in Samples)
                {
                    if (sample != null && sample.Enabled && !sample.IsCurrent(Template.Revision)) return true;
                }

                return false;
            }
        }

        /// <summary>创建标定任务的完整深拷贝。</summary>
        public CalibrationJob DeepClone()
        {
            CalibrationJob clone = (CalibrationJob)MemberwiseClone();
            clone.Template = Template == null ? null : Template.DeepClone();
            clone.SolverOptions = SolverOptions == null ? null : SolverOptions.DeepClone();
            clone.Samples = new List<CalibrationSample>();
            if (Samples != null)
            {
                foreach (CalibrationSample sample in Samples)
                    clone.Samples.Add(sample == null ? null : sample.DeepClone());
            }

            return clone;
        }
    }
}
