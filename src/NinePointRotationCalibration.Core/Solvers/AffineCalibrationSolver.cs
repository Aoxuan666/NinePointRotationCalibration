using System;
using System.Collections.Generic;

namespace NinePointRotationCalibration
{
    internal static class AffineCalibrationSolver
    {
        public static bool Solve(CalibrationJob job, CalibrationResult result)
        {
            CalibrationSolverOptions options = job.SolverOptions;
            List<AffineObservation> observations = CollectObservations(job, result.Diagnostics, options);
            result.CandidateCount = observations.Count;

            if (observations.Count < options.MinimumTranslationSamples)
            {
                return Fail(result, CalibrationErrorCode.InsufficientSamples,
                    string.Format("Need at least {0} eligible translation samples; got {1}.", options.MinimumTranslationSamples, observations.Count));
            }

            double minX = double.PositiveInfinity;
            double maxX = double.NegativeInfinity;
            double minY = double.PositiveInfinity;
            double maxY = double.NegativeInfinity;
            foreach (AffineObservation observation in observations)
            {
                minX = Math.Min(minX, observation.X);
                maxX = Math.Max(maxX, observation.X);
                minY = Math.Min(minY, observation.Y);
                maxY = Math.Max(maxY, observation.Y);
            }

            result.XTravel = maxX - minX;
            result.YTravel = maxY - minY;
            if (result.XTravel < options.MinimumAxisTravel || result.YTravel < options.MinimumAxisTravel)
            {
                return Fail(result, CalibrationErrorCode.DegenerateGeometry,
                    "Translation samples do not cover the configured minimum travel on both axes.");
            }

            bool[] inliers;
            AffineMatrix2D matrix;
            if (!FindInitialModel(observations, options, out matrix, out inliers))
            {
                return Fail(result, CalibrationErrorCode.DegenerateGeometry,
                    "Translation samples are collinear, duplicated, or do not form a stable affine model.");
            }

            for (int iteration = 0; iteration < 3; iteration++)
            {
                if (!FitAffine(observations, inliers, out matrix))
                {
                    return Fail(result, CalibrationErrorCode.DegenerateGeometry,
                        "Could not refit an affine model from the selected inliers.");
                }

                List<double> residuals = ComputeResiduals(observations, matrix);
                double threshold = SolverMath.MadThreshold(
                    residuals,
                    options.MadMultiplier,
                    options.RansacInlierThresholdPixels);
                bool[] refined = new bool[observations.Count];
                int refinedCount = 0;
                for (int i = 0; i < observations.Count; i++)
                {
                    refined[i] = residuals[i] <= threshold;
                    if (refined[i]) refinedCount++;
                }

                if (refinedCount < options.MinimumTranslationInliers)
                    break;
                if (MasksEqual(inliers, refined))
                    break;
                inliers = refined;
            }

            int inlierCount = Count(inliers);
            if (inlierCount < options.MinimumTranslationInliers)
            {
                return Fail(result, CalibrationErrorCode.InsufficientSamples,
                    string.Format("Only {0} translation inliers remain after robust filtering.", inlierCount));
            }

            if (!FitAffine(observations, inliers, out matrix))
            {
                return Fail(result, CalibrationErrorCode.DegenerateGeometry,
                    "Final affine inliers do not define an invertible model.");
            }

            AffineMatrix2D inverse;
            if (!matrix.TryInvert(out inverse))
            {
                return Fail(result, CalibrationErrorCode.DegenerateGeometry,
                    "The fitted stage-to-pixel matrix is singular.");
            }

            result.StageToPixelMatrix = matrix;
            result.PixelToStageMatrix = inverse;
            result.Determinant = matrix.Determinant;
            result.ConditionNumber = matrix.ConditionNumber;
            result.IsMirrored = matrix.Determinant < 0.0;
            result.InlierCount = inlierCount;
            result.RejectedCount = observations.Count - inlierCount;

            Vector2D xAxis = new Vector2D(matrix.M11, matrix.M21);
            Vector2D yAxis = new Vector2D(matrix.M12, matrix.M22);
            result.StageXAxisInImage = xAxis;
            result.StageYAxisInImage = yAxis;
            result.ResolutionX = xAxis.Length <= 1e-15 ? double.PositiveInfinity : 1.0 / xAxis.Length;
            result.ResolutionY = yAxis.Length <= 1e-15 ? double.PositiveInfinity : 1.0 / yAxis.Length;
            double cosine = ((xAxis.X * yAxis.X) + (xAxis.Y * yAxis.Y)) / (xAxis.Length * yAxis.Length);
            result.AxisAngleDegrees = AngleMath.RadiansToDegrees(Math.Acos(Numeric.Clamp(cosine, -1.0, 1.0)));

            List<double> pixelResiduals = new List<double>();
            List<double> stageResiduals = new List<double>();
            List<double> weights = new List<double>();
            double maxPixel = 0.0;
            double maxStage = 0.0;
            for (int i = 0; i < observations.Count; i++)
            {
                AffineObservation observation = observations[i];
                Point2D predictedPixel = matrix.Transform(new Point2D(observation.X, observation.Y));
                double dc = predictedPixel.X - observation.Column;
                double dr = predictedPixel.Y - observation.Row;
                double pixelResidual = Math.Sqrt((dc * dc) + (dr * dr));
                Point2D predictedStage = inverse.Transform(new Point2D(observation.Column, observation.Row));
                double dsx = predictedStage.X - observation.X;
                double dsy = predictedStage.Y - observation.Y;
                double stageResidual = Math.Sqrt((dsx * dsx) + (dsy * dsy));

                observation.IsInlier = inliers[i];
                observation.Residual = pixelResidual;
                CalibrationSampleDiagnostic diagnostic = observation.Diagnostic;
                diagnostic.IsInlier = inliers[i];
                diagnostic.PredictedImagePose = new ImagePose(predictedPixel.Y, predictedPixel.X, observation.Sample.ImagePose.AngleDegrees);
                diagnostic.PredictedStagePoint = new StagePoint(predictedStage.X, predictedStage.Y);
                diagnostic.ResidualColumnPixels = dc;
                diagnostic.ResidualRowPixels = dr;
                diagnostic.ResidualPixels = pixelResidual;
                diagnostic.ResidualStageX = dsx;
                diagnostic.ResidualStageY = dsy;
                diagnostic.ResidualStageDistance = stageResidual;
                diagnostic.RejectionReason = inliers[i] ? null : "Affine residual outlier";

                if (inliers[i])
                {
                    pixelResiduals.Add(pixelResidual);
                    stageResiduals.Add(stageResidual);
                    weights.Add(observation.Weight);
                    maxPixel = Math.Max(maxPixel, pixelResidual);
                    maxStage = Math.Max(maxStage, stageResidual);
                }
            }

            result.RmsResidualPixels = SolverMath.WeightedRms(pixelResiduals, weights);
            result.MaxResidualPixels = maxPixel;
            result.RmsResidualStage = SolverMath.WeightedRms(stageResiduals, weights);
            result.MaxResidualStage = maxStage;

            if (result.RejectedCount > 0)
                result.Warnings.Add(result.RejectedCount + " translation sample(s) were rejected as outliers.");
            if (result.IsMirrored)
                result.Warnings.Add("The fitted coordinate mapping is mirrored (negative determinant). Verify axis directions.");

            if (!Numeric.IsFinite(result.ConditionNumber) || result.ConditionNumber > options.MaxConditionNumber)
            {
                return Fail(result, CalibrationErrorCode.DegenerateGeometry,
                    string.Format("Affine condition number {0:G6} exceeds limit {1:G6}.", result.ConditionNumber, options.MaxConditionNumber));
            }

            if (result.RmsResidualPixels > options.MaxRmsResidualPixels || result.MaxResidualPixels > options.MaxResidualPixels)
            {
                return Fail(result, CalibrationErrorCode.ResidualTooLarge,
                    string.Format("Affine residual is too large (RMS {0:0.###} px, max {1:0.###} px).",
                        result.RmsResidualPixels, result.MaxResidualPixels));
            }

            result.TranslationSuccess = true;
            return true;
        }

