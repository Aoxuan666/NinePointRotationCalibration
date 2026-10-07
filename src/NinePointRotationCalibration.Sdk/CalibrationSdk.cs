using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using NinePointRotationCalibration.Halcon;

namespace NinePointRotationCalibration
{
    /// <summary>
    /// 面向上位机的简化标定入口：制作模板、添加九点、添加旋转点、生成映射关系。
    /// </summary>
    public sealed class CalibrationSdk : IDisposable
    {
        private readonly NinePointRotationCalibrationSdk _sdk;
        private bool _disposed;

        /// <summary>创建使用内置 HALCON 引擎的简化 SDK。</summary>
        public CalibrationSdk()
        {
            _sdk = new NinePointRotationCalibrationSdk();
        }

        internal CalibrationSdk(ITemplateMatchingEngine templateEngine)
        {
            _sdk = new NinePointRotationCalibrationSdk(
                templateEngine ?? throw new ArgumentNullException(nameof(templateEngine)));
        }

        /// <summary>
        /// 打开模板设置页面。确认后返回带新模板的 Job 并清空旧样本；取消时原样返回。
        /// </summary>
        /// <param name="referenceImage">清晰的模板参考图。</param>
        /// <param name="job">已有 Job；首次示教可为空。</param>
        /// <param name="owner">父窗口，可为空。</param>
        /// <returns>确认后的新 Job；调用方应保存返回值。</returns>
        public CalibrationJob OpenTemplateDialog(Bitmap referenceImage,CalibrationJob job = null,IWin32Window owner = null)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(CalibrationSdk));
            if (referenceImage == null) throw new ArgumentNullException(nameof(referenceImage));

            TemplateSetupResult result = _sdk.OpenTemplateDialog(
                referenceImage,
                job == null ? null : job.Template,
                owner);
            if (!result.Accepted)
            {
                if (result.ErrorCode == CalibrationErrorCode.None)
                    return job;
                throw new InvalidOperationException(string.Format(
                    "模板设置失败 [{0}]：{1}",
                    result.ErrorCode,
                    string.IsNullOrWhiteSpace(result.Message) ? "未知错误" : result.Message));
            }

            if (result.Template == null || result.Template.IsModelDirty ||
                result.Template.ModelData == null || result.Template.ModelData.Length == 0)
            {
                throw new InvalidOperationException("模板尚未创建完成，请在模板页面点击“创建模型”后再确认。");
            }

            CalibrationJob updated = job == null ? new CalibrationJob() : job.DeepClone();
            updated.Template = result.Template.DeepClone();
            updated.Samples.Clear();
            updated.UpdatedAtUtc = DateTime.UtcNow;
            return updated;
        }

        /// <summary>定位图像并添加一个九点标定样本。</summary>
        /// <param name="job">当前 Job。</param>
        /// <param name="image">当前位置拍摄的图像。</param>
        /// <param name="axisX">机械 X 坐标。</param>
        /// <param name="axisY">机械 Y 坐标。</param>
        /// <returns>添加样本后的新 Job；调用方应保存返回值。</returns>
        public CalibrationJob AddSample(CalibrationJob job,Bitmap image,double axisX,double axisY)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(CalibrationSdk));
            if (job == null) throw new ArgumentNullException(nameof(job));
            if (image == null) throw new ArgumentNullException(nameof(image));
            if (job.Template == null || job.Template.IsModelDirty ||
                job.Template.ModelData == null || job.Template.ModelData.Length == 0)
            {
                throw new InvalidOperationException("请先调用 OpenTemplateDialog 创建有效模板。");
            }

            int sampleCount = job.Samples == null
                ? 0
                : job.Samples.Count(item =>
                    item != null && item.Kind == CalibrationSampleKind.Translation);
            SampleCaptureResult result = _sdk.CaptureSample(
                job,
                image,
                axisX,
                axisY,
                0.0,
                "P" + (sampleCount + 1),
                CalibrationSampleKind.Translation);
            if (!result.Success || result.Job == null)
            {
                throw new InvalidOperationException(string.Format(
                    "采点失败 [{0}]：{1}",
                    result.ErrorCode,
                    string.IsNullOrWhiteSpace(result.Message) ? "未知错误" : result.Message));
            }

            return result.Job;
        }

        /// <summary>定位图像并添加一个旋转标定样本。</summary>
        /// <param name="job">当前 Job。</param>
        /// <param name="image">当前角度拍摄的图像。</param>
        /// <param name="axisX">机械 X 坐标。</param>
        /// <param name="axisY">机械 Y 坐标。</param>
        /// <param name="axisAngleDegrees">机械旋转角度，单位为度。</param>
        /// <returns>添加旋转样本后的新 Job；调用方应保存返回值。</returns>
        public CalibrationJob AddRotationSample(CalibrationJob job,Bitmap image,double axisX,double axisY,double axisAngleDegrees)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(CalibrationSdk));
            if (job == null) throw new ArgumentNullException(nameof(job));
            if (image == null) throw new ArgumentNullException(nameof(image));
            if (job.Template == null || job.Template.IsModelDirty ||
                job.Template.ModelData == null || job.Template.ModelData.Length == 0)
            {
                throw new InvalidOperationException("请先调用 OpenTemplateDialog 创建有效模板。");
            }

            int sampleCount = job.Samples == null
                ? 0
                : job.Samples.Count(item =>
                    item != null && item.Kind == CalibrationSampleKind.Rotation);
            SampleCaptureResult result = _sdk.CaptureSample(
                job,
                image,
                axisX,
                axisY,
                axisAngleDegrees,
                "R" + (sampleCount + 1),
                CalibrationSampleKind.Rotation);
            if (!result.Success || result.Job == null)
            {
                throw new InvalidOperationException(string.Format(
                    "旋转采点失败 [{0}]：{1}",
                    result.ErrorCode,
                    string.IsNullOrWhiteSpace(result.Message) ? "未知错误" : result.Message));
            }

            CalibrationJob updated = result.Job;
            if (updated.SolverOptions != null)
                updated.SolverOptions.RequireRotationCalibration = true;
            return updated;
        }

        /// <summary>使用 Job 中累计的九点和旋转点生成映射关系。</summary>
        /// <param name="job">已经完成采点的 Job。</param>
        /// <returns>正逆矩阵、旋转中心、角度关系和残差。</returns>
        public CalibrationResult Calculate(CalibrationJob job)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(CalibrationSdk));
            if (job == null) throw new ArgumentNullException(nameof(job));
            return _sdk.Calculate(job);
        }

        /// <summary>释放 HALCON 模型资源。</summary>
        public void Dispose()
        {
            if (_disposed) return;
            _sdk.Dispose();
            _disposed = true;
        }
    }
}
