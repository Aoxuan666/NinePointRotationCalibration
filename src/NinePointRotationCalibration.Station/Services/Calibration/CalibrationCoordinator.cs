using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NinePointRotationCalibration.Station.Models;

namespace NinePointRotationCalibration.Station.Services.Calibration
{
    /// <summary>标定会话流程异常，携带协议标准全大写错误码。</summary>
    [Serializable]
    public sealed class CalibrationWorkflowException : Exception
    {
        public CalibrationWorkflowException(string errorCode, string message)
            : base(message)
        {
            ErrorCode = string.IsNullOrWhiteSpace(errorCode)
                ? ProtocolErrorCodes.CalibrationSolveFailed
                : errorCode;
        }

        public CalibrationWorkflowException(string errorCode, string message, Exception innerException)
            : base(message, innerException)
        {
            ErrorCode = string.IsNullOrWhiteSpace(errorCode)
                ? ProtocolErrorCodes.CalibrationSolveFailed
                : errorCode;
        }

        public string ErrorCode { get; private set; }
    }

    /// <summary>
    /// 协调双吸嘴（Left/R1、Right/R2）独立 9+5 标定会话握手、运动、采图、HALCON 定位、
    /// 异常中止保护（不覆盖旧标定）和标定求解的站级流程服务。
    /// </summary>
    public sealed class CalibrationCoordinator : IDisposable
    {
        private readonly object _sync = new object();
        private readonly NinePointRotationCalibrationSdk _sdk;
        private readonly Func<ProtocolEnvelope, CancellationToken, Task> _sendAsync;
        private readonly Func<CancellationToken, Task<CameraFrame>> _captureAsync;
        private readonly Func<TeachingSide, CalibrationJob, Task> _saveJobAsync;
        private readonly ConcurrentDictionary<string, PendingStartAck> _pendingStartAcks;
        private readonly ConcurrentDictionary<string, PendingMove> _pendingMoves;
        private readonly SemaphoreSlim _captureGate;
        private readonly SemaphoreSlim _autoGate;
        private readonly Dictionary<TeachingSide, CalibrationJob> _committedJobs;
        private readonly Dictionary<TeachingSide, Dictionary<int, CalibrationPointRecord>> _records;
        private CalibrationSession _activeSession;
        private TeachingSide _activeNozzle;
        private IReadOnlyList<CalibrationPlanPoint> _plan;
        private double _stepX;
        private double _stepY;
        private double _stepR;
        private int _motionTimeoutMilliseconds;
        private int _startAckTimeoutMilliseconds;
        private int _sessionSequence;
        private bool _disposed;

        public CalibrationCoordinator(
            NinePointRotationCalibrationSdk sdk,
            Func<ProtocolEnvelope, CancellationToken, Task> sendAsync,
            Func<CancellationToken, Task<CameraFrame>> captureAsync,
            Func<TeachingSide, CalibrationJob, Task> saveJobAsync,
            StationOptions options,
            CalibrationJob leftJob,
            CalibrationJob rightJob)
        {
            _sdk = sdk ?? throw new ArgumentNullException(nameof(sdk));
            _sendAsync = sendAsync ?? throw new ArgumentNullException(nameof(sendAsync));
            _captureAsync = captureAsync ?? throw new ArgumentNullException(nameof(captureAsync));
            _saveJobAsync = saveJobAsync;
            _pendingStartAcks = new ConcurrentDictionary<string, PendingStartAck>(StringComparer.OrdinalIgnoreCase);
            _pendingMoves = new ConcurrentDictionary<string, PendingMove>(StringComparer.OrdinalIgnoreCase);
            _captureGate = new SemaphoreSlim(1, 1);
            _autoGate = new SemaphoreSlim(1, 1);
            _committedJobs = new Dictionary<TeachingSide, CalibrationJob>
            {
                { TeachingSide.Left, new CalibrationJob { Name = "Calibration-Left" } },
                { TeachingSide.Right, new CalibrationJob { Name = "Calibration-Right" } }
            };
            _records = new Dictionary<TeachingSide, Dictionary<int, CalibrationPointRecord>>
            {
                { TeachingSide.Left, new Dictionary<int, CalibrationPointRecord>() },
                { TeachingSide.Right, new Dictionary<int, CalibrationPointRecord>() }
            };
            _activeNozzle = TeachingSide.Left;
            UpdateOptions(options ?? new StationOptions());
            SetJob(TeachingSide.Left, leftJob ?? new CalibrationJob { Name = "Calibration-Left" });
            SetJob(TeachingSide.Right, rightJob ?? new CalibrationJob { Name = "Calibration-Right" });
        }

        public event EventHandler<CalibrationPointEventArgs> PointChanged;
        public event EventHandler<CalibrationResultEventArgs> CalculationCompleted;
        public event EventHandler<FrameEventArgs> FrameCaptured;
        public event EventHandler<LogEventArgs> Log;

        /// <summary>当前默认操作的吸嘴侧别（Left=R1 / Right=R2）。</summary>
        public TeachingSide ActiveNozzle
        {
            get { lock (_sync) return _activeNozzle; }
            set { lock (_sync) _activeNozzle = value; }
        }

        /// <summary>指示自动标定是否正在执行。</summary>
        public bool IsAutomaticRunning
        {
            get
            {
                lock (_sync) return _activeSession != null && !_activeSession.IsManual;
            }
        }

        /// <summary>指示当前是否存在活动中的标定会话（自动或手动）。</summary>
        public bool HasActiveSession
        {
            get
            {
                lock (_sync) return _activeSession != null;
            }
        }

        /// <summary>返回当前活动标定会话 ID（无活动会话时为空）。</summary>
        public string ActiveSessionId
        {
            get
            {
                lock (_sync) return _activeSession == null ? string.Empty : _activeSession.SessionId;
            }
        }

        /// <summary>返回指定吸嘴已提交生效的标定任务独立快照。</summary>
        public CalibrationJob GetJobSnapshot(TeachingSide nozzle)
        {
            lock (_sync) return _committedJobs[nozzle].DeepClone();
        }

        /// <summary>返回当前默认吸嘴已提交生效的标定任务独立快照。</summary>
        public CalibrationJob GetJobSnapshot()
        {
            return GetJobSnapshot(ActiveNozzle);
        }

        /// <summary>返回指定吸嘴所有标定点状态的独立快照。</summary>
        public IReadOnlyList<CalibrationPointRecord> GetRecordsSnapshot(TeachingSide nozzle)
        {
            lock (_sync)
            {
                return _records[nozzle].Values
                    .OrderBy(record => record.PointNumber)
                    .Select(record => record.DeepClone())
                    .ToList();
            }
        }

        /// <summary>返回当前默认吸嘴所有标定点状态的独立快照。</summary>
        public IReadOnlyList<CalibrationPointRecord> GetRecordsSnapshot()
        {
            return GetRecordsSnapshot(ActiveNozzle);
        }

        /// <summary>更新标定步长和超时配置，并同步刷新双吸嘴标定点计划。</summary>
        public void UpdateOptions(StationOptions options)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            IReadOnlyList<CalibrationPlanPoint> plan = CalibrationPlan.Create(
                options.TranslationStepX,
                options.TranslationStepY,
                options.RotationStepDegrees);

            lock (_sync)
            {
                _plan = plan;
                _stepX = options.TranslationStepX;
                _stepY = options.TranslationStepY;
                _stepR = options.RotationStepDegrees;
                _motionTimeoutMilliseconds = Math.Max(1000, options.MotionTimeoutMilliseconds);
                _startAckTimeoutMilliseconds = Math.Max(500, options.CalibrationStartAckTimeoutMilliseconds);
                SyncPlanRecordsLocked(TeachingSide.Left, plan);
                SyncPlanRecordsLocked(TeachingSide.Right, plan);
            }
        }

        /// <summary>替换指定吸嘴的已提交标定任务，并将已有样本映射到标定点状态。</summary>
        public void SetJob(TeachingSide nozzle, CalibrationJob job)
        {
            if (job == null) throw new ArgumentNullException(nameof(job));
            lock (_sync)
            {
                CalibrationJob clone = job.DeepClone();
                if (clone.SolverOptions == null) clone.SolverOptions = new CalibrationSolverOptions();
                else clone.SolverOptions.MigrateLegacyDefaultPixelThresholds();
                _committedJobs[nozzle] = clone;
                RebuildRecordsFromJobLocked(nozzle, _committedJobs[nozzle]);
            }
        }

        /// <summary>替换当前默认吸嘴的标定任务。</summary>
        public void SetJob(CalibrationJob job)
        {
            SetJob(ActiveNozzle, job);
        }