        private static List<AffineObservation> CollectObservations(
            CalibrationJob job,
            IList<CalibrationSampleDiagnostic> diagnostics,
            CalibrationSolverOptions options)
        {
            List<AffineObservation> observations = new List<AffineObservation>();
            long revision = job.Template == null ? 0 : job.Template.Revision;
            if (job.Samples == null) return observations;

            for (int i = 0; i < job.Samples.Count; i++)
            {
                CalibrationSample sample = job.Samples[i];
                CalibrationSampleDiagnostic diagnostic = diagnostics[i];
                if (sample == null || sample.Kind != CalibrationSampleKind.Translation) continue;
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
                observations.Add(new AffineObservation
                {
                    Sample = sample,
                    Diagnostic = diagnostic,
                    Index = i,
                    X = sample.MachinePose.X,
                    Y = sample.MachinePose.Y,
                    Column = sample.ImagePose.Column,
                    Row = sample.ImagePose.Row,
                    Weight = SolverMath.WeightFromScore(sample.MatchScore, options.UseMatchScoreWeights)
                });
            }

            return observations;
        }

        private static bool FindInitialModel(
            IList<AffineObservation> observations,
            CalibrationSolverOptions options,
            out AffineMatrix2D bestMatrix,
            out bool[] bestInliers)
        {
            bestMatrix = default(AffineMatrix2D);
            bestInliers = null;
            if (!options.EnableRansac)
            {
                bestInliers = AllTrue(observations.Count);
                return FitAffine(observations, bestInliers, out bestMatrix);
            }

            int bestCount = -1;
            double bestSupport = double.NegativeInfinity;
            double bestError = double.PositiveInfinity;
            long combinations = ((long)observations.Count * (observations.Count - 1) * (observations.Count - 2)) / 6;
            int limit = Math.Max(1, options.RansacIterations);

            if (combinations <= limit)
            {
                int tested = 0;
                for (int i = 0; i < observations.Count - 2 && tested < limit; i++)
                {
                    for (int j = i + 1; j < observations.Count - 1 && tested < limit; j++)
                    {
                        for (int k = j + 1; k < observations.Count && tested < limit; k++)
                        {
                            EvaluateCandidate(observations, options, i, j, k,
                                ref bestMatrix, ref bestInliers, ref bestCount, ref bestSupport, ref bestError);
                            tested++;
                        }
                    }
                }
            }
            else
            {
                Random random = new Random(options.RandomSeed);
                HashSet<string> tested = new HashSet<string>();
                int attempts = 0;
                while (tested.Count < limit && attempts < limit * 20)
                {
                    attempts++;
                    int a = random.Next(observations.Count);
                    int b = random.Next(observations.Count);
                    int c = random.Next(observations.Count);
                    if (a == b || a == c || b == c) continue;
                    int i = Math.Min(a, Math.Min(b, c));
                    int k = Math.Max(a, Math.Max(b, c));
                    int j = a + b + c - i - k;
                    string key = i + ":" + j + ":" + k;
                    if (!tested.Add(key)) continue;
                    EvaluateCandidate(observations, options, i, j, k,
                        ref bestMatrix, ref bestInliers, ref bestCount, ref bestSupport, ref bestError);
                }
            }

            return bestInliers != null && bestCount >= options.MinimumTranslationInliers;
        }

