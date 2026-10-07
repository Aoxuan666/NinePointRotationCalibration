using System;
using System.Collections.Generic;

namespace NinePointRotationCalibration
{
    internal static class RotationCalibrationSolver
    {
        public static bool Solve(CalibrationJob job, CalibrationResult result)
        {
            CalibrationSolverOptions options = job.SolverOptions;
            RotationCalibrationResult rotation = new RotationCalibrationResult();
            result.Rotation = rotation;
            List<RotationObservation> observations = CollectObservations(job, result.Diagnostics, options);
            rotation.CandidateCount = observations.Count;

            if (observations.Count < options.MinimumRotationSamples)
            {
                return Fail(rotation, CalibrationErrorCode.RotationInsufficient,
                    string.Format("Need at least {0} eligible rotation samples; got {1}.", options.MinimumRotationSamples, observations.Count));
            }

            RotationAngleFit same = FitAngleDirection(observations, 1, options);
            RotationAngleFit opposite = FitAngleDirection(observations, -1, options);
            RotationAngleFit angleFit = ChooseAngleFit(same, opposite, options.MinimumRotationInliers);
            if (angleFit == null)
            {
                return Fail(rotation, CalibrationErrorCode.RotationDegenerate,
                    "Rotation angle samples cannot be fitted to a +1 or -1 direction model.");
            }

            if (angleFit.Span < options.MinimumRotationSpanDegrees)
            {
                rotation.MachineAngleSpanDegrees = angleFit.Span;
                return Fail(rotation, CalibrationErrorCode.RotationInsufficient,
                    string.Format("Rotation span {0:0.###} degrees is below the configured minimum {1:0.###} degrees.",
                        angleFit.Span, options.MinimumRotationSpanDegrees));
            }

            StagePoint referenceStage = ComputeReferenceStage(observations, angleFit.Inliers);
            NormalizeForStageTranslation(observations, result.StageToPixelMatrix, referenceStage);

            bool[] centerInliers;
            double centerColumn;
            double centerRow;
            double localX;
            double localY;
            if (!FindRobustCenter(observations, angleFit, options, out centerInliers,
                out centerColumn, out centerRow, out localX, out localY))
            {
                return Fail(rotation, CalibrationErrorCode.RotationDegenerate,
                    "Rotation samples do not define a stable rotation center.");
            }

            bool[] combined = new bool[observations.Count];
            int combinedCount = 0;
            for (int i = 0; i < observations.Count; i++)
            {
                combined[i] = angleFit.Inliers[i] && centerInliers[i];
                if (combined[i]) combinedCount++;
            }

            if (combinedCount < options.MinimumRotationInliers)
            {
                return Fail(rotation, CalibrationErrorCode.RotationInsufficient,
                    string.Format("Only {0} rotation inliers remain after robust filtering.", combinedCount));
            }

            if (!FitRigidCenter(observations, combined, angleFit.Direction,
                out centerColumn, out centerRow, out localX, out localY))
            {
                return Fail(rotation, CalibrationErrorCode.RotationDegenerate,
                    "Final rotation inliers do not define a stable center.");
            }

            angleFit = RefitFixedDirection(observations, combined, angleFit.Direction);
            if (angleFit == null)
            {
                return Fail(rotation, CalibrationErrorCode.RotationDegenerate,
                    "Could not refit the final rotation angle relation.");
            }

            rotation.Direction = angleFit.Direction > 0 ? RotationDirection.Same : RotationDirection.Opposite;
            rotation.AngleOffsetDegrees = AngleMath.NormalizeDegrees(angleFit.Offset);
            rotation.UnconstrainedSlope = angleFit.Slope;
            rotation.AngleRmsDegrees = angleFit.Rms;
            rotation.MaxAngleResidualDegrees = angleFit.MaxResidual;
            rotation.MachineAngleSpanDegrees = angleFit.Span;
            rotation.RotationCenterImage = new ImagePoint(centerRow, centerColumn);
            Point2D centerStage = result.PixelToStageMatrix.Transform(new Point2D(centerColumn, centerRow));
            rotation.RotationCenterStage = new StagePoint(centerStage.X, centerStage.Y);
            rotation.ReferenceStage = referenceStage;
            rotation.RadiusPixels = Math.Sqrt((localX * localX) + (localY * localY));
            rotation.InlierCount = combinedCount;
            rotation.RejectedCount = observations.Count - combinedCount;

            List<double> centerResiduals = new List<double>();
            List<double> centerWeights = new List<double>();
            double maxCenterResidual = 0.0;
            for (int i = 0; i < observations.Count; i++)
            {
                RotationObservation observation = observations[i];
                double phi = AngleMath.DegreesToRadians(angleFit.Direction * observation.MachineAngle);
                double cos = Math.Cos(phi);
                double sin = Math.Sin(phi);
                double predictedNormalizedColumn = centerColumn + (cos * localX) - (sin * localY);
                double predictedNormalizedRow = centerRow + (sin * localX) + (cos * localY);
                Vector2D stageShift = result.StageToPixelMatrix.TransformVector(
                    new Vector2D(observation.StageX - referenceStage.X, observation.StageY - referenceStage.Y));
                double predictedColumn = predictedNormalizedColumn + stageShift.X;
                double predictedRow = predictedNormalizedRow + stageShift.Y;
                double dc = predictedColumn - observation.Column;
                double dr = predictedRow - observation.Row;
                double centerResidual = Math.Sqrt((dc * dc) + (dr * dr));
                double predictedAngle = AngleMath.NormalizeDegrees(
                    (angleFit.Direction * observation.MachineAngle) + angleFit.Offset);
                double angleResidual = AngleMath.NormalizeDegrees(observation.ImageAngle - predictedAngle);

                observation.AngleResidual = angleResidual;
                observation.CenterResidual = centerResidual;
                observation.AngleInlier = angleFit.Inliers[i];
                observation.CenterInlier = centerInliers[i];
                CalibrationSampleDiagnostic diagnostic = observation.Diagnostic;
                diagnostic.IsInlier = combined[i];
                diagnostic.PredictedImagePose = new ImagePose(predictedRow, predictedColumn, predictedAngle);
                Point2D predictedStage = result.PixelToStageMatrix.Transform(new Point2D(predictedColumn, predictedRow));
                diagnostic.PredictedStagePoint = new StagePoint(predictedStage.X, predictedStage.Y);
                diagnostic.ResidualColumnPixels = dc;
                diagnostic.ResidualRowPixels = dr;
                diagnostic.ResidualPixels = centerResidual;
                diagnostic.PredictedImageAngleDegrees = predictedAngle;
                diagnostic.AngleResidualDegrees = angleResidual;
                diagnostic.RotationCenterResidualPixels = centerResidual;
                if (!angleFit.Inliers[i]) diagnostic.RejectionReason = "Rotation angle outlier";
                else if (!centerInliers[i]) diagnostic.RejectionReason = "Rotation center outlier";
                else diagnostic.RejectionReason = null;

                if (combined[i])
                {
                    centerResiduals.Add(centerResidual);
                    centerWeights.Add(observation.Weight);
                    maxCenterResidual = Math.Max(maxCenterResidual, centerResidual);
                }
            }

            rotation.CenterRmsResidualPixels = SolverMath.WeightedRms(centerResiduals, centerWeights);
            rotation.MaxCenterResidualPixels = maxCenterResidual;
            if (rotation.RejectedCount > 0)
                result.Warnings.Add(rotation.RejectedCount + " rotation sample(s) were rejected as outliers.");

            if (rotation.AngleRmsDegrees > options.MaxAngleRmsDegrees
                || rotation.CenterRmsResidualPixels > options.MaxRotationCenterRmsPixels)
            {
                return Fail(rotation, CalibrationErrorCode.RotationResidualTooLarge,
                    string.Format("Rotation residual is too large (angle RMS {0:0.###} deg, center RMS {1:0.###} px).",
                        rotation.AngleRmsDegrees, rotation.CenterRmsResidualPixels));
            }

            rotation.IsAvailable = true;
            rotation.Success = true;
            rotation.ErrorCode = CalibrationErrorCode.None;
            rotation.Message = "Rotation calibration succeeded.";
            return true;
        }

