using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using NinePointRotationCalibration.Halcon;
using NinePointRotationCalibration.Station.Models;
using NinePointRotationCalibration.Station.Services.Inspection;

namespace NinePointRotationCalibration.Station.Services.Teaching
{
    /// <summary>示教工作流异常，携带协议标准全大写错误码。</summary>
    [Serializable]
    public sealed class TeachingWorkflowException : Exception
    {
        public TeachingWorkflowException(string errorCode, string message)
            : base(message)
        {
            ErrorCode = string.IsNullOrWhiteSpace(errorCode)
                ? ProtocolErrorCodes.InternalError
                : errorCode;
        }

        public TeachingWorkflowException(string errorCode, string message, Exception innerException)
            : base(message, innerException)
        {
            ErrorCode = string.IsNullOrWhiteSpace(errorCode)
                ? ProtocolErrorCodes.InternalError
                : errorCode;
        }

        public string ErrorCode { get; private set; }
    }

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

        /// <summary>从外部加载或导入配置文件夹时整体替换示教数据快照。</summary>
        public void SetData(TeachingData data)
        {
            lock (_sync) _data = Clone(data ?? new TeachingData());
        }

        /// <summary>仅记录指定侧当前机械轴坐标（不触发拍照，避免轴在示教位遮挡相机）；若已先完成图像模板匹配则自动合并为有效示教。</summary>
        public async Task<TeachingPose> RecordMachinePoseAsync(
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
                TeachingPose existing;
                lock (_sync)
                {
                    existing = Clone(side == TeachingSide.Left ? _data.Left : _data.Right);
                }

                // 若先前处于“已定位图像、待记轴坐标”半完成态，则保留图像位姿并合成完整示教；
                // 若原先已是完整示教或未示教，则开启新一轮示教周期并等待移轴后重新拍照模板匹配。
                bool keepPendingImage = existing != null &&
                    !existing.IsValid &&
                    existing.HasImagePose &&
                    IsFinite(existing.ImageRow) &&
                    IsFinite(existing.ImageColumn) &&
                    IsFinite(existing.ImageAngleDegrees);

                TeachingPose pose = new TeachingPose
                {
                    Side = side,
                    HasMachinePose = true,
                    HasImagePose = keepPendingImage,
                    IsValid = keepPendingImage,
                    ImageRow = keepPendingImage ? existing.ImageRow : 0.0,
                    ImageColumn = keepPendingImage ? existing.ImageColumn : 0.0,
                    ImageAngleDegrees = keepPendingImage ? existing.ImageAngleDegrees : 0.0,
                    MatchScore = keepPendingImage ? existing.MatchScore : 0.0,
                    MachineX = machinePose.X,
                    MachineY = machinePose.Y,
                    MachineR = machinePose.ThetaDegrees,
                    CapturedAtUtc = DateTime.UtcNow
                };

                TeachingData snapshot;
                lock (_sync)
                {
                    snapshot = Clone(_data);
                    if (side == TeachingSide.Left) snapshot.Left = Clone(pose);
                    else snapshot.Right = Clone(pose);
                }

                if (_saveAsync != null)
                {
                    try
                    {
                        await _saveAsync(snapshot).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        LocalDebugLog.WriteException("ERROR", "teaching", "Failed to save recorded machine pose for " + side, ex);
                        throw new TeachingWorkflowException(
                            ProtocolErrorCodes.TeachingSaveFailed,
                            "Failed to save teaching data: " + ex.Message,
                            ex);
                    }
                }

                lock (_sync)
                {
                    _data = Clone(snapshot);
                }
                LocalDebugLog.Write(
                    "INFO",
                    "teaching",
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "Recorded teaching machine pose: side={0}, machine=({1:0.###},{2:0.###},{3:0.###}deg), hasImage={4}, valid={5}",
                        side,
                        pose.MachineX,
                        pose.MachineY,
                        pose.MachineR,
                        pose.HasImagePose,
                        pose.IsValid));
                EventHandler<TeachingEventArgs> handler = TeachingChanged;
                if (handler != null) handler(this, new TeachingEventArgs(pose));
                return Clone(pose);
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>在轴移出相机视野后触发一次拍照并用定位模板匹配当前基准板图像坐标，与已记录轴坐标合并保存。</summary>
        public Task<TeachingPose> LocateImagePoseAsync(
            TeachingSide side,
            CancellationToken cancellationToken)
        {
            return LocateImagePoseAsync(side, 0, cancellationToken);
        }

