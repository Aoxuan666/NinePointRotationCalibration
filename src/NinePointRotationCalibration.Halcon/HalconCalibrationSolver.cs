using System;
using System.Collections.Generic;

namespace NinePointRotationCalibration.Halcon
{
    /// <summary>
    /// Production calibration solver. HALCON performs every affine fit,
    /// inverse, coordinate transform and linear-system solve.
    /// </summary>
    public static class HalconCalibrationSolver
    {
        public static CalibrationResult Solve(CalibrationJob job)
        {
            if (job == null)
                return CalibrationResult.Failure(CalibrationErrorCode.ParameterInvalid, "Calibration job is null.");
            CalibrationResult result = new CalibrationResult();
            InitializeDiagnostics(job, result);
            string error;
            if (!Validate(job, out error)) return Fail(result, CalibrationErrorCode.ParameterInvalid, error);
            try
            {
                if (!SolveTranslation(job, result)) return result;
                bool rotationOk = SolveRotation(job, result);
                if (!rotationOk && job.SolverOptions.RequireRotationCalibration)
                    return Fail(result, result.Rotation.ErrorCode, result.Rotation.Message);
                if (!rotationOk)
                {
                    if (HasRotationSample(job))
                        result.Warnings.Add("Rotation calibration was not accepted: " + result.Rotation.Message);
                    result.Success = true;
                    result.ErrorCode = CalibrationErrorCode.None;
                    result.Message = "Translation calibration succeeded; rotation calibration is optional and unavailable.";
                    return result;
                }
                result.Success = true;
                result.ErrorCode = CalibrationErrorCode.None;
                result.Message = "Nine-point and rotation calibration succeeded with HALCON.";
                return result;
            }
            catch (Exception exception)
            {
                return Fail(result, CalibrationErrorCode.InternalError,
                    "HALCON calibration failed: " + exception.Message);
            }
        }