        private static List<RotationObservation> CollectObservations(
            CalibrationJob job,
            IList<CalibrationSampleDiagnostic> diagnostics,
            CalibrationSolverOptions options)
        {
            List<RotationObservation> observations = new List<RotationObservation>();
            long revision = job.Template == null ? 0 : job.Template.Revision;
            if (job.Samples == null) return observations;
            for (int i = 0; i < job.Samples.Count; i++)
            {
                CalibrationSample sample = job.Samples[i];
                CalibrationSampleDiagnostic diagnostic = diagnostics[i];
                if (sample == null || sample.Kind != CalibrationSampleKind.Rotation) continue;
                if (!sample.Enabled)
                {
                    diagnostic.RejectionReason = "Sample is disabled";
                    continue;
                }
                if (sample.MachinePose == null || !sample.MachinePose.IsFinite || sample.ImagePose == null || !sample.ImagePose.IsFinite)
                {
                    diagnostic.RejectionReason = "Sample pose is invalid";
                    continue;
                }
                if (sample.TemplateRevision != revision)
                {
                    diagnostic.RejectionReason = "Template revision mismatch";
                    continue;
                }
                if (!Numeric.IsFinite(sample.MatchScore) || sample.MatchScore < options.MinimumMatchScore)
                {
                    diagnostic.RejectionReason = "Match score is below threshold";
                    continue;
                }

                diagnostic.IsEligible = true;
                observations.Add(new RotationObservation
                {
                    Sample = sample,
                    Diagnostic = diagnostic,
                    Index = i,
                    MachineAngle = sample.MachinePose.ThetaDegrees,
                    ImageAngle = sample.ImagePose.AngleDegrees,
                    Column = sample.ImagePose.Column,
                    Row = sample.ImagePose.Row,
                    StageX = sample.MachinePose.X,
                    StageY = sample.MachinePose.Y,
                    Weight = SolverMath.WeightFromScore(sample.MatchScore, options.UseMatchScoreWeights)
                });
            }

            return observations;
        }

