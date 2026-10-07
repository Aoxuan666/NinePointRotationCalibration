using System;
using System.Collections.Generic;
using System.Linq;
using Core = NinePointRotationCalibration;
using NinePointRotationCalibration.WinForms.Geometry;
using NinePointRotationCalibration.WinForms.Models;

namespace NinePointRotationCalibration.WinForms.Adapters
{
    /// <summary>
    /// Converts the UI editing models to the stable Core DTOs used by the SDK.
    /// Pass the original Core object as the baseline when updating so fields that
    /// are not editable in the UI (job identity, solver limits and HALCON metadata)
    /// remain unchanged.
    /// </summary>
    public static class CoreUiMapper
    {
        public static TemplateEditorState FromCoreTemplate(Core.TemplateDefinition template)
        {
            TemplateEditorState state = new TemplateEditorState();
            if (template == null)
            {
                return state;
            }

            state.TemplateRoi = FromCoreRegion(template.TemplateRoi);
            state.SearchRoi = FromCoreRegion(template.SearchRegion);
            state.ReferenceAnchor = FromCorePoint(template.ReferenceAnchor);
            state.AnchorLocked = template.IsReferenceAnchorLocked;
            state.MaskInverted = template.IsMaskInverted;
            state.ModelData = template.ModelData == null ? null : (byte[])template.ModelData.Clone();
            state.ModelHash = template.ModelHash;
            state.ModelFormat = template.ModelType == Core.TemplateModelType.Shape ? "shm" : "ncm";
            state.DomainCenter = FromCorePoint(template.DomainCenter);
            Core.ImagePose modelReference = template.ModelReference ?? new Core.ImagePose();
            state.ModelReferencePosition = new ImageCoordinate(modelReference.Row, modelReference.Column);
            state.ModelReferenceAngleDegrees = modelReference.AngleDegrees;
            state.ReferenceImageWidth = template.ReferenceImageWidth;
            state.ReferenceImageHeight = template.ReferenceImageHeight;
            state.HalconVersion = template.HalconVersion;
            state.Revision = template.Revision;
            state.ModelDirty = template.IsModelDirty;

            Core.ShapeModelParameters shape = template.ShapeParameters ?? new Core.ShapeModelParameters();
            Core.NccModelParameters ncc = template.NccParameters ?? new Core.NccModelParameters();
            Core.MatchParameters match = template.MatchParameters ?? new Core.MatchParameters();
            bool shapeModel = template.ModelType == Core.TemplateModelType.Shape;
            state.Parameters.ModelType = template.ModelType;
            state.Parameters.NumLevels = shapeModel ? shape.NumLevels : ncc.NumLevels;
            state.Parameters.AngleStartDegrees = RadiansToDegrees(shapeModel ? shape.AngleStartRad : ncc.AngleStartRad);
            state.Parameters.AngleExtentDegrees = RadiansToDegrees(shapeModel ? shape.AngleExtentRad : ncc.AngleExtentRad);
            state.Parameters.AngleStepDegrees = RadiansToDegrees(shapeModel ? shape.AngleStepRad : ncc.AngleStepRad);
            state.Parameters.Optimization = shape.Optimization;
            state.Parameters.Metric = shapeModel ? shape.Metric : ncc.Metric;
            state.Parameters.Contrast = shape.Contrast;
            state.Parameters.MinimumContrast = shape.MinContrast;
            state.Parameters.MinimumScore = match.MinScore;
            state.Parameters.Greediness = match.Greediness;
            state.Parameters.MaximumOverlap = match.MaxOverlap;
            state.Parameters.MatchCount = match.NumMatches;
            state.Parameters.SubPixel = match.SubPixel;
            state.Parameters.TimeoutMilliseconds = match.TimeoutMilliseconds;
            state.Parameters.ContourPointSpacingPixels = match.ContourPointSpacingPixels > 0.0
                ? match.ContourPointSpacingPixels
                : 3.0;

            state.MaskStrokes = new List<MaskStroke>();
            if (template.MaskRegions != null)
            {
                foreach (Core.TemplateMaskShape mask in template.MaskRegions)
                {
                    if (mask == null || mask.Region == null || mask.Region.Kind != Core.RegionKind.Freehand)
                    {
                        continue;
                    }

                    state.MaskStrokes.Add(new MaskStroke
                    {
                        IsErase = mask.Operation == Core.MaskOperation.Exclude,
                        Radius = Math.Max(1.0, mask.Region.Radius),
                        Points = ToUiPoints(mask.Region.Points)
                    });
                }
            }

            if (template.EraseStrokes != null)
            {
                foreach (Core.TemplateEraseStroke stroke in template.EraseStrokes.Where(item => item != null))
                {
                    state.MaskStrokes.Add(new MaskStroke
                    {
                        IsErase = stroke.Operation == Core.MaskOperation.Exclude,
                        Radius = stroke.Radius,
                        Points = ToUiPoints(stroke.Points)
                    });
                }
            }

            return state;
        }