        private static bool SolveTranslation(CalibrationJob job, CalibrationResult result)
        {
            CalibrationSolverOptions options = job.SolverOptions;
            List<TranslationItem> items = TranslationItems(job, result.Diagnostics, options);
            result.CandidateCount = items.Count;
            if (items.Count < options.MinimumTranslationSamples)
                return FailTranslation(result, CalibrationErrorCode.InsufficientSamples,
                    string.Format("Need at least {0} eligible translation samples; got {1}.",
                        options.MinimumTranslationSamples, items.Count));

            double minX = double.PositiveInfinity, maxX = double.NegativeInfinity;
            double minY = double.PositiveInfinity, maxY = double.NegativeInfinity;
            foreach (TranslationItem item in items)
            {
                minX = Math.Min(minX, item.X); maxX = Math.Max(maxX, item.X);
                minY = Math.Min(minY, item.Y); maxY = Math.Max(maxY, item.Y);
            }
            result.XTravel = maxX - minX;
            result.YTravel = maxY - minY;
            if (result.XTravel < options.MinimumAxisTravel || result.YTravel < options.MinimumAxisTravel)
                return FailTranslation(result, CalibrationErrorCode.DegenerateGeometry,
                    "Translation samples do not cover the configured minimum travel on both axes.");

            bool[] inliers;
            AffineMatrix2D matrix;
            if (!InitialAffine(items, options, out matrix, out inliers))
                return FailTranslation(result, CalibrationErrorCode.DegenerateGeometry,
                    "HALCON could not determine a stable affine model.");
            for (int iteration = 0; iteration < 3; iteration++)
            {
                if (!FitAffine(items, inliers, out matrix))
                    return FailTranslation(result, CalibrationErrorCode.DegenerateGeometry,
                        "HALCON could not refit the affine model.");
                List<double> residuals = AffineResiduals(items, matrix);
                double threshold = MadThreshold(residuals, options.MadMultiplier,
                    options.RansacInlierThresholdPixels);
                bool[] refined = new bool[items.Count];
                int count = 0;
                for (int i = 0; i < items.Count; i++)
                { refined[i] = residuals[i] <= threshold; if (refined[i]) count++; }
                if (count < options.MinimumTranslationInliers || Equal(inliers, refined)) break;
                inliers = refined;
            }
            int inlierCount = Count(inliers);
            if (inlierCount < options.MinimumTranslationInliers || !FitAffine(items, inliers, out matrix))
                return FailTranslation(result, CalibrationErrorCode.InsufficientSamples,
                    "Too few stable translation inliers remain.");
            AffineMatrix2D inverse;
            if (!HalconGeometry.TryInvert(matrix, out inverse))
                return FailTranslation(result, CalibrationErrorCode.DegenerateGeometry,
                    "HALCON reported a singular stage-to-pixel transform.");

            result.StageToPixelMatrix = matrix;
            result.PixelToStageMatrix = inverse;
            result.Determinant = matrix.Determinant;
            result.ConditionNumber = matrix.ConditionNumber;
            result.IsMirrored = matrix.Determinant < 0.0;
            result.InlierCount = inlierCount;
            result.RejectedCount = items.Count - inlierCount;
            Vector2D xAxis = HalconGeometry.TransformVector(matrix, new Vector2D(1.0, 0.0));
            Vector2D yAxis = HalconGeometry.TransformVector(matrix, new Vector2D(0.0, 1.0));
            result.StageXAxisInImage = xAxis;
            result.StageYAxisInImage = yAxis;
            result.ResolutionX = xAxis.Length <= 1e-15 ? double.PositiveInfinity : 1.0 / xAxis.Length;
            result.ResolutionY = yAxis.Length <= 1e-15 ? double.PositiveInfinity : 1.0 / yAxis.Length;
            double axisDenominator = xAxis.Length * yAxis.Length;
            double axisCosine = axisDenominator <= 1e-15 ? double.NaN :
                ((xAxis.X * yAxis.X) + (xAxis.Y * yAxis.Y)) / axisDenominator;
            result.AxisAngleDegrees = AngleMath.RadiansToDegrees(
                Math.Acos(Math.Max(-1.0, Math.Min(1.0, axisCosine))));

            List<double> pixelResiduals = new List<double>();
            List<double> stageResiduals = new List<double>();
            List<double> weights = new List<double>();
            double maxPixel = 0.0, maxStage = 0.0;
            for (int i = 0; i < items.Count; i++)
            {
                TranslationItem item = items[i];
                Point2D pixel = HalconGeometry.TransformPoint(matrix, new Point2D(item.X, item.Y));
                Point2D stage = HalconGeometry.TransformPoint(inverse, new Point2D(item.Column, item.Row));
                double dc = pixel.X - item.Column, dr = pixel.Y - item.Row;
                double dsx = stage.X - item.X, dsy = stage.Y - item.Y;
                double pixelResidual = Math.Sqrt((dc * dc) + (dr * dr));
                double stageResidual = Math.Sqrt((dsx * dsx) + (dsy * dsy));
                CalibrationSampleDiagnostic diagnostic = item.Diagnostic;
                diagnostic.IsInlier = inliers[i];
                diagnostic.PredictedImagePose = new ImagePose(pixel.Y, pixel.X, item.Sample.ImagePose.AngleDegrees);
                diagnostic.PredictedStagePoint = new StagePoint(stage.X, stage.Y);
                diagnostic.ResidualColumnPixels = dc; diagnostic.ResidualRowPixels = dr;
                diagnostic.ResidualPixels = pixelResidual;
                diagnostic.ResidualStageX = dsx; diagnostic.ResidualStageY = dsy;
                diagnostic.ResidualStageDistance = stageResidual;
                diagnostic.RejectionReason = inliers[i] ? null : "Affine residual outlier";
                if (!inliers[i]) continue;
                pixelResiduals.Add(pixelResidual); stageResiduals.Add(stageResidual); weights.Add(item.Weight);
                maxPixel = Math.Max(maxPixel, pixelResidual); maxStage = Math.Max(maxStage, stageResidual);
            }
            result.RmsResidualPixels = WeightedRms(pixelResiduals, weights);
            result.RmsResidualStage = WeightedRms(stageResiduals, weights);
            result.MaxResidualPixels = maxPixel; result.MaxResidualStage = maxStage;
            if (result.RejectedCount > 0)
                result.Warnings.Add(result.RejectedCount + " translation sample(s) were rejected as outliers.");
            if (result.IsMirrored)
                result.Warnings.Add("The fitted coordinate mapping is mirrored (negative determinant). Verify axis directions.");
            if (!Finite(result.ConditionNumber) || result.ConditionNumber > options.MaxConditionNumber)
                return FailTranslation(result, CalibrationErrorCode.DegenerateGeometry,
                    "HALCON affine condition number exceeds the configured limit.");
            if (result.RmsResidualPixels > options.MaxRmsResidualPixels ||
                result.MaxResidualPixels > options.MaxResidualPixels)
                return FailTranslation(result, CalibrationErrorCode.ResidualTooLarge,
                    string.Format("HALCON affine residual is too large (RMS {0:0.###} px, max {1:0.###} px).",
                        result.RmsResidualPixels, result.MaxResidualPixels));
            result.TranslationSuccess = true;
            return true;
        }