        private static RotationAngleFit FitAngleDirection(
            IList<RotationObservation> observations,
            int direction,
            CalibrationSolverOptions options)
        {
            bool[] included = new bool[observations.Count];
            for (int i = 0; i < included.Length; i++) included[i] = true;

            for (int iteration = 0; iteration < 4; iteration++)
            {
                double offset = CircularOffset(observations, included, direction);
                if (!Numeric.IsFinite(offset)) return null;
                double[] residuals = new double[observations.Count];
                List<double> magnitudes = new List<double>();
                for (int i = 0; i < observations.Count; i++)
                {
                    residuals[i] = AngleMath.NormalizeDegrees(
                        observations[i].ImageAngle - ((direction * observations[i].MachineAngle) + offset));
                    if (included[i]) magnitudes.Add(Math.Abs(residuals[i]));
                }

                double threshold = SolverMath.MadThreshold(
                    magnitudes,
                    options.MadMultiplier,
                    options.AngleOutlierThresholdDegrees);
                bool[] refined = new bool[observations.Count];
                int count = 0;
                for (int i = 0; i < observations.Count; i++)
                {
                    refined[i] = Math.Abs(residuals[i]) <= threshold;
                    if (refined[i]) count++;
                }

                if (count < options.MinimumRotationInliers) return null;
                if (Equal(included, refined))
                {
                    included = refined;
                    break;
                }
                included = refined;
            }

            return RefitFixedDirection(observations, included, direction);
        }

        private static RotationAngleFit RefitFixedDirection(
            IList<RotationObservation> observations,
            bool[] included,
            int direction)
        {
            int count = Count(included);
            if (count < 2) return null;
            double offset = CircularOffset(observations, included, direction);
            if (!Numeric.IsFinite(offset)) return null;
            double[] residuals = new double[observations.Count];
            List<double> usedResiduals = new List<double>();
            List<double> weights = new List<double>();
            double max = 0.0;
            double reference = 0.0;
            bool hasReference = false;
            double minMachine = double.PositiveInfinity;
            double maxMachine = double.NegativeInfinity;
            double sumWeight = 0.0;
            double meanX = 0.0;
            double meanY = 0.0;

            for (int i = 0; i < observations.Count; i++)
            {
                residuals[i] = AngleMath.NormalizeDegrees(
                    observations[i].ImageAngle - ((direction * observations[i].MachineAngle) + offset));
                if (!included[i]) continue;
                if (!hasReference)
                {
                    reference = observations[i].MachineAngle;
                    hasReference = true;
                }
                double x = reference + AngleMath.NormalizeDegrees(observations[i].MachineAngle - reference);
                double y = (direction * x) + offset + residuals[i];
                double weight = observations[i].Weight;
                sumWeight += weight;
                meanX += weight * x;
                meanY += weight * y;
                minMachine = Math.Min(minMachine, x);
                maxMachine = Math.Max(maxMachine, x);
                usedResiduals.Add(residuals[i]);
                weights.Add(weight);
                max = Math.Max(max, Math.Abs(residuals[i]));
            }

            meanX /= sumWeight;
            meanY /= sumWeight;
            double numerator = 0.0;
            double denominator = 0.0;
            for (int i = 0; i < observations.Count; i++)
            {
                if (!included[i]) continue;
                double x = reference + AngleMath.NormalizeDegrees(observations[i].MachineAngle - reference);
                double y = (direction * x) + offset + residuals[i];
                numerator += observations[i].Weight * (x - meanX) * (y - meanY);
                denominator += observations[i].Weight * (x - meanX) * (x - meanX);
            }

            return new RotationAngleFit
            {
                Direction = direction,
                Offset = offset,
                Slope = denominator <= 1e-15 ? double.NaN : numerator / denominator,
                Span = maxMachine - minMachine,
                Rms = SolverMath.WeightedRms(usedResiduals, weights),
                MaxResidual = max,
                InlierCount = count,
                Inliers = (bool[])included.Clone(),
                Residuals = residuals
            };
        }

