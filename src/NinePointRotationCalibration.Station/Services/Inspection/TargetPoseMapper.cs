using System;
using System.Collections.Generic;
using NinePointRotationCalibration.Station.Models;

namespace NinePointRotationCalibration.Station.Services.Inspection
{
    /// <summary>
    /// 双吸嘴独立双 R 轴（左吸嘴 R1 / 右吸嘴 R2）定位吸取 + 盲摆固定位置坐标映射器。
    /// 左右吸嘴分别使用各自独立标定的 XY 仿射矩阵、机械旋转中心、旋转方向系数与示教基准，
    /// 输出保持与输入 currentRLeft / currentRRight 同一连续角度坐标系（不做 [-180,180] 或 [0,360] 归一化）。
    /// </summary>
    public sealed class TargetPoseMapper
    {
        /// <summary>单标定兼容入口：左右吸嘴共用同一份标定与同一 R 轴角。</summary>
        public List<MappedTargetPose> Map(
            CalibrationResult calibration,
            TeachingData teaching,
            TemplateMatchResult boardMatch,
            double currentR)
        {
            return Map(calibration, calibration, teaching, boardMatch, currentR, currentR);
        }

        /// <summary>双 R 轴独立入口：分别为左吸嘴（R1）与右吸嘴（R2）计算目标机械坐标及连续坐标系角度。</summary>
        public List<MappedTargetPose> Map(
            CalibrationResult leftCalibration,
            CalibrationResult rightCalibration,
            TeachingData teaching,
            TemplateMatchResult boardMatch,
            double currentRLeft,
            double currentRRight)
        {
            List<MappedTargetPose> result = new List<MappedTargetPose>();
            if (boardMatch == null || !boardMatch.Success || teaching == null) return result;
            if (!IsFinite(boardMatch.Row) || !IsFinite(boardMatch.Column) || !IsFinite(boardMatch.AngleDegrees))
                return result;

            CalibrationResult effectiveLeft = IsCalibrationUsable(leftCalibration)
                ? leftCalibration
                : (IsCalibrationUsable(rightCalibration) ? rightCalibration : null);
            CalibrationResult effectiveRight = IsCalibrationUsable(rightCalibration)
                ? rightCalibration
                : (IsCalibrationUsable(leftCalibration) ? leftCalibration : null);

            if (effectiveLeft != null && IsFinite(currentRLeft))
            {
                AddMappedPose(result, effectiveLeft, teaching.Left, boardMatch, currentRLeft);
            }

            if (effectiveRight != null && IsFinite(currentRRight))
            {
                AddMappedPose(result, effectiveRight, teaching.Right, boardMatch, currentRRight);
            }

            return result;
        }