        private static bool SolveRotation(CalibrationJob job, CalibrationResult result)
        {
            CalibrationSolverOptions options = job.SolverOptions;
            RotationCalibrationResult rotation = new RotationCalibrationResult();
            result.Rotation = rotation;
            List<RotationItem> items = RotationItems(job, result.Diagnostics, options);
            rotation.CandidateCount = items.Count;
            if (items.Count < options.MinimumRotationSamples)
                return FailRotation(rotation, CalibrationErrorCode.RotationInsufficient,
                    string.Format("Need at least {0} eligible rotation samples; got {1}.",
                        options.MinimumRotationSamples, items.Count));
            AngleFit fit = ChooseAngleFit(FitAngle(items, 1, options),
                FitAngle(items, -1, options), options.MinimumRotationInliers);
            if (fit == null)
                return FailRotation(rotation, CalibrationErrorCode.RotationDegenerate,
                    "Rotation angles cannot be fitted to a +1 or -1 direction model.");
            if (fit.Span < options.MinimumRotationSpanDegrees)
            {
                rotation.MachineAngleSpanDegrees = fit.Span;
                return FailRotation(rotation, CalibrationErrorCode.RotationInsufficient,
                    "Rotation span is below the configured minimum.");
            }

            StagePoint reference = WeightedStageCenter(items, fit.Inliers);
            foreach (RotationItem item in items)
            {
                Vector2D shift = HalconGeometry.TransformVector(result.StageToPixelMatrix,
                    new Vector2D(item.StageX - reference.X, item.StageY - reference.Y));
                item.NormalizedColumn = item.Column - shift.X;
                item.NormalizedRow = item.Row - shift.Y;
            }
            bool[] centerInliers = (bool[])fit.Inliers.Clone();
            double centerColumn = 0.0, centerRow = 0.0, localX = 0.0, localY = 0.0;
            for (int iteration = 0; iteration < 4; iteration++)
            {
                if (!FitCenter(items, centerInliers, fit.Direction,
                    out centerColumn, out centerRow, out localX, out localY))
                    return FailRotation(rotation, CalibrationErrorCode.RotationDegenerate,
                        "HALCON could not determine a stable rotation center.");
                List<double> residuals = new List<double>();
                for (int i = 0; i < items.Count; i++)
                    if (fit.Inliers[i]) residuals.Add(CenterResidual(items[i], fit.Direction,
                        centerColumn, centerRow, localX, localY));
                double threshold = MadThreshold(residuals, options.MadMultiplier,
                    options.CenterOutlierThresholdPixels);
                bool[] refined = new bool[items.Count];
                int count = 0;
                for (int i = 0; i < items.Count; i++)
                {
                    refined[i] = fit.Inliers[i] && CenterResidual(items[i], fit.Direction,
                        centerColumn, centerRow, localX, localY) <= threshold;
                    if (refined[i]) count++;
                }
                if (count < options.MinimumRotationInliers)
                    return FailRotation(rotation, CalibrationErrorCode.RotationInsufficient,
                        "Too few rotation-center inliers remain.");
                if (Equal(centerInliers, refined)) { centerInliers = refined; break; }
                centerInliers = refined;
            }
            if (!FitCenter(items, centerInliers, fit.Direction,
                out centerColumn, out centerRow, out localX, out localY))
                return FailRotation(rotation, CalibrationErrorCode.RotationDegenerate,
                    "HALCON could not fit the final rotation center.");
            fit = RefitAngle(items, centerInliers, fit.Direction);
            int inlierCount = Count(centerInliers);
            rotation.Direction = fit.Direction > 0 ? RotationDirection.Same : RotationDirection.Opposite;
            rotation.AngleOffsetDegrees = AngleMath.NormalizeDegrees(fit.Offset);
            rotation.UnconstrainedSlope = fit.Slope;
            rotation.AngleRmsDegrees = fit.Rms;
            rotation.MaxAngleResidualDegrees = fit.MaxResidual;
            rotation.MachineAngleSpanDegrees = fit.Span;
            rotation.RotationCenterImage = new ImagePoint(centerRow, centerColumn);
            Point2D centerStage = HalconGeometry.TransformPoint(result.PixelToStageMatrix,
                new Point2D(centerColumn, centerRow));
            rotation.RotationCenterStage = new StagePoint(centerStage.X, centerStage.Y);
            rotation.ReferenceStage = reference;
            rotation.RadiusPixels = Math.Sqrt((localX * localX) + (localY * localY));
            rotation.InlierCount = inlierCount;
            rotation.RejectedCount = items.Count - inlierCount;

            List<double> residualValues = new List<double>();
            List<double> residualWeights = new List<double>();
            double maxCenterResidual = 0.0;
            for (int i = 0; i < items.Count; i++)
            {
                RotationItem item = items[i];
                double phi = AngleMath.DegreesToRadians(fit.Direction * item.MachineAngle);
                double cos = Math.Cos(phi), sin = Math.Sin(phi);
                double normalizedColumn = centerColumn + (cos * localX) - (sin * localY);
                double normalizedRow = centerRow + (sin * localX) + (cos * localY);
                Vector2D shift = HalconGeometry.TransformVector(result.StageToPixelMatrix,
                    new Vector2D(item.StageX - reference.X, item.StageY - reference.Y));
                double predictedColumn = normalizedColumn + shift.X;
                double predictedRow = normalizedRow + shift.Y;
                double dc = predictedColumn - item.Column, dr = predictedRow - item.Row;
                double residual = Math.Sqrt((dc * dc) + (dr * dr));
                double predictedAngle = AngleMath.NormalizeDegrees(
                    (fit.Direction * item.MachineAngle) + fit.Offset);
                CalibrationSampleDiagnostic diagnostic = item.Diagnostic;
                diagnostic.IsInlier = centerInliers[i];
                diagnostic.PredictedImagePose = new ImagePose(predictedRow, predictedColumn, predictedAngle);
                Point2D stage = HalconGeometry.TransformPoint(result.PixelToStageMatrix,
                    new Point2D(predictedColumn, predictedRow));
                diagnostic.PredictedStagePoint = new StagePoint(stage.X, stage.Y);
                diagnostic.ResidualColumnPixels = dc; diagnostic.ResidualRowPixels = dr;
                diagnostic.ResidualPixels = residual;
                diagnostic.PredictedImageAngleDegrees = predictedAngle;
                diagnostic.AngleResidualDegrees = AngleMath.NormalizeDegrees(item.ImageAngle - predictedAngle);
                diagnostic.RotationCenterResidualPixels = residual;
                diagnostic.RejectionReason = centerInliers[i] ? null : "Rotation residual outlier";
                if (!centerInliers[i]) continue;
                residualValues.Add(residual); residualWeights.Add(item.Weight);
                maxCenterResidual = Math.Max(maxCenterResidual, residual);
            }
            rotation.CenterRmsResidualPixels = WeightedRms(residualValues, residualWeights);
            rotation.MaxCenterResidualPixels = maxCenterResidual;
            if (rotation.RejectedCount > 0)
                result.Warnings.Add(rotation.RejectedCount + " rotation sample(s) were rejected as outliers.");
            if (rotation.AngleRmsDegrees > options.MaxAngleRmsDegrees ||
                rotation.CenterRmsResidualPixels > options.MaxRotationCenterRmsPixels)
                return FailRotation(rotation, CalibrationErrorCode.RotationResidualTooLarge,
                    "Rotation residual exceeds the configured limit.");
            rotation.IsAvailable = true;
            rotation.Success = true;
            rotation.ErrorCode = CalibrationErrorCode.None;
            rotation.Message = "Rotation calibration succeeded with HALCON.";
            return true;
        }

