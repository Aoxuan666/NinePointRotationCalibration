using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NinePointRotationCalibration.Station.Models;

namespace NinePointRotationCalibration.Station.Services.Calibration
{
    public sealed class CalibrationCoordinator : IDisposable
    {
        private readonly object _sync = new object();
        private readonly NinePointRotationCalibrationSdk _sdk;
        private readonly Func<ProtocolEnvelope, CancellationToken, Task> _sendAsync;
        private readonly Func<CancellationToken, Task<CameraFrame>> _captureAsync;
        private readonly Func<CalibrationJob, Task> _saveJobAsync;
        private readonly ConcurrentDictionary<string, PendingMove> _pendingMoves;
        private readonly SemaphoreSlim _captureGate;
        private readonly SemaphoreSlim _autoGate;
        private readonly Dictionary<int, CalibrationPointRecord> _records;
        private CancellationTokenSource _automaticCancellation;
        private CalibrationJob _job;
        private IReadOnlyList<CalibrationPlanPoint> _plan;
        private int _motionTimeoutMilliseconds;
        private bool _disposed;

        public CalibrationCoordinator(
            NinePointRotationCalibrationSdk sdk,
            Func<ProtocolEnvelope, CancellationToken, Task> sendAsync,
            Func<CancellationToken, Task<CameraFrame>> captureAsync,
            Func<CalibrationJob, Task> saveJobAsync,
            StationOptions options,
            CalibrationJob job)
        {
            _sdk = sdk ?? throw new ArgumentNullException(nameof(sdk));
            _sendAsync = sendAsync ?? throw new ArgumentNullException(nameof(sendAsync));
            _captureAsync = captureAsync ?? throw new ArgumentNullException(nameof(captureAsync));
            _saveJobAsync = saveJobAsync;
            _pendingMoves = new ConcurrentDictionary<string, PendingMove>(StringComparer.OrdinalIgnoreCase);
            _captureGate = new SemaphoreSlim(1, 1);
            _autoGate = new SemaphoreSlim(1, 1);
            _records = new Dictionary<int, CalibrationPointRecord>();
            UpdateOptions(options ?? new StationOptions());
            SetJob(job ?? new CalibrationJob());
        }

        public event EventHandler<CalibrationPointEventArgs> PointChanged;
        public event EventHandler<CalibrationResultEventArgs> CalculationCompleted;
        public event EventHandler<FrameEventArgs> FrameCaptured;
        public event EventHandler<LogEventArgs> Log;

        public bool IsAutomaticRunning
        {
            get
            {
                lock (_sync) return _automaticCancellation != null;
            }
        }

        public CalibrationJob GetJobSnapshot()
        {
            lock (_sync) return _job.DeepClone();
        }

