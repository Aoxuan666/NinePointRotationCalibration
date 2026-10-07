using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using NinePointRotationCalibration.Halcon;
using NinePointRotationCalibration.WinForms.Adapters;
using NinePointRotationCalibration.WinForms.Forms;
using NinePointRotationCalibration.WinForms.Models;

namespace NinePointRotationCalibration
{
    /// <summary>
    /// 九点加旋转标定 SDK 入口。上位机负责运动、停稳和图像采集。
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public sealed class NinePointRotationCalibrationSdk : IDisposable
    {
        private readonly ITemplateMatchingEngine _templateEngine;
        private readonly bool _ownsTemplateEngine;
        private bool _disposed;

        /// <summary>使用内置 HALCON 模板引擎创建 SDK。</summary>
        public NinePointRotationCalibrationSdk()
            : this(new HalconTemplateEngine(), true)
        {
        }

        /// <summary>使用外部模板引擎创建 SDK；释放 SDK 时不会释放该引擎。</summary>
        /// <param name="templateEngine">模板匹配引擎。</param>
        public NinePointRotationCalibrationSdk(ITemplateMatchingEngine templateEngine)
            : this(templateEngine, false)
        {
        }

        private NinePointRotationCalibrationSdk(ITemplateMatchingEngine templateEngine, bool ownsTemplateEngine)
        {
            _templateEngine = templateEngine ?? throw new ArgumentNullException(nameof(templateEngine));
            _ownsTemplateEngine = ownsTemplateEngine;
        }

        /// <summary>根据参考图和模板参数创建 HALCON 模型。</summary>
        /// <param name="referenceImage">模板参考图。</param>
        /// <param name="definition">ROI、掩膜和建模参数。</param>
        /// <returns>包含模型数据和轮廓的创建结果。</returns>
        public Halcon.TemplateBuildResult BuildTemplate(Bitmap referenceImage, TemplateDefinition definition)
        {
            ThrowIfDisposed();
            if (referenceImage == null)
                return Halcon.TemplateBuildResult.Failure(CalibrationErrorCode.ParameterInvalid, "Reference image is null.");
            if (definition == null)
                return Halcon.TemplateBuildResult.Failure(CalibrationErrorCode.ParameterInvalid, "Template definition is null.");
            try
            {
                Halcon.TemplateBuildResult result = _templateEngine.BuildTemplate(referenceImage, definition.DeepClone());
                return result ?? Halcon.TemplateBuildResult.Failure(
                    CalibrationErrorCode.InternalError,
                    "Template engine returned no build result.");
            }
            catch (Exception exception)
            {
                return Halcon.TemplateBuildResult.Failure(
                    MapEngineException(exception),
                    "Template build failed: " + exception.Message);
            }
        }

        /// <summary>使用 Job 中的模板定位一个目标。</summary>
        /// <param name="image">待定位图像。</param>
        /// <param name="job">包含有效模板的标定 Job。</param>
        /// <param name="returnOverlay">是否返回匹配轮廓。</param>
        /// <returns>匹配位置、角度、分数和参考中心。</returns>
        public TemplateMatchResult Locate(Bitmap image, CalibrationJob job, bool returnOverlay = false)
        {
            ThrowIfDisposed();
            if (image == null)
                return TemplateMatchResult.Failure(CalibrationErrorCode.ParameterInvalid, "Input image is null.");
            if (job == null || job.Template == null)
                return TemplateMatchResult.Failure(CalibrationErrorCode.TemplateMissing, "Calibration job or template is missing.");
            try
            {
                TemplateMatchResult result = _templateEngine.Locate(image, job.Template.DeepClone(), returnOverlay);
                return result ?? TemplateMatchResult.Failure(
                    CalibrationErrorCode.InternalError,
                    "Template engine returned no match result.");
            }
            catch (Exception exception)
            {
                return TemplateMatchResult.Failure(
                    MapEngineException(exception),
                    "Template locate failed: " + exception.Message);
            }
        }

        /// <summary>使用 Job 中的模板定位多个目标。</summary>
        /// <param name="image">待定位图像。</param>
        /// <param name="job">包含有效模板的标定 Job。</param>
        /// <param name="returnOverlay">是否返回匹配轮廓。</param>
        /// <returns>按分数排序的匹配结果集合。</returns>
        public Halcon.TemplateMatchCollectionResult LocateAll(Bitmap image, CalibrationJob job, bool returnOverlay = false)
        {
            ThrowIfDisposed();
            if (image == null)
                return Halcon.TemplateMatchCollectionResult.Failure(CalibrationErrorCode.ParameterInvalid, "Input image is null.");
            if (job == null || job.Template == null)
                return Halcon.TemplateMatchCollectionResult.Failure(CalibrationErrorCode.TemplateMissing, "Calibration job or template is missing.");
            try
            {
                Halcon.TemplateMatchCollectionResult result = _templateEngine.LocateAll(image, job.Template.DeepClone(), returnOverlay);
                return result ?? Halcon.TemplateMatchCollectionResult.Failure(
                    CalibrationErrorCode.InternalError,
                    "Template engine returned no collection result.");
            }
            catch (Exception exception)
            {
                return Halcon.TemplateMatchCollectionResult.Failure(
                    MapEngineException(exception),
                    "Template locate-all failed: " + exception.Message);
            }
        }

        /// <summary>定位图像并向 Job 追加一个标定样本。</summary>
        /// <param name="job">当前 Job；方法不会修改该对象。</param>
        /// <param name="image">当前位置拍摄的图像。</param>
        /// <param name="machineX">机械 X 坐标。</param>
        /// <param name="machineY">机械 Y 坐标。</param>
        /// <param name="machineThetaDegrees">机械角度，单位为度。</param>
        /// <param name="tag">点位名称，可为空。</param>
        /// <param name="kind">平移点或旋转点。</param>
        /// <returns>采点结果；成功后必须用返回的 Job 替换当前 Job。</returns>
        public SampleCaptureResult CaptureSample(
            CalibrationJob job,
            Bitmap image,
            double machineX,
            double machineY,
            double machineThetaDegrees = 0.0,
            string tag = null,
            CalibrationSampleKind kind = CalibrationSampleKind.Translation)
        {
            ThrowIfDisposed();
            if (job == null)
                return SampleCaptureResult.Failure(CalibrationErrorCode.ParameterInvalid, "Calibration job is null.");
            if (!IsFinite(machineX) || !IsFinite(machineY) || !IsFinite(machineThetaDegrees))
                return SampleCaptureResult.Failure(CalibrationErrorCode.ParameterInvalid, "Machine pose contains a non-finite value.", job);

            TemplateMatchResult match = Locate(image, job, false);
            if (match == null)
                return SampleCaptureResult.Failure(
                    CalibrationErrorCode.InternalError,
                    "Template engine returned no match result.",
                    job);
            if (!match.Success)
                return SampleCaptureResult.Failure(match.ErrorCode, match.Message, job, match);
            return AddSampleFromMatch(job, match, machineX, machineY, machineThetaDegrees, tag, kind);
        }

        /// <summary>复用已有匹配结果向 Job 追加样本，避免重复定位。</summary>
        /// <param name="job">当前 Job；方法不会修改该对象。</param>
        /// <param name="match">已成功的模板匹配结果。</param>
        /// <param name="machineX">机械 X 坐标。</param>
        /// <param name="machineY">机械 Y 坐标。</param>
        /// <param name="machineThetaDegrees">机械角度，单位为度。</param>
        /// <param name="tag">点位名称，可为空。</param>
        /// <param name="kind">平移点或旋转点。</param>
        /// <returns>采点结果；成功后必须用返回的 Job 替换当前 Job。</returns>
        public SampleCaptureResult AddSampleFromMatch(
            CalibrationJob job,
            TemplateMatchResult match,
            double machineX,
            double machineY,
            double machineThetaDegrees = 0.0,
            string tag = null,
            CalibrationSampleKind kind = CalibrationSampleKind.Translation)
        {
            ThrowIfDisposed();
            if (job == null)
                return SampleCaptureResult.Failure(CalibrationErrorCode.ParameterInvalid, "Calibration job is null.");
            if (!Enum.IsDefined(typeof(CalibrationSampleKind), kind))
                return SampleCaptureResult.Failure(CalibrationErrorCode.ParameterInvalid, "Calibration sample kind is unsupported.", job, match);
            if (match == null || !match.Success)
            {
                CalibrationErrorCode code = match == null ? CalibrationErrorCode.TemplateMatchFailed : match.ErrorCode;
                return SampleCaptureResult.Failure(code, match == null ? "Template match result is missing." : match.Message, job, match);
            }
            if (!match.Anchor.IsFinite || match.ModelPose == null || !match.ModelPose.IsFinite || !IsFinite(match.Score))
                return SampleCaptureResult.Failure(CalibrationErrorCode.ParameterInvalid, "Template match result contains invalid values.", job, match);
            if (!IsFinite(machineX) || !IsFinite(machineY) || !IsFinite(machineThetaDegrees))
                return SampleCaptureResult.Failure(CalibrationErrorCode.ParameterInvalid, "Machine pose contains a non-finite value.", job, match);

            CalibrationJob updated = job.DeepClone();
            CalibrationSample sample = new CalibrationSample
            {
                Kind = kind,
                MachinePose = new MachinePose(machineX, machineY, machineThetaDegrees),
                ImagePose = new ImagePose(match.Anchor.Row, match.Anchor.Column, match.AngleDegrees),
                MatchScore = match.Score,
                Tag = tag,
                Enabled = true
            };
            CalibrationSample added = updated.AddSample(sample);
            return new SampleCaptureResult
            {
                Success = true,
                ErrorCode = CalibrationErrorCode.None,
                Message = kind == CalibrationSampleKind.Rotation ? "Rotation sample added." : "Translation sample added.",
                Job = updated,
                Sample = added.DeepClone(),
                Match = match.DeepClone()
            };
        }

        /// <summary>
        /// 定位并追加样本的简化接口；失败时抛出异常，生产流程优先使用 CaptureSample。
        /// </summary>
        /// <param name="job">当前 Job。</param>
        /// <param name="image">当前位置拍摄的图像。</param>
        /// <param name="machineX">机械 X 坐标。</param>
        /// <param name="machineY">机械 Y 坐标。</param>
        /// <param name="machineThetaDegrees">机械角度，单位为度。</param>
        /// <param name="tag">点位名称，可为空。</param>
        /// <param name="kind">平移点或旋转点。</param>
        /// <returns>追加样本后的新 Job。</returns>
        public CalibrationJob AddSample(
            CalibrationJob job,
            Bitmap image,
            double machineX,
            double machineY,
            double machineThetaDegrees = 0.0,
            string tag = null,
            CalibrationSampleKind kind = CalibrationSampleKind.Translation)
        {
            SampleCaptureResult result = CaptureSample(job, image, machineX, machineY, machineThetaDegrees, tag, kind);
            if (!result.Success)
                throw new InvalidOperationException(result.ErrorCode + ": " + result.Message);
            return result.Job;
        }

        /// <summary>按样本 ID 删除一个点，返回新 Job。</summary>
        /// <param name="job">当前 Job。</param>
        /// <param name="sampleId">待删除的样本 ID。</param>
        /// <returns>删除后的新 Job。</returns>
        public CalibrationJob RemoveSample(CalibrationJob job, string sampleId)
        {
            ThrowIfDisposed();
            if (job == null) throw new ArgumentNullException(nameof(job));
            CalibrationJob updated = job.DeepClone();
            if (updated.Samples != null)
                updated.Samples.RemoveAll(item => item != null && string.Equals(item.SampleId, sampleId, StringComparison.OrdinalIgnoreCase));
            updated.UpdatedAtUtc = DateTime.UtcNow;
            return updated;
        }

        /// <summary>清空全部样本或指定类型的样本，返回新 Job。</summary>
        /// <param name="job">当前 Job。</param>
        /// <param name="kind">为空时清空全部，否则只清空指定类型。</param>
        /// <returns>清理后的新 Job。</returns>
        public CalibrationJob ClearSamples(CalibrationJob job, CalibrationSampleKind? kind = null)
        {
            ThrowIfDisposed();
            if (job == null) throw new ArgumentNullException(nameof(job));
            CalibrationJob updated = job.DeepClone();
            if (kind.HasValue)
                updated.Samples.RemoveAll(item => item != null && item.Kind == kind.Value);
            else
                updated.Samples.Clear();
            updated.UpdatedAtUtc = DateTime.UtcNow;
            return updated;
        }

        /// <summary>根据当前 Job 计算 XY 仿射矩阵和旋转标定结果。</summary>
        /// <param name="job">包含有效模板和标定样本的 Job。</param>
        /// <returns>矩阵、旋转中心、残差和诊断结果。</returns>
        public CalibrationResult Calculate(CalibrationJob job)
        {
            ThrowIfDisposed();
            try
            {
                return CalibrationSolver.Solve(job == null ? null : job.DeepClone());
            }
            catch (Exception exception)
            {
                return CalibrationResult.Failure(
                    CalibrationErrorCode.InternalError,
                    "Calibration calculation failed: " + exception.Message);
            }
        }

        /// <summary>校验模板、样本数量、空间分布和求解参数。</summary>
        /// <param name="job">待校验 Job。</param>
        /// <param name="scope">校验范围。</param>
        /// <returns>包含错误和警告的校验结果。</returns>
        public JobValidationResult Validate(
            CalibrationJob job,
            CalibrationValidationScope scope = CalibrationValidationScope.Full)
        {
            ThrowIfDisposed();
            try
            {
                return CalibrationJobValidator.Validate(job == null ? null : job.DeepClone(), scope);
            }
            catch (Exception exception)
            {
                JobValidationResult result = new JobValidationResult();
                result.Add(ValidationSeverity.Error, "VALIDATION_INTERNAL_ERROR",
                    "Calibration validation failed: " + exception.Message);
                return result;
            }
        }

        /// <summary>将 Job 保存为 .nprcal 文件；失败时抛出异常。</summary>
        /// <param name="path">目标文件路径。</param>
        /// <param name="job">待保存 Job。</param>
        public void SaveJob(string path, CalibrationJob job)
        {
            ThrowIfDisposed();
            JobPackageSerializer.Save(path, job);
        }

        /// <summary>尝试保存 Job，并以结构化结果返回错误。</summary>
        /// <param name="path">目标文件路径。</param>
        /// <param name="job">待保存 Job。</param>
        /// <returns>保存状态和错误信息。</returns>
        public JobPersistenceResult TrySaveJob(string path, CalibrationJob job)
        {
            ThrowIfDisposed();
            return JobPackageSerializer.TrySave(path, job);
        }

        /// <summary>从 .nprcal 文件加载 Job；失败时抛出异常。</summary>
        /// <param name="path">Job 文件路径。</param>
        /// <returns>加载并完成完整性校验的 Job。</returns>
        public CalibrationJob LoadJob(string path)
        {
            ThrowIfDisposed();
            return JobPackageSerializer.Load(path);
        }

        /// <summary>尝试加载 Job，并以结构化结果返回错误。</summary>
        /// <param name="path">Job 文件路径。</param>
        /// <returns>加载状态；成功时 Job 属性有效。</returns>
        public JobPersistenceResult TryLoadJob(string path)
        {
            ThrowIfDisposed();
            return JobPackageSerializer.TryLoad(path);
        }

        /// <summary>打开完整标定配置窗口。必须从 STA UI 线程调用。</summary>
        /// <param name="referenceImage">参考图像。</param>
        /// <param name="currentJob">已有 Job；为空时创建新 Job。</param>
        /// <param name="owner">父窗口。</param>
        /// <returns>确认时返回更新后的 Job，取消时返回原 Job 的副本。</returns>
        public CalibrationSetupResult OpenSetupDialog(
            Bitmap referenceImage,
            CalibrationJob currentJob = null,
            IWin32Window owner = null)
        {
            ThrowIfDisposed();
            if (referenceImage == null)
                return CalibrationSetupResult.Failure(CalibrationErrorCode.ParameterInvalid, "Reference image is null.");
            if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
            {
                return CalibrationSetupResult.Failure(
                    CalibrationErrorCode.ParameterInvalid,
                    "OpenSetupDialog must be called from an STA thread. WinForms UI threads are STA by default.");
            }

            CalibrationJob baseline = currentJob == null ? new CalibrationJob() : currentJob.DeepClone();
            try
            {
                CalibrationSetupState initial = CoreUiMapper.FromCoreJob(baseline);
                using (SdkCalibrationWorkflow workflow = new SdkCalibrationWorkflow(_templateEngine, baseline))
                using (CalibrationSetupForm form = new CalibrationSetupForm(referenceImage, initial, workflow))
                {
                    DialogResult dialogResult = owner == null ? form.ShowDialog() : form.ShowDialog(owner);
                    if (dialogResult != DialogResult.OK || form.ResultState == null)
                        return CalibrationSetupResult.Cancelled(baseline);

                    CalibrationJob updated = CoreUiMapper.ToCoreJob(
                        form.ResultState,
                        baseline,
                        referenceImage.Width,
                        referenceImage.Height);
                    return new CalibrationSetupResult
                    {
                        Accepted = true,
                        ErrorCode = CalibrationErrorCode.None,
                        Message = "Calibration setup accepted.",
                        Job = updated
                    };
                }
            }
            catch (Exception exception)
            {
                return CalibrationSetupResult.Failure(CalibrationErrorCode.InternalError, "Failed to open calibration setup: " + exception.Message);
            }
        }

        /// <summary>只打开模板编辑窗口。必须从 STA UI 线程调用。</summary>
        /// <param name="referenceImage">模板参考图。</param>
        /// <param name="currentTemplate">已有模板；为空时创建新模板。</param>
        /// <param name="owner">父窗口。</param>
        /// <returns>确认时返回更新后的模板。</returns>
        public TemplateSetupResult OpenTemplateDialog(
            Bitmap referenceImage,
            TemplateDefinition currentTemplate = null,
            IWin32Window owner = null)
        {
            ThrowIfDisposed();
            if (referenceImage == null)
                return TemplateSetupResult.Failure(CalibrationErrorCode.ParameterInvalid, "Reference image is null.");
            if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
            {
                return TemplateSetupResult.Failure(
                    CalibrationErrorCode.ParameterInvalid,
                    "OpenTemplateDialog must be called from an STA thread. WinForms UI threads are STA by default.");
            }

            TemplateDefinition baseline = currentTemplate == null
                ? new TemplateDefinition()
                : currentTemplate.DeepClone();
            CalibrationJob workflowJob = new CalibrationJob { Template = baseline.DeepClone() };
            try
            {
                TemplateEditorState initial = CoreUiMapper.FromCoreTemplate(baseline);
                using (SdkCalibrationWorkflow workflow = new SdkCalibrationWorkflow(_templateEngine, workflowJob))
                using (TemplateEditorForm form = new TemplateEditorForm(referenceImage, initial, workflow.TemplateEditorService))
                {
                    DialogResult dialogResult = owner == null ? form.ShowDialog() : form.ShowDialog(owner);
                    if (dialogResult != DialogResult.OK || form.ResultState == null)
                        return TemplateSetupResult.Cancelled(baseline);

                    TemplateDefinition updated = CoreUiMapper.ToCoreTemplate(
                        form.ResultState,
                        baseline,
                        referenceImage.Width,
                        referenceImage.Height);
                    return new TemplateSetupResult
                    {
                        Accepted = true,
                        ErrorCode = CalibrationErrorCode.None,
                        Message = "Template setup accepted.",
                        Template = updated
                    };
                }
            }
            catch (Exception exception)
            {
                return TemplateSetupResult.Failure(CalibrationErrorCode.InternalError, "Failed to open template setup: " + exception.Message);
            }
        }

        /// <summary>清除已加载的 HALCON 模型缓存。</summary>
        public void ClearTemplateCache()
        {
            ThrowIfDisposed();
            _templateEngine.ClearCache();
        }

        /// <summary>释放 SDK 持有的 HALCON 资源。</summary>
        public void Dispose()
        {
            if (_disposed) return;
            if (_ownsTemplateEngine) _templateEngine.Dispose();
            _disposed = true;
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(NinePointRotationCalibrationSdk));
        }

        private static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }

        private static CalibrationErrorCode MapEngineException(Exception exception)
        {
            if (exception is ArgumentException || exception is ArgumentOutOfRangeException)
                return CalibrationErrorCode.ParameterInvalid;
            if (exception is InvalidOperationException)
                return CalibrationErrorCode.HalconRuntimeError;
            return CalibrationErrorCode.InternalError;
        }
    }
}
