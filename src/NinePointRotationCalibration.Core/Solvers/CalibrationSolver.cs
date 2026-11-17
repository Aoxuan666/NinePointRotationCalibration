using System;
using System.Collections.Generic;

namespace NinePointRotationCalibration
{
    /// <summary>
    /// 求解像素与机械坐标的仿射关系，并按配置求解机械角度与旋转中心。
    /// </summary>
    public static class CalibrationSolver
    {
        /// <summary>执行九点仿射标定，并按配置决定是否继续求解旋转关系。</summary>
        public static CalibrationResult Solve(CalibrationJob job)
        {
            return Solve(job, null);
        }

        /// <summary>
        /// 执行九点和旋转标定；提供几何后端时，底层矩阵计算由该后端完成。
        /// </summary>
        public static CalibrationResult Solve(
            CalibrationJob job,
            ICalibrationGeometryBackend geometryBackend)
        {
            CalibrationSolverOptions options = job == null ? null : job.SolverOptions;
            CalibrationResult result = SolveCore(job, geometryBackend);
            result.RmsAssessment = CalibrationRmsEvaluator.Evaluate(result, options);
            return result;
        }

        private static CalibrationResult SolveCore(
            CalibrationJob job,
            ICalibrationGeometryBackend geometryBackend)
        {
            if (job == null)
                return CalibrationResult.Failure(CalibrationErrorCode.ParameterInvalid, "Calibration job is null.");

            CalibrationResult result = new CalibrationResult();
            InitializeDiagnostics(job, result);

            string enumError;
            if (!ValidateEnumValues(job, out enumError))
                return CopyDiagnostics(result, CalibrationResult.Failure(CalibrationErrorCode.ParameterInvalid, enumError));

            // 先检查任务和样本是否失效，确保启用的旧版本样本始终返回明确错误。
            if (job.HasStaleEnabledSamples)
            {
                CalibrationResult stale = CalibrationResult.Failure(
                    CalibrationErrorCode.StaleSamples,
                    "One or more enabled samples belong to an older template revision.");
                return CopyDiagnostics(result, stale);
            }

            string optionsError;
            if (!ValidateOptions(job.SolverOptions, out optionsError))
                return CopyDiagnostics(result, CalibrationResult.Failure(CalibrationErrorCode.ParameterInvalid, optionsError));

            // 无模型任务可用于外部采集姿态后的纯几何求解；模型创建后若参数变化，
            // 必须重新建模才能继续标定。
            if (job.Template != null && job.Template.IsModelDirty &&
                (job.Template.ModelData != null && job.Template.ModelData.Length > 0 ||
                 job.Template.Revision > 1 || !string.IsNullOrWhiteSpace(job.Template.ModelHash)))
            {
                CalibrationResult dirty = CalibrationResult.Failure(
                    CalibrationErrorCode.TemplateModelDirty,
                    "Template settings changed and the model must be rebuilt before calibration.");
                return CopyDiagnostics(result, dirty);
            }

            try
            {
                if (!AffineCalibrationSolver.Solve(job, result, geometryBackend))
                {
                    return result;
                }

                bool rotationSucceeded = RotationCalibrationSolver.Solve(job, result, geometryBackend);
                if (!rotationSucceeded && job.SolverOptions.RequireRotationCalibration)
                {
                    result.Success = false;
                    result.ErrorCode = result.Rotation.ErrorCode;
                    result.Message = result.Rotation.Message;
                    return result;
                }

                if (!rotationSucceeded)
                {
                    if (HasEnabledRotationSample(job))
                        result.Warnings.Add("Rotation calibration was not accepted: " + result.Rotation.Message);
                    result.Success = true;
                    result.ErrorCode = CalibrationErrorCode.None;
                    result.Message = "Translation calibration succeeded; rotation calibration is optional and unavailable.";
                    return result;
                }

                result.Success = true;
                result.ErrorCode = CalibrationErrorCode.None;
                result.Message = "Nine-point and rotation calibration succeeded.";
                return result;
            }
            catch (Exception exception)
            {
                result.Success = false;
                result.ErrorCode = CalibrationErrorCode.InternalError;
                result.Message = "Calibration solver failed: " + exception.Message;
                return result;
            }
        }

        /// <summary>仅执行平移仿射标定，不要求旋转样本。</summary>
        public static CalibrationResult SolveTranslation(CalibrationJob job)
        {
            return SolveTranslation(job, null);
        }