        public static Core.TemplateDefinition ToCoreTemplate(
            TemplateEditorState state,
            Core.TemplateDefinition baseline = null,
            int referenceImageWidth = 0,
            int referenceImageHeight = 0)
        {
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            Core.TemplateDefinition template = baseline == null
                ? new Core.TemplateDefinition()
                : baseline.DeepClone();
            TemplateEditorParameters parameters = state.Parameters ?? new TemplateEditorParameters();
            template.ModelType = parameters.ModelType;
            template.TemplateRoi = ToCoreRegion(state.TemplateRoi);
            template.SearchRegion = ToCoreRegion(state.SearchRoi);
            template.ReferenceAnchor = ToCorePoint(state.ReferenceAnchor);
            template.IsReferenceAnchorLocked = state.AnchorLocked;
            template.IsMaskInverted = state.MaskInverted;
            template.ReferenceImageWidth = referenceImageWidth > 0
                ? referenceImageWidth
                : state.ReferenceImageWidth > 0 ? state.ReferenceImageWidth : template.ReferenceImageWidth;
            template.ReferenceImageHeight = referenceImageHeight > 0
                ? referenceImageHeight
                : state.ReferenceImageHeight > 0 ? state.ReferenceImageHeight : template.ReferenceImageHeight;
            template.Revision = Math.Max(1L, state.Revision);
            template.IsModelDirty = state.ModelDirty;
            template.ModelData = state.ModelData == null ? null : (byte[])state.ModelData.Clone();
            template.ModelHash = state.ModelHash;
            template.DomainCenter = ToCorePoint(state.DomainCenter);
            template.ModelReference = new Core.ImagePose(
                state.ModelReferencePosition.Row,
                state.ModelReferencePosition.Column,
                state.ModelReferenceAngleDegrees);
            template.HalconVersion = state.HalconVersion;

            if (template.ShapeParameters == null)
            {
                template.ShapeParameters = new Core.ShapeModelParameters();
            }
            template.ShapeParameters.NumLevels = parameters.NumLevels;
            template.ShapeParameters.AngleStartRad = DegreesToRadians(parameters.AngleStartDegrees);
            template.ShapeParameters.AngleExtentRad = DegreesToRadians(parameters.AngleExtentDegrees);
            template.ShapeParameters.AngleStepRad = DegreesToRadians(parameters.AngleStepDegrees);
            template.ShapeParameters.Optimization = parameters.Optimization;
            template.ShapeParameters.Metric = parameters.Metric;
            template.ShapeParameters.Contrast = (int)Math.Round(parameters.Contrast);
            template.ShapeParameters.MinContrast = (int)Math.Round(parameters.MinimumContrast);

            if (template.NccParameters == null)
            {
                template.NccParameters = new Core.NccModelParameters();
            }
            template.NccParameters.NumLevels = parameters.NumLevels;
            template.NccParameters.AngleStartRad = DegreesToRadians(parameters.AngleStartDegrees);
            template.NccParameters.AngleExtentRad = DegreesToRadians(parameters.AngleExtentDegrees);
            template.NccParameters.AngleStepRad = DegreesToRadians(parameters.AngleStepDegrees);
            template.NccParameters.Metric = parameters.Metric;

            if (template.MatchParameters == null)
            {
                template.MatchParameters = new Core.MatchParameters();
            }
            template.MatchParameters.AngleStartRad = DegreesToRadians(parameters.AngleStartDegrees);
            template.MatchParameters.AngleExtentRad = DegreesToRadians(parameters.AngleExtentDegrees);
            template.MatchParameters.MinScore = parameters.MinimumScore;
            template.MatchParameters.NumMatches = parameters.MatchCount;
            template.MatchParameters.MaxOverlap = parameters.MaximumOverlap;
            template.MatchParameters.SubPixel = parameters.SubPixel;
            template.MatchParameters.NumLevels = parameters.NumLevels;
            template.MatchParameters.Greediness = parameters.Greediness;
            template.MatchParameters.TimeoutMilliseconds = parameters.TimeoutMilliseconds;
            template.MatchParameters.ContourPointSpacingPixels = parameters.ContourPointSpacingPixels;

            // Preserve non-freehand masks that the brush editor cannot alter.
            List<Core.TemplateMaskShape> retainedMasks = (template.MaskRegions ?? new List<Core.TemplateMaskShape>())
                .Where(item => item != null && item.Region != null && item.Region.Kind != Core.RegionKind.Freehand)
                .Select(item => item.DeepClone())
                .ToList();
            template.MaskRegions = retainedMasks;
            template.EraseStrokes = new List<Core.TemplateEraseStroke>();

            foreach (MaskStroke stroke in state.MaskStrokes ?? new List<MaskStroke>())
            {
                if (stroke == null || stroke.Points == null || stroke.Points.Count == 0)
                {
                    continue;
                }

                template.EraseStrokes.Add(new Core.TemplateEraseStroke
                {
                    Operation = stroke.IsErase ? Core.MaskOperation.Exclude : Core.MaskOperation.Include,
                    Radius = Math.Max(1.0, stroke.Radius),
                    Points = ToCorePoints(stroke.Points)
                });
            }

            return template;
        }