        private static void EvaluateCandidate(
            IList<AffineObservation> observations,
            CalibrationSolverOptions options,
            int first,
            int second,
            int third,
            ref AffineMatrix2D bestMatrix,
            ref bool[] bestInliers,
            ref int bestCount,
            ref double bestSupport,
            ref double bestError)
        {
            bool[] seed = new bool[observations.Count];
            seed[first] = true;
            seed[second] = true;
            seed[third] = true;
            AffineMatrix2D candidate;
            if (!FitAffine(observations, seed, out candidate)) return;

            bool[] inliers = new bool[observations.Count];
            int count = 0;
            double support = 0.0;
            double error = 0.0;
            for (int i = 0; i < observations.Count; i++)
            {
                double residual = Residual(observations[i], candidate);
                if (residual <= options.RansacInlierThresholdPixels)
                {
                    inliers[i] = true;
                    count++;
                    support += observations[i].Weight;
                    error += observations[i].Weight * residual * residual;
                }
            }

            bool better = count > bestCount
                || (count == bestCount && support > bestSupport + 1e-12)
                || (count == bestCount && Math.Abs(support - bestSupport) <= 1e-12 && error < bestError);
            if (!better) return;
            bestCount = count;
            bestSupport = support;
            bestError = error;
            bestMatrix = candidate;
            bestInliers = inliers;
        }