        /// <summary>使用指定几何后端仅执行平移仿射标定。</summary>
        public static CalibrationResult SolveTranslation(
            CalibrationJob job,
            ICalibrationGeometryBackend geometryBackend)
        {
            if (job == null)
                return CalibrationResult.Failure(CalibrationErrorCode.ParameterInvalid, "Calibration job is null.");
            // 深拷贝任务后再关闭旋转要求，避免修改调用方传入的配置。
            CalibrationJob copy = job.DeepClone();
            if (copy.SolverOptions == null) copy.SolverOptions = new CalibrationSolverOptions();
            copy.SolverOptions.RequireRotationCalibration = false;
            return Solve(copy, geometryBackend);
        }

        /// <summary>执行标定并以布尔值返回成功状态。</summary>
        public static bool TrySolve(CalibrationJob job, out CalibrationResult result)
        {
            result = Solve(job);
            return result.Success;
        }

        private static void InitializeDiagnostics(CalibrationJob job, CalibrationResult result)
        {
            if (job.Samples == null) return;
            for (int i = 0; i < job.Samples.Count; i++)
            {
                CalibrationSample sample = job.Samples[i];
                result.Diagnostics.Add(new CalibrationSampleDiagnostic
                {
                    SampleIndex = i,
                    SampleId = sample == null ? null : sample.SampleId,
                    Tag = sample == null ? null : sample.Tag,
                    Kind = sample == null ? CalibrationSampleKind.Translation : sample.Kind,
                    RejectionReason = sample == null ? "Sample is null" : null
                });
            }
        }

        private static bool ValidateOptions(CalibrationSolverOptions options, out string error)
        {
            if (options == null)
            {
                error = "Calibration solver options are missing.";
                return false;
            }

            if (options.MinimumTranslationSamples < 3 || options.MinimumTranslationInliers < 3
                || options.MinimumTranslationInliers > options.MinimumTranslationSamples)
            {
                error = "Translation sample limits are invalid.";
                return false;
            }
            if (options.MinimumRotationSamples < 3 || options.MinimumRotationInliers < 3
                || options.MinimumRotationInliers > options.MinimumRotationSamples)
            {
                error = "Rotation sample limits are invalid.";
                return false;
            }
            if (!Positive(options.MinimumAxisTravel) || !Positive(options.RansacInlierThresholdPixels)
                || !Positive(options.MadMultiplier) || !Positive(options.MaxRmsResidualPixels)
                || !Positive(options.MaxResidualPixels) || !Positive(options.MaxConditionNumber)
                || !Positive(options.MinimumRotationSpanDegrees) || !Positive(options.AngleOutlierThresholdDegrees)
                || !Positive(options.MaxAngleRmsDegrees) || !Positive(options.CenterOutlierThresholdPixels)
                || !Positive(options.MaxRotationCenterRmsPixels))
            {
                error = "One or more solver thresholds are invalid.";
                return false;
            }
            if (!Numeric.IsFinite(options.MinimumMatchScore) || options.MinimumMatchScore < 0.0 || options.MinimumMatchScore > 1.0)
            {
                error = "Minimum match score must be in [0, 1].";
                return false;
            }

            error = null;
            return true;
        }

        private static bool HasEnabledRotationSample(CalibrationJob job)
        {
            if (job.Samples == null) return false;
            foreach (CalibrationSample sample in job.Samples)
            {
                if (sample != null && sample.Enabled && sample.Kind == CalibrationSampleKind.Rotation) return true;
            }
            return false;
        }

        private static bool ValidateEnumValues(CalibrationJob job, out string error)
        {
            if (!Enum.IsDefined(typeof(LinearUnit), job.LinearUnit))
            {
                error = "Calibration linear unit is unsupported.";
                return false;
            }

            if (!Enum.IsDefined(typeof(RotationConvention), job.RotationConvention))
            {
                error = "Rotation convention is unsupported.";
                return false;
            }

            if (job.Template != null && !Enum.IsDefined(typeof(TemplateModelType), job.Template.ModelType))
            {
                error = "Template model type is unsupported.";
                return false;
            }

            if (job.Samples != null)
            {
                foreach (CalibrationSample sample in job.Samples)
                {
                    if (sample != null && !Enum.IsDefined(typeof(CalibrationSampleKind), sample.Kind))
                    {
                        error = "Calibration sample kind is unsupported.";
                        return false;
                    }
                }
            }

            error = null;
            return true;
        }

        private static CalibrationResult CopyDiagnostics(CalibrationResult source, CalibrationResult target)
        {
            target.Diagnostics = source.Diagnostics;
            return target;
        }

        private static bool Positive(double value) { return Numeric.IsFinite(value) && value > 0.0; }
    }
}