        private static double CircularOffset(IList<RotationObservation> observations, bool[] included, int direction)
        {
            List<double> offsets = new List<double>();
            List<double> weights = new List<double>();
            for (int i = 0; i < observations.Count; i++)
            {
                if (!included[i]) continue;
                offsets.Add(AngleMath.NormalizeDegrees(
                    observations[i].ImageAngle - (direction * observations[i].MachineAngle)));
                weights.Add(observations[i].Weight);
            }

            return offsets.Count == 0 ? double.NaN : AngleMath.CircularMeanDegrees(offsets, weights);
        }

        private static RotationAngleFit ChooseAngleFit(RotationAngleFit first, RotationAngleFit second, int minimumInliers)
        {
            bool firstValid = first != null && first.InlierCount >= minimumInliers;
            bool secondValid = second != null && second.InlierCount >= minimumInliers;
            if (!firstValid) return secondValid ? second : null;
            if (!secondValid) return first;
            if (first.InlierCount != second.InlierCount) return first.InlierCount > second.InlierCount ? first : second;
            return first.Rms <= second.Rms ? first : second;
        }

        private static StagePoint ComputeReferenceStage(IList<RotationObservation> observations, bool[] included)
        {
            double x = 0.0;
            double y = 0.0;
            double totalWeight = 0.0;
            for (int i = 0; i < observations.Count; i++)
            {
                if (!included[i]) continue;
                x += observations[i].Weight * observations[i].StageX;
                y += observations[i].Weight * observations[i].StageY;
                totalWeight += observations[i].Weight;
            }

            return new StagePoint(x / totalWeight, y / totalWeight);
        }

        private static void NormalizeForStageTranslation(
            IList<RotationObservation> observations,
            AffineMatrix2D stageToPixel,
            StagePoint referenceStage)
        {
            foreach (RotationObservation observation in observations)
            {
                Vector2D shift = stageToPixel.TransformVector(
                    new Vector2D(observation.StageX - referenceStage.X, observation.StageY - referenceStage.Y));
                observation.NormalizedColumn = observation.Column - shift.X;
                observation.NormalizedRow = observation.Row - shift.Y;
            }
        }

        private static bool FindRobustCenter(
            IList<RotationObservation> observations,
            RotationAngleFit angleFit,
            CalibrationSolverOptions options,
            out bool[] bestInliers,
            out double centerColumn,
            out double centerRow,
            out double localX,
            out double localY)
        {
            bestInliers = null;
            centerColumn = centerRow = localX = localY = 0.0;
            int bestCount = -1;
            double bestError = double.PositiveInfinity;

            for (int first = 0; first < observations.Count - 1; first++)
            {
                if (!angleFit.Inliers[first]) continue;
                for (int second = first + 1; second < observations.Count; second++)
                {
                    if (!angleFit.Inliers[second]) continue;
                    bool[] seed = new bool[observations.Count];
                    seed[first] = true;
                    seed[second] = true;
                    double cc;
                    double cr;
                    double lx;
                    double ly;
                    if (!FitRigidCenter(observations, seed, angleFit.Direction, out cc, out cr, out lx, out ly)) continue;

                    bool[] candidate = new bool[observations.Count];
                    int count = 0;
                    double error = 0.0;
                    for (int i = 0; i < observations.Count; i++)
                    {
                        if (!angleFit.Inliers[i]) continue;
                        double residual = CenterResidual(observations[i], angleFit.Direction, cc, cr, lx, ly);
                        if (residual <= options.CenterOutlierThresholdPixels)
                        {
                            candidate[i] = true;
                            count++;
                            error += observations[i].Weight * residual * residual;
                        }
                    }

                    if (count > bestCount || (count == bestCount && error < bestError))
                    {
                        bestCount = count;
                        bestError = error;
                        bestInliers = candidate;
                        centerColumn = cc;
                        centerRow = cr;
                        localX = lx;
                        localY = ly;
                    }
                }
            }

            if (bestInliers == null || bestCount < options.MinimumRotationInliers)
            {
                bestInliers = (bool[])angleFit.Inliers.Clone();
            }

            for (int iteration = 0; iteration < 3; iteration++)
            {
                if (!FitRigidCenter(observations, bestInliers, angleFit.Direction,
                    out centerColumn, out centerRow, out localX, out localY)) return false;
                List<double> residuals = new List<double>();
                for (int i = 0; i < observations.Count; i++)
                {
                    if (angleFit.Inliers[i])
                        residuals.Add(CenterResidual(observations[i], angleFit.Direction, centerColumn, centerRow, localX, localY));
                }

                double threshold = SolverMath.MadThreshold(
                    residuals,
                    options.MadMultiplier,
                    options.CenterOutlierThresholdPixels);
                bool[] refined = new bool[observations.Count];
                int count = 0;
                for (int i = 0; i < observations.Count; i++)
                {
                    refined[i] = angleFit.Inliers[i]
                        && CenterResidual(observations[i], angleFit.Direction, centerColumn, centerRow, localX, localY) <= threshold;
                    if (refined[i]) count++;
                }

                if (count < options.MinimumRotationInliers) return false;
                if (Equal(bestInliers, refined))
                {
                    bestInliers = refined;
                    break;
                }
                bestInliers = refined;
            }

            return FitRigidCenter(observations, bestInliers, angleFit.Direction,
                out centerColumn, out centerRow, out localX, out localY);
        }

