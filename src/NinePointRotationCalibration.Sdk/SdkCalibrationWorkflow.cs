using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NinePointRotationCalibration.Halcon;
using NinePointRotationCalibration.WinForms.Adapters;
using NinePointRotationCalibration.WinForms.Geometry;
using NinePointRotationCalibration.WinForms.Models;
using NinePointRotationCalibration.WinForms.Services;
using UiTemplateBuildResult = NinePointRotationCalibration.WinForms.Models.TemplateBuildResult;

namespace NinePointRotationCalibration
{
    internal sealed class SdkCalibrationWorkflow : ICalibrationWorkflow, ITemplateEditorService, IDisposable
    {
        private readonly ITemplateMatchingEngine _engine;
        private readonly CalibrationJob _baseline;

        public SdkCalibrationWorkflow(ITemplateMatchingEngine engine, CalibrationJob baseline)
        {
            _engine = engine ?? throw new ArgumentNullException(nameof(engine));
            _baseline = baseline == null ? new CalibrationJob() : baseline.DeepClone();
        }

        public ITemplateEditorService TemplateEditorService
        {
            get { return this; }
        }

        public Task<TemplateFeatureResult> ExtractFeaturesAsync(
            Bitmap image,
            TemplateEditorState state,
            CancellationToken cancellationToken)
        {
            return Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                TemplateDefinition template = CoreUiMapper.ToCoreTemplate(state, _baseline.Template, image.Width, image.Height);
                Halcon.TemplateBuildResult build = _engine.BuildTemplate(image, template);
                cancellationToken.ThrowIfCancellationRequested();
                if (!build.Success)
                {
                    return new TemplateFeatureResult { Success = false, Message = build.Message };
                }

                List<List<ImageCoordinate>> contours = ToUiContours(build.ContourSegments);
                return new TemplateFeatureResult
                {
                    Success = true,
                    Message = "模板特征提取完成。",
                    Contours = contours,
                    FeaturePoints = SampleFeaturePoints(contours, 4000),
                    RecommendedParameters = state.Parameters == null ? null : state.Parameters.DeepClone()
                };
            }, cancellationToken);
        }

        public Task<UiTemplateBuildResult> CreateModelAsync(
            Bitmap image,
            TemplateEditorState state,
            CancellationToken cancellationToken)
        {
            return Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                TemplateDefinition template = CoreUiMapper.ToCoreTemplate(state, _baseline.Template, image.Width, image.Height);
                Halcon.TemplateBuildResult build = _engine.BuildTemplate(image, template);
                cancellationToken.ThrowIfCancellationRequested();
                if (!build.Success || build.Template == null)
                {
                    return new UiTemplateBuildResult { Success = false, Message = build.Message };
                }

                TemplateDefinition output = build.Template;
                return new UiTemplateBuildResult
                {
                    Success = true,
                    Message = build.Message,
                    ModelData = output.ModelData == null ? null : (byte[])output.ModelData.Clone(),
                    ModelFormat = output.ModelType == TemplateModelType.Shape ? "shm" : "ncm",
                    ModelHash = output.ModelHash,
                    DomainCenter = ToUiPoint(output.DomainCenter),
                    ModelReferencePosition = output.ModelReference == null
                        ? default(ImageCoordinate)
                        : new ImageCoordinate(output.ModelReference.Row, output.ModelReference.Column),
                    ModelReferenceAngleDegrees = output.ModelReference == null ? 0.0 : output.ModelReference.AngleDegrees,
                    ReferenceImageWidth = output.ReferenceImageWidth,
                    ReferenceImageHeight = output.ReferenceImageHeight,
                    HalconVersion = output.HalconVersion,
                    Contours = ToUiContours(build.ContourSegments)
                };
            }, cancellationToken);
        }

        public Task<TemplateTestResult> TestMatchAsync(
            Bitmap image,
            TemplateEditorState state,
            CancellationToken cancellationToken)
        {
            return Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                TemplateDefinition template = CoreUiMapper.ToCoreTemplate(state, _baseline.Template, image.Width, image.Height);
                TemplateMatchResult match = _engine.Locate(image, template, true);
                cancellationToken.ThrowIfCancellationRequested();
                return CoreUiMapper.FromCoreTemplateTest(match);
            }, cancellationToken);
        }

        public Task<CalibrationLocateUiResult> LocateAsync(
            Bitmap image,
            TemplateEditorState template,
            CancellationToken cancellationToken)
        {
            return Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                TemplateDefinition coreTemplate = CoreUiMapper.ToCoreTemplate(template, _baseline.Template, image.Width, image.Height);
                TemplateMatchResult match = _engine.Locate(image, coreTemplate, true);
                cancellationToken.ThrowIfCancellationRequested();
                return CoreUiMapper.FromCoreMatch(match);
            }, cancellationToken);
        }

        public Task<CalibrationCalculationUiResult> CalculateAsync(
            IReadOnlyList<CalibrationSampleRow> samples,
            TemplateEditorState template,
            CancellationToken cancellationToken)
        {
            return Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                CalibrationSetupState state = CoreUiMapper.FromCoreJob(_baseline);
                state.Template = template == null ? new TemplateEditorState() : template.DeepClone();
                state.Samples = (samples ?? new List<CalibrationSampleRow>())
                    .Where(item => item != null)
                    .Select(item => item.DeepClone())
                    .ToList();
                CalibrationJob job = CoreUiMapper.ToCoreJob(state, _baseline);
                long templateRevision = job.Template == null ? 0 : job.Template.Revision;
                job.SolverOptions.RequireRotationCalibration = job.Samples.Any(
                    item => item != null
                        && item.Enabled
                        && item.Kind == CalibrationSampleKind.Rotation
                        && item.TemplateRevision == templateRevision);
                CalibrationResult result = CalibrationSolver.Solve(job);
                cancellationToken.ThrowIfCancellationRequested();

                List<CalibrationSampleRow> updated = state.Samples.Select(item => item.DeepClone()).ToList();
                CoreUiMapper.ApplyDiagnostics(updated, result);
                return new CalibrationCalculationUiResult
                {
                    Success = result.Success,
                    Message = result.Message,
                    Result = CoreUiMapper.FromCoreResult(result),
                    UpdatedSamples = updated
                };
            }, cancellationToken);
        }

        public void Dispose()
        {
            // The SDK owns the shared engine; closing a dialog must not dispose it.
        }

        private static ImageCoordinate ToUiPoint(ImagePoint point)
        {
            return new ImageCoordinate(point.Row, point.Column);
        }

        private static List<List<ImageCoordinate>> ToUiContours(IEnumerable<List<ImagePoint>> contours)
        {
            return (contours ?? Enumerable.Empty<List<ImagePoint>>())
                .Where(segment => segment != null)
                .Select(segment => segment.Select(ToUiPoint).ToList())
                .ToList();
        }

        private static List<ImageCoordinate> SampleFeaturePoints(
            IList<List<ImageCoordinate>> contours,
            int maximumCount)
        {
            List<ImageCoordinate> all = (contours ?? new List<List<ImageCoordinate>>())
                .Where(segment => segment != null)
                .SelectMany(segment => segment)
                .ToList();
            if (all.Count <= maximumCount) return all;

            List<ImageCoordinate> sampled = new List<ImageCoordinate>(maximumCount);
            double step = (double)all.Count / maximumCount;
            for (int index = 0; index < maximumCount; index++)
                sampled.Add(all[(int)Math.Floor(index * step)]);
            return sampled;
        }
    }
}
