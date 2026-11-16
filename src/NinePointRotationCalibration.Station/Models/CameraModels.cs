using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NinePointRotationCalibration.Station.Models
{
    public enum CameraFeatureKind
    {
        Number,
        Integer,
        Boolean,
        Enumeration,
        String
    }

    public sealed class CameraFeatureValue
    {
        public CameraFeatureValue()
        {
            EnumValues = new List<string>();
            IsReadable = true;
        }

        public string Name { get; set; }
        public string DisplayName { get; set; }
        public CameraFeatureKind Kind { get; set; }
        public string Value { get; set; }
        public double Minimum { get; set; }
        public double Maximum { get; set; }
        public double Increment { get; set; }
        public bool HasRange { get; set; }
        public bool IsReadable { get; set; }
        public bool IsWritable { get; set; }
        public string Unit { get; set; }
        public List<string> EnumValues { get; private set; }

        public CameraFeatureValue Clone()
        {
            CameraFeatureValue clone = (CameraFeatureValue)MemberwiseClone();
            clone.EnumValues = new List<string>(EnumValues ?? new List<string>());
            return clone;
        }
    }

    public sealed class CameraDescriptor
    {
        public string Id { get; set; }
        public string DisplayName { get; set; }

        public override string ToString()
        {
            return string.IsNullOrWhiteSpace(DisplayName) ? Id : DisplayName;
        }
    }

    public sealed class CameraParameterSet
    {
        public CameraParameterSet()
        {
            ExposureMicroseconds = 10000.0;
            Gain = 0.0;
            Gamma = 1.0;
            Saturation = 0.0;
            Width = 0;
            Height = 0;
            TriggerMode = "Software";
            Features = new List<CameraFeatureValue>();
        }

        public double ExposureMicroseconds { get; set; }
        public double Gain { get; set; }
        public double Gamma { get; set; }
        public double Saturation { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public string PixelFormat { get; set; }
        public string TriggerMode { get; set; }
        public bool ReverseX { get; set; }
        public bool ReverseY { get; set; }
        public List<CameraFeatureValue> Features { get; set; }

        public void ReplaceFeatures(IEnumerable<CameraFeatureValue> features)
        {
            Features = features == null
                ? new List<CameraFeatureValue>()
                : features.Where(item => item != null).Select(item => item.Clone()).ToList();
        }

        public CameraFeatureValue FindFeature(string name)
        {
            return Features == null || string.IsNullOrWhiteSpace(name)
                ? null
                : Features.Find(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));
        }

        public CameraParameterSet Clone()
        {
            CameraParameterSet clone = (CameraParameterSet)MemberwiseClone();
            clone.Features = new List<CameraFeatureValue>();
            if (Features != null)
                foreach (CameraFeatureValue feature in Features)
                    if (feature != null) clone.Features.Add(feature.Clone());
            return clone;
        }
    }

    public interface ICameraParameterDevice
    {
        Task<CameraParameterSet> GetParametersAsync(CancellationToken cancellationToken);
        Task SetParametersAsync(CameraParameterSet parameters, CancellationToken cancellationToken);
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