        public static CalibrationSetupState FromCoreJob(
            Core.CalibrationJob job,
            Core.CalibrationResult result = null)
        {
            CalibrationSetupState state = new CalibrationSetupState();
            if (job == null)
            {
                if (result != null)
                {
                    state.Result = FromCoreResult(result);
                }
                return state;
            }

            state.Name = job.Name;
            state.LinearUnit = FromCoreLinearUnit(job.LinearUnit, job.CustomLinearUnitName);
            state.Template = FromCoreTemplate(job.Template);
            if (job.SolverOptions != null)
            {
                state.MinimumTranslationSamples = job.SolverOptions.MinimumTranslationSamples;
                state.MinimumRotationSamples = job.SolverOptions.MinimumRotationSamples;
            }
            state.Samples = new List<CalibrationSampleRow>();
            int translationSequence = 0;
            int rotationSequence = 0;
            foreach (Core.CalibrationSample sample in job.Samples ?? new List<Core.CalibrationSample>())
            {
                if (sample == null)
                {
                    continue;
                }
                int sequence = sample.Kind == Core.CalibrationSampleKind.Rotation
                    ? ++rotationSequence
                    : ++translationSequence;
                state.Samples.Add(FromCoreSample(sample, sequence));
            }

            if (result != null)
            {
                state.Result = FromCoreResult(result);
                ApplyDiagnostics(state.Samples, result);
            }
            return state;
        }

        public static Core.CalibrationJob ToCoreJob(
            CalibrationSetupState state,
            Core.CalibrationJob baseline = null,
            int referenceImageWidth = 0,
            int referenceImageHeight = 0)
        {
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            Core.CalibrationJob job = baseline == null ? new Core.CalibrationJob() : baseline.DeepClone();
            job.Name = state.Name;
            ApplyCoreLinearUnit(job, state.LinearUnit);
            job.Template = ToCoreTemplate(state.Template ?? new TemplateEditorState(), job.Template, referenceImageWidth, referenceImageHeight);
            if (job.SolverOptions == null)
            {
                job.SolverOptions = new Core.CalibrationSolverOptions();
            }
            job.SolverOptions.MinimumTranslationSamples = state.MinimumTranslationSamples;
            job.SolverOptions.MinimumRotationSamples = state.MinimumRotationSamples;
            job.Samples = (state.Samples ?? new List<CalibrationSampleRow>())
                .Where(item => item != null)
                .Select(ToCoreSample)
                .ToList();
            job.UpdatedAtUtc = DateTime.UtcNow;
            return job;
        }

