using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using NinePointRotationCalibration.Station.Models;

namespace NinePointRotationCalibration.Station.Services.Camera
{
    /// <summary>相机采图调度服务，通过串行门控协调平滑实时预览与高优先级生产/标定抓拍。</summary>
    public sealed class CameraAcquisitionService : IDisposable
    {
        private const int MinimumPreviewIntervalMilliseconds = 16;
        private const int PreviewFailureThreshold = 3;
        private readonly SemaphoreSlim _deviceGate = new SemaphoreSlim(1, 1);
        private readonly object _sync = new object();
        private ICameraDevice _device;
        private CancellationTokenSource _previewCancellation;
        private Task _previewTask;
        private int _previewIntervalMilliseconds;
        private int _disposeRequested;
        private volatile bool _disposed;
        private long _liveFrameCount;
        private long _liveDroppedFrameCount;
        private double _liveFps;

        // 生产与流程抓拍优先级高于预览：有高优先级抓拍排队以及最后一次抓拍后的静默期内，
        // 预览循环主动让出相机，避免连续生产期间上位机触发排队等待预览曝光。
        private const int PreviewQuietPeriodMilliseconds = 1000;
        private int _priorityCapturesPending;
        private long _lastPriorityCaptureTicks;

        public CameraAcquisitionService(ICameraDevice device, int previewIntervalMilliseconds)
        {
            _device = device ?? throw new ArgumentNullException(nameof(device));
            _previewIntervalMilliseconds = Math.Max(MinimumPreviewIntervalMilliseconds, previewIntervalMilliseconds);
        }

        public event EventHandler<FrameEventArgs> PreviewFrame;
        public event Action<bool, string> PreviewHealthChanged;
        public event Action<bool> PreviewRunningChanged;
        public event EventHandler<LogEventArgs> Log;

        /// <summary>可选背压回调：UI 正在渲染上一帧时返回 true，以便实时循环快速跳过冗余拷贝避免堆积。</summary>
        public Func<bool> PreviewConsumerBusy { get; set; }

        public string ProviderName
        {
            get { lock (_sync) return _device == null ? string.Empty : _device.ProviderName; }
        }

        public bool IsConnected
        {
            get { lock (_sync) return _device != null && _device.IsConnected; }
        }

        public string ConnectedDeviceId
        {
            get { lock (_sync) return _device == null ? null : _device.ConnectedDeviceId; }
        }

        public bool IsPreviewRunning
        {
            get { lock (_sync) return _previewCancellation != null; }
        }

        public int PreviewIntervalMilliseconds
        {
            get { return Volatile.Read(ref _previewIntervalMilliseconds); }
        }

        public long LiveFrameCount
        {
            get { return Interlocked.Read(ref _liveFrameCount); }
        }

        public long LiveDroppedFrameCount
        {
            get { return Interlocked.Read(ref _liveDroppedFrameCount); }
        }

        public double LiveFps
        {
            get { lock (_sync) return _liveFps; }
        }

        /// <summary>设置后台实时预览采图间隔（最低 16 毫秒，支持最高约 60 FPS 流畅采集）。</summary>
        public void SetPreviewInterval(int milliseconds)
        {
            int clamped = Math.Max(MinimumPreviewIntervalMilliseconds, Math.Min(5000, milliseconds));
            Volatile.Write(ref _previewIntervalMilliseconds, clamped);
            LocalDebugLog.Write("INFO", "camera.preview", "Preview interval updated to " + clamped + "ms.");
        }

        /// <summary>串行枚举当前可用相机设备。</summary>
        public async Task<IReadOnlyList<CameraDescriptor>> EnumerateAsync(CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            await _deviceGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await _device.EnumerateAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _deviceGate.Release();
            }
        }

        // Retained for callers compiled against the previous connection API.
        public Task ConnectAsync(string deviceId, double exposureMicroseconds, CancellationToken cancellationToken)
        {
            return ConnectAsync(deviceId, cancellationToken);
        }

