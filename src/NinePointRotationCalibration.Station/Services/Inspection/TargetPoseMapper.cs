using System;
using System.Collections.Generic;
using NinePointRotationCalibration.Station.Models;

namespace NinePointRotationCalibration.Station.Services.Inspection
{
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

            StagePoint detectedStage = calibration.TransformImageToStage(boardMatch.Anchor);
            StagePoint taughtImageStage = calibration.TransformImageToStage(
                new ImagePoint(teaching.ImageRow, teaching.ImageColumn));

            double direction = calibration.Rotation.DirectionMultiplier;

            // imageAngle = direction * machineAngle + offset, so the inverse delta is
            // machineDelta = direction * (detectedImageAngle - taughtImageAngle).
            double imageCorrection = NormalizeDegrees(boardMatch.AngleDegrees - teaching.ImageAngleDegrees);
            double targetR = teaching.MachineR + (direction * imageCorrection);
            double offsetR = NormalizeDegrees(targetR - currentR);

            result.Add(new MappedTargetPose
            {
                Side = teaching.Side,
                X = teaching.MachineX + detectedStage.X - taughtImageStage.X,
                Y = teaching.MachineY + detectedStage.Y - taughtImageStage.Y,
                ROffset = offsetR,
                TargetR = currentR + offsetR
            });
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