        public static CalibrationLocateUiResult FromCoreMatch(Core.TemplateMatchResult match)
        {
            if (match == null)
            {
                return new CalibrationLocateUiResult { Success = false, Message = "模板定位未返回结果。" };
            }

            return new CalibrationLocateUiResult
            {
                Success = match.Success,
                Message = match.Message,
                Position = FromCorePoint(match.Anchor),
                AngleDegrees = match.AngleDegrees,
                Score = match.Score,
                ElapsedMilliseconds = match.ElapsedMilliseconds,
                MatchedContours = ToUiContours(match.ContourSegments, match.Contour)
            };
        }

        public static TemplateTestResult FromCoreTemplateTest(Core.TemplateMatchResult match)
        {
            CalibrationLocateUiResult locate = FromCoreMatch(match);
            return new TemplateTestResult
            {
                Success = locate.Success,
                Message = locate.Message,
                Position = locate.Position,
                AngleDegrees = locate.AngleDegrees,
                Score = locate.Score,
                ElapsedMilliseconds = locate.ElapsedMilliseconds,
                MatchedContours = locate.MatchedContours
            };
        }

        public static CalibrationResultView FromCoreResult(Core.CalibrationResult result)
        {
            if (result == null)
            {
                return null;
            }

            CalibrationResultView view = new CalibrationResultView
            {
                Success = result.Success,
                ErrorCode = result.ErrorCode.ToString(),
                Message = result.Message,
                PixelToStage = ToArray(result.PixelToStageMatrix),
                StageToPixel = ToArray(result.StageToPixelMatrix),
                ResolutionX = result.ResolutionX,
                ResolutionY = result.ResolutionY,
                AxisAngleDegrees = result.AxisAngleDegrees,
                Determinant = result.Determinant,
                ConditionNumber = result.ConditionNumber,
                RmsResidualPixels = result.RmsResidualPixels,
                MaximumResidualPixels = result.MaxResidualPixels,
                InlierCount = result.InlierCount,
                RejectedCount = result.RejectedCount
            };
            if (result.Rotation != null && result.Rotation.IsAvailable)
            {
                view.RotationDirection = (int)result.Rotation.Direction;
                view.RotationOffsetDegrees = result.Rotation.AngleOffsetDegrees;
                view.RotationCenterImage = FromCorePoint(result.Rotation.RotationCenterImage);
                view.RotationCenterStageX = result.Rotation.RotationCenterStage.X;
                view.RotationCenterStageY = result.Rotation.RotationCenterStage.Y;
                view.RotationAngleRmsDegrees = result.Rotation.AngleRmsDegrees;
                view.RotationCenterResidualPixels = result.Rotation.CenterRmsResidualPixels;
            }

            view.Diagnostics = new List<string>();
            foreach (string warning in result.Warnings ?? new List<string>())
            {
                if (!string.IsNullOrWhiteSpace(warning))
                {
                    view.Diagnostics.Add("警告: " + warning);
                }
            }
            foreach (Core.CalibrationSampleDiagnostic diagnostic in result.Diagnostics ?? new List<Core.CalibrationSampleDiagnostic>())
            {
                if (diagnostic == null)
                {
                    continue;
                }
                view.Diagnostics.Add(string.Format(
                    "{0}: {1}, residual={2:0.###} px{3}",
                    string.IsNullOrWhiteSpace(diagnostic.Tag) ? diagnostic.SampleId : diagnostic.Tag,
                    diagnostic.IsInlier ? "inlier" : "rejected",
                    diagnostic.ResidualPixels,
                    string.IsNullOrWhiteSpace(diagnostic.RejectionReason) ? string.Empty : ", " + diagnostic.RejectionReason));
            }
            return view;
        }

