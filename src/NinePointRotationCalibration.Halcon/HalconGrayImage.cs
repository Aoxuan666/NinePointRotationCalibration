using System;
using System.Diagnostics;
using System.Drawing;
using HalconDotNet;

namespace NinePointRotationCalibration.Halcon
{
    /// <summary>
    /// 从 Bitmap 转换一次得到的 HALCON 灰度图，供单次检测的模板匹配与 Blob 分析等阶段共享，避免重复转图。
    /// </summary>
    public sealed class HalconGrayImage : IDisposable
    {
        private HImage _image;

        private HalconGrayImage(HImage image, int width, int height, double conversionMilliseconds)
        {
            _image = image;
            Width = width;
            Height = height;
            ConversionElapsedMilliseconds = conversionMilliseconds;
        }

        public int Width { get; private set; }
        public int Height { get; private set; }
        public Size Size { get { return new Size(Width, Height); } }
        /// <summary>Bitmap 转换为灰度 HImage 的耗时（毫秒）。</summary>
        public double ConversionElapsedMilliseconds { get; private set; }

        internal HImage Image
        {
            get
            {
                if (_image == null) throw new ObjectDisposedException(nameof(HalconGrayImage));
                return _image;
            }
        }

        /// <summary>将源 Bitmap 复制并转换为单通道 HALCON 灰度图。</summary>
        public static HalconGrayImage FromBitmap(Bitmap source)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            Stopwatch watch = Stopwatch.StartNew();
            HImage image = HalconImageConverter.ToGrayHImage(source);
            watch.Stop();
            return new HalconGrayImage(image, source.Width, source.Height, watch.Elapsed.TotalMilliseconds);
        }

        /// <summary>释放底层 HALCON 图像句柄。</summary>
        public void Dispose()
        {
            HImage image = _image;
            _image = null;
            if (image != null) image.Dispose();
        }
    }

    /// <summary>
    /// 可选的引擎扩展接口：直接复用已转换的灰度图进行定位（不支持该接口的伪造引擎仍接收 Bitmap）。
    /// </summary>
    public interface IGrayImageTemplateMatchingEngine
    {
        /// <summary>在已转换的灰度图中定位所有匹配目标。</summary>
        TemplateMatchCollectionResult LocateAll(HalconGrayImage image, TemplateDefinition definition, bool includeContour = true);
    }
}
