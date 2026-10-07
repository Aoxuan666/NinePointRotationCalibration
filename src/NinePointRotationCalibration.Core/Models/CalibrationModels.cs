using System;
using System.Collections.Generic;

namespace NinePointRotationCalibration
{
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
        public long TemplateRevision { get; set; }
        public string Tag { get; set; }
        public DateTime CapturedAtUtc { get; set; }

        public bool IsCurrent(long templateRevision) { return TemplateRevision == templateRevision; }

        public CalibrationSample DeepClone()
        {
            CalibrationSample clone = (CalibrationSample)MemberwiseClone();
            clone.MachinePose = MachinePose == null ? null : MachinePose.DeepClone();
            clone.ImagePose = ImagePose == null ? null : ImagePose.DeepClone();
            return clone;
        }
    }

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
        public double MinimumAxisTravel { get; set; }
        public double MinimumMatchScore { get; set; }
        public bool EnableRansac { get; set; }
        public int RansacIterations { get; set; }
        public double RansacInlierThresholdPixels { get; set; }
        public double MadMultiplier { get; set; }
        public bool UseMatchScoreWeights { get; set; }
        public double MaxRmsResidualPixels { get; set; }
        public double MaxResidualPixels { get; set; }
        public double MaxConditionNumber { get; set; }
        public bool RequireRotationCalibration { get; set; }
        public int MinimumRotationSamples { get; set; }
        public int MinimumRotationInliers { get; set; }
        public double MinimumRotationSpanDegrees { get; set; }
        public double AngleOutlierThresholdDegrees { get; set; }
        public double MaxAngleRmsDegrees { get; set; }
        public double CenterOutlierThresholdPixels { get; set; }
        public double MaxRotationCenterRmsPixels { get; set; }
        public int RandomSeed { get; set; }

        public CalibrationSolverOptions DeepClone() { return (CalibrationSolverOptions)MemberwiseClone(); }
    }

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

        public void MarkTemplateChanged(bool requiresModelRebuild = true)
        {
            if (Template == null) Template = new TemplateDefinition();
            Template.MarkChanged(requiresModelRebuild);
            UpdatedAtUtc = DateTime.UtcNow;
        }

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