        public static void ApplyDiagnostics(
            IList<CalibrationSampleRow> samples,
            Core.CalibrationResult result)
        {
            if (samples == null || result == null || result.Diagnostics == null)
            {
                return;
            }

            foreach (Core.CalibrationSampleDiagnostic diagnostic in result.Diagnostics.Where(item => item != null))
            {
                CalibrationSampleRow row = null;
                if (!string.IsNullOrWhiteSpace(diagnostic.SampleId))
                {
                    row = samples.FirstOrDefault(item => item != null && item.SampleId == diagnostic.SampleId);
                }
                if (row == null && diagnostic.SampleIndex >= 0 && diagnostic.SampleIndex < samples.Count)
                {
                    row = samples[diagnostic.SampleIndex];
                }
                if (row == null)
                {
                    continue;
                }
                row.IsInlier = diagnostic.IsInlier;
                row.ResidualPixels = row.Kind == CalibrationUiSampleKind.Rotation && diagnostic.RotationCenterResidualPixels > 0.0
                    ? diagnostic.RotationCenterResidualPixels
                    : diagnostic.ResidualPixels;
                row.Status = diagnostic.IsInlier
                    ? "内点"
                    : string.IsNullOrWhiteSpace(diagnostic.RejectionReason) ? "已剔除" : diagnostic.RejectionReason;
            }
        }

        private static CalibrationSampleRow FromCoreSample(Core.CalibrationSample sample, int sequence)
        {
            Core.MachinePose machine = sample.MachinePose ?? new Core.MachinePose();
            Core.ImagePose image = sample.ImagePose ?? new Core.ImagePose();
            return new CalibrationSampleRow
            {
                SampleId = sample.SampleId,
                CapturedAtUtc = sample.CapturedAtUtc,
                Enabled = sample.Enabled,
                Sequence = sequence,
                Kind = sample.Kind == Core.CalibrationSampleKind.Rotation
                    ? CalibrationUiSampleKind.Rotation
                    : CalibrationUiSampleKind.Translation,
                Tag = sample.Tag,
                MachineX = machine.X,
                MachineY = machine.Y,
                MachineThetaDegrees = machine.ThetaDegrees,
                ImageRow = image.Row,
                ImageColumn = image.Column,
                ImageAngleDegrees = image.AngleDegrees,
                Score = sample.MatchScore,
                IsInlier = true,
                TemplateRevision = sample.TemplateRevision,
                Status = sample.Enabled ? "已采集" : "已禁用"
            };
        }

        private static Core.CalibrationSample ToCoreSample(CalibrationSampleRow row)
        {
            return new Core.CalibrationSample
            {
                SampleId = string.IsNullOrWhiteSpace(row.SampleId) ? Guid.NewGuid().ToString("N") : row.SampleId,
                CapturedAtUtc = row.CapturedAtUtc == default(DateTime) ? DateTime.UtcNow : row.CapturedAtUtc,
                Enabled = row.Enabled,
                Kind = row.Kind == CalibrationUiSampleKind.Rotation
                    ? Core.CalibrationSampleKind.Rotation
                    : Core.CalibrationSampleKind.Translation,
                Tag = row.Tag,
                MachinePose = new Core.MachinePose(row.MachineX, row.MachineY, row.MachineThetaDegrees),
                ImagePose = new Core.ImagePose(row.ImageRow, row.ImageColumn, row.ImageAngleDegrees),
                MatchScore = row.Score,
                TemplateRevision = row.TemplateRevision
            };
        }

        private static RotatedRectangle FromCoreRegion(Core.RegionDefinition region)
        {
            if (region == null)
            {
                return null;
            }
            if (region.Kind == Core.RegionKind.Rectangle1 || region.Kind == Core.RegionKind.Rectangle2)
            {
                return new RotatedRectangle(
                    region.CenterRow,
                    region.CenterColumn,
                    region.Length1,
                    region.Length2,
                    region.Kind == Core.RegionKind.Rectangle2 ? RadiansToDegrees(region.PhiRadians) : 0.0);
            }
            if (region.Kind == Core.RegionKind.Circle)
            {
                return new RotatedRectangle(region.CenterRow, region.CenterColumn, region.Radius, region.Radius, 0.0);
            }
            return BoundsToRectangle(region.Points);
        }