        private static bool InitialAffine(IList<TranslationItem> items, CalibrationSolverOptions options,
            out AffineMatrix2D bestMatrix, out bool[] bestInliers)
        {
            bestMatrix = default(AffineMatrix2D); bestInliers = null;
            if (!options.EnableRansac)
            { bestInliers = AllTrue(items.Count); return FitAffine(items, bestInliers, out bestMatrix); }
            int bestCount = -1; double bestSupport = -1.0, bestError = double.PositiveInfinity;
            int limit = Math.Max(1, options.RansacIterations);
            long combinations = ((long)items.Count * (items.Count - 1) * (items.Count - 2)) / 6;
            Random random = new Random(options.RandomSeed);
            HashSet<string> tested = new HashSet<string>();
            int sequential = 0, attempts = 0;
            while (tested.Count < limit && attempts++ < limit * 20)
            {
                int a, b, c;
                if (combinations <= limit)
                {
                    if (!Combination(items.Count, sequential++, out a, out b, out c)) break;
                }
                else
                {
                    a = random.Next(items.Count); b = random.Next(items.Count); c = random.Next(items.Count);
                    if (a == b || a == c || b == c) continue;
                    int low = Math.Min(a, Math.Min(b, c)), high = Math.Max(a, Math.Max(b, c));
                    b = a + b + c - low - high; a = low; c = high;
                }
                if (!tested.Add(a + ":" + b + ":" + c)) continue;
                bool[] seed = new bool[items.Count]; seed[a] = seed[b] = seed[c] = true;
                AffineMatrix2D candidate;
                if (!FitAffine(items, seed, out candidate)) continue;
                bool[] inliers = new bool[items.Count]; int count = 0;
                double support = 0.0, error = 0.0;
                for (int i = 0; i < items.Count; i++)
                {
                    double residual = AffineResidual(items[i], candidate);
                    if (residual > options.RansacInlierThresholdPixels) continue;
                    inliers[i] = true; count++; support += items[i].Weight;
                    error += items[i].Weight * residual * residual;
                }
                bool better = count > bestCount || (count == bestCount && support > bestSupport + 1e-12) ||
                    (count == bestCount && Math.Abs(support - bestSupport) <= 1e-12 && error < bestError);
                if (!better) continue;
                bestCount = count; bestSupport = support; bestError = error;
                bestMatrix = candidate; bestInliers = inliers;
            }
            return bestInliers != null && bestCount >= options.MinimumTranslationInliers;
        }

