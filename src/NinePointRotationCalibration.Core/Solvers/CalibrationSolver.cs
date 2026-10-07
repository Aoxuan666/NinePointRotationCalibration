using System;
using System.Collections.Generic;

namespace NinePointRotationCalibration
{
    /// <summary>
    /// Solves the pixel/stage affine relationship and, when requested, the
    /// machine/image angle relationship and rotation center.
    /// </summary>
    public static class CalibrationSolver
    {
        public static CalibrationResult Solve(CalibrationJob job)
        {
            if (job == null)
                return CalibrationResult.Failure(CalibrationErrorCode.ParameterInvalid, "Calibration job is null.");

            CalibrationResult result = new CalibrationResult();
            InitializeDiagnostics(job, result);

            string enumError;
            if (!ValidateEnumValues(job, out enumError))
                return CopyDiagnostics(result, CalibrationResult.Failure(CalibrationErrorCode.ParameterInvalid, enumError));

            // Check job/sample invalidation before any numerical option check so
            // an enabled stale sample always has the same actionable error.
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

            // A brand-new, model-less job is useful for the pure geometry API
            // (and for hosts that solve from externally acquired poses).  Once
            // a model has been created, however, changing its definition must
            // invalidate calibration until the model is rebuilt.
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
                if (!AffineCalibrationSolver.Solve(job, result))
                {
                    return result;
                }

                bool rotationSucceeded = RotationCalibrationSolver.Solve(job, result);
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

        public static CalibrationResult SolveTranslation(CalibrationJob job)
        {
            if (job == null)
                return CalibrationResult.Failure(CalibrationErrorCode.ParameterInvalid, "Calibration job is null.");
            CalibrationJob copy = job.DeepClone();
            if (copy.SolverOptions == null) copy.SolverOptions = new CalibrationSolverOptions();
            copy.SolverOptions.RequireRotationCalibration = false;
            return Solve(copy);
        }

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
