using System;
using System.Diagnostics;
using System.Drawing;
using HalconDotNet;

namespace NinePointRotationCalibration.Halcon
{
    /// <summary>
    /// A grayscale HALCON image converted once from a bitmap and shared by every
    /// stage of one inspection (matching, blob analysis), so the frame is not
    /// converted again for each stage.
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
        public double ConversionElapsedMilliseconds { get; private set; }

        internal HImage Image
        {
            get
            {
                if (_image == null) throw new ObjectDisposedException(nameof(HalconGrayImage));
                return _image;
            }
        }

        public static HalconGrayImage FromBitmap(Bitmap source)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            Stopwatch watch = Stopwatch.StartNew();
            HImage image = HalconImageConverter.ToGrayHImage(source);
            watch.Stop();
            return new HalconGrayImage(image, source.Width, source.Height, watch.Elapsed.TotalMilliseconds);
        }

        public void Dispose()
        {
            HImage image = _image;
            _image = null;
            if (image != null) image.Dispose();
        }
    }

    /// <summary>
    /// Optional engine capability: locate on an already converted gray image.
    /// Engines without it (for example test fakes) keep receiving the bitmap.
    /// </summary>
    public interface IGrayImageTemplateMatchingEngine
    {
        TemplateMatchCollectionResult LocateAll(HalconGrayImage image, TemplateDefinition definition, bool includeContour = true);
    }
}
