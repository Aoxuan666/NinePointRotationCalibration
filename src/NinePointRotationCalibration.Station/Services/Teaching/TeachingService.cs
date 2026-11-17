using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using NinePointRotationCalibration.Halcon;
using NinePointRotationCalibration.Station.Models;
using NinePointRotationCalibration.Station.Services.Inspection;

namespace NinePointRotationCalibration.Station.Services.Teaching
{
    /// <summary>负责左右工位示教采图、模板定位和示教数据保存。</summary>
    public sealed class TeachingService : IDisposable
    {
        private readonly object _sync = new object();
        private readonly NinePointRotationCalibrationSdk _sdk;
        private readonly Func<CancellationToken, Task<CameraFrame>> _captureAsync;
        private readonly Func<CalibrationJob> _getPositioningJob;
        private readonly Func<TeachingData, Task> _saveAsync;
        private readonly SemaphoreSlim _gate;
        private TeachingData _data;
        private bool _disposed;

        public TeachingService(
            NinePointRotationCalibrationSdk sdk,
            Func<CancellationToken, Task<CameraFrame>> captureAsync,
            Func<CalibrationJob> getPositioningJob,
            Func<TeachingData, Task> saveAsync,
            TeachingData initial)
        {
            _sdk = sdk ?? throw new ArgumentNullException(nameof(sdk));
            _captureAsync = captureAsync ?? throw new ArgumentNullException(nameof(captureAsync));
            _getPositioningJob = getPositioningJob ?? throw new ArgumentNullException(nameof(getPositioningJob));
            _saveAsync = saveAsync;
            _gate = new SemaphoreSlim(1, 1);
            _data = Clone(initial ?? new TeachingData());
        }

        public event EventHandler<TeachingEventArgs> TeachingChanged;
        public event EventHandler<FrameEventArgs> FrameCaptured;

        /// <summary>获取当前左、右吸嘴示教数据的线程安全快照。</summary>
        public TeachingData GetSnapshot()
        {
            lock (_sync) return Clone(_data);
        }

        /// <summary>采集一幅图像并使用 HALCON 模板定位生成指定侧的示教姿态。</summary>
        public async Task<TeachingPose> CaptureAsync(
            TeachingSide side,
            MachinePose machinePose,
            CancellationToken cancellationToken)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(TeachingService));
            if (machinePose == null || !machinePose.IsFinite)
                throw new ArgumentException("A finite machine pose is required.", nameof(machinePose));

            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                CalibrationJob job = _getPositioningJob();
                if (job == null || job.Template == null || job.Template.ModelData == null || job.Template.ModelData.Length == 0)
                    throw new InvalidOperationException("Positioning template is not configured.");

                using (CameraFrame frame = await _captureAsync(cancellationToken).ConfigureAwait(false))
                {
                    PublishFrame(frame.Image, "teaching." + side.ToString().ToLowerInvariant());
                    TemplateMatchResult match = await Task.Run(
                        () => LocateReferenceBoard(frame.Image, job), cancellationToken).ConfigureAwait(false);
                    if (match == null || !match.Success)
                        throw new InvalidOperationException(match == null ? "Template locate returned no result." : match.Message);

                    // 记录示教基准时刻的视觉与机械位姿；MachineR 同时作为后续盲摆回正的固定角
                    TeachingPose pose = new TeachingPose
                    {
                        Side = side,
                        IsValid = true,
                        ImageRow = match.Row,
                        ImageColumn = match.Column,
                        ImageAngleDegrees = match.AngleDegrees,
                        MachineX = machinePose.X,
                        MachineY = machinePose.Y,
                        MachineR = machinePose.ThetaDegrees,
                        CapturedAtUtc = DateTime.UtcNow
                    };

                    TeachingData snapshot;
                    lock (_sync)
                    {
                        if (side == TeachingSide.Left) _data.Left = Clone(pose);
                        else _data.Right = Clone(pose);
                        snapshot = Clone(_data);
                    }
                    if (_saveAsync != null) await _saveAsync(snapshot).ConfigureAwait(false);
                    EventHandler<TeachingEventArgs> handler = TeachingChanged;
                    if (handler != null) handler(this, new TeachingEventArgs(pose));
                    return Clone(pose);
                }
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>
        /// 在一拍多板治具中，左右吸嘴示教必须稳定锁定同一块基准板（按从上到下、从左到右排序的第 1 号板），
        /// 防止因不同帧间最高匹配分数跳变导致左、右吸嘴示教锁定在不同板位上。
        /// </summary>
        private TemplateMatchResult LocateReferenceBoard(Bitmap image, CalibrationJob job)
        {
            if (job != null && job.Template != null &&
                job.Template.MatchParameters != null &&
                job.Template.MatchParameters.NumMatches != 1)
            {
                TemplateMatchCollectionResult collection = _sdk.LocateAll(image, job, false);
                if (collection != null && collection.Success &&
                    collection.Matches != null && collection.Matches.Count > 0)
                {
                    List<TemplateMatchResult> ordered = BoardInspectionService.OrderMatches(
                        collection.Matches,
                        job.Template,
                        null);
                    if (ordered.Count > 0) return ordered[0];
                }
            }

            return _sdk.Locate(image, job, false);
        }

        /// <summary>清除指定侧的示教姿态并保存修改。</summary>
        public async Task<TeachingPose> ClearAsync(
            TeachingSide side,
            CancellationToken cancellationToken)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(TeachingService));

            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                TeachingPose cleared = new TeachingPose
                {
                    Side = side,
                    IsValid = false,
                    CapturedAtUtc = DateTime.UtcNow
                };

                TeachingData snapshot;
                lock (_sync)
                {
                    if (side == TeachingSide.Left) _data.Left = Clone(cleared);
                    else _data.Right = Clone(cleared);
                    snapshot = Clone(_data);
                }

                if (_saveAsync != null) await _saveAsync(snapshot).ConfigureAwait(false);
                EventHandler<TeachingEventArgs> handler = TeachingChanged;
                if (handler != null) handler(this, new TeachingEventArgs(cleared));
                return Clone(cleared);
            }
            finally
            {
                _gate.Release();
            }
        }

        private void PublishFrame(Bitmap image, string source)
        {
            EventHandler<FrameEventArgs> handler = FrameCaptured;
            if (handler == null || image == null) return;
            // 复制图像帧向外发布，避免订阅方与示教流程共用同一位图句柄
            using (FrameEventArgs args = new FrameEventArgs((Bitmap)image.Clone(), source))
                handler(this, args);
        }

        private static TeachingData Clone(TeachingData source)
        {
            return new TeachingData
            {
                Left = Clone(source == null ? null : source.Left) ?? new TeachingPose { Side = TeachingSide.Left },
                Right = Clone(source == null ? null : source.Right) ?? new TeachingPose { Side = TeachingSide.Right }
            };
        }

        private static TeachingPose Clone(TeachingPose source)
        {
            if (source == null) return null;
            return new TeachingPose
            {
                Side = source.Side,
                IsValid = source.IsValid,
                ImageRow = source.ImageRow,
                ImageColumn = source.ImageColumn,
                ImageAngleDegrees = source.ImageAngleDegrees,
                MachineX = source.MachineX,
                MachineY = source.MachineY,
                MachineR = source.MachineR,
                CapturedAtUtc = source.CapturedAtUtc
            };
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _gate.Dispose();
        }
    }
}
