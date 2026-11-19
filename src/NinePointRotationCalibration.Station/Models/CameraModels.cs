using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NinePointRotationCalibration.Station.Models
{
    /// <summary>相机可调属性的数据类型。</summary>
    public enum CameraFeatureKind
    {
        Number,
        Integer,
        Boolean,
        Enumeration,
        String
    }

    /// <summary>描述单个通用相机参数及其取值范围、单位和读写属性。</summary>
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

        /// <summary>创建当前参数项及枚举值列表的独立副本。</summary>
        public CameraFeatureValue Clone()
        {
            CameraFeatureValue clone = (CameraFeatureValue)MemberwiseClone();
            clone.EnumValues = new List<string>(EnumValues ?? new List<string>());
            return clone;
        }
    }

    /// <summary>已枚举相机设备的标识与界面显示名称。</summary>
    public sealed class CameraDescriptor
    {
        public string Id { get; set; }
        public string DisplayName { get; set; }

        public override string ToString()
        {
            return string.IsNullOrWhiteSpace(DisplayName) ? Id : DisplayName;
        }
    }

    /// <summary>相机核心采集参数及扩展属性集合。</summary>
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

        /// <summary>用指定特性集合替换当前扩展属性列表（逐项深拷贝）。</summary>
        public void ReplaceFeatures(IEnumerable<CameraFeatureValue> features)
        {
            Features = features == null
                ? new List<CameraFeatureValue>()
                : features.Where(item => item != null).Select(item => item.Clone()).ToList();
        }

        /// <summary>按属性名（忽略大小写）查找扩展属性。</summary>
        public CameraFeatureValue FindFeature(string name)
        {
            return Features == null || string.IsNullOrWhiteSpace(name)
                ? null
                : Features.Find(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>创建当前参数集的深拷贝。</summary>
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

    /// <summary>支持读取与批量下发相机参数的设备扩展接口。</summary>
    public interface ICameraParameterDevice
    {
        /// <summary>从相机读取当前参数及可写扩展属性。</summary>
        Task<CameraParameterSet> GetParametersAsync(CancellationToken cancellationToken);
        /// <summary>将给定参数集下发至已连接的相机。</summary>
        Task SetParametersAsync(CameraParameterSet parameters, CancellationToken cancellationToken);
    }

    /// <summary>封装单帧采集图像及帧序号、UTC 采集时间（持有位图资源）。</summary>
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

        /// <summary>释放底层位图内存。</summary>
        public void Dispose()
        {
            Bitmap image = Image;
            Image = null;
            if (image != null) image.Dispose();
        }
    }

    /// <summary>相机硬件驱动与模拟相机的统一控制接口。</summary>
    public interface ICameraDevice : IDisposable
    {
        string ProviderName { get; }
        bool IsConnected { get; }
        string ConnectedDeviceId { get; }
        /// <summary>枚举当前可用的相机设备列表。</summary>
        Task<IReadOnlyList<CameraDescriptor>> EnumerateAsync(CancellationToken cancellationToken);
        /// <summary>按设备标识连接相机并开启取流。</summary>
        Task ConnectAsync(string deviceId, CancellationToken cancellationToken);
        /// <summary>断开相机连接并释放底层流句柄。</summary>
        Task DisconnectAsync();
        /// <summary>设置相机曝光时间（微秒）。</summary>
        Task SetExposureAsync(double exposureMicroseconds, CancellationToken cancellationToken);
        /// <summary>触发并采集一帧图像。</summary>
        Task<CameraFrame> CaptureAsync(CancellationToken cancellationToken);
    }

    /// <summary>支持低延迟实时采集流的可选相机扩展接口，避免预览路径做多余清缓冲或重复查询。</summary>
    public interface ICameraLivePreviewDevice
    {
        /// <summary>按实时预览优化路径采集最新一帧图像。</summary>
        Task<CameraFrame> CapturePreviewAsync(CancellationToken cancellationToken);
    }
}