        private static bool FitRigidCenter(
            IList<RotationObservation> observations,
            bool[] included,
            int direction,
            out double centerColumn,
            out double centerRow,
            out double localX,
            out double localY)
        {
            centerColumn = centerRow = localX = localY = 0.0;
            double[,] normal = new double[4, 4];
            double[] right = new double[4];
            int count = 0;
            for (int i = 0; i < observations.Count; i++)
            {
                if (!included[i]) continue;
                RotationObservation observation = observations[i];
                double phi = AngleMath.DegreesToRadians(direction * observation.MachineAngle);
                double cos = Math.Cos(phi);
                double sin = Math.Sin(phi);
                AddNormalEquation(normal, right, new[] { 1.0, 0.0, cos, -sin }, observation.NormalizedColumn, observation.Weight);
                AddNormalEquation(normal, right, new[] { 0.0, 1.0, sin, cos }, observation.NormalizedRow, observation.Weight);
                count++;
            }

            if (count < 2) return false;
            double[] solution;
            if (!SolverMath.SolveLinear(normal, right, out solution)) return false;
            centerColumn = solution[0];
            centerRow = solution[1];
            localX = solution[2];
            localY = solution[3];
            return Numeric.IsFinite(centerColumn) && Numeric.IsFinite(centerRow)
                && Numeric.IsFinite(localX) && Numeric.IsFinite(localY);
        }

        private static void AddNormalEquation(double[,] normal, double[] right, double[] coefficients, double value, double weight)
        {
            for (int row = 0; row < coefficients.Length; row++)
            {
                right[row] += weight * coefficients[row] * value;
                for (int column = 0; column < coefficients.Length; column++)
                    normal[row, column] += weight * coefficients[row] * coefficients[column];
            }
        }

        private static double CenterResidual(
            RotationObservation observation,
            int direction,
            double centerColumn,
            double centerRow,
            double localX,
            double localY)
        {
            double phi = AngleMath.DegreesToRadians(direction * observation.MachineAngle);
            double cos = Math.Cos(phi);
            double sin = Math.Sin(phi);
            double predictedColumn = centerColumn + (cos * localX) - (sin * localY);
            double predictedRow = centerRow + (sin * localX) + (cos * localY);
            double dc = predictedColumn - observation.NormalizedColumn;
            double dr = predictedRow - observation.NormalizedRow;
            return Math.Sqrt((dc * dc) + (dr * dr));
        }

        private static int Count(bool[] values)
        {
            int count = 0;
            if (values != null) for (int i = 0; i < values.Length; i++) if (values[i]) count++;
            return count;
        }

        private static bool Equal(bool[] left, bool[] right)
        {
            if (left == null || right == null || left.Length != right.Length) return false;
            for (int i = 0; i < left.Length; i++) if (left[i] != right[i]) return false;
            return true;
        }

        private static bool Fail(RotationCalibrationResult result, CalibrationErrorCode code, string message)
        {
            result.Success = false;
            result.ErrorCode = code;
            result.Message = message;
            return false;
        }
    }
}
