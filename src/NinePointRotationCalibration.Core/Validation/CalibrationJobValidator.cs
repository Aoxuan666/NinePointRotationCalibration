using System;
using System.Collections.Generic;

namespace NinePointRotationCalibration
{
    public static class CalibrationJobValidator
    {
        public static JobValidationResult Validate(
            CalibrationJob job,
            CalibrationValidationScope scope = CalibrationValidationScope.Full)
        {
            JobValidationResult result = new JobValidationResult();
            if (job == null)
            {
                result.Add(ValidationSeverity.Error, "JOB_NULL", "Calibration job is null.");
                return result;
            }

            if (job.SchemaVersion <= 0 || job.SchemaVersion > CalibrationJob.CurrentSchemaVersion)
            {
                result.Add(ValidationSeverity.Error, "SCHEMA_UNSUPPORTED",
                    "Calibration job schema version is unsupported.", "SchemaVersion");
            }

            if (!Enum.IsDefined(typeof(LinearUnit), job.LinearUnit))
                result.Add(ValidationSeverity.Error, "LINEAR_UNIT_INVALID", "Calibration linear unit is unsupported.", "LinearUnit");

            if (!Enum.IsDefined(typeof(RotationConvention), job.RotationConvention))
                result.Add(ValidationSeverity.Error, "ROTATION_CONVENTION_INVALID", "Rotation convention is unsupported.", "RotationConvention");

            ValidateTemplate(job.Template, result);
            ValidateOptions(job.SolverOptions, result);
            if (scope != CalibrationValidationScope.Configuration)
            {
                ValidateSamples(job, scope, result);
            }

            return result;
        }