        private static void AddMappedPose(
            ICollection<MappedTargetPose> result,
            CalibrationResult calibration,
            TeachingPose teaching,
            TemplateMatchResult boardMatch,
            double currentR)
        {
            if (!IsUsable(teaching)) return;

            ImagePoint taughtAnchor = new ImagePoint(teaching.ImageRow, teaching.ImageColumn);
            StagePoint detectedStage = calibration.TransformImageToStage(boardMatch.Anchor);
            StagePoint taughtImageStage = calibration.TransformImageToStage(taughtAnchor);
            if (!IsFinite(detectedStage.X) || !IsFinite(detectedStage.Y) ||
                !IsFinite(taughtImageStage.X) || !IsFinite(taughtImageStage.Y))
            {
                return;
            }

            // 计算来料板在机械台面坐标系下的真实旋转角增量（自动处理镜像轴或图像行向下坐标系）。
            double boardRotation = NormalizeDegrees(
                StageAngleDegrees(calibration, boardMatch.Anchor, boardMatch.AngleDegrees) -
                StageAngleDegrees(calibration, taughtAnchor, teaching.ImageAngleDegrees));
            double radians = boardRotation * Math.PI / 180.0;
            double cos = Math.Cos(radians);
            double sin = Math.Sin(radians);

            // 若五点旋转标定测定了该侧 R 轴机械旋转中心相对于标定参考点（ReferenceStage）的偏心向量：
            // C_rot(M) = M + (RotationCenterStage - ReferenceStage)。
            // 吸取时该侧 R 轴旋转对齐 PCB，去固定位置盲摆时转回固定示教角，自动补偿旋转中心偏心摆臂位移。
            double rotCenterOffsetX = 0.0;
            double rotCenterOffsetY = 0.0;
            if (HasCalibratedRotationCenter(calibration.Rotation))
            {
                rotCenterOffsetX = calibration.Rotation.RotationCenterStage.X - calibration.Rotation.ReferenceStage.X;
                rotCenterOffsetY = calibration.Rotation.RotationCenterStage.Y - calibration.Rotation.ReferenceStage.Y;
            }

            double armX = (teaching.MachineX - taughtImageStage.X) + rotCenterOffsetX;
            double armY = (teaching.MachineY - taughtImageStage.Y) + rotCenterOffsetY;

            double direction = calibration.Rotation.DirectionMultiplier;

            // 相对该侧当前 R 轴计算最小角度补偿增量 offsetR，targetR = currentR + offsetR 保持与输入 currentR 同一连续坐标系（不做归一化）。
            double imageCorrection = NormalizeDegrees(boardMatch.AngleDegrees - teaching.ImageAngleDegrees);
            double pickR = teaching.MachineR + (direction * imageCorrection);
            double offsetR = NormalizeDegrees(pickR - currentR);
            double targetR = currentR + offsetR;

            double targetX = detectedStage.X - rotCenterOffsetX + (armX * cos) - (armY * sin);
            double targetY = detectedStage.Y - rotCenterOffsetY + (armX * sin) + (armY * cos);
            if (!IsFinite(targetX) || !IsFinite(targetY) ||
                !IsFinite(offsetR) || !IsFinite(targetR) || !IsFinite(teaching.MachineR))
            {
                return;
            }

            result.Add(new MappedTargetPose
            {
                Side = teaching.Side,
                Axis = ProtocolNozzle.GetAxisR(teaching.Side),
                X = targetX,
                Y = targetY,
                ROffset = offsetR,
                TargetR = targetR,
                PickR = targetR,
                BlindPlaceR = teaching.MachineR
            });
        }

        private static bool IsCalibrationUsable(CalibrationResult calibration)
        {
            return calibration != null && calibration.TranslationSuccess &&
                calibration.Rotation != null && calibration.Rotation.Success &&
                calibration.Rotation.IsAvailable &&
                Math.Abs(calibration.Rotation.DirectionMultiplier) >= 0.5;
        }

        private static bool HasCalibratedRotationCenter(RotationCalibrationResult rotation)
        {
            if (rotation == null || !rotation.IsAvailable || !rotation.Success) return false;
            if (!IsFinite(rotation.RotationCenterStage.X) || !IsFinite(rotation.RotationCenterStage.Y) ||
                !IsFinite(rotation.ReferenceStage.X) || !IsFinite(rotation.ReferenceStage.Y))
            {
                return false;
            }

            return Math.Abs(rotation.RotationCenterStage.X - rotation.ReferenceStage.X) > 1e-9 ||
                   Math.Abs(rotation.RotationCenterStage.Y - rotation.ReferenceStage.Y) > 1e-9;
        }

        /// <summary>
        /// 将图像坐标系下的模板朝向角换算为机械台面坐标系中的角度。
        /// HALCON 角度在 Row 向下的图像中为逆时针正方向，因此单位方向向量为 (cos a, -sin a)。
        /// </summary>
        private static double StageAngleDegrees(CalibrationResult calibration, ImagePoint anchor, double imageAngleDegrees)
        {
            const double ProbeLengthPixels = 100.0;
            double radians = imageAngleDegrees * Math.PI / 180.0;
            ImagePoint probe = new ImagePoint(
                anchor.Row - (ProbeLengthPixels * Math.Sin(radians)),
                anchor.Column + (ProbeLengthPixels * Math.Cos(radians)));
            StagePoint origin = calibration.TransformImageToStage(anchor);
            StagePoint tip = calibration.TransformImageToStage(probe);
            return Math.Atan2(tip.Y - origin.Y, tip.X - origin.X) * 180.0 / Math.PI;
        }

        internal static double NormalizeDegrees(double degrees)
        {
            double value = degrees % 360.0;
            if (value <= -180.0) value += 360.0;
            if (value > 180.0) value -= 360.0;
            return value;
        }

        private static bool IsUsable(TeachingPose pose)
        {
            return pose != null && pose.IsValid &&
                IsFinite(pose.ImageRow) && IsFinite(pose.ImageColumn) &&
                IsFinite(pose.ImageAngleDegrees) && IsFinite(pose.MachineX) &&
                IsFinite(pose.MachineY) && IsFinite(pose.MachineR);
        }

        private static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }
    }
}