        private static bool Combination(int count, int index, out int a, out int b, out int c)
        {
            int current = 0;
            for (a = 0; a < count - 2; a++)
                for (b = a + 1; b < count - 1; b++)
                    for (c = b + 1; c < count; c++)
                        if (current++ == index) return true;
            a = b = c = 0; return false;
        }

        private static bool FitAffine(IList<TranslationItem> items, bool[] included,
            out AffineMatrix2D matrix)
        {
            List<double> x = new List<double>(), y = new List<double>();
            List<double> columns = new List<double>(), rows = new List<double>();
            for (int i = 0; i < items.Count; i++)
            {
                if (included != null && !included[i]) continue;
                x.Add(items[i].X); y.Add(items[i].Y);
                columns.Add(items[i].Column); rows.Add(items[i].Row);
            }
            return HalconGeometry.TryFitAffine(x, y, columns, rows, out matrix);
        }

        private static List<double> AffineResiduals(IList<TranslationItem> items, AffineMatrix2D matrix)
        {
            List<double> output = new List<double>(items.Count);
            foreach (TranslationItem item in items) output.Add(AffineResidual(item, matrix));
            return output;
        }

        private static double AffineResidual(TranslationItem item, AffineMatrix2D matrix)
        {
            Point2D point = HalconGeometry.TransformPoint(matrix, new Point2D(item.X, item.Y));
            double dx = point.X - item.Column, dy = point.Y - item.Row;
            return Math.Sqrt((dx * dx) + (dy * dy));
        }

        private static AngleFit FitAngle(IList<RotationItem> items, int direction,
            CalibrationSolverOptions options)
        {
            bool[] included = AllTrue(items.Count);
            for (int iteration = 0; iteration < 4; iteration++)
            {
                double offset = CircularOffset(items, included, direction);
                List<double> residuals = new List<double>();
                for (int i = 0; i < items.Count; i++) if (included[i])
                    residuals.Add(Math.Abs(AngleMath.NormalizeDegrees(items[i].ImageAngle -
                        ((direction * items[i].MachineAngle) + offset))));
                double threshold = MadThreshold(residuals, options.MadMultiplier,
                    options.AngleOutlierThresholdDegrees);
                bool[] refined = new bool[items.Count]; int count = 0;
                for (int i = 0; i < items.Count; i++)
                {
                    refined[i] = Math.Abs(AngleMath.NormalizeDegrees(items[i].ImageAngle -
                        ((direction * items[i].MachineAngle) + offset))) <= threshold;
                    if (refined[i]) count++;
                }
                if (count < options.MinimumRotationInliers) return null;
                if (Equal(included, refined)) { included = refined; break; }
                included = refined;
            }
            return RefitAngle(items, included, direction);
        }

