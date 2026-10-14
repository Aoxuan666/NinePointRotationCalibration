using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;

namespace NinePointRotationCalibration.Station.Models
{
    public sealed class CameraDescriptor
    {
        public string Id { get; set; }
        public string DisplayName { get; set; }

        public override string ToString()
        {
            return string.IsNullOrWhiteSpace(DisplayName) ? Id : DisplayName;
        }
    }

    public sealed class CameraFrame : IDisposable
    {
        public CameraFrame(Bitmap image, long sequence, DateTime capturedAtUtc)
        {
            Image = image ?? throw new ArgumentNullException(nameof(image));
            Sequence = sequence;
            CapturedAtUtc = capturedAtUtc;
        }

        public Bitmap Image { get; private set; }
        public long Sequence { get; private set; }
        public DateTime CapturedAtUtc { get; private set; }

        public void Dispose()
        {
            Bitmap image = Image;
            Image = null;
            if (image != null) image.Dispose();
        }
    }

    public interface ICameraDevice : IDisposable
    {
        string ProviderName { get; }
        bool IsConnected { get; }
        string ConnectedDeviceId { get; }
        Task<IReadOnlyList<CameraDescriptor>> EnumerateAsync(CancellationToken cancellationToken);
        Task ConnectAsync(string deviceId, CancellationToken cancellationToken);
        Task DisconnectAsync();
        Task SetExposureAsync(double exposureMicroseconds, CancellationToken cancellationToken);
        Task<CameraFrame> CaptureAsync(CancellationToken cancellationToken);
    }
}
