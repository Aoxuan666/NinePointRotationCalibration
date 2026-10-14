using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using HalconDotNet;

namespace NinePointRotationCalibration.Halcon
{
    /// <summary>
    /// Converts caller-owned bitmaps without retaining or modifying them.
    /// </summary>
    public static class HalconImageConverter
    {
        public static HImage ToGrayHImage(Bitmap source)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (source.Width <= 0 || source.Height <= 0)
                throw new ArgumentException("The input image has no pixels.", nameof(source));

            Bitmap bitmap = source;
            bool ownsBitmap = false;
            PixelFormat lockFormat;
            string colorFormat;
            BitmapData data = null;
            HImage interleaved = null;
            try
            {
                bool tightlyPacked24 = source.PixelFormat == PixelFormat.Format24bppRgb &&
                    ((source.Width * 3) & 3) == 0;
                if (tightlyPacked24)
                {
                    lockFormat = PixelFormat.Format24bppRgb;
                    colorFormat = "bgr";
                }
                else if (source.PixelFormat == PixelFormat.Format32bppRgb)
                {
                    lockFormat = PixelFormat.Format32bppRgb;
                    colorFormat = "bgrx";
                }
                else
                {
                    bitmap = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppRgb);
                    ownsBitmap = true;
                    using (Graphics graphics = Graphics.FromImage(bitmap))
                        graphics.DrawImageUnscaled(source, 0, 0);
                    lockFormat = PixelFormat.Format32bppRgb;
                    colorFormat = "bgrx";
                }

                data = bitmap.LockBits(
                    new Rectangle(0, 0, bitmap.Width, bitmap.Height),
                    ImageLockMode.ReadOnly,
                    lockFormat);
                interleaved = new HImage();
                interleaved.GenImageInterleaved(
                    data.Scan0,
                    colorFormat,
                    bitmap.Width,
                    bitmap.Height,
                    -1,
                    "byte",
                    bitmap.Width,
                    bitmap.Height,
                    0,
                    0,
                    -1,
                    0);

                // rgb1_to_gray creates an independently owned HALCON image before
                // the Bitmap buffer is unlocked.
                return interleaved.Rgb1ToGray();
            }
            finally
            {
                if (interleaved != null) interleaved.Dispose();
                if (data != null) bitmap.UnlockBits(data);
                if (ownsBitmap) bitmap.Dispose();
            }
        }

        public static Bitmap ToGrayBitmap(HImage image)
        {
            if (image == null) throw new ArgumentNullException(nameof(image));

            int channels;
            using (HTuple channelCount = image.CountChannels())
                channels = channelCount.I;
            if (channels != 1)
                throw new ArgumentException("Only single-channel HALCON images can be converted to a grayscale bitmap.", nameof(image));

            string type;
            int width;
            int height;
            IntPtr pointer = image.GetImagePointer1(out type, out width, out height);
            if (!string.Equals(type, "byte", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Only HALCON byte images can be converted to a grayscale bitmap.", nameof(image));

            byte[] pixels = new byte[checked(width * height)];
            Marshal.Copy(pointer, pixels, 0, pixels.Length);
            Bitmap bitmap = new Bitmap(width, height, PixelFormat.Format8bppIndexed);
            ColorPalette palette = bitmap.Palette;
            for (int index = 0; index < palette.Entries.Length; index++)
                palette.Entries[index] = Color.FromArgb(index, index, index);
            bitmap.Palette = palette;
            BitmapData data = null;
            try
            {
                data = bitmap.LockBits(new Rectangle(0, 0, width, height),
                    ImageLockMode.WriteOnly, PixelFormat.Format8bppIndexed);
                int stride = Math.Abs(data.Stride);
                for (int y = 0; y < height; y++)
                {
                    IntPtr destination = data.Stride >= 0
                        ? IntPtr.Add(data.Scan0, y * data.Stride)
                        : IntPtr.Add(data.Scan0, (height - 1 - y) * stride);
                    Marshal.Copy(pixels, y * width, destination, width);
                }
                return bitmap;
            }
            catch
            {
                if (data != null)
                {
                    bitmap.UnlockBits(data);
                    data = null;
                }
                bitmap.Dispose();
                throw;
            }
            finally
            {
                if (data != null) bitmap.UnlockBits(data);
            }
        }
    }
}
