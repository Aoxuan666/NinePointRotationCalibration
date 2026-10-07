using System.Collections.Generic;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using NinePointRotationCalibration.WinForms.Models;

namespace NinePointRotationCalibration.WinForms.Services
{
    public interface ITemplateEditorService
    {
        Task<TemplateFeatureResult> ExtractFeaturesAsync(
            Bitmap image,
            TemplateEditorState state,
            CancellationToken cancellationToken);

        Task<TemplateBuildResult> CreateModelAsync(
            Bitmap image,
            TemplateEditorState state,
            CancellationToken cancellationToken);

        Task<TemplateTestResult> TestMatchAsync(
            Bitmap image,
            TemplateEditorState state,
            CancellationToken cancellationToken);
    }

    public interface ICalibrationWorkflow
    {
        ITemplateEditorService TemplateEditorService { get; }

        Task<CalibrationLocateUiResult> LocateAsync(
            Bitmap image,
            TemplateEditorState template,
            CancellationToken cancellationToken);

        Task<CalibrationCalculationUiResult> CalculateAsync(
            IReadOnlyList<CalibrationSampleRow> samples,
            TemplateEditorState template,
            CancellationToken cancellationToken);
    }
}