        private static AngleFit RefitAngle(IList<RotationItem> items, bool[] included, int direction)
        {
            if (Count(included) < 2) return null;
            double offset = CircularOffset(items, included, direction);
            double reference = 0.0, min = double.PositiveInfinity, max = double.NegativeInfinity;
            bool hasReference = false; double total = 0.0, meanX = 0.0, meanY = 0.0;
            List<double> residuals = new List<double>(), weights = new List<double>();
            double maxResidual = 0.0;
            for (int i = 0; i < items.Count; i++)
            {
                if (!included[i]) continue;
                if (!hasReference) { reference = items[i].MachineAngle; hasReference = true; }
                double x = reference + AngleMath.NormalizeDegrees(items[i].MachineAngle - reference);
                double residual = AngleMath.NormalizeDegrees(items[i].ImageAngle -
                    ((direction * items[i].MachineAngle) + offset));
                double y = (direction * x) + offset + residual;
                total += items[i].Weight; meanX += items[i].Weight * x; meanY += items[i].Weight * y;
                min = Math.Min(min, x); max = Math.Max(max, x);
                residuals.Add(residual); weights.Add(items[i].Weight);
                maxResidual = Math.Max(maxResidual, Math.Abs(residual));
            }
            meanX /= total; meanY /= total;
            double numerator = 0.0, denominator = 0.0;
            for (int i = 0; i < items.Count; i++) if (included[i])
            {
                double x = reference + AngleMath.NormalizeDegrees(items[i].MachineAngle - reference);
                double residual = AngleMath.NormalizeDegrees(items[i].ImageAngle -
                    ((direction * items[i].MachineAngle) + offset));
                double y = (direction * x) + offset + residual;
                numerator += items[i].Weight * (x - meanX) * (y - meanY);
                denominator += items[i].Weight * (x - meanX) * (x - meanX);
            }
            return new AngleFit { Direction = direction, Offset = offset,
                Slope = denominator <= 1e-15 ? double.NaN : numerator / denominator,
                Span = max - min, Rms = WeightedRms(residuals, weights),
                MaxResidual = maxResidual, Inliers = (bool[])included.Clone(),
                InlierCount = Count(included) };
        }

        private static double CircularOffset(IList<RotationItem> items, bool[] included, int direction)
        {
            List<double> values = new List<double>(), weights = new List<double>();
            for (int i = 0; i < items.Count; i++) if (included[i])
            {
                values.Add(AngleMath.NormalizeDegrees(items[i].ImageAngle -
                    (direction * items[i].MachineAngle)));
                weights.Add(items[i].Weight);
            }
            return AngleMath.CircularMeanDegrees(values, weights);
        }

        private static AngleFit ChooseAngleFit(AngleFit first, AngleFit second, int minimum)
        {
            bool firstOk = first != null && first.InlierCount >= minimum;
            bool secondOk = second != null && second.InlierCount >= minimum;
            if (!firstOk) return secondOk ? second : null;
            if (!secondOk) return first;
            if (first.InlierCount != second.InlierCount)
                return first.InlierCount > second.InlierCount ? first : second;
            return first.Rms <= second.Rms ? first : second;
        }

        private static StagePoint WeightedStageCenter(IList<RotationItem> items, bool[] included)
        {
            double x = 0.0, y = 0.0, total = 0.0;
            for (int i = 0; i < items.Count; i++) if (included[i])
            { x += items[i].Weight * items[i].StageX; y += items[i].Weight * items[i].StageY; total += items[i].Weight; }
            return new StagePoint(x / total, y / total);
        }

        private static bool FitCenter(IList<RotationItem> items, bool[] included, int direction,
            out double centerColumn, out double centerRow, out double localX, out double localY)
        {
            centerColumn = centerRow = localX = localY = 0.0;
            double[,] normal = new double[4, 4]; double[] right = new double[4]; int count = 0;
            for (int i = 0; i < items.Count; i++) if (included[i])
            {
                double phi = AngleMath.DegreesToRadians(direction * items[i].MachineAngle);
                double cos = Math.Cos(phi), sin = Math.Sin(phi);
                AddNormal(normal, right, new[] { 1.0, 0.0, cos, -sin },
                    items[i].NormalizedColumn, items[i].Weight);
                AddNormal(normal, right, new[] { 0.0, 1.0, sin, cos },
                    items[i].NormalizedRow, items[i].Weight);
                count++;
            }
            double[] solution;
            if (count < 2 || !HalconGeometry.TrySolveLinear(normal, right, out solution)) return false;
            centerColumn = solution[0]; centerRow = solution[1];
            localX = solution[2]; localY = solution[3];
            return Finite(centerColumn) && Finite(centerRow) && Finite(localX) && Finite(localY);
        }

        private static void AddNormal(double[,] normal, double[] right,
            double[] coefficients, double value, double weight)
        {
            for (int row = 0; row < coefficients.Length; row++)
            {
                right[row] += weight * coefficients[row] * value;
                for (int column = 0; column < coefficients.Length; column++)
                    normal[row, column] += weight * coefficients[row] * coefficients[column];
            }
        }

        private static double CenterResidual(RotationItem item, int direction,
            double centerColumn, double centerRow, double localX, double localY)
        {
            double phi = AngleMath.DegreesToRadians(direction * item.MachineAngle);
            double cos = Math.Cos(phi), sin = Math.Sin(phi);
            double dc = centerColumn + (cos * localX) - (sin * localY) - item.NormalizedColumn;
            double dr = centerRow + (sin * localX) + (cos * localY) - item.NormalizedRow;
            return Math.Sqrt((dc * dc) + (dr * dr));
        }

