using System;
using System.Collections.Generic;
using NinePointRotationCalibration.Station.Models;

namespace NinePointRotationCalibration.Station.Services.Inspection
{
    /// <summary>
    /// 单轴双吸嘴（左/右吸嘴）定位吸取 + 盲摆固定位置坐标映射器。
    /// 工艺动作：吸取时旋转 R 轴对齐来料 PCB 角度（PickR = MachineR + ROffset），
    /// 吸取后移动至固定盲摆工位并将 R 轴转回固定示教角（BlindPlaceR = MachineR，常为 0°）。
    /// 当旋转标定包含有效的机械旋转中心（RotationCenterStage / ReferenceStage）时，
    /// 自动补偿 R 轴从吸取对齐角转回盲摆固定角过程中的旋转中心偏心摆臂位移。
    /// </summary>
    public sealed class TargetPoseMapper
    {
        public List<MappedTargetPose> Map(
            CalibrationResult calibration,
            TeachingData teaching,
            TemplateMatchResult boardMatch,
            double currentR)
        {
            List<MappedTargetPose> result = new List<MappedTargetPose>();
            if (calibration == null || !calibration.TranslationSuccess || boardMatch == null || !boardMatch.Success)
                return result;
            if (calibration.Rotation == null || !calibration.Rotation.Success ||
                !calibration.Rotation.IsAvailable ||
                Math.Abs(calibration.Rotation.DirectionMultiplier) < 0.5)
                return result;
            if (teaching == null) return result;
            if (!IsFinite(currentR) || !IsFinite(boardMatch.AngleDegrees)) return result;

            AddMappedPose(result, calibration, teaching.Left, boardMatch, currentR);
            AddMappedPose(result, calibration, teaching.Right, boardMatch, currentR);
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

            // 计算来料板在机械台面坐标系下的真实旋转角增量（自动处理镜像轴或图像行向下坐标系）。
            double boardRotation = NormalizeDegrees(
                StageAngleDegrees(calibration, boardMatch.Anchor, boardMatch.AngleDegrees) -
                StageAngleDegrees(calibration, taughtAnchor, teaching.ImageAngleDegrees));
            double radians = boardRotation * Math.PI / 180.0;
            double cos = Math.Cos(radians);
            double sin = Math.Sin(radians);

            // 若五点旋转标定测定了 R 轴机械旋转中心相对于标定参考点（ReferenceStage）的偏心向量：
            // C_rot(M) = M + (RotationCenterStage - ReferenceStage)。
            // 吸取时 R 轴旋转对齐 PCB，去固定位置盲摆时 R 轴转回固定示教角（0°）：
            // 吸取时刻必须让旋转后的吸嘴/板锚点向量与 R 轴旋转中心严格重合，转回 0° 盲摆时才能落位零偏差。
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

            // imageAngle = direction * machineAngle + offset => machineDelta = direction * deltaImageAngle
            double imageCorrection = NormalizeDegrees(boardMatch.AngleDegrees - teaching.ImageAngleDegrees);
            double pickR = teaching.MachineR + (direction * imageCorrection);
            double offsetR = NormalizeDegrees(pickR - currentR);
            double targetR = currentR + offsetR;

            result.Add(new MappedTargetPose
            {
                Side = teaching.Side,
                X = detectedStage.X - rotCenterOffsetX + (armX * cos) - (armY * sin),
                Y = detectedStage.Y - rotCenterOffsetY + (armX * sin) + (armY * cos),
                ROffset = offsetR,
                TargetR = targetR,
                PickR = targetR,
                BlindPlaceR = teaching.MachineR
            });
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
        /// Direction of a template pose expressed as an angle in the stage frame.
        /// HALCON angles turn counter-clockwise on screen with rows pointing down,
        /// so the pose direction in (column, row) is (cos a, -sin a).
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
