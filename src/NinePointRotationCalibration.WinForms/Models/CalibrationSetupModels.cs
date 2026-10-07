using System;
using System.Collections.Generic;
using System.Linq;
using NinePointRotationCalibration.WinForms.Geometry;

namespace NinePointRotationCalibration.WinForms.Models
{
    public enum CalibrationUiSampleKind
    {
        Translation,
        Rotation
    }

    [Serializable]
    public sealed class CalibrationSampleRow
    {
        public string SampleId { get; set; }

        public DateTime CapturedAtUtc { get; set; }

        public bool Enabled { get; set; } = true;

        public int Sequence { get; set; }

        public CalibrationUiSampleKind Kind { get; set; }

        public string Tag { get; set; }

        public double MachineX { get; set; }

        public double MachineY { get; set; }

        public double MachineThetaDegrees { get; set; }

        public double ImageRow { get; set; }

        public double ImageColumn { get; set; }

        public double ImageAngleDegrees { get; set; }

        public double Score { get; set; }

        public double ResidualPixels { get; set; }

        public bool IsInlier { get; set; } = true;

        public long TemplateRevision { get; set; }

        public string Status { get; set; }

        public CalibrationSampleRow DeepClone()
        {
            return (CalibrationSampleRow)MemberwiseClone();
        }
    }

    [Serializable]
    public sealed class CalibrationResultView
    {
        public CalibrationResultView()
        {
            PixelToStage = new double[6];
            StageToPixel = new double[6];
            Diagnostics = new List<string>();
        }

        public bool Success { get; set; }

        public string ErrorCode { get; set; }

        public string Message { get; set; }

        public double[] PixelToStage { get; set; }

        public double[] StageToPixel { get; set; }

        public double ResolutionX { get; set; }

        public double ResolutionY { get; set; }

        public double AxisAngleDegrees { get; set; }

        public double Determinant { get; set; }

        public double ConditionNumber { get; set; }

        public double RmsResidualPixels { get; set; }

        public double MaximumResidualPixels { get; set; }

        public int InlierCount { get; set; }

        public int RejectedCount { get; set; }

        public int RotationDirection { get; set; }

        public double RotationOffsetDegrees { get; set; }

        public ImageCoordinate? RotationCenterImage { get; set; }

        public double RotationCenterStageX { get; set; }

        public double RotationCenterStageY { get; set; }

        public double RotationAngleRmsDegrees { get; set; }

        public double RotationCenterResidualPixels { get; set; }

        public List<string> Diagnostics { get; set; }

        public CalibrationResultView DeepClone()
        {
            CalibrationResultView clone = (CalibrationResultView)MemberwiseClone();
            clone.PixelToStage = PixelToStage == null ? null : (double[])PixelToStage.Clone();
            clone.StageToPixel = StageToPixel == null ? null : (double[])StageToPixel.Clone();
            clone.Diagnostics = new List<string>(Diagnostics ?? new List<string>());
            return clone;
        }
    }

    [Serializable]
    public sealed class CalibrationSetupState
    {
        public CalibrationSetupState()
        {
            Template = new TemplateEditorState();
            Samples = new List<CalibrationSampleRow>();
            LinearUnit = "mm";
            MinimumTranslationSamples = 9;
            MinimumRotationSamples = 3;
        }

        public string Name { get; set; }

        public string LinearUnit { get; set; }

        public TemplateEditorState Template { get; set; }

        public List<CalibrationSampleRow> Samples { get; set; }

        public CalibrationResultView Result { get; set; }

        public int MinimumTranslationSamples { get; set; }

        public int MinimumRotationSamples { get; set; }

        public CalibrationSetupState DeepClone()
        {
            return new CalibrationSetupState
            {
                Name = Name,
                LinearUnit = LinearUnit,
                Template = Template == null ? new TemplateEditorState() : Template.DeepClone(),
                Samples = (Samples ?? new List<CalibrationSampleRow>())
                    .Where(item => item != null)
                    .Select(item => item.DeepClone())
                    .ToList(),
                Result = Result == null ? null : Result.DeepClone(),
                MinimumTranslationSamples = MinimumTranslationSamples,
                MinimumRotationSamples = MinimumRotationSamples
            };
        }
    }

    public sealed class CalibrationLocateUiResult : TemplateOperationResult
    {
        public ImageCoordinate Position { get; set; }

        public double AngleDegrees { get; set; }

        public double Score { get; set; }

        public double ElapsedMilliseconds { get; set; }

        public List<List<ImageCoordinate>> MatchedContours { get; set; } = new List<List<ImageCoordinate>>();
    }

    public sealed class CalibrationCalculationUiResult : TemplateOperationResult
    {
        public CalibrationResultView Result { get; set; }

        public List<CalibrationSampleRow> UpdatedSamples { get; set; } = new List<CalibrationSampleRow>();
    }
}
