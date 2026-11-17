using System;
using System.Collections.Generic;

namespace NinePointRotationCalibration
{
    /// <summary>单个标定样本的求解诊断明细，包含内点状态、预测值与残差。</summary>
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

        /// <summary>创建样本诊断信息的深拷贝。</summary>
        public CalibrationSampleDiagnostic DeepClone()
        {
            CalibrationSampleDiagnostic clone = (CalibrationSampleDiagnostic)MemberwiseClone();
            clone.PredictedImagePose = PredictedImagePose == null ? null : PredictedImagePose.DeepClone();
            return clone;
        }
    }

    /// <summary>旋转标定求解结果，包含角度方向、零偏与图像/机械坐标中的旋转中心。</summary>
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
        /// <summary>旋转方向乘子（Same 为 +1，Opposite 为 -1，Unknown 为 0）。</summary>
        public double DirectionMultiplier { get { return (double)(int)Direction; } }
        /// <summary>图像角与机械角关系式中的零偏常数（度）。</summary>
        public double AngleOffsetDegrees { get; set; }
        /// <summary>未约束方向的线性斜率诊断值，正常应接近 +1 或 -1。</summary>
        public double UnconstrainedSlope { get; set; }
        public double AngleRmsDegrees { get; set; }
        public double MaxAngleResidualDegrees { get; set; }
        public double MachineAngleSpanDegrees { get; set; }
        public ImagePoint RotationCenterImage { get; set; }
        public StagePoint RotationCenterStage { get; set; }
        /// <summary>旋转中心归一化所使用的参考机械台面坐标。</summary>
        public StagePoint ReferenceStage { get; set; }
        public double RadiusPixels { get; set; }
        public double CenterRmsResidualPixels { get; set; }
        public double MaxCenterResidualPixels { get; set; }
        public int CandidateCount { get; set; }
        public int InlierCount { get; set; }
        public int RejectedCount { get; set; }

        /// <summary>创建旋转结果的深拷贝。</summary>
        public RotationCalibrationResult DeepClone() { return (RotationCalibrationResult)MemberwiseClone(); }
    }

    /// <summary>九点仿射与旋转标定的汇总输出结果。</summary>
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
        /// <summary>机械平台坐标到图像像素坐标（Column, Row）的二维仿射矩阵。</summary>
        public AffineMatrix2D StageToPixelMatrix { get; set; }
        /// <summary>图像像素坐标到机械平台坐标的逆仿射矩阵。</summary>
        public AffineMatrix2D PixelToStageMatrix { get; set; }

        /// <summary>沿机械 X 轴移动时测得的像素当量（每像素对应的机械单位）。</summary>
        public double ResolutionX { get; set; }

        /// <summary>沿机械 Y 轴移动时测得的像素当量（每像素对应的机械单位）。</summary>
        public double ResolutionY { get; set; }

        public Vector2D StageXAxisInImage { get; set; }
        public Vector2D StageYAxisInImage { get; set; }
        public double AxisAngleDegrees { get; set; }
        public double Determinant { get; set; }
        public double ConditionNumber { get; set; }
        /// <summary>行列式为负时为 true，表示像素与机械坐标系存在镜像翻转。</summary>
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

        /// <summary>使用标定结果将机械坐标点映射到图像像素坐标。</summary>
        public ImagePoint TransformStageToImage(StagePoint point)
        {
            Point2D transformed = StageToPixelMatrix.Transform(point.ToCartesian());
            return ImagePoint.FromCartesian(transformed);
        }

        /// <summary>使用标定结果将图像像素坐标映射到机械坐标点。</summary>
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

        /// <summary>创建标定结果的完整深拷贝。</summary>
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

        /// <summary>构造失败的标定结果。</summary>
        public static CalibrationResult Failure(CalibrationErrorCode errorCode, string message)
        {
            return new CalibrationResult { Success = false, TranslationSuccess = false, ErrorCode = errorCode, Message = message };
        }
    }
}