        private static bool FitAffine(IList<AffineObservation> observations, bool[] included, out AffineMatrix2D matrix)
        {
            matrix = default(AffineMatrix2D);
            int count = 0;
            double totalWeight = 0.0;
            double meanX = 0.0;
            double meanY = 0.0;
            double meanColumn = 0.0;
            double meanRow = 0.0;
            for (int i = 0; i < observations.Count; i++)
            {
                if (included != null && !included[i]) continue;
                AffineObservation observation = observations[i];
                double weight = observation.Weight;
                count++;
                totalWeight += weight;
                meanX += weight * observation.X;
                meanY += weight * observation.Y;
                meanColumn += weight * observation.Column;
                meanRow += weight * observation.Row;
            }

            if (count < 3 || totalWeight <= 0.0) return false;
            meanX /= totalWeight;
            meanY /= totalWeight;
            meanColumn /= totalWeight;
            meanRow /= totalWeight;

            double sxx = 0.0;
            double sxy = 0.0;
            double syy = 0.0;
            double txColumn = 0.0;
            double tyColumn = 0.0;
            double txRow = 0.0;
            double tyRow = 0.0;
            for (int i = 0; i < observations.Count; i++)
            {
                if (included != null && !included[i]) continue;
                AffineObservation observation = observations[i];
                double dx = observation.X - meanX;
                double dy = observation.Y - meanY;
                double dc = observation.Column - meanColumn;
                double dr = observation.Row - meanRow;
                double weight = observation.Weight;
                sxx += weight * dx * dx;
                sxy += weight * dx * dy;
                syy += weight * dy * dy;
                txColumn += weight * dx * dc;
                tyColumn += weight * dy * dc;
                txRow += weight * dx * dr;
                tyRow += weight * dy * dr;
            }

            double determinant = (sxx * syy) - (sxy * sxy);
            double covarianceScale = Math.Max(1.0, sxx + syy);
            if (Math.Abs(determinant) <= covarianceScale * covarianceScale * 1e-12) return false;

            double m11 = ((txColumn * syy) - (tyColumn * sxy)) / determinant;
            double m12 = ((tyColumn * sxx) - (txColumn * sxy)) / determinant;
            double m21 = ((txRow * syy) - (tyRow * sxy)) / determinant;
            double m22 = ((tyRow * sxx) - (txRow * sxy)) / determinant;
            matrix = new AffineMatrix2D(
                m11,
                m12,
                meanColumn - (m11 * meanX) - (m12 * meanY),
                m21,
                m22,
                meanRow - (m21 * meanX) - (m22 * meanY));
            return matrix.IsFinite;
        }

        private static List<double> ComputeResiduals(IList<AffineObservation> observations, AffineMatrix2D matrix)
        {
            List<double> residuals = new List<double>(observations.Count);
            foreach (AffineObservation observation in observations) residuals.Add(Residual(observation, matrix));
            return residuals;
        }

        private static double Residual(AffineObservation observation, AffineMatrix2D matrix)
        {
            Point2D predicted = matrix.Transform(new Point2D(observation.X, observation.Y));
            double dc = predicted.X - observation.Column;
            double dr = predicted.Y - observation.Row;
            return Math.Sqrt((dc * dc) + (dr * dr));
        }

        private static bool[] AllTrue(int count)
        {
            bool[] values = new bool[count];
            for (int i = 0; i < count; i++) values[i] = true;
            return values;
        }

        private static int Count(bool[] values)
        {
            int count = 0;
            if (values != null)
            {
                for (int i = 0; i < values.Length; i++) if (values[i]) count++;
            }

            return count;
        }

        private static bool MasksEqual(bool[] left, bool[] right)
        {
            if (left == null || right == null || left.Length != right.Length) return false;
            for (int i = 0; i < left.Length; i++) if (left[i] != right[i]) return false;
            return true;
        }

        private static bool Fail(CalibrationResult result, CalibrationErrorCode code, string message)
        {
            result.Success = false;
            result.TranslationSuccess = false;
            result.ErrorCode = code;
            result.Message = message;
            return false;
        }
    }
}
