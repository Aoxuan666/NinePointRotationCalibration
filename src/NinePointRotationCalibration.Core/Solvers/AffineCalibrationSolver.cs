using System;
using System.Collections.Generic;

namespace NinePointRotationCalibration
{
    /// <summary>使用带鲁棒外点剔除的仿射模型求解像素与机械坐标关系。</summary>
    internal static class AffineCalibrationSolver
    {
        /// <summary>拟合平移样本并输出正逆仿射矩阵及残差诊断。</summary>
        public static bool Solve(
            CalibrationJob job,
            CalibrationResult result,
            ICalibrationGeometryBackend geometryBackend)
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
            if (!FindInitialModel(observations, options, geometryBackend, out matrix, out inliers))
            {
                return Fail(result, CalibrationErrorCode.DegenerateGeometry,
                    "Translation samples are collinear, duplicated, or do not form a stable affine model.");
            }

            // 基于初值执行最多 3 轮 MAD 自适应重加权内点精化，逐步剔除漂移外点。
            for (int iteration = 0; iteration < 3; iteration++)
            {
                if (!FitAffine(observations, inliers, geometryBackend, out matrix))
                {
                    return Fail(result, CalibrationErrorCode.DegenerateGeometry,
                        "Could not refit an affine model from the selected inliers.");
                }

                List<double> residuals = ComputeResiduals(observations, matrix, geometryBackend);
                List<double> inlierResiduals = new List<double>();
                for (int i = 0; i < observations.Count; i++)
                {
                    if (inliers[i]) inlierResiduals.Add(residuals[i]);
                }
                double threshold = SolverMath.MadThreshold(
                    inlierResiduals.Count > 0 ? inlierResiduals : residuals,
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

            if (!FitAffine(observations, inliers, geometryBackend, out matrix))
            {
                return Fail(result, CalibrationErrorCode.DegenerateGeometry,
                    "Final affine inliers do not define an invertible model.");
            }

            AffineMatrix2D inverse;
            if (geometryBackend == null
                ? !matrix.TryInvert(out inverse)
                : !geometryBackend.TryInvert(matrix, out inverse))
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

            // 从仿射矩阵两列提取机械 X/Y 轴在图像中的基向量，换算像素当量与轴正交夹角。
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
                Point2D predictedPixel = geometryBackend == null
                    ? matrix.Transform(new Point2D(observation.X, observation.Y))
                    : geometryBackend.TransformPoint(matrix, new Point2D(observation.X, observation.Y));
                double dc = predictedPixel.X - observation.Column;
                double dr = predictedPixel.Y - observation.Row;
                double pixelResidual = Math.Sqrt((dc * dc) + (dr * dr));
                Point2D predictedStage = geometryBackend == null
                    ? inverse.Transform(new Point2D(observation.Column, observation.Row))
                    : geometryBackend.TransformPoint(inverse, new Point2D(observation.Column, observation.Row));
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

        /// <summary>通过 RANSAC（三点全枚举或随机采样）寻找抗外点的初始仿射模型。</summary>
        private static bool FindInitialModel(
            IList<AffineObservation> observations,
            CalibrationSolverOptions options,
            ICalibrationGeometryBackend geometryBackend,
            out AffineMatrix2D bestMatrix,
            out bool[] bestInliers)
        {
            bestMatrix = default(AffineMatrix2D);
            bestInliers = null;
            if (!options.EnableRansac)
            {
                bestInliers = AllTrue(observations.Count);
                return FitAffine(observations, bestInliers, geometryBackend, out bestMatrix);
            }

            int bestCount = -1;
            double bestSupport = double.NegativeInfinity;
            double bestError = double.PositiveInfinity;
            int orderIndex = Math.Max(0, Math.Min(observations.Count - 1, options.MinimumTranslationInliers - 1));
            bool hasLmedsCandidate = false;
            AffineMatrix2D bestLmedsMatrix = default(AffineMatrix2D);
            double bestLmedsOrderResidual = double.PositiveInfinity;
            double[] bestLmedsResiduals = null;
            long combinations = ((long)observations.Count * (observations.Count - 1) * (observations.Count - 2)) / 6;
            int limit = Math.Max(1, options.RansacIterations);

            // 组合总数不超过迭代上限时枚举全部三点子集；否则使用固定种子随机抽样。
            if (combinations <= limit)
            {
                int tested = 0;
                for (int i = 0; i < observations.Count - 2 && tested < limit; i++)
                {
                    for (int j = i + 1; j < observations.Count - 1 && tested < limit; j++)
                    {
                        for (int k = j + 1; k < observations.Count && tested < limit; k++)
                        {
                            EvaluateCandidate(observations, options, geometryBackend, i, j, k, orderIndex,
                                ref bestMatrix, ref bestInliers, ref bestCount, ref bestSupport, ref bestError,
                                ref hasLmedsCandidate, ref bestLmedsMatrix, ref bestLmedsOrderResidual, ref bestLmedsResiduals);
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
                    EvaluateCandidate(observations, options, geometryBackend, i, j, k, orderIndex,
                        ref bestMatrix, ref bestInliers, ref bestCount, ref bestSupport, ref bestError,
                        ref hasLmedsCandidate, ref bestLmedsMatrix, ref bestLmedsOrderResidual, ref bestLmedsResiduals);
                }
            }

            if (bestInliers != null && bestCount >= options.MinimumTranslationInliers)
            {
                return true;
            }

            // 若样本几何并不退化，仅因高分辨率宽视场镜头畸变/轻微倾斜导致严格三点外推残差超过固定 RANSAC 阈值，
            // 回退至 LMedS（最小第 k 阶残差）鲁棒初值，既能剔除粗大外点又避免误报 DegenerateGeometry。
            if (hasLmedsCandidate && bestLmedsResiduals != null)
            {
                double floorThreshold = Math.Max(options.RansacInlierThresholdPixels, bestLmedsOrderResidual * 1.25);
                double adaptiveThreshold = SolverMath.MadThreshold(bestLmedsResiduals, options.MadMultiplier, floorThreshold);
                bool[] fallbackInliers = new bool[observations.Count];
                int fallbackCount = 0;
                for (int i = 0; i < observations.Count; i++)
                {
                    if (bestLmedsResiduals[i] <= adaptiveThreshold)
                    {
                        fallbackInliers[i] = true;
                        fallbackCount++;
                    }
                }

                if (fallbackCount >= options.MinimumTranslationInliers)
                {
                    bestMatrix = bestLmedsMatrix;
                    bestInliers = fallbackInliers;
                    return true;
                }
            }

            // 兜底尝试全样本最小二乘（仅在全部三点候选均未命中时检验是否真的共线/奇异）。
            bool[] allInliers = AllTrue(observations.Count);
            AffineMatrix2D globalMatrix;
            if (FitAffine(observations, allInliers, geometryBackend, out globalMatrix)
                && Numeric.IsFinite(globalMatrix.ConditionNumber)
                && globalMatrix.ConditionNumber <= options.MaxConditionNumber)
            {
                bestMatrix = globalMatrix;
                bestInliers = allInliers;
                return true;
            }

            return false;
        }

        private static void EvaluateCandidate(
            IList<AffineObservation> observations,
            CalibrationSolverOptions options,
            ICalibrationGeometryBackend geometryBackend,
            int first,
            int second,
            int third,
            int orderIndex,
            ref AffineMatrix2D bestMatrix,
            ref bool[] bestInliers,
            ref int bestCount,
            ref double bestSupport,
            ref double bestError,
            ref bool hasLmedsCandidate,
            ref AffineMatrix2D bestLmedsMatrix,
            ref double bestLmedsOrderResidual,
            ref double[] bestLmedsResiduals)
        {
            bool[] seed = new bool[observations.Count];
            seed[first] = true;
            seed[second] = true;
            seed[third] = true;
            AffineMatrix2D candidate;
            if (!FitAffine(observations, seed, geometryBackend, out candidate)) return;
            if (!Numeric.IsFinite(candidate.ConditionNumber) || candidate.ConditionNumber > options.MaxConditionNumber) return;

            bool[] inliers = new bool[observations.Count];
            double[] residuals = new double[observations.Count];
            double[] sortedResiduals = new double[observations.Count];
            int count = 0;
            double support = 0.0;
            double error = 0.0;
            for (int i = 0; i < observations.Count; i++)
            {
                double residual = Residual(observations[i], candidate, geometryBackend);
                residuals[i] = residual;
                sortedResiduals[i] = residual;
                if (residual <= options.RansacInlierThresholdPixels)
                {
                    inliers[i] = true;
                    count++;
                    support += observations[i].Weight;
                    error += observations[i].Weight * residual * residual;
                }
            }

            Array.Sort(sortedResiduals);
            double orderResidual = sortedResiduals[orderIndex];
            if (Numeric.IsFinite(orderResidual) && orderResidual < bestLmedsOrderResidual)
            {
                hasLmedsCandidate = true;
                bestLmedsOrderResidual = orderResidual;
                bestLmedsMatrix = candidate;
                bestLmedsResiduals = residuals;
            }

            // 优选内点数最多、权重支持度最高、加权误差最小的候选模型。
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

        /// <summary>基于指定内点集合拟合二维仿射矩阵（优先调用几何后端，否则使用加权最小二乘）。</summary>
        private static bool FitAffine(
            IList<AffineObservation> observations,
            bool[] included,
            ICalibrationGeometryBackend geometryBackend,
            out AffineMatrix2D matrix)
        {
            matrix = default(AffineMatrix2D);
            if (geometryBackend != null)
            {
                List<double> sourceX = new List<double>();
                List<double> sourceY = new List<double>();
                List<double> targetX = new List<double>();
                List<double> targetY = new List<double>();
                for (int i = 0; i < observations.Count; i++)
                {
                    if (included != null && !included[i]) continue;
                    sourceX.Add(observations[i].X);
                    sourceY.Add(observations[i].Y);
                    targetX.Add(observations[i].Column);
                    targetY.Add(observations[i].Row);
                }

                return geometryBackend.TryFitAffine(sourceX, sourceY, targetX, targetY, out matrix);
            }

            // 先求加权质心并中心化坐标，避免机械/像素绝对坐标偏大导致正规方程病态。
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

            // 克莱姆法则求解中心化后的 2x2 线性部分，再由质心映射反推平移项。
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

        private static List<double> ComputeResiduals(
            IList<AffineObservation> observations,
            AffineMatrix2D matrix,
            ICalibrationGeometryBackend geometryBackend)
        {
            List<double> residuals = new List<double>(observations.Count);
            foreach (AffineObservation observation in observations)
                residuals.Add(Residual(observation, matrix, geometryBackend));
            return residuals;
        }

        private static double Residual(
            AffineObservation observation,
            AffineMatrix2D matrix,
            ICalibrationGeometryBackend geometryBackend)
        {
            Point2D predicted = geometryBackend == null
                ? matrix.Transform(new Point2D(observation.X, observation.Y))
                : geometryBackend.TransformPoint(matrix, new Point2D(observation.X, observation.Y));
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
