using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using NinePointRotationCalibration.Station.Models;

namespace NinePointRotationCalibration.Station.Services.Camera
{
    /// <summary>相机采图调度服务，通过串行门控协调实时预览与高优先级生产/标定抓拍。</summary>
    public sealed class CameraAcquisitionService : IDisposable
    {
        private const int PreviewFailureThreshold = 3;
        private readonly SemaphoreSlim _deviceGate = new SemaphoreSlim(1, 1);
        private readonly object _sync = new object();
        private ICameraDevice _device;
        private CancellationTokenSource _previewCancellation;
        private Task _previewTask;
        private int _previewIntervalMilliseconds;
        private bool _disposed;

        // 生产与流程抓拍优先级高于预览：有高优先级抓拍排队以及最后一次抓拍后的静默期内，
        // 预览循环主动让出相机，避免连续生产期间上位机触发排队等待预览曝光。
        private const int PreviewQuietPeriodMilliseconds = 1000;
        private int _priorityCapturesPending;
        private long _lastPriorityCaptureTicks;

        public CameraAcquisitionService(ICameraDevice device, int previewIntervalMilliseconds)
        {
            _device = device ?? throw new ArgumentNullException(nameof(device));
            _previewIntervalMilliseconds = Math.Max(50, previewIntervalMilliseconds);
        }

        public event EventHandler<FrameEventArgs> PreviewFrame;
        public event Action<bool, string> PreviewHealthChanged;
        public event EventHandler<LogEventArgs> Log;

        public string ProviderName
        {
            get { lock (_sync) return _device.ProviderName; }
        }

        public bool IsConnected
        {
            get { lock (_sync) return _device.IsConnected; }
        }

        public string ConnectedDeviceId
        {
            get { lock (_sync) return _device.ConnectedDeviceId; }
        }

        /// <summary>设置后台实时预览采图间隔（最低 50 毫秒）。</summary>
        public void SetPreviewInterval(int milliseconds)
        {
            _previewIntervalMilliseconds = Math.Max(50, milliseconds);
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

        /// <summary>连接指定相机并同步设置初始曝光时间。</summary>
        public async Task ConnectAsync(
            string deviceId,
            double exposureMicroseconds,
            CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            await _deviceGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_device.IsConnected) await _device.DisconnectAsync().ConfigureAwait(false);
                await _device.ConnectAsync(deviceId, cancellationToken).ConfigureAwait(false);
                await _device.SetExposureAsync(exposureMicroseconds, cancellationToken).ConfigureAwait(false);
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

        private async Task<CameraFrame> CaptureCoreAsync(CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            await _deviceGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (!_device.IsConnected) throw new InvalidOperationException("Camera is not connected.");
                CameraFrame frame = await _device.CaptureAsync(cancellationToken).ConfigureAwait(false);
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
            lock (_sync)
            {
                if (_previewCancellation != null) return;
                _previewCancellation = new CancellationTokenSource();
                _previewTask = Task.Run(() => PreviewLoopAsync(_previewCancellation.Token));
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
                _previewCancellation = null;
                _previewTask = null;
            }
            if (cancellation == null) return;
            cancellation.Cancel();
            try
            {
                if (task != null) await task.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                cancellation.Dispose();
            }
        }

        private async Task PreviewLoopAsync(CancellationToken cancellationToken)
        {
            int consecutiveFailures = 0;
            bool failureReported = false;
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    if (PreviewShouldYield())
                    {
                        await Task.Delay(_previewIntervalMilliseconds, cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    using (CameraFrame frame = await CaptureCoreAsync(cancellationToken).ConfigureAwait(false))
                        PublishPreviewFrame(frame.Image);

                    consecutiveFailures = 0;
                    if (failureReported)
                    {
                        failureReported = false;
                        OnLog("INFO", "Camera preview recovered.");
                        OnPreviewHealthChanged(
                            true,
                            ProviderName + " / " + ConnectedDeviceId);
                    }
                    await Task.Delay(_previewIntervalMilliseconds, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception exception)
                {
                    consecutiveFailures++;
                    if (consecutiveFailures == 1)
                        OnLog("WARN", "Camera preview failed: " + exception.Message);
                    // 连续失败达到阈值后上报预览健康异常，避免单帧偶发波动误报故障
                    if (!failureReported && consecutiveFailures >= PreviewFailureThreshold)
                    {
                        failureReported = true;
                        string detail = "Preview failed " + consecutiveFailures +
                                        " consecutive times: " + exception.Message;
                        OnLog("ERROR", detail);
                        OnPreviewHealthChanged(false, detail);
                    }
                    await Task.Delay(Math.Max(500, _previewIntervalMilliseconds), cancellationToken).ConfigureAwait(false);
                }
            }
        }

        private void PublishPreviewFrame(Bitmap image)
        {
            EventHandler<FrameEventArgs> handler = PreviewFrame;
            if (handler == null || image == null) return;
            // 克隆位图再发布事件，确保底层 CameraFrame 释放后 UI 端仍可安全读取
            using (FrameEventArgs args = new FrameEventArgs((Bitmap)image.Clone(), "preview"))
                handler(this, args);
        }

        private void OnLog(string level, string message)
        {
            EventHandler<LogEventArgs> handler = Log;
            if (handler != null) handler(this, new LogEventArgs(level, message));
        }

        private void OnPreviewHealthChanged(bool available, string detail)
        {
            Action<bool, string> handler = PreviewHealthChanged;
            if (handler != null) handler(available, detail);
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(CameraAcquisitionService));
        }

        /// <summary>停止预览、断开设备并释放底层相机及信号量资源。</summary>
        public void Dispose()
        {
            if (_disposed) return;
            try { StopPreviewAsync().GetAwaiter().GetResult(); } catch { }
            try { DisconnectAsync().GetAwaiter().GetResult(); } catch { }
            _disposed = true;
            lock (_sync)
            {
                if (_device != null) _device.Dispose();
                _device = null;
            }
            _deviceGate.Dispose();
        }
    }
}
