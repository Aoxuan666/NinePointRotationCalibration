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

            ImagePoint taughtAnchor = new ImagePoint(teaching.ImageRow, teaching.ImageColumn);
            StagePoint detectedStage = calibration.TransformImageToStage(boardMatch.Anchor);
            StagePoint taughtImageStage = calibration.TransformImageToStage(taughtAnchor);

            // The taught target is rigidly attached to the board: keep its lever arm
            // from the board anchor (in stage units) and rotate it by the board's
            // rotation since teaching. The rotation is measured in the stage frame by
            // mapping the template direction through the affine, so mirrored setups
            // and the downward image row axis are handled without sign guessing.
            double boardRotation = NormalizeDegrees(
                StageAngleDegrees(calibration, boardMatch.Anchor, boardMatch.AngleDegrees) -
                StageAngleDegrees(calibration, taughtAnchor, teaching.ImageAngleDegrees));
            double radians = boardRotation * Math.PI / 180.0;
            double cos = Math.Cos(radians);
            double sin = Math.Sin(radians);
            double armX = teaching.MachineX - taughtImageStage.X;
            double armY = teaching.MachineY - taughtImageStage.Y;

            double direction = calibration.Rotation.DirectionMultiplier;

            // imageAngle = direction * machineAngle + offset, so the inverse delta is
            // machineDelta = direction * (detectedImageAngle - taughtImageAngle).
            double imageCorrection = NormalizeDegrees(boardMatch.AngleDegrees - teaching.ImageAngleDegrees);
            double targetR = teaching.MachineR + (direction * imageCorrection);
            double offsetR = NormalizeDegrees(targetR - currentR);

            result.Add(new MappedTargetPose
            {
                Side = teaching.Side,
                X = detectedStage.X + (armX * cos) - (armY * sin),
                Y = detectedStage.Y + (armX * sin) + (armY * cos),
                ROffset = offsetR,
                TargetR = currentR + offsetR
            });
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
