using System;
using System.Collections.Generic;

namespace NinePointRotationCalibration
{
    [Serializable]
    public sealed class CalibrationSampleDiagnostic
    {
        public CalibrationSampleDiagnostic()
        {
            PredictedImagePose = new ImagePose();
            PredictedStagePoint = new StagePoint();
        }

        public int SampleIndex { get; set; }
        public string SampleId { get; set; }
        public string Tag { get; set; }
        public CalibrationSampleKind Kind { get; set; }
        public bool IsEligible { get; set; }
        public bool IsInlier { get; set; }
        public string RejectionReason { get; set; }
        public ImagePose PredictedImagePose { get; set; }
        public StagePoint PredictedStagePoint { get; set; }
        public double ResidualColumnPixels { get; set; }
        public double ResidualRowPixels { get; set; }
        public double ResidualPixels { get; set; }
        public double ResidualStageX { get; set; }
        public double ResidualStageY { get; set; }
        public double ResidualStageDistance { get; set; }
        public double PredictedImageAngleDegrees { get; set; }
        public double AngleResidualDegrees { get; set; }
        public double RotationCenterResidualPixels { get; set; }

        public CalibrationSampleDiagnostic DeepClone()
        {
            CalibrationSampleDiagnostic clone = (CalibrationSampleDiagnostic)MemberwiseClone();
            clone.PredictedImagePose = PredictedImagePose == null ? null : PredictedImagePose.DeepClone();
            return clone;
        }
    }

    [Serializable]
    public sealed class RotationCalibrationResult
    {
        public RotationCalibrationResult()
        {
            ErrorCode = CalibrationErrorCode.RotationInsufficient;
        }

        public bool IsAvailable { get; set; }
        public bool Success { get; set; }
        public CalibrationErrorCode ErrorCode { get; set; }
        public string Message { get; set; }
        public RotationDirection Direction { get; set; }
        public double DirectionMultiplier { get { return (double)(int)Direction; } }
        public double AngleOffsetDegrees { get; set; }
        public double UnconstrainedSlope { get; set; }
        public double AngleRmsDegrees { get; set; }
        public double MaxAngleResidualDegrees { get; set; }
        public double MachineAngleSpanDegrees { get; set; }
        public ImagePoint RotationCenterImage { get; set; }
        public StagePoint RotationCenterStage { get; set; }
        public StagePoint ReferenceStage { get; set; }
        public double RadiusPixels { get; set; }
        public double CenterRmsResidualPixels { get; set; }
        public double MaxCenterResidualPixels { get; set; }
        public int CandidateCount { get; set; }
        public int InlierCount { get; set; }
        public int RejectedCount { get; set; }

        public RotationCalibrationResult DeepClone() { return (RotationCalibrationResult)MemberwiseClone(); }
    }

    [Serializable]
    public sealed class CalibrationResult
    {
        public CalibrationResult()
        {
            Diagnostics = new List<CalibrationSampleDiagnostic>();
            Warnings = new List<string>();
            Rotation = new RotationCalibrationResult();
        }

        public bool Success { get; set; }
        public bool TranslationSuccess { get; set; }
        public CalibrationErrorCode ErrorCode { get; set; }
        public string Message { get; set; }
        public AffineMatrix2D StageToPixelMatrix { get; set; }
        public AffineMatrix2D PixelToStageMatrix { get; set; }

        /// <summary>Stage units per pixel measured while moving the machine X axis.</summary>
        public double ResolutionX { get; set; }

        /// <summary>Stage units per pixel measured while moving the machine Y axis.</summary>
        public double ResolutionY { get; set; }

        public Vector2D StageXAxisInImage { get; set; }
        public Vector2D StageYAxisInImage { get; set; }
        public double AxisAngleDegrees { get; set; }
        public double Determinant { get; set; }
        public double ConditionNumber { get; set; }
        public bool IsMirrored { get; set; }
        public double RmsResidualPixels { get; set; }
        public double MaxResidualPixels { get; set; }
        public double RmsResidualStage { get; set; }
        public double MaxResidualStage { get; set; }
        public double XTravel { get; set; }
        public double YTravel { get; set; }
        public int CandidateCount { get; set; }
        public int InlierCount { get; set; }
        public int RejectedCount { get; set; }
        public RotationCalibrationResult Rotation { get; set; }
        public List<CalibrationSampleDiagnostic> Diagnostics { get; set; }
        public List<string> Warnings { get; set; }

        public ImagePoint TransformStageToImage(StagePoint point)
        {
            Point2D transformed = StageToPixelMatrix.Transform(point.ToCartesian());
            return ImagePoint.FromCartesian(transformed);
        }

        public StagePoint TransformImageToStage(ImagePoint point)
        {
            Point2D transformed = PixelToStageMatrix.Transform(point.ToCartesian());
            return StagePoint.FromCartesian(transformed);
        }

        public RotationDirection RotationDirection
        {
            get { return Rotation == null ? NinePointRotationCalibration.RotationDirection.Unknown : Rotation.Direction; }
        }

        public double RotationOffsetDegrees
        {
            get { return Rotation == null ? double.NaN : Rotation.AngleOffsetDegrees; }
        }

        public ImagePoint RotationCenterImage
        {
            get { return Rotation == null ? default(ImagePoint) : Rotation.RotationCenterImage; }
        }

        public StagePoint RotationCenterStage
        {
            get { return Rotation == null ? default(StagePoint) : Rotation.RotationCenterStage; }
        }

        public CalibrationResult DeepClone()
        {
            CalibrationResult clone = (CalibrationResult)MemberwiseClone();
            clone.Rotation = Rotation == null ? null : Rotation.DeepClone();
            clone.Warnings = Warnings == null ? new List<string>() : new List<string>(Warnings);
            clone.Diagnostics = new List<CalibrationSampleDiagnostic>();
            if (Diagnostics != null)
            {
                foreach (CalibrationSampleDiagnostic diagnostic in Diagnostics)
                    clone.Diagnostics.Add(diagnostic == null ? null : diagnostic.DeepClone());
            }

            return clone;
        }

        public static CalibrationResult Failure(CalibrationErrorCode errorCode, string message)
        {
            return new CalibrationResult { Success = false, TranslationSuccess = false, ErrorCode = errorCode, Message = message };
        }
    }
}
