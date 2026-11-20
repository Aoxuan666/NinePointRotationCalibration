using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Runtime.InteropServices;
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
    /// <summary>连接 WinForms 编辑模型与 HALCON 模板引擎的异步工作流。</summary>
    internal sealed class SdkCalibrationWorkflow : ICalibrationWorkflow, ITemplateEditorService, IDisposable
    {
        private readonly ITemplateMatchingEngine _engine;
        private readonly ICalibrationGeometryBackend _calibrationGeometryBackend;
        private readonly CalibrationJob _baseline;
        private readonly object _grayCacheSync = new object();
        private HalconGrayImage _cachedGrayImage;
        private Bitmap _cachedSourceBitmap;
        private int _cachedWidth;
        private int _cachedHeight;
        private ulong _cachedSignature;

        public SdkCalibrationWorkflow(ITemplateMatchingEngine engine, CalibrationJob baseline)
        {
            _engine = engine ?? throw new ArgumentNullException(nameof(engine));
            // 界面计算也走 HALCON 几何后端，保证与 SDK 公开入口使用同一套标定底层实现。
            _calibrationGeometryBackend = new HalconCalibrationGeometryBackend();
            _baseline = baseline == null ? new CalibrationJob() : baseline.DeepClone();
        }

        public ITemplateEditorService TemplateEditorService
        {
            get { return this; }
        }

        /// <summary>提取模板轮廓并生成用于界面预览的特征点（回传自适应阈值计算结果）。</summary>
        public Task<TemplateFeatureResult> ExtractFeaturesAsync(
            Bitmap image,
            TemplateEditorState state,
            CancellationToken cancellationToken)
        {
            return Task.Run(() =>
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return new TemplateFeatureResult { Success = false, Message = "已取消" };
                }
                TemplateDefinition template = CoreUiMapper.ToCoreTemplate(state, _baseline.Template, image.Width, image.Height);
                Halcon.TemplateBuildResult build;
                IFeaturePreviewTemplateMatchingEngine previewEngine = _engine as IFeaturePreviewTemplateMatchingEngine;
                if (previewEngine != null)
                {
                    lock (_grayCacheSync)
                    {
                        HalconGrayImage gray = GetOrUpdateGrayCacheLocked(image);
                        build = previewEngine.PreviewFeatures(gray, template);
                    }
                }
                else
                {
                    build = _engine.BuildTemplate(image, template);
                }
                if (cancellationToken.IsCancellationRequested)
                {
                    return new TemplateFeatureResult { Success = false, Message = "已取消" };
                }
                if (!build.Success)
                {
                    return new TemplateFeatureResult { Success = false, Message = build.Message };
                }

                List<List<ImageCoordinate>> contours = ToUiContours(build.ContourSegments);
                return new TemplateFeatureResult
                {
                    Success = true,
                    Message = string.IsNullOrWhiteSpace(build.Message) ? "模板特征提取完成。" : build.Message,
                    Contours = contours,
                    FeaturePoints = SampleFeaturePoints(contours, 4000),
                    RecommendedParameters = MergeResolvedThresholds(state == null ? null : state.Parameters, build.Template)
                };
            });
        }

        /// <summary>根据编辑参数创建 HALCON 模板模型（回传实际生效的自适应阈值参数）。</summary>
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
                    Contours = ToUiContours(build.ContourSegments),
                    ResolvedParameters = MergeResolvedThresholds(state == null ? null : state.Parameters, output)
                };
            }, cancellationToken);
        }

        /// <summary>测试模板匹配，返回定位姿态与变换后的匹配轮廓。</summary>
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

        /// <summary>定位当前图像中的模板并转换为界面结果。</summary>
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

        /// <summary>将界面样本转换为核心任务并执行标定求解。</summary>
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
                CalibrationResult result = CalibrationSolver.Solve(job, _calibrationGeometryBackend);
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
            // 共享引擎由 SDK 统一管理，关闭窗口时不能释放它；仅释放工作流局部持有的灰度缓存。
            lock (_grayCacheSync)
            {
                if (_cachedGrayImage != null)
                {
                    _cachedGrayImage.Dispose();
                    _cachedGrayImage = null;
                }
                _cachedSourceBitmap = null;
                _cachedWidth = 0;
                _cachedHeight = 0;
                _cachedSignature = 0;
            }
        }

        private HalconGrayImage GetOrUpdateGrayCacheLocked(Bitmap image)
        {
            if (_cachedGrayImage != null &&
                ReferenceEquals(image, _cachedSourceBitmap) &&
                _cachedWidth == image.Width &&
                _cachedHeight == image.Height)
            {
                return _cachedGrayImage;
            }

            ulong signature = ComputeBitmapSignature(image);
            if (_cachedGrayImage != null &&
                _cachedWidth == image.Width &&
                _cachedHeight == image.Height &&
                _cachedSignature == signature)
            {
                return _cachedGrayImage;
            }

            if (_cachedGrayImage != null)
            {
                _cachedGrayImage.Dispose();
                _cachedGrayImage = null;
            }

            _cachedGrayImage = HalconGrayImage.FromBitmap(image);
            _cachedSourceBitmap = image;
            _cachedWidth = image.Width;
            _cachedHeight = image.Height;
            _cachedSignature = signature;
            return _cachedGrayImage;
        }

        private static ulong ComputeBitmapSignature(Bitmap image)
        {
            const ulong fnvOffset = 14695981039346656037UL;
            const ulong fnvPrime = 1099511628211UL;
            ulong hash = fnvOffset;
            hash = (hash ^ (ulong)image.Width) * fnvPrime;
            hash = (hash ^ (ulong)image.Height) * fnvPrime;
            hash = (hash ^ (ulong)image.PixelFormat) * fnvPrime;

            int bytesPerPixel = Math.Max(1, Image.GetPixelFormatSize(image.PixelFormat) / 8);
            Rectangle bounds = new Rectangle(0, 0, image.Width, image.Height);
            BitmapData data = image.LockBits(bounds, ImageLockMode.ReadOnly, image.PixelFormat);
            try
            {
                int rows = Math.Min(16, image.Height);
                int cols = Math.Min(16, image.Width);
                for (int r = 0; r < rows; r++)
                {
                    int y = (int)((long)r * image.Height / rows);
                    IntPtr rowPtr = IntPtr.Add(data.Scan0, y * data.Stride);
                    for (int c = 0; c < cols; c++)
                    {
                        int x = (int)((long)c * image.Width / cols);
                        byte sample = Marshal.ReadByte(rowPtr, x * bytesPerPixel);
                        hash = (hash ^ sample) * fnvPrime;
                    }
                }
            }
            finally
            {
                image.UnlockBits(data);
            }

            return hash;
        }

        private static TemplateEditorParameters MergeResolvedThresholds(
            TemplateEditorParameters source,
            TemplateDefinition resolvedTemplate)
        {
            TemplateEditorParameters copy = source == null
                ? new TemplateEditorParameters()
                : source.DeepClone();
            if (resolvedTemplate != null && resolvedTemplate.ShapeParameters != null)
            {
                ShapeModelParameters shape = resolvedTemplate.ShapeParameters;
                copy.Contrast = shape.Contrast;
                copy.MinimumContrast = shape.MinContrast;
                if (shape.FeatureExtraction != null)
                {
                    copy.EdgeLowThreshold = shape.FeatureExtraction.EdgeLowThreshold;
                    copy.EdgeHighThreshold = shape.FeatureExtraction.EdgeHighThreshold;
                }
            }

            return copy;
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
            // 按均匀步长抽样，限制界面特征点预览数量以保证绘制流畅。
            double step = (double)all.Count / maximumCount;
            for (int index = 0; index < maximumCount; index++)
                sampled.Add(all[(int)Math.Floor(index * step)]);
            return sampled;
        }
    }
}
