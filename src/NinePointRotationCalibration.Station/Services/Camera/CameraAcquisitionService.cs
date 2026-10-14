using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using NinePointRotationCalibration.Station.Models;

namespace NinePointRotationCalibration.Station.Services.Camera
{
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

        public void SetPreviewInterval(int milliseconds)
        {
            _previewIntervalMilliseconds = Math.Max(50, milliseconds);
        }

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

        public async Task<CameraFrame> CaptureAsync(CancellationToken cancellationToken)
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
                    using (CameraFrame frame = await CaptureAsync(cancellationToken).ConfigureAwait(false))
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