        private static void ValidateTemplate(TemplateDefinition template, JobValidationResult result)
        {
            if (template == null)
            {
                result.Add(ValidationSeverity.Error, "TEMPLATE_NULL", "Template definition is missing.", "Template");
                return;
            }

            ValidateRegion(template.TemplateRoi, true, "Template.TemplateRoi", result);
            ValidateRegion(template.SearchRegion, false, "Template.SearchRegion", result);

            if (!Enum.IsDefined(typeof(TemplateModelType), template.ModelType))
                result.Add(ValidationSeverity.Error, "MODEL_TYPE_INVALID", "Template model type is unsupported.", "Template.ModelType");

            if (!template.ReferenceAnchor.IsFinite)
                result.Add(ValidationSeverity.Error, "ANCHOR_INVALID", "Template reference anchor must be finite.", "Template.ReferenceAnchor");
            if (!template.DomainCenter.IsFinite)
                result.Add(ValidationSeverity.Error, "DOMAIN_CENTER_INVALID", "Template domain center must be finite.", "Template.DomainCenter");

            if (template.MatchParameters == null)
            {
                result.Add(ValidationSeverity.Error, "MATCH_PARAMETERS_NULL", "Match parameters are missing.", "Template.MatchParameters");
            }
            else
            {
                MatchParameters parameters = template.MatchParameters;
                if (!InRange(parameters.MinScore, 0.0, 1.0))
                    result.Add(ValidationSeverity.Error, "MIN_SCORE_INVALID", "Minimum match score must be in [0, 1].", "Template.MatchParameters.MinScore");
                if (parameters.NumMatches < 1)
                    result.Add(ValidationSeverity.Error, "NUM_MATCHES_INVALID", "Number of matches must be at least one.", "Template.MatchParameters.NumMatches");
                if (!InRange(parameters.MaxOverlap, 0.0, 1.0))
                    result.Add(ValidationSeverity.Error, "MAX_OVERLAP_INVALID", "Maximum overlap must be in [0, 1].", "Template.MatchParameters.MaxOverlap");
                if (!InRange(parameters.Greediness, 0.0, 1.0))
                    result.Add(ValidationSeverity.Error, "GREEDINESS_INVALID", "Greediness must be in [0, 1].", "Template.MatchParameters.Greediness");
                if (!Numeric.IsFinite(parameters.AngleStartRad) || !Numeric.IsFinite(parameters.AngleExtentRad) || parameters.AngleExtentRad <= 0.0)
                    result.Add(ValidationSeverity.Error, "MATCH_ANGLE_INVALID", "Runtime angle range is invalid.", "Template.MatchParameters");
            }

            if (template.ModelType == TemplateModelType.Shape)
            {
                ShapeModelParameters parameters = template.ShapeParameters;
                if (parameters == null)
                {
                    result.Add(ValidationSeverity.Error, "SHAPE_PARAMETERS_NULL", "Shape model parameters are missing.", "Template.ShapeParameters");
                }
                else
                {
                    if (!Numeric.IsFinite(parameters.AngleStartRad) || !Numeric.IsFinite(parameters.AngleExtentRad) || parameters.AngleExtentRad <= 0.0)
                        result.Add(ValidationSeverity.Error, "MODEL_ANGLE_INVALID", "Shape model angle range is invalid.", "Template.ShapeParameters");
                    if (parameters.Contrast <= 0 || parameters.MinContrast < 0 || parameters.MinContrast > parameters.Contrast)
                        result.Add(ValidationSeverity.Error, "CONTRAST_INVALID", "Shape model contrast values are invalid.", "Template.ShapeParameters");
                }
            }
            else if (template.ModelType == TemplateModelType.Ncc && template.NccParameters == null)
            {
                result.Add(ValidationSeverity.Error, "NCC_PARAMETERS_NULL", "NCC model parameters are missing.", "Template.NccParameters");
            }

            if (template.MaskRegions != null)
            {
                for (int i = 0; i < template.MaskRegions.Count; i++)
                {
                    TemplateMaskShape mask = template.MaskRegions[i];
                    if (mask == null)
                    {
                        result.Add(ValidationSeverity.Error, "MASK_NULL", "A template mask entry is null.", "Template.MaskRegions[" + i + "]");
                    }
                    else
                    {
                        if (!Enum.IsDefined(typeof(MaskOperation), mask.Operation))
                            result.Add(ValidationSeverity.Error, "MASK_OPERATION_INVALID", "Template mask operation is unsupported.", "Template.MaskRegions[" + i + "].Operation");
                        ValidateRegion(mask.Region, true, "Template.MaskRegions[" + i + "].Region", result);
                    }
                }
            }

            if (template.EraseStrokes != null)
            {
                for (int i = 0; i < template.EraseStrokes.Count; i++)
                {
                    TemplateEraseStroke stroke = template.EraseStrokes[i];
                    if (stroke == null || !Numeric.IsFinite(stroke.Radius) || stroke.Radius <= 0.0 || stroke.Points == null || stroke.Points.Count == 0)
                    {
                        result.Add(ValidationSeverity.Error, "ERASE_STROKE_INVALID", "Erase stroke geometry is invalid.", "Template.EraseStrokes[" + i + "]");
                    }
                    else if (stroke.Operation != MaskOperation.Exclude && stroke.Operation != MaskOperation.Include)
                    {
                        result.Add(ValidationSeverity.Error, "ERASE_STROKE_OPERATION_INVALID", "Erase/restore stroke operation is invalid.", "Template.EraseStrokes[" + i + "].Operation");
                    }
                }
            }

            if (template.ModelData == null || template.ModelData.Length == 0)
                result.Add(ValidationSeverity.Error, "MODEL_MISSING", "Template model data is missing.", "Template.ModelData");
            else
            {
                string computedHash = TemplateDefinition.ComputeModelHash(template.ModelData);
                if (!string.IsNullOrWhiteSpace(template.ModelHash) && !string.Equals(template.ModelHash, computedHash, StringComparison.OrdinalIgnoreCase))
                    result.Add(ValidationSeverity.Error, "MODEL_HASH_MISMATCH", "Template model hash does not match model data.", "Template.ModelHash");
            }

            if (template.IsModelDirty)
                result.Add(ValidationSeverity.Error, "MODEL_DIRTY", "Template settings changed and the model must be rebuilt.", "Template.IsModelDirty");
        }