        /// <summary>连接指定相机，沿用设备当前采集参数。</summary>
        public async Task ConnectAsync(
            string deviceId,
            CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            await _deviceGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_device.IsConnected) await _device.DisconnectAsync().ConfigureAwait(false);
                await _device.ConnectAsync(deviceId, cancellationToken).ConfigureAwait(false);
                OnLog("INFO", "Camera connected: " + _device.ProviderName + " / " + _device.ConnectedDeviceId);
            }
            finally
            {
                _deviceGate.Release();
            }
        }

        /// <summary>先停止后台预览循环，再安全断开相机连接。</summary>
        public async Task DisconnectAsync()
        {
            if (_disposed) return;
            await StopPreviewAsync().ConfigureAwait(false);
            await _deviceGate.WaitAsync().ConfigureAwait(false);
            try
            {
                await _device.DisconnectAsync().ConfigureAwait(false);
                OnLog("INFO", "Camera disconnected.");
            }
            finally
            {
                _deviceGate.Release();
            }
        }

        /// <summary>在线更新相机曝光时间（微秒）。</summary>
        public async Task SetExposureAsync(double exposureMicroseconds, CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            await _deviceGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (!_device.IsConnected) throw new InvalidOperationException("Camera is not connected.");
                await _device.SetExposureAsync(exposureMicroseconds, cancellationToken).ConfigureAwait(false);
                OnLog("INFO", "Camera exposure updated to " + exposureMicroseconds.ToString("0.###") + " us.");
            }
            finally
            {
                _deviceGate.Release();
            }
        }

        /// <summary>读取当前已连接相机的可调参数集。</summary>
        public async Task<CameraParameterSet> GetParametersAsync(CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            await _deviceGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ICameraParameterDevice parameterDevice = _device as ICameraParameterDevice;
                if (parameterDevice == null)
                    throw new NotSupportedException("The selected camera driver does not expose adjustable parameters.");
                if (!_device.IsConnected) throw new InvalidOperationException("Camera is not connected.");
                return await parameterDevice.GetParametersAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _deviceGate.Release();
            }
        }

        /// <summary>下发相机参数集至当前已连接相机。</summary>
        public async Task SetParametersAsync(CameraParameterSet parameters, CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            if (parameters == null) throw new ArgumentNullException(nameof(parameters));
            await _deviceGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ICameraParameterDevice parameterDevice = _device as ICameraParameterDevice;
                if (parameterDevice == null)
                    throw new NotSupportedException("The selected camera driver does not expose adjustable parameters.");
                if (!_device.IsConnected) throw new InvalidOperationException("Camera is not connected.");
                await parameterDevice.SetParametersAsync(parameters, cancellationToken).ConfigureAwait(false);
                OnLog("INFO", "Camera parameters applied.");
            }
            finally
            {
                _deviceGate.Release();
            }
        }

        /// <summary>执行高优先级单帧抓拍，并在排队与静默期内抑制后台预览。</summary>
        public async Task<CameraFrame> CaptureAsync(CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            Interlocked.Increment(ref _priorityCapturesPending);
            try
            {
                return await CaptureCoreAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Exchange(ref _lastPriorityCaptureTicks, DateTime.UtcNow.Ticks);
                Interlocked.Decrement(ref _priorityCapturesPending);
            }
        }

        private bool PreviewShouldYield()
        {
            if (Volatile.Read(ref _priorityCapturesPending) > 0) return true;
            long last = Interlocked.Read(ref _lastPriorityCaptureTicks);
            return last != 0 &&
                   (DateTime.UtcNow.Ticks - last) < TimeSpan.TicksPerMillisecond * PreviewQuietPeriodMilliseconds;
        }

        private async Task<CameraFrame> CaptureCoreAsync(CancellationToken cancellationToken, bool isPreview = false)
        {
            ThrowIfDisposed();
            await _deviceGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_device == null || !_device.IsConnected) throw new InvalidOperationException("Camera is not connected.");
                ICameraLivePreviewDevice livePreviewDevice = isPreview ? _device as ICameraLivePreviewDevice : null;
                CameraFrame frame = livePreviewDevice != null
                    ? await livePreviewDevice.CapturePreviewAsync(cancellationToken).ConfigureAwait(false)
                    : await _device.CaptureAsync(cancellationToken).ConfigureAwait(false);
                if (frame == null || frame.Image == null)
                {
                    if (frame != null) frame.Dispose();
                    throw new InvalidOperationException("Camera returned an empty frame.");
                }
                return frame;
            }
            finally
            {
                _deviceGate.Release();
            }
        }

        /// <summary>启动后台实时预览采图循环。</summary>
        public void StartPreview()
        {
            ThrowIfDisposed();
            bool started = false;
            lock (_sync)
            {
                if (_previewCancellation != null) return;
                _previewCancellation = new CancellationTokenSource();
                _liveFps = 0.0;
                CancellationToken token = _previewCancellation.Token;
                _previewTask = Task.Run(() => PreviewLoopAsync(token));
                started = true;
            }
            if (started)
            {
                LocalDebugLog.Write(
                    "INFO",
                    "camera.preview",
                    "Live preview loop started: provider='" + ProviderName + "', device='" + ConnectedDeviceId +
                    "', interval=" + PreviewIntervalMilliseconds + "ms.");
                OnPreviewRunningChanged(true);
            }
        }

        /// <summary>取消并等待后台预览循环退出。</summary>
        public async Task StopPreviewAsync()
        {
            CancellationTokenSource cancellation;
            Task task;
            lock (_sync)
            {
                cancellation = _previewCancellation;
                task = _previewTask;
                _liveFps = 0.0;
            }
            if (cancellation == null) return;
            try
            {
                cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
            try
            {
                if (task != null) await task.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exception)
            {
                LocalDebugLog.WriteException("WARN", "camera.preview", "Preview task terminated with error during stop", exception);
            }
            finally
            {
                bool cleared = false;
                lock (_sync)
                {
                    if (ReferenceEquals(_previewCancellation, cancellation))
                    {
                        _previewCancellation = null;
                        _previewTask = null;
                        cleared = true;
                    }
                }
                cancellation.Dispose();
                if (cleared)
                {
                    LocalDebugLog.Write(
                        "INFO",
                        "camera.preview",
                        "Live preview loop stopped: totalFrames=" + LiveFrameCount + ", droppedFrames=" + LiveDroppedFrameCount + ".");
                    OnPreviewRunningChanged(false);
                }
            }
        }

        private async Task PreviewLoopAsync(CancellationToken cancellationToken)
        {
            int consecutiveFailures = 0;
            bool failureReported = false;
            bool firstFrame = true;
            Stopwatch frameWatch = new Stopwatch();
            Stopwatch fpsWindowWatch = Stopwatch.StartNew();
            Stopwatch statsLogWatch = Stopwatch.StartNew();
            int windowFrames = 0;

            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    int targetInterval = PreviewIntervalMilliseconds;
                    if (PreviewShouldYield())
                    {
                        await Task.Delay(Math.Min(100, Math.Max(20, targetInterval)), cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    // UI 渲染端繁忙时让出极短时间片避免无效抓帧与内存抖动
                    Func<bool> consumerBusy = PreviewConsumerBusy;
                    if (consumerBusy != null)
                    {
                        bool busy = false;
                        try { busy = consumerBusy(); } catch { }
                        if (busy)
                        {
                            Interlocked.Increment(ref _liveDroppedFrameCount);
                            await Task.Delay(Math.Min(16, targetInterval), cancellationToken).ConfigureAwait(false);
                            continue;
                        }
                    }

                    frameWatch.Restart();
                    using (CameraFrame frame = await CaptureCoreAsync(cancellationToken, true).ConfigureAwait(false))
                    {
                        if (cancellationToken.IsCancellationRequested) break;
                        if (firstFrame)
                        {
                            firstFrame = false;
                            LocalDebugLog.Write("INFO", "camera.preview", "First preview frame: " +
                                frame.Image.Width + "x" + frame.Image.Height + ", format=" + frame.Image.PixelFormat + ".");
                        }
                        Interlocked.Increment(ref _liveFrameCount);
                        windowFrames++;
                        double fpsElapsed = fpsWindowWatch.Elapsed.TotalSeconds;
                        if (fpsElapsed >= 0.5)
                        {
                            double measuredFps = windowFrames / fpsElapsed;
                            lock (_sync) _liveFps = measuredFps;
                            windowFrames = 0;
                            fpsWindowWatch.Restart();
                        }

                        PublishPreviewFrame(frame.Image);
                    }

                    consecutiveFailures = 0;
                    if (failureReported)
                    {
                        failureReported = false;
                        LocalDebugLog.Write("INFO", "camera.preview", "Camera live preview recovered.");
                        OnLog("INFO", "Camera preview recovered.");
                        OnPreviewHealthChanged(
                            true,
                            ProviderName + " / " + ConnectedDeviceId);
                    }

                    if (statsLogWatch.Elapsed.TotalSeconds >= 30.0)
                    {
                        statsLogWatch.Restart();
                        LocalDebugLog.Write(
                            "DEBUG",
                            "camera.preview",
                            string.Format(
                                CultureInfo.InvariantCulture,
                                "Live stream heartbeat: fps={0:0.0}, totalFrames={1}, dropped={2}, lastGrabMs={3:0.0}, interval={4}ms",
                                LiveFps,
                                LiveFrameCount,
                                LiveDroppedFrameCount,
                                frameWatch.Elapsed.TotalMilliseconds,
                                targetInterval));
                    }

                    // 按周期扣减本次采集与拷贝耗时，避免固定 Sleep 叠加造成帧率掉半和卡顿
                    int remainingMs = targetInterval - (int)frameWatch.ElapsedMilliseconds;
                    if (remainingMs >= 4)
                    {
                        await Task.Delay(remainingMs, cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        await Task.Yield();
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    consecutiveFailures++;
                    if (consecutiveFailures == 1)
                    {
                        LocalDebugLog.WriteException("WARN", "camera.preview", "Camera preview grab failed (attempt #1)", exception);
                        OnLog("WARN", "Camera preview failed: " + exception.Message);
                    }
                    // 连续失败达到阈值后上报预览健康异常，避免单帧偶发波动误报故障
                    if (!failureReported && consecutiveFailures >= PreviewFailureThreshold)
                    {
                        failureReported = true;
                        string detail = "Preview failed " + consecutiveFailures +
                                        " consecutive times: " + exception.Message;
                        LocalDebugLog.WriteException("ERROR", "camera.preview", detail, exception);
                        OnLog("ERROR", detail);
                        OnPreviewHealthChanged(false, detail);
                    }
                    try
                    {
                        await Task.Delay(Math.Max(500, PreviewIntervalMilliseconds), cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }
        }

        private void PublishPreviewFrame(Bitmap image)
        {
            EventHandler<FrameEventArgs> handler = PreviewFrame;
            if (handler == null || image == null) return;
            // 直接借用 CameraFrame 位图同步分发（ownsImage=false），省去每帧 15MB+ 的中间全图 Clone
            using (FrameEventArgs args = new FrameEventArgs(image, "preview", false))
            {
                foreach (EventHandler<FrameEventArgs> subscriber in handler.GetInvocationList())
                {
                    try
                    {
                        subscriber(this, args);
                    }
                    catch (Exception exception)
                    {
                        LocalDebugLog.WriteException("WARN", "camera.preview", "PreviewFrame subscriber threw an exception", exception);
                    }
                }
            }
        }

        private void OnLog(string level, string message)
        {
            EventHandler<LogEventArgs> handler = Log;
            if (handler == null) return;
            try
            {
                handler(this, new LogEventArgs(level, message));
            }
            catch
            {
            }
        }

        private void OnPreviewHealthChanged(bool available, string detail)
        {
            Action<bool, string> handler = PreviewHealthChanged;
            if (handler == null) return;
            try
            {
                handler(available, detail);
            }
            catch (Exception exception)
            {
                LocalDebugLog.WriteException("WARN", "camera.preview", "PreviewHealthChanged subscriber threw an exception", exception);
            }
        }

        private void OnPreviewRunningChanged(bool running)
        {
            Action<bool> handler = PreviewRunningChanged;
            if (handler == null) return;
            try
            {
                handler(running);
            }
            catch (Exception exception)
            {
                LocalDebugLog.WriteException("WARN", "camera.preview", "PreviewRunningChanged subscriber threw an exception", exception);
            }
        }

        private void ThrowIfDisposed()
        {
            if (Volatile.Read(ref _disposeRequested) != 0 || _disposed)
                throw new ObjectDisposedException(nameof(CameraAcquisitionService));
        }

        /// <summary>停止预览、断开设备并释放底层相机及信号量资源。</summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposeRequested, 1) != 0) return;
            try { StopPreviewAsync().GetAwaiter().GetResult(); } catch { }
            try { DisconnectAsync().GetAwaiter().GetResult(); } catch { }
            bool gateAcquired = _deviceGate.Wait(TimeSpan.FromSeconds(5));
            try
            {
                _disposed = true;
                ICameraDevice device;
                lock (_sync)
                {
                    device = _device;
                    _device = null;
                }
                if (device != null) device.Dispose();
            }
            finally
            {
                if (gateAcquired) _deviceGate.Release();
            }
            _deviceGate.Dispose();
        }
    }
}