        private static Core.RegionDefinition ToCoreRegion(RotatedRectangle rectangle)
        {
            if (rectangle == null)
            {
                return null;
            }
            return new Core.RegionDefinition
            {
                Kind = Core.RegionKind.Rectangle2,
                CenterRow = rectangle.CenterRow,
                CenterColumn = rectangle.CenterColumn,
                PhiRadians = DegreesToRadians(rectangle.AngleDegrees),
                Length1 = rectangle.HalfWidth,
                Length2 = rectangle.HalfHeight
            };
        }

        private static RotatedRectangle BoundsToRectangle(IList<Core.ImagePoint> points)
        {
            if (points == null || points.Count == 0)
            {
                return null;
            }
            double minRow = points.Min(item => item.Row);
            double maxRow = points.Max(item => item.Row);
            double minColumn = points.Min(item => item.Column);
            double maxColumn = points.Max(item => item.Column);
            return new RotatedRectangle(
                (minRow + maxRow) / 2.0,
                (minColumn + maxColumn) / 2.0,
                Math.Max(1.0, (maxColumn - minColumn) / 2.0),
                Math.Max(1.0, (maxRow - minRow) / 2.0),
                0.0);
        }

        private static ImageCoordinate FromCorePoint(Core.ImagePoint point)
        {
            return new ImageCoordinate(point.Row, point.Column);
        }

        private static Core.ImagePoint ToCorePoint(ImageCoordinate point)
        {
            return new Core.ImagePoint(point.Row, point.Column);
        }

        private static List<ImageCoordinate> ToUiPoints(IEnumerable<Core.ImagePoint> points)
        {
            return (points ?? Enumerable.Empty<Core.ImagePoint>())
                .Select(FromCorePoint)
                .ToList();
        }

        private static List<Core.ImagePoint> ToCorePoints(IEnumerable<ImageCoordinate> points)
        {
            return (points ?? Enumerable.Empty<ImageCoordinate>())
                .Select(ToCorePoint)
                .ToList();
        }

        private static List<List<ImageCoordinate>> ToUiContours(
            IList<List<Core.ImagePoint>> segments,
            IList<Core.ImagePoint> fallback)
        {
            List<List<ImageCoordinate>> result = new List<List<ImageCoordinate>>();
            if (segments != null)
            {
                result.AddRange(segments.Where(item => item != null).Select(ToUiPoints));
            }
            if (result.Count == 0 && fallback != null && fallback.Count > 0)
            {
                result.Add(ToUiPoints(fallback));
            }
            return result;
        }

        private static double[] ToArray(Core.AffineMatrix2D matrix)
        {
            return new[]
            {
                matrix.M11,
                matrix.M12,
                matrix.OffsetX,
                matrix.M21,
                matrix.M22,
                matrix.OffsetY
            };
        }

        private static string FromCoreLinearUnit(Core.LinearUnit unit, string customName)
        {
            switch (unit)
            {
                case Core.LinearUnit.Micrometer:
                    return "um";
                case Core.LinearUnit.Inch:
                    return "inch";
                case Core.LinearUnit.Custom:
                    return string.IsNullOrWhiteSpace(customName) ? "custom" : customName;
                default:
                    return "mm";
            }
        }

        private static void ApplyCoreLinearUnit(Core.CalibrationJob job, string unit)
        {
            switch ((unit ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "um":
                case "µm":
                    job.LinearUnit = Core.LinearUnit.Micrometer;
                    job.CustomLinearUnitName = null;
                    break;
                case "inch":
                case "in":
                    job.LinearUnit = Core.LinearUnit.Inch;
                    job.CustomLinearUnitName = null;
                    break;
                case "mm":
                    job.LinearUnit = Core.LinearUnit.Millimeter;
                    job.CustomLinearUnitName = null;
                    break;
                default:
                    job.LinearUnit = Core.LinearUnit.Custom;
                    job.CustomLinearUnitName = unit;
                    break;
            }
        }

        private static double DegreesToRadians(double degrees)
        {
            return degrees * Math.PI / 180.0;
        }

        private static double RadiansToDegrees(double radians)
        {
            return radians * 180.0 / Math.PI;
        }
    }
}