        /// <summary>在轴移出相机视野后触发一次拍照并用定位模板匹配当前基准板图像坐标（支持可选采图超时限制）。</summary>
        public async Task<TeachingPose> LocateImagePoseAsync(
            TeachingSide side,
            int captureTimeoutMilliseconds,
            CancellationToken cancellationToken)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(TeachingService));

            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                CalibrationJob job = _getPositioningJob();
                if (job == null || job.Template == null || job.Template.ModelData == null || job.Template.ModelData.Length == 0)
                {
                    LocalDebugLog.Write("WARN", "teaching", "LocateImage aborted: positioning template is not configured. side=" + side);
                    throw new TeachingWorkflowException(
                        ProtocolErrorCodes.PositioningTemplateMissing,
                        "定位模板尚未配置或模型不可用，请先在「模板」页创建或加载定位模板。");
                }

                LocalDebugLog.Write("INFO", "teaching", "LocateImage start (trigger capture + positioning match): side=" + side);

                CameraFrame frame = await CaptureFrameWithOptionalTimeoutAsync(captureTimeoutMilliseconds, cancellationToken)
                    .ConfigureAwait(false);
                using (frame)
                {
                    LocalDebugLog.Write(
                        "INFO",
                        "teaching",
                        "Captured teaching locate frame: side=" + side + ", seq=" + frame.Sequence + ", size=" +
                        (frame.Image == null ? "null" : frame.Image.Width + "x" + frame.Image.Height));
                    PublishFrame(frame.Image, "teaching." + side.ToString().ToLowerInvariant());

                    TemplateMatchResult match = await LocateReferenceBoardWithValidationAsync(frame.Image, job, side, cancellationToken)
                        .ConfigureAwait(false);

                    TeachingPose existing;
                    lock (_sync)
                    {
                        existing = Clone(side == TeachingSide.Left ? _data.Left : _data.Right);
                    }

                    bool hasMachine = existing != null &&
                        (existing.HasMachinePose || existing.IsValid) &&
                        IsFinite(existing.MachineX) &&
                        IsFinite(existing.MachineY) &&
                        IsFinite(existing.MachineR);

                    TeachingPose pose = new TeachingPose
                    {
                        Side = side,
                        HasMachinePose = hasMachine,
                        HasImagePose = true,
                        IsValid = hasMachine,
                        ImageRow = match.Row,
                        ImageColumn = match.Column,
                        ImageAngleDegrees = match.AngleDegrees,
                        MatchScore = IsFinite(match.Score) ? match.Score : 0.0,
                        MachineX = hasMachine ? existing.MachineX : 0.0,
                        MachineY = hasMachine ? existing.MachineY : 0.0,
                        MachineR = hasMachine ? existing.MachineR : 0.0,
                        CapturedAtUtc = DateTime.UtcNow
                    };

                    TeachingData snapshot;
                    lock (_sync)
                    {
                        snapshot = Clone(_data);
                        if (side == TeachingSide.Left) snapshot.Left = Clone(pose);
                        else snapshot.Right = Clone(pose);
                    }

                    if (_saveAsync != null)
                    {
                        try
                        {
                            await _saveAsync(snapshot).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            LocalDebugLog.WriteException("ERROR", "teaching", "Failed to save located image pose for " + side, ex);
                            throw new TeachingWorkflowException(
                                ProtocolErrorCodes.TeachingSaveFailed,
                                "Failed to save teaching data: " + ex.Message,
                                ex);
                        }
                    }

                    lock (_sync)
                    {
                        _data = Clone(snapshot);
                    }
                    LocalDebugLog.Write(
                        "INFO",
                        "teaching",
                        "Teaching image located and saved: " + StationLogFormatter.FormatTeachingPose(pose) +
                        ", matchScore=" + pose.MatchScore.ToString("0.###", CultureInfo.InvariantCulture) +
                        ", hasMachine=" + pose.HasMachinePose + ", valid=" + pose.IsValid);
                    EventHandler<TeachingEventArgs> handler = TeachingChanged;
                    if (handler != null) handler(this, new TeachingEventArgs(pose, match.ContourSegments));
                    return Clone(pose);
                }
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>采集一幅图像并使用 HALCON 模板定位生成指定侧的示教姿态。</summary>
        public Task<TeachingPose> CaptureAsync(
            TeachingSide side,
            MachinePose machinePose,
            CancellationToken cancellationToken)
        {
            return CaptureAsync(side, machinePose, 0, cancellationToken);
        }

        /// <summary>采集一幅图像并使用 HALCON 模板定位生成指定侧的示教姿态（支持可选采图超时限制）。</summary>
        public async Task<TeachingPose> CaptureAsync(
            TeachingSide side,
            MachinePose machinePose,
            int captureTimeoutMilliseconds,
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
                {
                    LocalDebugLog.Write("WARN", "teaching", "Capture aborted: positioning template is not configured. side=" + side);
                    throw new TeachingWorkflowException(
                        ProtocolErrorCodes.PositioningTemplateMissing,
                        "Positioning template is not configured.");
                }

                LocalDebugLog.Write(
                    "INFO",
                    "teaching",
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "Capture start: side={0}, inputMachine=({1:0.###},{2:0.###},{3:0.###}deg)",
                        side,
                        machinePose.X,
                        machinePose.Y,
                        machinePose.ThetaDegrees));

                CameraFrame frame = await CaptureFrameWithOptionalTimeoutAsync(captureTimeoutMilliseconds, cancellationToken)
                    .ConfigureAwait(false);

                using (frame)
                {
                    LocalDebugLog.Write(
                        "INFO",
                        "teaching",
                        "Captured teaching frame: side=" + side + ", seq=" + frame.Sequence + ", size=" +
                        (frame.Image == null ? "null" : frame.Image.Width + "x" + frame.Image.Height));
                    PublishFrame(frame.Image, "teaching." + side.ToString().ToLowerInvariant());

                    TemplateMatchResult match = await LocateReferenceBoardWithValidationAsync(frame.Image, job, side, cancellationToken)
                        .ConfigureAwait(false);

                    // 记录示教基准时刻的视觉与机械位姿；MachineR 同时作为后续盲摆回正的固定角
                    TeachingPose pose = new TeachingPose
                    {
                        Side = side,
                        IsValid = true,
                        HasMachinePose = true,
                        HasImagePose = true,
                        ImageRow = match.Row,
                        ImageColumn = match.Column,
                        ImageAngleDegrees = match.AngleDegrees,
                        MatchScore = IsFinite(match.Score) ? match.Score : 0.0,
                        MachineX = machinePose.X,
                        MachineY = machinePose.Y,
                        MachineR = machinePose.ThetaDegrees,
                        CapturedAtUtc = DateTime.UtcNow
                    };

                    TeachingData snapshot;
                    lock (_sync)
                    {
                        snapshot = Clone(_data);
                        if (side == TeachingSide.Left) snapshot.Left = Clone(pose);
                        else snapshot.Right = Clone(pose);
                    }

                    // 先持久化成功后再提交内存状态，防止保存失败导致内存与磁盘不一致（旧示教完整保留）
                    if (_saveAsync != null)
                    {
                        try
                        {
                            await _saveAsync(snapshot).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            LocalDebugLog.WriteException("ERROR", "teaching", "Failed to save teaching data for " + side, ex);
                            throw new TeachingWorkflowException(
                                ProtocolErrorCodes.TeachingSaveFailed,
                                "Failed to save teaching data: " + ex.Message,
                                ex);
                        }
                    }

                    lock (_sync)
                    {
                        _data = Clone(snapshot);
                    }
                    LocalDebugLog.Write(
                        "INFO",
                        "teaching",
                        "Teaching saved: " + StationLogFormatter.FormatTeachingPose(pose) +
                        ", matchScore=" + pose.MatchScore.ToString("0.###", CultureInfo.InvariantCulture));
                    EventHandler<TeachingEventArgs> handler = TeachingChanged;
                    if (handler != null) handler(this, new TeachingEventArgs(pose, match.ContourSegments));
                    return Clone(pose);
                }
            }
            finally
            {
                _gate.Release();
            }
        }

        private async Task<CameraFrame> CaptureFrameWithOptionalTimeoutAsync(
            int captureTimeoutMilliseconds,
            CancellationToken cancellationToken)
        {
            if (captureTimeoutMilliseconds > 0)
            {
                using (CancellationTokenSource captureCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    captureCts.CancelAfter(captureTimeoutMilliseconds);
                    return await CaptureFrameWithMappedErrorsAsync(captureCts.Token, cancellationToken, captureTimeoutMilliseconds)
                        .ConfigureAwait(false);
                }
            }
            return await CaptureFrameWithMappedErrorsAsync(cancellationToken, cancellationToken, 0)
                .ConfigureAwait(false);
        }

        private async Task<TemplateMatchResult> LocateReferenceBoardWithValidationAsync(
            Bitmap image,
            CalibrationJob job,
            TeachingSide side,
            CancellationToken cancellationToken)
        {
            TemplateMatchResult match;
            try
            {
                match = await Task.Run(
                    () => LocateReferenceBoard(image, job, true), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                LocalDebugLog.WriteException("WARN", "teaching", "Template locate threw an exception for " + side, ex);
                throw new TeachingWorkflowException(ProtocolErrorCodes.MatchFailed, "Template locate failed: " + ex.Message, ex);
            }

            if (match == null || !match.Success ||
                !IsFinite(match.Row) || !IsFinite(match.Column) || !IsFinite(match.AngleDegrees))
            {
                string errMsg = match == null || string.IsNullOrWhiteSpace(match.Message)
                    ? "Positioning template match failed."
                    : match.Message;
                LocalDebugLog.Write("WARN", "teaching", "Template locate failed for " + side + ": " + errMsg);
                throw new TeachingWorkflowException(ProtocolErrorCodes.MatchFailed, errMsg);
            }

            double minScore = (job != null && job.Template != null && job.Template.MatchParameters != null && IsFinite(job.Template.MatchParameters.MinScore))
                ? Math.Max(0.0, Math.Min(1.0, job.Template.MatchParameters.MinScore))
                : 0.0;
            if (!IsFinite(match.Score) || match.Score < minScore)
            {
                string lowScoreMsg = string.Format(
                    CultureInfo.InvariantCulture,
                    "示教定位模板匹配分数 {0:0.###} 低于最低匹配分数 {1:0.###}。",
                    IsFinite(match.Score) ? match.Score : 0.0,
                    minScore);
                LocalDebugLog.Write("WARN", "teaching", "Template locate score below MinScore for " + side + ": " + lowScoreMsg);
                throw new TeachingWorkflowException(ProtocolErrorCodes.MatchFailed, lowScoreMsg);
            }

            return match;
        }

        private async Task<CameraFrame> CaptureFrameWithMappedErrorsAsync(
            CancellationToken captureToken,
            CancellationToken callerToken,
            int timeoutMilliseconds)
        {
            CameraFrame frame;
            try
            {
                frame = await _captureAsync(captureToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (callerToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException ex)
            {
                string msg = timeoutMilliseconds > 0
                    ? "Camera capture timed out (" + timeoutMilliseconds + " ms)."
                    : "Camera capture timed out.";
                LocalDebugLog.Write("WARN", "teaching", msg);
                throw new TeachingWorkflowException(ProtocolErrorCodes.CameraCaptureFailed, msg, ex);
            }
            catch (Exception ex)
            {
                LocalDebugLog.WriteException("WARN", "teaching", "Camera capture failed during teaching", ex);
                throw new TeachingWorkflowException(
                    ProtocolErrorCodes.CameraCaptureFailed,
                    "Camera capture failed: " + ex.Message,
                    ex);
            }

            if (frame == null || frame.Image == null)
            {
                if (frame != null) frame.Dispose();
                throw new TeachingWorkflowException(
                    ProtocolErrorCodes.CameraCaptureFailed,
                    "Camera capture returned an empty frame.");
            }

            return frame;
        }

        /// <summary>
        /// 在一拍多板治具中，左右吸嘴示教必须稳定锁定同一块基准板（按从上到下、从左到右排序的第 1 号板），
        /// 防止因默认 NumMatches=1 时帧间最高匹配分数跳变导致左、右吸嘴示教锁定在不同板位上。
        /// </summary>
        private TemplateMatchResult LocateReferenceBoard(Bitmap image, CalibrationJob job, bool includeContour = true)
        {
            if (job == null || job.Template == null) return _sdk.Locate(image, job, includeContour);

            CalibrationJob multiMatchJob = job.DeepClone();
            if (multiMatchJob.Template.MatchParameters != null)
            {
                multiMatchJob.Template.MatchParameters.NumMatches = 0;
            }

            TemplateMatchCollectionResult collection = _sdk.LocateAll(image, multiMatchJob, includeContour);
            if (collection != null && collection.Success &&
                collection.Matches != null && collection.Matches.Count > 0)
            {
                List<TemplateMatchResult> ordered = BoardInspectionService.OrderMatches(
                    collection.Matches,
                    multiMatchJob.Template,
                    null);
                if (ordered.Count > 0) return ordered[0];
            }

            return new TemplateMatchResult
            {
                Success = false,
                ErrorCode = collection == null ? CalibrationErrorCode.TemplateMatchFailed : collection.ErrorCode,
                Message = collection == null || string.IsNullOrWhiteSpace(collection.Message)
                    ? "Positioning template match was not found."
                    : collection.Message
            };
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
                    HasMachinePose = false,
                    HasImagePose = false,
                    CapturedAtUtc = DateTime.UtcNow
                };

                TeachingData snapshot;
                lock (_sync)
                {
                    snapshot = Clone(_data);
                    if (side == TeachingSide.Left) snapshot.Left = Clone(cleared);
                    else snapshot.Right = Clone(cleared);
                }

                if (_saveAsync != null) await _saveAsync(snapshot).ConfigureAwait(false);
                lock (_sync)
                {
                    _data = Clone(snapshot);
                }
                LocalDebugLog.Write("INFO", "teaching", "Cleared teaching data for side=" + side + ".");
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
                HasMachinePose = source.HasMachinePose || source.IsValid,
                HasImagePose = source.HasImagePose || source.IsValid,
                ImageRow = source.ImageRow,
                ImageColumn = source.ImageColumn,
                ImageAngleDegrees = source.ImageAngleDegrees,
                MatchScore = source.MatchScore,
                MachineX = source.MachineX,
                MachineY = source.MachineY,
                MachineR = source.MachineR,
                CapturedAtUtc = source.CapturedAtUtc
            };
        }

        private static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _gate.Dispose();
        }
    }
}