        /// <summary>更新指定吸嘴标定模板的最低匹配分数（同步作用于已提交任务与当前活动会话，不清空采样）。</summary>
        public void UpdateTemplateMinScore(TeachingSide nozzle, double minScore)
        {
            if (!IsFinite(minScore)) return;
            double clamped = Math.Max(0.0, Math.Min(1.0, minScore));
            lock (_sync)
            {
                CalibrationJob committed = _committedJobs[nozzle];
                if (committed != null && committed.Template != null)
                {
                    if (committed.Template.MatchParameters == null)
                        committed.Template.MatchParameters = new MatchParameters();
                    committed.Template.MatchParameters.MinScore = clamped;
                }
                if (_activeSession != null && _activeSession.Nozzle == nozzle &&
                    _activeSession.WorkingJob != null && _activeSession.WorkingJob.Template != null)
                {
                    if (_activeSession.WorkingJob.Template.MatchParameters == null)
                        _activeSession.WorkingJob.Template.MatchParameters = new MatchParameters();
                    _activeSession.WorkingJob.Template.MatchParameters.MinScore = clamped;
                }
            }
        }

        /// <summary>主动开启指定吸嘴的手动标定会话（发送 calibration.started 并锁存原点）。</summary>
        public async Task<string> StartManualSessionAsync(TeachingSide nozzle, CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            await _captureGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                CalibrationSession existing;
                lock (_sync) existing = _activeSession;
                if (existing != null)
                {
                    if (!existing.IsManual)
                        throw new InvalidOperationException("自动标定正在运行，无法同时进入手动标定。");
                    if (existing.Nozzle == nozzle)
                        return existing.SessionId;
                    // 切换吸嘴前先安全关闭旧手动会话并通知上位机退出旧会话
                    await EndSessionAndNotifyLockedOrUnlockedAsync(
                        existing,
                        false,
                        ProtocolErrorCodes.Cancelled,
                        "Switched manual calibration nozzle.",
                        null,
                        null).ConfigureAwait(false);
                }

                CalibrationSession started = await BeginSessionHandshakeAsync(nozzle, true, cancellationToken)
                    .ConfigureAwait(false);
                return started.SessionId;
            }
            finally
            {
                _captureGate.Release();
            }
        }

        /// <summary>执行指定吸嘴的单个标定点（自动确保会话握手与原点锁存，不覆盖磁盘旧标定）。</summary>
        public Task<CalibrationPointRecord> CapturePointAsync(int pointNumber, CancellationToken cancellationToken)
        {
            return CapturePointAsync(ActiveNozzle, pointNumber, cancellationToken);
        }

        /// <summary>执行指定吸嘴的单个标定点：确保会话握手 -> 发 move.request -> 等 arrived/failed -> 采图定位 -> 写入会话暂存集。</summary>
        public async Task<CalibrationPointRecord> CapturePointAsync(
            TeachingSide nozzle,
            int pointNumber,
            CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            await _captureGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                CalibrationSession session;
                lock (_sync) session = _activeSession;
                if (session == null)
                {
                    session = await BeginSessionHandshakeAsync(nozzle, true, cancellationToken).ConfigureAwait(false);
                }
                else if (session.Nozzle != nozzle)
                {
                    if (!session.IsManual)
                        throw new InvalidOperationException("自动标定正在运行中，无法采集其他吸嘴点位。");
                    await EndSessionAndNotifyLockedOrUnlockedAsync(
                        session,
                        false,
                        ProtocolErrorCodes.Cancelled,
                        "Switched manual calibration nozzle.",
                        null,
                        null).ConfigureAwait(false);
                    session = await BeginSessionHandshakeAsync(nozzle, true, cancellationToken).ConfigureAwait(false);
                }

                return await CapturePointCoreAsync(session, pointNumber, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _captureGate.Release();
            }
        }

        /// <summary>按当前点位计划依次执行指定吸嘴全部 14 个点并求解；任何中断/失败均保留旧标定。</summary>
        public Task<CalibrationResult> RunAutomaticAsync(CancellationToken cancellationToken)
        {
            return RunAutomaticAsync(ActiveNozzle, cancellationToken);
        }

        /// <summary>按当前点位计划依次执行指定吸嘴全部 14 个点并求解；任何中断/失败均保留旧标定。</summary>
        public async Task<CalibrationResult> RunAutomaticAsync(
            TeachingSide nozzle,
            CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            await _autoGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            CalibrationSession session = null;
            CancellationTokenSource linked = null;
            try
            {
                // 若存在残留手动会话，先中止并清理
                CalibrationSession existingManual;
                lock (_sync) existingManual = _activeSession;
                if (existingManual != null)
                {
                    if (!existingManual.IsManual)
                        throw new InvalidOperationException("Automatic calibration is already running.");
                    await EndSessionAndNotifyLockedOrUnlockedAsync(
                        existingManual,
                        false,
                        ProtocolErrorCodes.Cancelled,
                        "Replaced by automatic calibration session.",
                        null,
                        null).ConfigureAwait(false);
                }

                // 1. 前置校验模板有效性（未发送 started 前即可直接拦截）
                CalibrationJob committed;
                lock (_sync) committed = _committedJobs[nozzle].DeepClone();
                if (committed == null || committed.Template == null || !committed.Template.HasUsableModel)
                {
                    string message = ProtocolNozzle.GetDisplayName(nozzle) + " 标定模板尚未配置或模型不可用。";
                    throw new CalibrationWorkflowException(ProtocolErrorCodes.TemplateMissing, message);
                }

                // 2. 发起 calibration.started 会话握手并锁存原点
                session = await BeginSessionHandshakeAsync(nozzle, false, cancellationToken).ConfigureAwait(false);
                linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, session.Cancellation.Token);

                IReadOnlyList<CalibrationPlanPoint> plan;
                lock (_sync) plan = _plan.ToList();

                // 3. 逐点执行 1..14 采点（全部写入 session.WorkingJob，绝不覆写 _committedJobs）
                foreach (CalibrationPlanPoint point in plan)
                {
                    linked.Token.ThrowIfCancellationRequested();
                    CalibrationPointRecord pointRecord = await CapturePointCoreAsync(
                        session,
                        point.PointNumber,
                        linked.Token).ConfigureAwait(false);
                    // 若在同步匹配期间收到了上位机/本机取消指令，优先按取消路径回传 CANCELLED 及对应 correlationId
                    linked.Token.ThrowIfCancellationRequested();
                    if (pointRecord == null || pointRecord.Status != CalibrationPointStatus.Completed)
                    {
                        string failMsg = pointRecord == null
                            ? "Calibration point " + point.PointNumber + " match failed."
                            : (pointRecord.Message ?? "Match failed.");
                        await EndSessionAndNotifyLockedOrUnlockedAsync(
                            session,
                            false,
                            ProtocolErrorCodes.MatchFailed,
                            failMsg,
                            null,
                            null).ConfigureAwait(false);
                        throw new CalibrationWorkflowException(ProtocolErrorCodes.MatchFailed, failMsg);
                    }
                }

                // 4. 全部 14 点完成后求解并原子提交
                return await SolveAndCompleteSessionAsync(session, linked.Token).ConfigureAwait(false);
            }
            catch (MotionFailedException ex)
            {
                if (session != null)
                {
                    string detail = "[" + ex.Failure.Code + "] " + ex.Failure.Message;
                    await EndSessionAndNotifyLockedOrUnlockedAsync(
                        session,
                        false,
                        ProtocolErrorCodes.MotionFailed,
                        detail,
                        ex.Failure.MessageId,
                        null).ConfigureAwait(false);
                }
                throw new CalibrationWorkflowException(
                    ProtocolErrorCodes.MotionFailed,
                    "上位机拒绝或中止运动 [" + ex.Failure.Code + "]：" + ex.Failure.Message,
                    ex);
            }
            catch (TimeoutException ex)
            {
                if (session != null)
                {
                    await EndSessionAndNotifyLockedOrUnlockedAsync(
                        session,
                        false,
                        ProtocolErrorCodes.MotionTimeout,
                        ex.Message,
                        null,
                        null).ConfigureAwait(false);
                }
                throw;
            }
            catch (OperationCanceledException)
            {
                if (session != null)
                {
                    if (session.Disconnected)
                    {
                        ClearSessionWithoutSend(session, "TCP disconnected during calibration.");
                    }
                    else
                    {
                        string reason = string.IsNullOrWhiteSpace(session.CancelReason)
                            ? "Calibration cancelled."
                            : session.CancelReason;
                        await EndSessionAndNotifyLockedOrUnlockedAsync(
                            session,
                            false,
                            ProtocolErrorCodes.Cancelled,
                            reason,
                            session.CancelCorrelationId,
                            null).ConfigureAwait(false);
                    }
                }
                throw;
            }
            catch (CalibrationWorkflowException)
            {
                throw;
            }
            catch (Exception ex)
            {
                if (session != null)
                {
                    string errorCode = ex.Data != null && ex.Data.Contains("CalibrationErrorCode")
                        ? Convert.ToString(ex.Data["CalibrationErrorCode"], CultureInfo.InvariantCulture)
                        : ProtocolErrorCodes.CameraCaptureFailed;
                    await EndSessionAndNotifyLockedOrUnlockedAsync(
                        session,
                        false,
                        errorCode,
                        ex.Message,
                        null,
                        null).ConfigureAwait(false);
                }
                throw;
            }
            finally
            {
                if (linked != null) linked.Dispose();
                _autoGate.Release();
            }
        }

        /// <summary>同步请求取消正在运行的自动/手动标定会话（用于界面停止或销毁）。</summary>
        public void CancelAutomatic()
        {
            _ = CancelCalibrationAsync("Operator stopped calibration.");
        }

        /// <summary>主动取消当前活动标定会话（视觉界面点击“停止标定”或开关关闭），保留旧标定并通知上位机退出标定模式。</summary>
        public async Task CancelCalibrationAsync(string reason = null)
        {
            string cancelMessage = string.IsNullOrWhiteSpace(reason)
                ? "Operator stopped calibration."
                : reason.Trim();
            CalibrationSession session;
            lock (_sync)
            {
                session = _activeSession;
                if (session != null)
                {
                    if (string.IsNullOrWhiteSpace(session.CancelReason))
                    {
                        session.CancelReason = cancelMessage;
                    }
                    // 若上位机已先发来 calibration.cancel，保留其 CancelCorrelationId 以确保能关联回包
                }
            }

            if (session == null) return;

            try { session.Cancellation.Cancel(); } catch (ObjectDisposedException) { }
            CancelAllPendingWaits(cancelMessage);

            // 若是手动会话（没有 RunAutomaticAsync 外层捕获），在此直接发送 calibration.completed(CANCELLED) 并回滚暂存数据
            if (session.IsManual)
            {
                await EndSessionAndNotifyLockedOrUnlockedAsync(
                    session,
                    false,
                    ProtocolErrorCodes.Cancelled,
                    cancelMessage,
                    null,
                    null).ConfigureAwait(false);
            }
        }

        /// <summary>处理上位机主动发来的 calibration.cancel：立即停止下发、保留旧标定并回传 calibration.completed(CANCELLED)。</summary>
        public async Task HandleCalibrationCancelAsync(ProtocolEnvelope envelope)
        {
            if (envelope == null) return;
            string requestedSessionId = ProtocolValue.String(envelope.Data, "sessionId", string.Empty).Trim();
            string reason = ProtocolValue.String(envelope.Data, "reason", "Cancelled by host.").Trim();
            if (string.IsNullOrWhiteSpace(reason)) reason = "Cancelled by host.";

            CalibrationSession session;
            TeachingSide fallbackNozzle;
            bool matchedActive = false;
            lock (_sync)
            {
                fallbackNozzle = _activeNozzle;
                session = _activeSession;
                if (session != null &&
                    (string.IsNullOrWhiteSpace(requestedSessionId) ||
                     string.Equals(session.SessionId, requestedSessionId, StringComparison.OrdinalIgnoreCase)))
                {
                    matchedActive = true;
                    session.CancelCorrelationId = envelope.MessageId;
                    session.CancelReason = reason;
                }
            }

            if (matchedActive)
            {
                OnLog("WARN", "Host requested calibration.cancel (sessionId=" + session.SessionId + "): " + reason);
                try { session.Cancellation.Cancel(); } catch (ObjectDisposedException) { }
                CancelAllPendingWaits("Host calibration.cancel: " + reason);

                if (session.IsManual)
                {
                    await EndSessionAndNotifyLockedOrUnlockedAsync(
                        session,
                        false,
                        ProtocolErrorCodes.Cancelled,
                        reason,
                        envelope.MessageId,
                        null).ConfigureAwait(false);
                }
                return;
            }

            // 无对应活动会话时仍明确回复 calibration.completed(CANCELLED)，避免上位机等待超时
            OnLog("INFO", "Received calibration.cancel with no active matching session (sessionId=" + requestedSessionId + ").");
            await SendCompletedEnvelopeAsync(
                requestedSessionId,
                fallbackNozzle,
                false,
                ProtocolErrorCodes.Cancelled,
                "No active calibration session: " + reason,
                envelope.MessageId,
                null,
                null).ConfigureAwait(false);
        }

        /// <summary>使用当前默认吸嘴的会话暂存样本（或已提交任务）执行标定计算，成功时持久化并发送完成消息。</summary>
        public Task<CalibrationResult> CalculateAsync(CancellationToken cancellationToken)
        {
            return CalculateAsync(ActiveNozzle, cancellationToken);
        }

        /// <summary>使用指定吸嘴的会话暂存样本（或已提交任务）执行标定计算；仅在求解成功时持久化并覆盖旧标定。</summary>
        public async Task<CalibrationResult> CalculateAsync(TeachingSide nozzle, CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();

            CalibrationSession activeSession;
            lock (_sync)
            {
                activeSession = (_activeSession != null && _activeSession.Nozzle == nozzle)
                    ? _activeSession
                    : null;
            }

            if (activeSession != null)
            {
                return await SolveAndCompleteSessionAsync(activeSession, cancellationToken).ConfigureAwait(false);
            }

            // 无活动会话时重算已提交的任务
            CalibrationJob committedJob;
            lock (_sync) committedJob = _committedJobs[nozzle].DeepClone();
            CalibrationResult result = await Task.Run(() => _sdk.Calculate(committedJob), cancellationToken)
                .ConfigureAwait(false);
            string errorCode = result != null && result.Success
                ? ProtocolErrorCodes.None
                : ProtocolErrorCodes.CalibrationSolveFailed;
            OnCalculationCompleted(nozzle, result, string.Empty, errorCode);
            await SendCompletedEnvelopeAsync(
                string.Empty,
                nozzle,
                result != null && result.Success,
                errorCode,
                result == null ? "Calibration calculation returned null." : (result.Message ?? string.Empty),
                null,
                result,
                committedJob.SolverOptions).ConfigureAwait(false);
            return result;
        }

        /// <summary>处理上位机返回的标定相关同步应答报文（calibration.started.ack / motion.arrived / motion.failed）。</summary>
        public bool HandleProtocolMessage(ProtocolEnvelope envelope)
        {
            if (envelope == null) return false;

            if (string.Equals(envelope.Type, ProtocolMessageTypes.CalibrationStartedAck, StringComparison.OrdinalIgnoreCase))
            {
                return HandleCalibrationStartedAck(envelope);
            }

            if (string.Equals(envelope.Type, ProtocolMessageTypes.MotionArrived, StringComparison.OrdinalIgnoreCase))
            {
                return HandleMotionArrived(envelope);
            }

            if (string.Equals(envelope.Type, ProtocolMessageTypes.MotionFailed, StringComparison.OrdinalIgnoreCase))
            {
                return HandleMotionFailed(envelope);
            }

            return false;
        }

        /// <summary>TCP 断线时取消全部挂起标定等待并丢弃未提交的半途采样，保护旧标定。</summary>
        public void CancelPendingMoves(string reason = null)
        {
            CalibrationSession session;
            lock (_sync)
            {
                session = _activeSession;
                if (session != null)
                {
                    session.Disconnected = true;
                    session.CancelReason = reason ?? "TCP disconnected.";
                }
            }
            if (session != null)
            {
                try { session.Cancellation.Cancel(); } catch (ObjectDisposedException) { }
                if (session.IsManual)
                {
                    ClearSessionWithoutSend(session, reason ?? "TCP disconnected.");
                }
            }
            CancelAllPendingWaits(reason);
            if (!string.IsNullOrWhiteSpace(reason))
            {
                OnLog("WARN", "Cancelled pending calibration moves (old calibration preserved): " + reason);
            }
        }

        private async Task<CalibrationSession> BeginSessionHandshakeAsync(
            TeachingSide nozzle,
            bool isManual,
            CancellationToken cancellationToken)
        {
            CalibrationJob committed;
            double stepX;
            double stepY;
            double stepR;
            int totalPoints;
            lock (_sync)
            {
                committed = _committedJobs[nozzle].DeepClone();
                stepX = _stepX;
                stepY = _stepY;
                stepR = _stepR;
                totalPoints = _plan == null ? 14 : _plan.Count;
            }

            if (committed == null || committed.Template == null || !committed.Template.HasUsableModel)
            {
                throw new CalibrationWorkflowException(
                    ProtocolErrorCodes.TemplateMissing,
                    ProtocolNozzle.GetDisplayName(nozzle) + " 标定模板尚未配置或模型不可用。");
            }

            string sessionId = GenerateSessionId();
            string nozzleName = ProtocolNozzle.GetNozzleName(nozzle);
            string axisR = ProtocolNozzle.GetAxisR(nozzle);

            CalibrationJob workingJob = isManual
                ? committed.DeepClone()
                : _sdk.ClearSamples(committed.DeepClone());
            CalibrationSession session = new CalibrationSession(sessionId, nozzle, axisR, isManual, workingJob);

            ProtocolEnvelope started = ProtocolEnvelope.Create(ProtocolMessageTypes.CalibrationStarted);
            started.CorrelationId = null;
            started.Data["sessionId"] = sessionId;
            started.Data["nozzle"] = nozzleName;
            started.Data["axisR"] = axisR;
            started.Data["totalPoints"] = totalPoints;
            started.Data["stepX"] = ProtocolNumber.Round3(stepX);
            started.Data["stepY"] = ProtocolNumber.Round3(stepY);
            started.Data["stepR"] = ProtocolNumber.Round3(stepR);

            PendingStartAck pending = new PendingStartAck(started.MessageId, sessionId);
            if (!_pendingStartAcks.TryAdd(started.MessageId, pending))
                throw new InvalidOperationException("Duplicate calibration.started messageId.");

            CalibrationStartAck ack;
            try
            {
                LocalDebugLog.Write(
                    "INFO",
                    "calibration.session",
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "Sending calibration.started: sessionId={0}, nozzle={1}, axisR={2}, step=({3:0.###},{4:0.###},{5:0.###}), timeout={6}ms",
                        sessionId,
                        nozzleName,
                        axisR,
                        stepX,
                        stepY,
                        stepR,
                        _startAckTimeoutMilliseconds));
                await _sendAsync(started, cancellationToken).ConfigureAwait(false);
                ack = await WaitForStartAckAsync(pending, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException ex)
            {
                string msg = "等待上位机确认标定会话超时（calibration.started.ack > " + _startAckTimeoutMilliseconds + " ms）。";
                OnLog("ERROR", msg);
                await SendCompletedEnvelopeAsync(
                    sessionId,
                    nozzle,
                    false,
                    ProtocolErrorCodes.SessionRejected,
                    msg,
                    null,
                    null,
                    null).ConfigureAwait(false);
                session.Dispose();
                throw new CalibrationWorkflowException(ProtocolErrorCodes.SessionRejected, msg, ex);
            }
            catch (Exception)
            {
                session.Dispose();
                throw;
            }
            finally
            {
                PendingStartAck ignored;
                _pendingStartAcks.TryRemove(started.MessageId, out ignored);
            }

            if (!ack.Accepted)
            {
                string reason = string.IsNullOrWhiteSpace(ack.Reason) ? "上位机未说明拒绝原因" : ack.Reason.Trim();
                OnLog("WARN", "上位机拒绝开始标定 (sessionId=" + sessionId + ", nozzle=" + nozzleName + "): " + reason);
                await SendCompletedEnvelopeAsync(
                    sessionId,
                    nozzle,
                    false,
                    ProtocolErrorCodes.SessionRejected,
                    reason,
                    ack.MessageId,
                    null,
                    null).ConfigureAwait(false);
                session.Dispose();
                throw new CalibrationWorkflowException(
                    ProtocolErrorCodes.SessionRejected,
                    "上位机拒绝开始标定：" + reason);
            }

            if (!IsFinite(ack.OriginX) || !IsFinite(ack.OriginY) || !IsFinite(ack.OriginR))
            {
                const string invalidOriginMsg = "calibration.started.ack 缺少有效的原点坐标 (originX/originY/originR)。";
                OnLog("ERROR", invalidOriginMsg);
                await SendCompletedEnvelopeAsync(
                    sessionId,
                    nozzle,
                    false,
                    ProtocolErrorCodes.SessionRejected,
                    invalidOriginMsg,
                    ack.MessageId,
                    null,
                    null).ConfigureAwait(false);
                session.Dispose();
                throw new CalibrationWorkflowException(ProtocolErrorCodes.SessionRejected, invalidOriginMsg);
            }

            session.OriginX = ack.OriginX;
            session.OriginY = ack.OriginY;
            session.OriginR = ack.OriginR;
            session.HasLatchedOrigin = true;

            lock (_sync)
            {
                _activeSession = session;
                _activeNozzle = nozzle;
                if (!isManual)
                {
                    ResetRecordsPendingLocked(nozzle, sessionId);
                }
            }

            if (!isManual)
            {
                foreach (CalibrationPointRecord record in GetRecordsSnapshot(nozzle))
                    OnPointChanged(nozzle, record);
            }

            LocalDebugLog.Write(
                "INFO",
                "calibration.session",
                string.Format(
                    CultureInfo.InvariantCulture,
                    "Session started & origin latched: sessionId={0}, nozzle={1}, axisR={2}, origin=({3:0.###},{4:0.###},{5:0.###}deg)",
                    session.SessionId,
                    nozzleName,
                    axisR,
                    session.OriginX,
                    session.OriginY,
                    session.OriginR));
            OnLog(
                "INFO",
                string.Format(
                    CultureInfo.InvariantCulture,
                    "标定会话已启动 [{0}] {1}({2})，锁存原点=({3:0.000}, {4:0.000}, {5:0.000}°)",
                    session.SessionId,
                    nozzleName,
                    axisR,
                    session.OriginX,
                    session.OriginY,
                    session.OriginR));

            return session;
        }

        private async Task<CalibrationPointRecord> CapturePointCoreAsync(
            CalibrationSession session,
            int pointNumber,
            CancellationToken cancellationToken)
        {
            string requestId = null;
            CalibrationPlanPoint point;
            lock (_sync) point = CalibrationPlan.Find(_plan, pointNumber);

            try
            {
                UpdateRecord(session.Nozzle, session.SessionId, point, CalibrationPointStatus.Moving, "等待上位机运动到位...");

                ProtocolEnvelope request = ProtocolEnvelope.Create(ProtocolMessageTypes.MoveRequest);
                request.CorrelationId = null;
                requestId = request.MessageId;
                request.Data["sessionId"] = session.SessionId;
                request.Data["axisR"] = session.AxisR;
                request.Data["pointNumber"] = point.PointNumber;
                request.Data["kind"] = point.Kind == CalibrationSampleKind.Translation ? "translation" : "rotation";
                request.Data["coordinateMode"] = "calibration-origin-relative";
                request.Data["offsetX"] = ProtocolNumber.Round3(point.OffsetX);
                request.Data["offsetY"] = ProtocolNumber.Round3(point.OffsetY);
                request.Data["offsetR"] = ProtocolNumber.Round3(point.OffsetR);
                request.Data["sharedOrigin"] = point.IsSharedOrigin;
                request.Data["replyType"] = ProtocolMessageTypes.MotionArrived;

                PendingMove pending = new PendingMove(request.MessageId, session.SessionId, point.PointNumber);
                if (!_pendingMoves.TryAdd(request.MessageId, pending))
                    throw new InvalidOperationException("A move request with the same message ID already exists.");

                MoveWaitOutcome outcome;
                try
                {
                    LocalDebugLog.Write(
                        "INFO",
                        "calibration.move",
                        string.Format(
                            CultureInfo.InvariantCulture,
                            "Sending move.request: session={0}, axisR={1}, P{2} ({3}), msgId={4}, offset=({5:0.###},{6:0.###},{7:0.###}deg), timeout={8}ms",
                            session.SessionId,
                            session.AxisR,
                            point.PointNumber,
                            point.Tag,
                            request.MessageId,
                            point.OffsetX,
                            point.OffsetY,
                            point.OffsetR,
                            _motionTimeoutMilliseconds));
                    await _sendAsync(request, cancellationToken).ConfigureAwait(false);
                    outcome = await WaitForMoveOutcomeAsync(pending, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    PendingMove ignored;
                    _pendingMoves.TryRemove(request.MessageId, out ignored);
                }

                if (outcome.Failure != null)
                {
                    string failDetail = "[" + outcome.Failure.Code + "] " + outcome.Failure.Message;
                    UpdateRecord(session.Nozzle, session.SessionId, point, CalibrationPointStatus.Failed, failDetail);
                    OnLog("ERROR", "上位机上报运动失败 (P" + point.PointNumber + ")：" + failDetail);
                    if (session.IsManual)
                    {
                        await EndSessionAndNotifyLockedOrUnlockedAsync(
                            session,
                            false,
                            ProtocolErrorCodes.MotionFailed,
                            failDetail,
                            outcome.Failure.MessageId,
                            null).ConfigureAwait(false);
                        throw new CalibrationWorkflowException(
                            ProtocolErrorCodes.MotionFailed,
                            "上位机拒绝或中止运动 " + failDetail);
                    }
                    throw new MotionFailedException(outcome.Failure);
                }

                MotionArrival arrival = outcome.Arrival;
                LocalDebugLog.Write(
                    "INFO",
                    "calibration.move",
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "Motion arrived: session={0}, axisR={1}, P{2} ({3}), corrId={4}, actual=({5:0.###},{6:0.###},{7:0.###}deg)",
                        session.SessionId,
                        session.AxisR,
                        point.PointNumber,
                        point.Tag,
                        arrival.CorrelationId,
                        arrival.X,
                        arrival.Y,
                        arrival.R));

                UpdateRecord(session.Nozzle, session.SessionId, point, CalibrationPointStatus.Capturing, "运动已到位，正在采图...");
                CameraFrame frame;
                try
                {
                    frame = await _captureAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception captureEx)
                {
                    captureEx.Data["CalibrationErrorCode"] = ProtocolErrorCodes.CameraCaptureFailed;
                    throw;
                }

                using (frame)
                {
                    PublishFrame(frame.Image, "calibration." + session.AxisR.ToLowerInvariant() + "." + point.PointNumber);
                    UpdateRecord(session.Nozzle, session.SessionId, point, CalibrationPointStatus.Locating, "正在模板定位...");

                    // 同一标定点重采前先在会话暂存副本中剔除旧样本，不触碰已提交旧标定
                    CalibrationJob baseline;
                    lock (_sync)
                    {
                        baseline = session.WorkingJob.DeepClone();
                        CalibrationSample oldSample = baseline.Samples == null
                            ? null
                            : baseline.Samples.LastOrDefault(item => item != null &&
                                string.Equals(item.Tag, point.Tag, StringComparison.OrdinalIgnoreCase));
                        if (oldSample != null) baseline = _sdk.RemoveSample(baseline, oldSample.SampleId);
                    }

                    double minScore = GetTemplateMinScore(baseline);
                    SampleCaptureResult capture = _sdk.CaptureSample(
                        baseline,
                        frame.Image,
                        arrival.X,
                        arrival.Y,
                        arrival.R,
                        point.Tag,
                        point.Kind);
                    bool scoreBelowThreshold = capture != null && capture.Match != null &&
                        IsFinite(capture.Match.Score) && capture.Match.Score < minScore;
                    if (capture == null || !capture.Success || capture.Match == null ||
                        !IsFinite(capture.Match.Score) || scoreBelowThreshold)
                    {
                        string failMsg;
                        if (scoreBelowThreshold && capture.Match.Score > 0.0)
                        {
                            failMsg = string.Format(
                                CultureInfo.InvariantCulture,
                                "NG：点 {0} 模板匹配分数 {1:0.###} 低于设定匹配分数 {2:0.###}，标定中断。",
                                point.PointNumber,
                                capture.Match.Score,
                                minScore);
                        }
                        else
                        {
                            string rawReason = capture == null || string.IsNullOrWhiteSpace(capture.Message)
                                ? "未找到满足最低匹配分数的目标"
                                : capture.Message.Trim();
                            failMsg = string.Format(
                                CultureInfo.InvariantCulture,
                                "NG：点 {0} 模板匹配失败（最低匹配分数 {1:0.###}）：{2}，标定中断。",
                                point.PointNumber,
                                minScore,
                                rawReason);
                        }
                        LocalDebugLog.Write(
                            "WARN",
                            "calibration.locate",
                            "Locate NG for " + session.AxisR + " P" + point.PointNumber + " (" + point.Tag + "): " + failMsg);
                        OnLog("ERROR", failMsg);
                        CalibrationPointRecord failed = UpdateRecord(
                            session.Nozzle,
                            session.SessionId,
                            point,
                            CalibrationPointStatus.Failed,
                            failMsg,
                            arrival,
                            capture);
                        await SendPointResultAsync(session, request.MessageId, failed, CancellationToken.None)
                            .ConfigureAwait(false);
                        if (session.IsManual)
                        {
                            await EndSessionAndNotifyLockedOrUnlockedAsync(
                                session,
                                false,
                                ProtocolErrorCodes.MatchFailed,
                                failMsg,
                                null,
                                null).ConfigureAwait(false);
                            throw new CalibrationWorkflowException(ProtocolErrorCodes.MatchFailed, failMsg);
                        }
                        return failed;
                    }

                    // 仅写入会话暂存 WorkingJob，严禁半途覆盖已提交标定与磁盘文件！
                    lock (_sync) session.WorkingJob = capture.Job.DeepClone();
                    CalibrationPointRecord completed = UpdateRecord(
                        session.Nozzle,
                        session.SessionId,
                        point,
                        CalibrationPointStatus.Completed,
                        "Captured",
                        arrival,
                        capture);
                    LocalDebugLog.Write(
                        "INFO",
                        "calibration.locate",
                        string.Format(
                            CultureInfo.InvariantCulture,
                            "Captured {0} P{1} ({2}): img=({3:0.###},{4:0.###},{5:0.###}deg), score={6:0.###}, mach=({7:0.###},{8:0.###},{9:0.###}deg)",
                            session.AxisR,
                            completed.PointNumber,
                            completed.Tag,
                            completed.ImageRow,
                            completed.ImageColumn,
                            completed.ImageAngle,
                            completed.MatchScore,
                            completed.MachineX,
                            completed.MachineY,
                            completed.MachineR));
                    await SendPointResultAsync(session, request.MessageId, completed, CancellationToken.None)
                        .ConfigureAwait(false);
                    return completed;
                }
            }
            catch (MotionFailedException)
            {
                throw;
            }
            catch (CalibrationWorkflowException)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                UpdateRecord(session.Nozzle, session.SessionId, point, CalibrationPointStatus.Failed, "Cancelled");
                throw;
            }
            catch (TimeoutException exception)
            {
                LocalDebugLog.WriteException("ERROR", "calibration.move", "Calibration point " + pointNumber + " timed out", exception);
                CalibrationPointRecord failed = UpdateRecord(
                    session.Nozzle,
                    session.SessionId,
                    point,
                    CalibrationPointStatus.Failed,
                    "MOTION_TIMEOUT: " + exception.Message);
                if (!string.IsNullOrWhiteSpace(requestId))
                    await SendPointResultAsync(session, requestId, failed, CancellationToken.None).ConfigureAwait(false);
                OnLog("ERROR", "Calibration point " + pointNumber + " failed: " + exception.Message);
                if (session.IsManual)
                {
                    await EndSessionAndNotifyLockedOrUnlockedAsync(
                        session,
                        false,
                        ProtocolErrorCodes.MotionTimeout,
                        exception.Message,
                        null,
                        null).ConfigureAwait(false);
                }
                throw;
            }
            catch (Exception exception)
            {
                LocalDebugLog.WriteException("ERROR", "calibration.point", "Calibration point " + pointNumber + " failed", exception);
                CalibrationPointRecord failed = UpdateRecord(
                    session.Nozzle,
                    session.SessionId,
                    point,
                    CalibrationPointStatus.Failed,
                    exception.Message);
                if (!string.IsNullOrWhiteSpace(requestId))
                    await SendPointResultAsync(session, requestId, failed, CancellationToken.None).ConfigureAwait(false);
                OnLog("ERROR", "Calibration point " + pointNumber + " failed: " + exception.Message);
                throw;
            }
        }

        private async Task<CalibrationResult> SolveAndCompleteSessionAsync(
            CalibrationSession session,
            CancellationToken cancellationToken)
        {
            CalibrationJob workingJob;
            lock (_sync) workingJob = session.WorkingJob.DeepClone();

            CalibrationResult result = await Task.Run(() => _sdk.Calculate(workingJob), cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (result != null && result.Success)
            {
                // 仅当全部求解通过且未收到取消指令时才原子落盘并提交生效，彻底避免半途覆盖旧标定
                if (_saveJobAsync != null)
                {
                    await _saveJobAsync(session.Nozzle, workingJob.DeepClone()).ConfigureAwait(false);
                }

                lock (_sync)
                {
                    _committedJobs[session.Nozzle] = workingJob.DeepClone();
                    if (ReferenceEquals(_activeSession, session)) _activeSession = null;
                }

                OnCalculationCompleted(session.Nozzle, result, session.SessionId, ProtocolErrorCodes.None);
                await SendCompletedOnceAsync(
                    session,
                    true,
                    ProtocolErrorCodes.None,
                    result.Message ?? "Calibration succeeded.",
                    null,
                    result,
                    workingJob.SolverOptions).ConfigureAwait(false);
                session.Dispose();
                return result;
            }

            // 求解失败：保留旧标定结果不覆盖，通知上位机退出标定模式
            string failMessage = result == null
                ? "Calibration solve returned null."
                : ("[" + result.ErrorCode + "] " + (result.Message ?? "Calibration solve failed."));
            lock (_sync)
            {
                if (ReferenceEquals(_activeSession, session)) _activeSession = null;
            }
            OnLog("WARN", ProtocolNozzle.GetDisplayName(session.Nozzle) + " 标定求解失败，已保留原有标定结果：" + failMessage);
            OnCalculationCompleted(session.Nozzle, result, session.SessionId, ProtocolErrorCodes.CalibrationSolveFailed);
            await SendCompletedOnceAsync(
                session,
                false,
                ProtocolErrorCodes.CalibrationSolveFailed,
                failMessage,
                null,
                result,
                workingJob.SolverOptions).ConfigureAwait(false);
            session.Dispose();
            return result;
        }

        private async Task EndSessionAndNotifyLockedOrUnlockedAsync(
            CalibrationSession session,
            bool success,
            string errorCode,
            string message,
            string correlationId,
            CalibrationResult result)
        {
            if (session == null) return;
            lock (_sync)
            {
                if (ReferenceEquals(_activeSession, session)) _activeSession = null;
            }

            if (!success)
            {
                OnLog("WARN", "标定会话终止 [" + session.SessionId + "] (" + errorCode + ")，已保留原有标定结果：" + message);
            }

            await SendCompletedOnceAsync(
                session,
                success,
                errorCode,
                message,
                correlationId,
                result,
                null).ConfigureAwait(false);
            session.Dispose();
        }

        private void ClearSessionWithoutSend(CalibrationSession session, string reason)
        {
            if (session == null) return;
            lock (_sync)
            {
                if (ReferenceEquals(_activeSession, session)) _activeSession = null;
            }
            session.TryMarkCompletedSent();
            OnLog("WARN", "标定会话已中止 [" + session.SessionId + "] (" + ProtocolErrorCodes.Disconnected + ")，已保留旧标定：" + reason);
            session.Dispose();
        }

        private bool HandleCalibrationStartedAck(ProtocolEnvelope envelope)
        {
            PendingStartAck pending;
            if (string.IsNullOrWhiteSpace(envelope.CorrelationId) ||
                !_pendingStartAcks.TryGetValue(envelope.CorrelationId, out pending))
            {
                OnLog("WARN", "Ignored calibration.started.ack with unknown or missing correlationId.");
                return true;
            }

            string sessionId = ProtocolValue.String(envelope.Data, "sessionId", string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(sessionId) &&
                !string.Equals(sessionId, pending.SessionId, StringComparison.OrdinalIgnoreCase))
            {
                OnLog("WARN", "Ignored calibration.started.ack due to sessionId mismatch: expected=" + pending.SessionId + ", actual=" + sessionId);
                return true;
            }

            bool accepted;
            if (!ProtocolValue.TryBool(envelope.Data, "accepted", out accepted))
            {
                OnLog("WARN", "Ignored calibration.started.ack missing boolean 'accepted' field.");
                return true;
            }

            double originX = double.NaN;
            double originY = double.NaN;
            double originR = double.NaN;
            if (accepted)
            {
                double x;
                double y;
                double r;
                if (ProtocolValue.TryDouble(envelope.Data, "originX", out x) &&
                    ProtocolValue.TryDouble(envelope.Data, "originY", out y) &&
                    ProtocolValue.TryDouble(envelope.Data, "originR", out r))
                {
                    originX = x;
                    originY = y;
                    originR = r;
                }
            }

            string reason = ProtocolValue.String(envelope.Data, "reason", string.Empty);
            return pending.Completion.TrySetResult(new CalibrationStartAck
            {
                MessageId = envelope.MessageId,
                CorrelationId = envelope.CorrelationId,
                SessionId = string.IsNullOrWhiteSpace(sessionId) ? pending.SessionId : sessionId,
                Accepted = accepted,
                OriginX = originX,
                OriginY = originY,
                OriginR = originR,
                Reason = reason
            });
        }

        private bool HandleMotionArrived(ProtocolEnvelope envelope)
        {
            PendingMove pending;
            if (string.IsNullOrWhiteSpace(envelope.CorrelationId) ||
                !_pendingMoves.TryGetValue(envelope.CorrelationId, out pending))
            {
                OnLog("WARN", "Ignored motion.arrived with an unknown or missing correlationId.");
                return true;
            }

            string sessionId = ProtocolValue.String(envelope.Data, "sessionId", string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(sessionId) &&
                !string.Equals(sessionId, pending.SessionId, StringComparison.OrdinalIgnoreCase))
            {
                OnLog("WARN", "Ignored motion.arrived with mismatched sessionId: expected=" + pending.SessionId + ", actual=" + sessionId);
                return true;
            }

            // 必须同时匹配 CorrelationId、点号且三轴坐标为有限实数，才唤醒对应等待点位
            int pointNumber = ProtocolValue.Integer(envelope.Data, "pointNumber", int.MinValue);
            double x;
            double y;
            double r;
            if (pointNumber != pending.PointNumber ||
                !ProtocolValue.TryDouble(envelope.Data, "x", out x) ||
                !ProtocolValue.TryDouble(envelope.Data, "y", out y) ||
                !ProtocolValue.TryDouble(envelope.Data, "r", out r) ||
                !IsFinite(x) || !IsFinite(y) || !IsFinite(r))
            {
                OnLog("WARN", "Ignored invalid motion.arrived for calibration point " + pending.PointNumber + ".");
                return true;
            }

            return pending.Completion.TrySetResult(MoveWaitOutcome.FromArrival(new MotionArrival
            {
                CorrelationId = envelope.CorrelationId,
                SessionId = string.IsNullOrWhiteSpace(sessionId) ? pending.SessionId : sessionId,
                PointNumber = pointNumber,
                X = x,
                Y = y,
                R = r
            }));
        }

        private bool HandleMotionFailed(ProtocolEnvelope envelope)
        {
            PendingMove pending = null;
            if (!string.IsNullOrWhiteSpace(envelope.CorrelationId))
            {
                _pendingMoves.TryGetValue(envelope.CorrelationId, out pending);
            }

            if (pending == null)
            {
                OnLog("WARN", "Ignored motion.failed with an unknown or missing correlationId.");
                return true;
            }

            string sessionId = ProtocolValue.String(envelope.Data, "sessionId", string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(sessionId) &&
                !string.Equals(sessionId, pending.SessionId, StringComparison.OrdinalIgnoreCase))
            {
                OnLog("WARN", "Ignored motion.failed with mismatched sessionId: expected=" + pending.SessionId + ", actual=" + sessionId);
                return true;
            }

            int pointNumber = ProtocolValue.Integer(envelope.Data, "pointNumber", pending.PointNumber);
            string rawCode = ProtocolValue.String(envelope.Data, "code", "MOTION_FAILED").Trim();
            string code = string.IsNullOrWhiteSpace(rawCode) ? "MOTION_FAILED" : rawCode.ToUpperInvariant();
            string message = ProtocolValue.String(envelope.Data, "message", "Host reported motion failure.");

            return pending.Completion.TrySetResult(MoveWaitOutcome.FromFailure(new MotionFailure
            {
                MessageId = envelope.MessageId,
                CorrelationId = envelope.CorrelationId,
                SessionId = string.IsNullOrWhiteSpace(sessionId) ? pending.SessionId : sessionId,
                PointNumber = pointNumber,
                Code = code,
                Message = message
            }));
        }

        private async Task<CalibrationStartAck> WaitForStartAckAsync(
            PendingStartAck pending,
            CancellationToken cancellationToken)
        {
            using (CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                Task delay = Task.Delay(_startAckTimeoutMilliseconds, timeout.Token);
                Task completed = await Task.WhenAny(pending.Completion.Task, delay).ConfigureAwait(false);
                if (completed == pending.Completion.Task)
                {
                    timeout.Cancel();
                    return await pending.Completion.Task.ConfigureAwait(false);
                }
                cancellationToken.ThrowIfCancellationRequested();
                throw new TimeoutException("Timed out waiting for calibration.started.ack.");
            }
        }

        private async Task<MoveWaitOutcome> WaitForMoveOutcomeAsync(
            PendingMove pending,
            CancellationToken cancellationToken)
        {
            using (CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                Task delay = Task.Delay(_motionTimeoutMilliseconds, timeout.Token);
                Task completed = await Task.WhenAny(pending.Completion.Task, delay).ConfigureAwait(false);
                if (completed == pending.Completion.Task)
                {
                    timeout.Cancel();
                    return await pending.Completion.Task.ConfigureAwait(false);
                }
                cancellationToken.ThrowIfCancellationRequested();
                throw new TimeoutException("Timed out waiting for motion.arrived.");
            }
        }

        private async Task SendPointResultAsync(
            CalibrationSession session,
            string correlationId,
            CalibrationPointRecord record,
            CancellationToken cancellationToken)
        {
            ProtocolEnvelope response = ProtocolEnvelope.Create(ProtocolMessageTypes.CalibrationPointResult);
            response.CorrelationId = correlationId;
            response.Data["sessionId"] = session == null ? string.Empty : session.SessionId;
            response.Data["axisR"] = session == null ? ProtocolNozzle.GetAxisR(record.Nozzle) : session.AxisR;
            response.Data["pointNumber"] = record.PointNumber;
            response.Data["success"] = record.Status == CalibrationPointStatus.Completed;
            response.Data["status"] = record.Status.ToString();
            response.Data["machineX"] = ProtocolNumber.Round3(IsFinite(record.MachineX) ? record.MachineX : 0.0);
            response.Data["machineY"] = ProtocolNumber.Round3(IsFinite(record.MachineY) ? record.MachineY : 0.0);
            response.Data["machineR"] = ProtocolNumber.Round3(IsFinite(record.MachineR) ? record.MachineR : 0.0);
            response.Data["imageRow"] = ProtocolNumber.Round2(IsFinite(record.ImageRow) ? record.ImageRow : 0.0);
            response.Data["imageColumn"] = ProtocolNumber.Round2(IsFinite(record.ImageColumn) ? record.ImageColumn : 0.0);
            response.Data["imageAngle"] = ProtocolNumber.Round2(IsFinite(record.ImageAngle) ? record.ImageAngle : 0.0);
            response.Data["score"] = ProtocolNumber.Round3(IsFinite(record.MatchScore) ? record.MatchScore : 0.0);
            response.Data["message"] = record.Message ?? string.Empty;
            try
            {
                await _sendAsync(response, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                OnLog("WARN", "Calibration point " + record.PointNumber +
                    " result message could not be sent: " + exception.Message);
            }
        }

        private Task SendCompletedOnceAsync(
            CalibrationSession session,
            bool success,
            string errorCode,
            string message,
            string correlationId,
            CalibrationResult result,
            CalibrationSolverOptions solverOptions)
        {
            if (session == null || !session.TryMarkCompletedSent()) return Task.CompletedTask;
            string effectiveCorrelationId = correlationId;
            if (string.IsNullOrWhiteSpace(effectiveCorrelationId))
            {
                lock (_sync)
                {
                    if (!string.IsNullOrWhiteSpace(session.CancelCorrelationId))
                    {
                        effectiveCorrelationId = session.CancelCorrelationId;
                    }
                }
            }
            return SendCompletedEnvelopeAsync(
                session.SessionId,
                session.Nozzle,
                success,
                errorCode,
                message,
                effectiveCorrelationId,
                result,
                solverOptions);
        }

        private async Task SendCompletedEnvelopeAsync(
            string sessionId,
            TeachingSide nozzle,
            bool success,
            string errorCode,
            string message,
            string correlationId,
            CalibrationResult result,
            CalibrationSolverOptions solverOptions)
        {
            CalibrationRmsAssessment rms = result == null ? null : result.EvaluateRms(solverOptions);
            ProtocolEnvelope response = ProtocolEnvelope.Create(ProtocolMessageTypes.CalibrationCompleted);
            response.CorrelationId = string.IsNullOrWhiteSpace(correlationId) ? null : correlationId;
            response.Data["sessionId"] = sessionId ?? string.Empty;
            response.Data["nozzle"] = ProtocolNozzle.GetNozzleName(nozzle);
            response.Data["axisR"] = ProtocolNozzle.GetAxisR(nozzle);
            response.Data["success"] = success;
            response.Data["errorCode"] = string.IsNullOrWhiteSpace(errorCode)
                ? (success ? ProtocolErrorCodes.None : ProtocolErrorCodes.CalibrationSolveFailed)
                : errorCode.Trim().ToUpperInvariant();
            response.Data["message"] = message ?? string.Empty;
            response.Data["resolutionX"] = result == null ? 0.0 : ProtocolNumber.Round6(result.ResolutionX);
            response.Data["resolutionY"] = result == null ? 0.0 : ProtocolNumber.Round6(result.ResolutionY);
            response.Data["rmsResidualPixels"] = result == null ? 0.0 : ProtocolNumber.Round3(result.RmsResidualPixels);
            response.Data["rmsResidualStage"] = result == null ? 0.0 : ProtocolNumber.Round3(result.RmsResidualStage);
            response.Data["maxResidualPixels"] = result == null ? 0.0 : ProtocolNumber.Round3(result.MaxResidualPixels);
            response.Data["rotationCenterRmsPixels"] = result == null || result.Rotation == null
                ? 0.0
                : ProtocolNumber.Round3(result.Rotation.CenterRmsResidualPixels);
            response.Data["rotationAngleRmsDegrees"] = result == null || result.Rotation == null
                ? 0.0
                : ProtocolNumber.Round3(result.Rotation.AngleRmsDegrees);
            response.Data["rmsScore"] = rms == null ? 0 : rms.Score;
            response.Data["rmsGrade"] = rms == null ? string.Empty : (rms.GradeLabel ?? string.Empty);
            response.Data["rmsReasonable"] = rms != null && rms.IsReasonable;
            response.Data["rmsSummary"] = rms == null ? string.Empty : (rms.Summary ?? string.Empty);
            try
            {
                await _sendAsync(response, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                OnLog("WARN", "Calibration completion message could not be sent: " + exception.Message);
            }
        }

        private void SyncPlanRecordsLocked(TeachingSide nozzle, IReadOnlyList<CalibrationPlanPoint> plan)
        {
            Dictionary<int, CalibrationPointRecord> map = _records[nozzle];
            string axisR = ProtocolNozzle.GetAxisR(nozzle);
            foreach (CalibrationPlanPoint point in plan)
            {
                CalibrationPointRecord existing;
                if (!map.TryGetValue(point.PointNumber, out existing))
                {
                    existing = new CalibrationPointRecord
                    {
                        Nozzle = nozzle,
                        AxisR = axisR,
                        PointNumber = point.PointNumber,
                        Kind = point.Kind,
                        Tag = point.Tag,
                        Status = CalibrationPointStatus.Pending
                    };
                    map[point.PointNumber] = existing;
                }
                existing.Nozzle = nozzle;
                existing.AxisR = axisR;
                existing.OffsetX = point.OffsetX;
                existing.OffsetY = point.OffsetY;
                existing.OffsetR = point.OffsetR;
            }
        }

        private void RebuildRecordsFromJobLocked(TeachingSide nozzle, CalibrationJob job)
        {
            Dictionary<int, CalibrationPointRecord> map = _records[nozzle];
            long templateRevision = job.Template == null ? 0 : job.Template.Revision;
            string axisR = ProtocolNozzle.GetAxisR(nozzle);
            foreach (CalibrationPointRecord record in map.Values)
            {
                record.Nozzle = nozzle;
                record.AxisR = axisR;
                record.SessionId = string.Empty;
                record.Status = CalibrationPointStatus.Pending;
                record.MachineX = 0.0;
                record.MachineY = 0.0;
                record.MachineR = 0.0;
                record.ImageRow = 0.0;
                record.ImageColumn = 0.0;
                record.ImageAngle = 0.0;
                record.MatchScore = 0.0;
                record.CapturedAtUtc = null;
                record.Message = string.Empty;

                CalibrationSample sample = job.Samples == null
                    ? null
                    : job.Samples.LastOrDefault(item => item != null &&
                        item.Enabled &&
                        item.Kind == record.Kind &&
                        item.IsCurrent(templateRevision) &&
                        string.Equals(item.Tag, record.Tag, StringComparison.OrdinalIgnoreCase));
                if (sample == null || sample.MachinePose == null || sample.ImagePose == null
                    || !sample.MachinePose.IsFinite || !sample.ImagePose.IsFinite
                    || !IsFinite(sample.MatchScore)) continue;
                record.MachineX = sample.MachinePose.X;
                record.MachineY = sample.MachinePose.Y;
                record.MachineR = sample.MachinePose.ThetaDegrees;
                record.ImageRow = sample.ImagePose.Row;
                record.ImageColumn = sample.ImagePose.Column;
                record.ImageAngle = sample.ImagePose.AngleDegrees;
                record.MatchScore = sample.MatchScore;
                record.CapturedAtUtc = sample.CapturedAtUtc;
                record.Status = CalibrationPointStatus.Completed;
                record.Message = "Loaded";
            }
        }

        private void ResetRecordsPendingLocked(TeachingSide nozzle, string sessionId)
        {
            string axisR = ProtocolNozzle.GetAxisR(nozzle);
            foreach (CalibrationPointRecord record in _records[nozzle].Values)
            {
                record.Nozzle = nozzle;
                record.AxisR = axisR;
                record.SessionId = sessionId;
                record.Status = CalibrationPointStatus.Pending;
                record.MachineX = 0.0;
                record.MachineY = 0.0;
                record.MachineR = 0.0;
                record.ImageRow = 0.0;
                record.ImageColumn = 0.0;
                record.ImageAngle = 0.0;
                record.MatchScore = 0.0;
                record.CapturedAtUtc = null;
                record.Message = string.Empty;
            }
        }

        private CalibrationPointRecord UpdateRecord(
            TeachingSide nozzle,
            string sessionId,
            CalibrationPlanPoint point,
            CalibrationPointStatus status,
            string message,
            MotionArrival arrival = null,
            SampleCaptureResult capture = null)
        {
            CalibrationPointRecord snapshot;
            lock (_sync)
            {
                CalibrationPointRecord record = _records[nozzle][point.PointNumber];
                record.Nozzle = nozzle;
                record.AxisR = ProtocolNozzle.GetAxisR(nozzle);
                record.SessionId = sessionId ?? string.Empty;
                record.Status = status;
                record.Message = message;
                if (arrival != null)
                {
                    record.MachineX = arrival.X;
                    record.MachineY = arrival.Y;
                    record.MachineR = arrival.R;
                }
                if (status == CalibrationPointStatus.Completed && capture != null && capture.Match != null)
                {
                    record.ImageRow = capture.Match.Row;
                    record.ImageColumn = capture.Match.Column;
                    record.ImageAngle = capture.Match.AngleDegrees;
                    record.MatchScore = capture.Match.Score;
                    record.CapturedAtUtc = DateTime.UtcNow;
                }
                else if (status == CalibrationPointStatus.Failed)
                {
                    bool hasLowScoreLocation = capture != null && capture.Match != null &&
                        IsFinite(capture.Match.Score) && capture.Match.Score > 0.0 &&
                        capture.Match.Anchor.IsFinite;
                    if (hasLowScoreLocation)
                    {
                        record.ImageRow = capture.Match.Row;
                        record.ImageColumn = capture.Match.Column;
                        record.ImageAngle = IsFinite(capture.Match.AngleDegrees) ? capture.Match.AngleDegrees : double.NaN;
                        record.MatchScore = capture.Match.Score;
                    }
                    else
                    {
                        record.ImageRow = double.NaN;
                        record.ImageColumn = double.NaN;
                        record.ImageAngle = double.NaN;
                        record.MatchScore = (capture != null && capture.Match != null && IsFinite(capture.Match.Score))
                            ? capture.Match.Score
                            : 0.0;
                    }
                    record.CapturedAtUtc = null;
                }
                snapshot = record.DeepClone();
            }
            OnPointChanged(nozzle, snapshot);
            return snapshot;
        }

        private string GenerateSessionId()
        {
            int seq = Interlocked.Increment(ref _sessionSequence);
            return string.Format(
                CultureInfo.InvariantCulture,
                "cal-{0:yyyyMMdd}-{1:0000}",
                DateTime.Now,
                seq);
        }

        private void CancelAllPendingWaits(string reason)
        {
            foreach (KeyValuePair<string, PendingStartAck> entry in _pendingStartAcks)
            {
                entry.Value.Completion.TrySetCanceled();
            }
            foreach (KeyValuePair<string, PendingMove> entry in _pendingMoves)
            {
                entry.Value.Completion.TrySetCanceled();
            }
        }

        private void PublishFrame(Bitmap image, string source)
        {
            EventHandler<FrameEventArgs> handler = FrameCaptured;
            if (handler == null || image == null) return;
            using (FrameEventArgs args = new FrameEventArgs((Bitmap)image.Clone(), source))
                handler(this, args);
        }

        private void OnPointChanged(TeachingSide nozzle, CalibrationPointRecord record)
        {
            EventHandler<CalibrationPointEventArgs> handler = PointChanged;
            if (handler != null) handler(this, new CalibrationPointEventArgs(nozzle, record));
        }

        private void OnCalculationCompleted(
            TeachingSide nozzle,
            CalibrationResult result,
            string sessionId,
            string protocolErrorCode)
        {
            EventHandler<CalibrationResultEventArgs> handler = CalculationCompleted;
            if (handler != null) handler(this, new CalibrationResultEventArgs(nozzle, result, sessionId, protocolErrorCode));
        }

        private void OnLog(string level, string message)
        {
            EventHandler<LogEventArgs> handler = Log;
            if (handler != null) handler(this, new LogEventArgs(level, message));
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(CalibrationCoordinator));
        }

        private static double GetTemplateMinScore(CalibrationJob job)
        {
            if (job == null || job.Template == null || job.Template.MatchParameters == null) return 0.5;
            double configured = job.Template.MatchParameters.MinScore;
            if (!IsFinite(configured)) return 0.5;
            return Math.Max(0.0, Math.Min(1.0, configured));
        }

        private static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }

        /// <summary>取消进行中的标定会话与挂起等待，并释放同步资源。</summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            CalibrationSession session;
            lock (_sync)
            {
                session = _activeSession;
                _activeSession = null;
            }
            if (session != null)
            {
                try { session.Cancellation.Cancel(); } catch (ObjectDisposedException) { }
                session.Dispose();
            }
            CancelAllPendingWaits("Disposed");
            _captureGate.Dispose();
            _autoGate.Dispose();
        }

        private sealed class CalibrationSession : IDisposable
        {
            private int _completedSent;
            private bool _disposed;

            public CalibrationSession(
                string sessionId,
                TeachingSide nozzle,
                string axisR,
                bool isManual,
                CalibrationJob workingJob)
            {
                SessionId = sessionId;
                Nozzle = nozzle;
                AxisR = axisR;
                IsManual = isManual;
                WorkingJob = workingJob;
                Cancellation = new CancellationTokenSource();
            }

            public string SessionId { get; private set; }
            public TeachingSide Nozzle { get; private set; }
            public string AxisR { get; private set; }
            public bool IsManual { get; private set; }
            public bool HasLatchedOrigin { get; set; }
            public double OriginX { get; set; }
            public double OriginY { get; set; }
            public double OriginR { get; set; }
            public CalibrationJob WorkingJob { get; set; }
            public CancellationTokenSource Cancellation { get; private set; }
            public string CancelCorrelationId { get; set; }
            public string CancelReason { get; set; }
            public bool Disconnected { get; set; }

            public bool TryMarkCompletedSent()
            {
                return Interlocked.Exchange(ref _completedSent, 1) == 0;
            }

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                Cancellation.Dispose();
            }
        }

        private sealed class PendingStartAck
        {
            public PendingStartAck(string requestId, string sessionId)
            {
                RequestId = requestId;
                SessionId = sessionId;
                Completion = new TaskCompletionSource<CalibrationStartAck>(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            public string RequestId { get; private set; }
            public string SessionId { get; private set; }
            public TaskCompletionSource<CalibrationStartAck> Completion { get; private set; }
        }

        private sealed class PendingMove
        {
            public PendingMove(string requestId, string sessionId, int pointNumber)
            {
                RequestId = requestId;
                SessionId = sessionId;
                PointNumber = pointNumber;
                Completion = new TaskCompletionSource<MoveWaitOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            public string RequestId { get; private set; }
            public string SessionId { get; private set; }
            public int PointNumber { get; private set; }
            public TaskCompletionSource<MoveWaitOutcome> Completion { get; private set; }
        }

        private sealed class MoveWaitOutcome
        {
            public MotionArrival Arrival { get; private set; }
            public MotionFailure Failure { get; private set; }

            public static MoveWaitOutcome FromArrival(MotionArrival arrival)
            {
                return new MoveWaitOutcome { Arrival = arrival };
            }

            public static MoveWaitOutcome FromFailure(MotionFailure failure)
            {
                return new MoveWaitOutcome { Failure = failure };
            }
        }

        private sealed class MotionFailedException : Exception
        {
            public MotionFailedException(MotionFailure failure)
                : base(failure == null ? "Motion failed." : ("[" + failure.Code + "] " + failure.Message))
            {
                Failure = failure;
            }

            public MotionFailure Failure { get; private set; }
        }
    }
}