        private static List<TranslationItem> TranslationItems(CalibrationJob job,
            IList<CalibrationSampleDiagnostic> diagnostics, CalibrationSolverOptions options)
        {
            List<TranslationItem> output = new List<TranslationItem>();
            long revision = job.Template == null ? 0 : job.Template.Revision;
            if (job.Samples == null) return output;
            for (int i = 0; i < job.Samples.Count; i++)
            {
                CalibrationSample sample = job.Samples[i];
                if (sample == null || sample.Kind != CalibrationSampleKind.Translation) continue;
                if (!Eligible(sample, revision, options, diagnostics[i])) continue;
                output.Add(new TranslationItem { Sample = sample, Diagnostic = diagnostics[i],
                    X = sample.MachinePose.X, Y = sample.MachinePose.Y,
                    Column = sample.ImagePose.Column, Row = sample.ImagePose.Row,
                    Weight = Weight(sample.MatchScore, options.UseMatchScoreWeights) });
            }
            return output;
        }

        private static List<RotationItem> RotationItems(CalibrationJob job,
            IList<CalibrationSampleDiagnostic> diagnostics, CalibrationSolverOptions options)
        {
            List<RotationItem> output = new List<RotationItem>();
            long revision = job.Template == null ? 0 : job.Template.Revision;
            if (job.Samples == null) return output;
            for (int i = 0; i < job.Samples.Count; i++)
            {
                CalibrationSample sample = job.Samples[i];
                if (sample == null || sample.Kind != CalibrationSampleKind.Rotation) continue;
                if (!Eligible(sample, revision, options, diagnostics[i])) continue;
                output.Add(new RotationItem { Diagnostic = diagnostics[i],
                    MachineAngle = sample.MachinePose.ThetaDegrees,
                    ImageAngle = sample.ImagePose.AngleDegrees,
                    Column = sample.ImagePose.Column, Row = sample.ImagePose.Row,
                    StageX = sample.MachinePose.X, StageY = sample.MachinePose.Y,
                    Weight = Weight(sample.MatchScore, options.UseMatchScoreWeights) });
            }
            return output;
        }

        private static bool Eligible(CalibrationSample sample, long revision,
            CalibrationSolverOptions options, CalibrationSampleDiagnostic diagnostic)
        {
            if (!sample.Enabled) { diagnostic.RejectionReason = "Sample is disabled"; return false; }
            if (sample.MachinePose == null || !sample.MachinePose.IsFinite ||
                sample.ImagePose == null || !sample.ImagePose.IsFinite)
            { diagnostic.RejectionReason = "Sample pose is invalid"; return false; }
            if (sample.TemplateRevision != revision)
            { diagnostic.RejectionReason = "Template revision mismatch"; return false; }
            if (!Finite(sample.MatchScore) || sample.MatchScore < options.MinimumMatchScore)
            { diagnostic.RejectionReason = "Match score is below threshold"; return false; }
            diagnostic.IsEligible = true; return true;
        }

        private static void InitializeDiagnostics(CalibrationJob job, CalibrationResult result)
        {
            if (job.Samples == null) return;
            for (int i = 0; i < job.Samples.Count; i++)
            {
                CalibrationSample sample = job.Samples[i];
                result.Diagnostics.Add(new CalibrationSampleDiagnostic { SampleIndex = i,
                    SampleId = sample == null ? null : sample.SampleId,
                    Tag = sample == null ? null : sample.Tag,
                    Kind = sample == null ? CalibrationSampleKind.Translation : sample.Kind,
                    RejectionReason = sample == null ? "Sample is null" : null });
            }
        }