        private static void ValidateRegion(RegionDefinition region, bool required, string member, JobValidationResult result)
        {
            if (region == null)
            {
                if (required) result.Add(ValidationSeverity.Error, "REGION_MISSING", "Required region is missing.", member);
                return;
            }

            if (!Numeric.IsFinite(region.CenterRow) || !Numeric.IsFinite(region.CenterColumn) || !Numeric.IsFinite(region.PhiRadians))
                result.Add(ValidationSeverity.Error, "REGION_COORDINATE_INVALID", "Region coordinates must be finite.", member);

            if (!Enum.IsDefined(typeof(RegionKind), region.Kind))
            {
                result.Add(ValidationSeverity.Error, "REGION_KIND_INVALID", "Region kind is unsupported.", member + ".Kind");
                return;
            }

            switch (region.Kind)
            {
                case RegionKind.Rectangle1:
                case RegionKind.Rectangle2:
                    if (!Numeric.IsFinite(region.Length1) || !Numeric.IsFinite(region.Length2) || region.Length1 <= 0.0 || region.Length2 <= 0.0)
                        result.Add(ValidationSeverity.Error, "REGION_SIZE_INVALID", "Rectangle half-lengths must be positive.", member);
                    break;
                case RegionKind.Circle:
                    if (!Numeric.IsFinite(region.Radius) || region.Radius <= 0.0)
                        result.Add(ValidationSeverity.Error, "REGION_RADIUS_INVALID", "Circle radius must be positive.", member);
                    break;
                case RegionKind.Polygon:
                case RegionKind.Freehand:
                    if (region.Points == null || region.Points.Count < 3)
                        result.Add(ValidationSeverity.Error, "REGION_POINTS_INVALID", "Polygon/freehand region needs at least three points.", member);
                    else
                    {
                        foreach (ImagePoint point in region.Points)
                        {
                            if (!point.IsFinite)
                            {
                                result.Add(ValidationSeverity.Error, "REGION_POINT_INVALID", "Region contains a non-finite point.", member);
                                break;
                            }
                        }
                    }
                    break;
            }
        }

        private static void ValidateOptions(CalibrationSolverOptions options, JobValidationResult result)
        {
            if (options == null)
            {
                result.Add(ValidationSeverity.Error, "OPTIONS_NULL", "Calibration solver options are missing.", "SolverOptions");
                return;
            }

            if (options.MinimumTranslationSamples < 3 || options.MinimumTranslationInliers < 3
                || options.MinimumTranslationInliers > options.MinimumTranslationSamples)
                result.Add(ValidationSeverity.Error, "TRANSLATION_COUNT_INVALID", "Translation sample/inlier limits are invalid.", "SolverOptions");
            if (options.MinimumRotationSamples < 3 || options.MinimumRotationInliers < 3
                || options.MinimumRotationInliers > options.MinimumRotationSamples)
                result.Add(ValidationSeverity.Error, "ROTATION_COUNT_INVALID", "Rotation sample/inlier limits are invalid.", "SolverOptions");
            if (!Positive(options.MinimumAxisTravel) || !Positive(options.RansacInlierThresholdPixels)
                || !Positive(options.MadMultiplier) || !Positive(options.MaxRmsResidualPixels)
                || !Positive(options.MaxResidualPixels) || !Positive(options.MaxConditionNumber))
                result.Add(ValidationSeverity.Error, "TRANSLATION_THRESHOLD_INVALID", "Translation solver thresholds must be positive.", "SolverOptions");
            if (!InRange(options.MinimumMatchScore, 0.0, 1.0))
                result.Add(ValidationSeverity.Error, "SOLVER_SCORE_INVALID", "Solver minimum score must be in [0, 1].", "SolverOptions.MinimumMatchScore");
            if (!Positive(options.MinimumRotationSpanDegrees) || options.MinimumRotationSpanDegrees >= 360.0
                || !Positive(options.AngleOutlierThresholdDegrees) || !Positive(options.MaxAngleRmsDegrees)
                || !Positive(options.CenterOutlierThresholdPixels) || !Positive(options.MaxRotationCenterRmsPixels))
                result.Add(ValidationSeverity.Error, "ROTATION_THRESHOLD_INVALID", "Rotation solver thresholds are invalid.", "SolverOptions");
        }