        public IReadOnlyList<CalibrationPointRecord> GetRecordsSnapshot()
        {
            lock (_sync)
            {
                return _records.Values
                    .OrderBy(record => record.PointNumber)
                    .Select(record => record.DeepClone())
                    .ToList();
            }
        }

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
                _motionTimeoutMilliseconds = Math.Max(1000, options.MotionTimeoutMilliseconds);
                foreach (CalibrationPlanPoint point in plan)
                {
                    CalibrationPointRecord existing;
                    if (!_records.TryGetValue(point.PointNumber, out existing))
                    {
                        existing = new CalibrationPointRecord
                        {
                            PointNumber = point.PointNumber,
                            Kind = point.Kind,
                            Tag = point.Tag,
                            Status = CalibrationPointStatus.Pending
                        };
                        _records[point.PointNumber] = existing;
                    }
                    existing.OffsetX = point.OffsetX;
                    existing.OffsetY = point.OffsetY;
                    existing.OffsetR = point.OffsetR;
                }
            }
        }

        public void SetJob(CalibrationJob job)
        {
            if (job == null) throw new ArgumentNullException(nameof(job));
            lock (_sync)
            {
                _job = job.DeepClone();
                foreach (CalibrationPointRecord record in _records.Values)
                {
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

                    CalibrationSample sample = _job.Samples == null
                        ? null
                        : _job.Samples.LastOrDefault(item => item != null &&
                            string.Equals(item.Tag, record.Tag, StringComparison.OrdinalIgnoreCase));
                    if (sample == null) continue;
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
        }

        public async Task<CalibrationPointRecord> CapturePointAsync(int pointNumber, CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            await _captureGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            string requestId = null;
            try
            {
                CalibrationPlanPoint point;
                lock (_sync) point = CalibrationPlan.Find(_plan, pointNumber);
                UpdateRecord(point, CalibrationPointStatus.Moving, "Waiting for host motion.");

                ProtocolEnvelope request = ProtocolEnvelope.Create(ProtocolMessageTypes.MoveRequest);
                requestId = request.MessageId;
                request.Data["pointNumber"] = point.PointNumber;
                request.Data["kind"] = point.Kind == CalibrationSampleKind.Translation ? "translation" : "rotation";
                request.Data["coordinateMode"] = "calibration-origin-relative";
                request.Data["offsetX"] = point.OffsetX;
                request.Data["offsetY"] = point.OffsetY;
                request.Data["offsetR"] = point.OffsetR;
                request.Data["sharedOrigin"] = point.IsSharedOrigin;
                request.Data["replyType"] = ProtocolMessageTypes.MotionArrived;

                PendingMove pending = new PendingMove(request.MessageId, point.PointNumber);
                if (!_pendingMoves.TryAdd(request.MessageId, pending))
                    throw new InvalidOperationException("A move request with the same message ID already exists.");

                MotionArrival arrival;
                try
                {
                    await _sendAsync(request, cancellationToken).ConfigureAwait(false);
                    arrival = await WaitForArrivalAsync(pending, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    PendingMove ignored;
                    _pendingMoves.TryRemove(request.MessageId, out ignored);
                }

                UpdateRecord(point, CalibrationPointStatus.Capturing, "Motion arrived; capturing image.");
                using (CameraFrame frame = await _captureAsync(cancellationToken).ConfigureAwait(false))
                {
                    PublishFrame(frame.Image, "calibration." + point.PointNumber);
                    UpdateRecord(point, CalibrationPointStatus.Locating, "Locating calibration template.");

                    CalibrationJob baseline;
                    lock (_sync)
                    {
                        baseline = _job.DeepClone();
                        CalibrationSample oldSample = baseline.Samples == null
                            ? null
                            : baseline.Samples.LastOrDefault(item => item != null &&
                                string.Equals(item.Tag, point.Tag, StringComparison.OrdinalIgnoreCase));
                        if (oldSample != null) baseline = _sdk.RemoveSample(baseline, oldSample.SampleId);
                    }

                    SampleCaptureResult capture = _sdk.CaptureSample(
                        baseline,
                        frame.Image,
                        arrival.X,
                        arrival.Y,
                        arrival.R,
                        point.Tag,
                        point.Kind);
                    if (!capture.Success)
                    {
                        CalibrationPointRecord failed = UpdateRecord(
                            point,
                            CalibrationPointStatus.Failed,
                            capture.ErrorCode + ": " + capture.Message);
                        await SendPointResultAsync(request.MessageId, failed, CancellationToken.None).ConfigureAwait(false);
                        return failed;
                    }

                    lock (_sync) _job = capture.Job.DeepClone();
                    CalibrationPointRecord completed = UpdateRecord(
                        point,
                        CalibrationPointStatus.Completed,
                        "Captured",
                        arrival,
                        capture);
                    if (_saveJobAsync != null) await _saveJobAsync(capture.Job.DeepClone()).ConfigureAwait(false);
                    await SendPointResultAsync(request.MessageId, completed, CancellationToken.None).ConfigureAwait(false);
                    return completed;
                }
            }
            catch (OperationCanceledException)
            {
                CalibrationPlanPoint point;
                lock (_sync) point = CalibrationPlan.Find(_plan, pointNumber);
                CalibrationPointRecord failed = UpdateRecord(point, CalibrationPointStatus.Failed, "Cancelled");
                if (!string.IsNullOrWhiteSpace(requestId))
                    await SendPointResultAsync(requestId, failed, CancellationToken.None).ConfigureAwait(false);
                throw;
            }
            catch (TimeoutException exception)
            {
                CalibrationPlanPoint point;
                lock (_sync) point = CalibrationPlan.Find(_plan, pointNumber);
                CalibrationPointRecord failed = UpdateRecord(
                    point,
                    CalibrationPointStatus.Failed,
                    "MOTION_TIMEOUT: " + exception.Message);
                if (!string.IsNullOrWhiteSpace(requestId))
                    await SendPointResultAsync(requestId, failed, CancellationToken.None).ConfigureAwait(false);
                OnLog("ERROR", "Calibration point " + pointNumber + " failed: " + exception.Message);
                throw;
            }
            catch (Exception exception)
            {
                CalibrationPlanPoint point;
                lock (_sync) point = CalibrationPlan.Find(_plan, pointNumber);
                CalibrationPointRecord failed = UpdateRecord(point, CalibrationPointStatus.Failed, exception.Message);
                if (!string.IsNullOrWhiteSpace(requestId))
                    await SendPointResultAsync(requestId, failed, CancellationToken.None).ConfigureAwait(false);
                OnLog("ERROR", "Calibration point " + pointNumber + " failed: " + exception.Message);
                throw;
            }
            finally
            {
                _captureGate.Release();
            }
        }

        public async Task<CalibrationResult> RunAutomaticAsync(CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            await _autoGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            CancellationTokenSource linked = null;
            try
            {
                lock (_sync)
                {
                    if (_automaticCancellation != null)
                        throw new InvalidOperationException("Automatic calibration is already running.");
                    linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    _automaticCancellation = linked;
                }

                IReadOnlyList<CalibrationPlanPoint> plan;
                lock (_sync) plan = _plan.ToList();
                foreach (CalibrationPlanPoint point in plan)
                    await CapturePointAsync(point.PointNumber, linked.Token).ConfigureAwait(false);
                return await CalculateAsync(linked.Token).ConfigureAwait(false);
            }
            finally
            {
                lock (_sync)
                {
                    if (ReferenceEquals(_automaticCancellation, linked)) _automaticCancellation = null;
                }
                if (linked != null) linked.Dispose();
                _autoGate.Release();
            }
        }

        public void CancelAutomatic()
        {
            lock (_sync)
            {
                if (_automaticCancellation != null) _automaticCancellation.Cancel();
            }
        }

        public async Task<CalibrationResult> CalculateAsync(CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();
            CalibrationJob job;
            lock (_sync) job = _job.DeepClone();
            CalibrationResult result = await Task.Run(() => _sdk.Calculate(job), cancellationToken).ConfigureAwait(false);
            OnCalculationCompleted(result);

            ProtocolEnvelope response = ProtocolEnvelope.Create(ProtocolMessageTypes.CalibrationCompleted);
            response.Data["success"] = result.Success;
            response.Data["errorCode"] = result.ErrorCode.ToString();
            response.Data["message"] = result.Message ?? string.Empty;
            response.Data["rmsResidualPixels"] = result.RmsResidualPixels;
            response.Data["maxResidualPixels"] = result.MaxResidualPixels;
            response.Data["resolutionX"] = result.ResolutionX;
            response.Data["resolutionY"] = result.ResolutionY;
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
                OnLog("WARN", "Calibration was calculated, but the completion message could not be sent: " +
                    exception.Message);
            }
            return result;
        }

        public bool HandleProtocolMessage(ProtocolEnvelope envelope)
        {
            if (envelope == null || !string.Equals(
                envelope.Type, ProtocolMessageTypes.MotionArrived, StringComparison.OrdinalIgnoreCase))
                return false;

            PendingMove pending;
            if (string.IsNullOrWhiteSpace(envelope.CorrelationId) ||
                !_pendingMoves.TryGetValue(envelope.CorrelationId, out pending))
            {
                OnLog("WARN", "Ignored motion.arrived with an unknown or missing correlationId.");
                return true;
            }

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

            return pending.Completion.TrySetResult(new MotionArrival
            {
                CorrelationId = envelope.CorrelationId,
                PointNumber = pointNumber,
                X = x,
                Y = y,
                R = r
            });
        }

        private async Task<MotionArrival> WaitForArrivalAsync(PendingMove pending, CancellationToken cancellationToken)
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
            string correlationId,
            CalibrationPointRecord record,
            CancellationToken cancellationToken)
        {
            ProtocolEnvelope response = ProtocolEnvelope.Create(ProtocolMessageTypes.CalibrationPointResult);
            response.CorrelationId = correlationId;
            response.Data["pointNumber"] = record.PointNumber;
            response.Data["success"] = record.Status == CalibrationPointStatus.Completed;
            response.Data["status"] = record.Status.ToString();
            response.Data["machineX"] = record.MachineX;
            response.Data["machineY"] = record.MachineY;
            response.Data["machineR"] = record.MachineR;
            response.Data["imageRow"] = record.ImageRow;
            response.Data["imageColumn"] = record.ImageColumn;
            response.Data["imageAngle"] = record.ImageAngle;
            response.Data["score"] = record.MatchScore;
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
                    " was saved, but its result message could not be sent: " + exception.Message);
            }
        }

        private CalibrationPointRecord UpdateRecord(
            CalibrationPlanPoint point,
            CalibrationPointStatus status,
            string message,
            MotionArrival arrival = null,
            SampleCaptureResult capture = null)
        {
            CalibrationPointRecord snapshot;
            lock (_sync)
            {
                CalibrationPointRecord record = _records[point.PointNumber];
                record.Status = status;
                record.Message = message;
                if (arrival != null)
                {
                    record.MachineX = arrival.X;
                    record.MachineY = arrival.Y;
                    record.MachineR = arrival.R;
                }
                if (capture != null && capture.Match != null)
                {
                    record.ImageRow = capture.Match.Row;
                    record.ImageColumn = capture.Match.Column;
                    record.ImageAngle = capture.Match.AngleDegrees;
                    record.MatchScore = capture.Match.Score;
                    record.CapturedAtUtc = DateTime.UtcNow;
                }
                snapshot = record.DeepClone();
            }
            OnPointChanged(snapshot);
            return snapshot;
        }

        private void PublishFrame(Bitmap image, string source)
        {
            EventHandler<FrameEventArgs> handler = FrameCaptured;
            if (handler == null || image == null) return;
            using (FrameEventArgs args = new FrameEventArgs((Bitmap)image.Clone(), source))
                handler(this, args);
        }

        private void OnPointChanged(CalibrationPointRecord record)
        {
            EventHandler<CalibrationPointEventArgs> handler = PointChanged;
            if (handler != null) handler(this, new CalibrationPointEventArgs(record));
        }

        private void OnCalculationCompleted(CalibrationResult result)
        {
            EventHandler<CalibrationResultEventArgs> handler = CalculationCompleted;
            if (handler != null) handler(this, new CalibrationResultEventArgs(result));
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

        private static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            CancelAutomatic();
            foreach (PendingMove pending in _pendingMoves.Values)
                pending.Completion.TrySetCanceled();
            _captureGate.Dispose();
            _autoGate.Dispose();
        }

        private sealed class PendingMove
        {
            public PendingMove(string requestId, int pointNumber)
            {
                RequestId = requestId;
                PointNumber = pointNumber;
                Completion = new TaskCompletionSource<MotionArrival>(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            public string RequestId { get; private set; }
            public int PointNumber { get; private set; }
            public TaskCompletionSource<MotionArrival> Completion { get; private set; }
        }
    }
}