        private static bool Validate(CalibrationJob job, out string error)
        {
            if (!Enum.IsDefined(typeof(LinearUnit), job.LinearUnit) ||
                !Enum.IsDefined(typeof(RotationConvention), job.RotationConvention))
            { error = "Calibration unit or rotation convention is unsupported."; return false; }
            if (job.HasStaleEnabledSamples)
            { error = "One or more enabled samples belong to an older template revision."; return false; }
            CalibrationSolverOptions o = job.SolverOptions;
            if (o == null) { error = "Calibration solver options are missing."; return false; }
            if (o.MinimumTranslationSamples < 3 || o.MinimumTranslationInliers < 3 ||
                o.MinimumTranslationInliers > o.MinimumTranslationSamples ||
                o.MinimumRotationSamples < 3 || o.MinimumRotationInliers < 3 ||
                o.MinimumRotationInliers > o.MinimumRotationSamples)
            { error = "Calibration sample limits are invalid."; return false; }
            if (!Positive(o.MinimumAxisTravel) || !Positive(o.RansacInlierThresholdPixels) ||
                !Positive(o.MadMultiplier) || !Positive(o.MaxRmsResidualPixels) ||
                !Positive(o.MaxResidualPixels) || !Positive(o.MaxConditionNumber) ||
                !Positive(o.MinimumRotationSpanDegrees) || !Positive(o.AngleOutlierThresholdDegrees) ||
                !Positive(o.MaxAngleRmsDegrees) || !Positive(o.CenterOutlierThresholdPixels) ||
                !Positive(o.MaxRotationCenterRmsPixels) || !Finite(o.MinimumMatchScore) ||
                o.MinimumMatchScore < 0.0 || o.MinimumMatchScore > 1.0)
            { error = "One or more calibration thresholds are invalid."; return false; }
            if (job.Template != null && job.Template.IsModelDirty &&
                (job.Template.ModelData != null && job.Template.ModelData.Length > 0 ||
                 job.Template.Revision > 1 || !string.IsNullOrWhiteSpace(job.Template.ModelHash)))
            { error = "Template settings changed and the model must be rebuilt before calibration."; return false; }
            error = null; return true;
        }

        private static double MadThreshold(IList<double> values, double multiplier, double minimum)
        {
            if (values == null || values.Count == 0) return minimum;
            double median = Median(values); List<double> deviations = new List<double>(values.Count);
            for (int i = 0; i < values.Count; i++) deviations.Add(Math.Abs(values[i] - median));
            return Math.Max(minimum, median + (multiplier * 1.4826 * Median(deviations)));
        }

        private static double Median(IList<double> values)
        {
            double[] copy = new double[values.Count];
            for (int i = 0; i < values.Count; i++) copy[i] = values[i];
            Array.Sort(copy); int middle = copy.Length / 2;
            return copy.Length % 2 == 0 ? (copy[middle - 1] + copy[middle]) * 0.5 : copy[middle];
        }

        private static double WeightedRms(IList<double> values, IList<double> weights)
        {
            if (values == null || weights == null || values.Count == 0 || values.Count != weights.Count)
                return double.NaN;
            double sum = 0.0, total = 0.0;
            for (int i = 0; i < values.Count; i++)
            { sum += weights[i] * values[i] * values[i]; total += weights[i]; }
            return total <= 0.0 ? double.NaN : Math.Sqrt(sum / total);
        }

        private static double Weight(double score, bool enabled)
        { return enabled && Finite(score) ? Math.Max(0.01, score * score) : 1.0; }
        private static bool[] AllTrue(int count)
        { bool[] values = new bool[count]; for (int i = 0; i < count; i++) values[i] = true; return values; }
        private static int Count(bool[] values)
        { int count = 0; if (values != null) for (int i = 0; i < values.Length; i++) if (values[i]) count++; return count; }
        private static bool Equal(bool[] a, bool[] b)
        { if (a == null || b == null || a.Length != b.Length) return false;
          for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false; return true; }
        private static bool HasRotationSample(CalibrationJob job)
        { if (job.Samples == null) return false; foreach (CalibrationSample sample in job.Samples)
          if (sample != null && sample.Enabled && sample.Kind == CalibrationSampleKind.Rotation) return true; return false; }
        private static bool Finite(double value) { return !double.IsNaN(value) && !double.IsInfinity(value); }
        private static bool Positive(double value) { return Finite(value) && value > 0.0; }
        private static CalibrationResult Fail(CalibrationResult result, CalibrationErrorCode code, string message)
        { result.Success = false; result.TranslationSuccess = false; result.ErrorCode = code; result.Message = message; return result; }
        private static bool FailTranslation(CalibrationResult result, CalibrationErrorCode code, string message)
        { Fail(result, code, message); return false; }
        private static bool FailRotation(RotationCalibrationResult result, CalibrationErrorCode code, string message)
        { result.Success = false; result.ErrorCode = code; result.Message = message; return false; }

        private sealed class TranslationItem
        { public CalibrationSample Sample; public CalibrationSampleDiagnostic Diagnostic;
          public double X, Y, Column, Row, Weight; }
        private sealed class RotationItem
        { public CalibrationSampleDiagnostic Diagnostic; public double MachineAngle, ImageAngle;
          public double Column, Row, StageX, StageY, Weight, NormalizedColumn, NormalizedRow; }
        private sealed class AngleFit
        { public int Direction, InlierCount; public double Offset, Slope, Span, Rms, MaxResidual; public bool[] Inliers; }
    }
}