        private static void ValidateSamples(CalibrationJob job, CalibrationValidationScope scope, JobValidationResult result)
        {
            CalibrationSolverOptions options = job.SolverOptions ?? new CalibrationSolverOptions();
            long revision = job.Template == null ? 0 : job.Template.Revision;
            int translationCount = 0;
            int rotationCount = 0;
            double minX = double.PositiveInfinity;
            double maxX = double.NegativeInfinity;
            double minY = double.PositiveInfinity;
            double maxY = double.NegativeInfinity;
            List<double> rotationAngles = new List<double>();
            HashSet<string> ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (job.Samples != null)
            {
                for (int i = 0; i < job.Samples.Count; i++)
                {
                    CalibrationSample sample = job.Samples[i];
                    if (sample == null)
                    {
                        result.Add(ValidationSeverity.Error, "SAMPLE_NULL", "A sample entry is null.", "Samples[" + i + "]");
                        continue;
                    }

                    if (!string.IsNullOrWhiteSpace(sample.SampleId) && !ids.Add(sample.SampleId))
                        result.Add(ValidationSeverity.Warning, "SAMPLE_ID_DUPLICATE", "Sample identifier is duplicated.", "Samples[" + i + "].SampleId", sample.SampleId);
                    if (!Enum.IsDefined(typeof(CalibrationSampleKind), sample.Kind))
                    {
                        result.Add(ValidationSeverity.Error, "SAMPLE_KIND_INVALID", "Calibration sample kind is unsupported.", "Samples[" + i + "].Kind", sample.SampleId);
                        continue;
                    }
                    if (!sample.Enabled) continue;
                    if (sample.MachinePose == null || !sample.MachinePose.IsFinite || sample.ImagePose == null || !sample.ImagePose.IsFinite)
                    {
                        result.Add(ValidationSeverity.Error, "SAMPLE_POSE_INVALID", "Sample pose contains invalid values.", "Samples[" + i + "]", sample.SampleId);
                        continue;
                    }

                    if (sample.TemplateRevision != revision)
                    {
                        result.Add(ValidationSeverity.Error, "SAMPLE_STALE", "Sample was captured with a different template revision.", "Samples[" + i + "].TemplateRevision", sample.SampleId);
                        continue;
                    }

                    if (!Numeric.IsFinite(sample.MatchScore) || sample.MatchScore < options.MinimumMatchScore)
                    {
                        result.Add(ValidationSeverity.Warning, "SAMPLE_SCORE_LOW", "Sample match score is below the solver threshold.", "Samples[" + i + "].MatchScore", sample.SampleId);
                        continue;
                    }

                    if (sample.Kind == CalibrationSampleKind.Translation)
                    {
                        translationCount++;
                        minX = Math.Min(minX, sample.MachinePose.X);
                        maxX = Math.Max(maxX, sample.MachinePose.X);
                        minY = Math.Min(minY, sample.MachinePose.Y);
                        maxY = Math.Max(maxY, sample.MachinePose.Y);
                    }
                    else
                    {
                        rotationCount++;
                        rotationAngles.Add(sample.MachinePose.ThetaDegrees);
                    }
                }
            }

            if (translationCount < options.MinimumTranslationSamples)
                result.Add(ValidationSeverity.Error, "TRANSLATION_SAMPLES_INSUFFICIENT", "Not enough eligible translation samples.", "Samples");
            else if ((maxX - minX) < options.MinimumAxisTravel || (maxY - minY) < options.MinimumAxisTravel)
                result.Add(ValidationSeverity.Error, "TRANSLATION_TRAVEL_INSUFFICIENT", "Translation samples do not cover enough X/Y travel.", "Samples");

            if (scope == CalibrationValidationScope.Full && options.RequireRotationCalibration)
            {
                if (rotationCount < options.MinimumRotationSamples)
                    result.Add(ValidationSeverity.Error, "ROTATION_SAMPLES_INSUFFICIENT", "Not enough eligible rotation samples.", "Samples");
                else
                {
                    double span = CircularSpan(rotationAngles);
                    if (span < options.MinimumRotationSpanDegrees)
                        result.Add(ValidationSeverity.Error, "ROTATION_SPAN_INSUFFICIENT", "Rotation samples do not cover enough angle.", "Samples");
                }
            }
        }

        private static double CircularSpan(IList<double> angles)
        {
            if (angles == null || angles.Count < 2) return 0.0;
            double reference = angles[0];
            double min = reference;
            double max = reference;
            for (int i = 1; i < angles.Count; i++)
            {
                double value = reference + AngleMath.NormalizeDegrees(angles[i] - reference);
                min = Math.Min(min, value);
                max = Math.Max(max, value);
            }

            return max - min;
        }

        private static bool Positive(double value) { return Numeric.IsFinite(value) && value > 0.0; }
        private static bool InRange(double value, double min, double max) { return Numeric.IsFinite(value) && value >= min && value <= max; }
    }
}
