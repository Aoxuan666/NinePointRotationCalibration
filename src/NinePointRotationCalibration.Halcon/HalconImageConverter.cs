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

            // Fast paths read the bitmap buffer directly. Only unusual formats
            // (and indexed bitmaps with a non-gray palette) pay for a GDI+ redraw.
            HImage converted;
            if (source.PixelFormat == PixelFormat.Format8bppIndexed && HasGrayPalette(source))
            {
                converted = FromGray8(source);
                if (converted != null) return converted;
            }
            else if (source.PixelFormat == PixelFormat.Format24bppRgb)
            {
                converted = FromInterleaved(source, PixelFormat.Format24bppRgb, "bgr", 3);
                if (converted != null) return converted;
            }
            else if (source.PixelFormat == PixelFormat.Format32bppRgb ||
                     source.PixelFormat == PixelFormat.Format32bppArgb ||
                     source.PixelFormat == PixelFormat.Format32bppPArgb)
            {
                converted = FromInterleaved(source, source.PixelFormat, "bgrx", 4);
                if (converted != null) return converted;
            }

            using (Bitmap redrawn = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppRgb))
            {
                using (Graphics graphics = Graphics.FromImage(redrawn))
                    graphics.DrawImageUnscaled(source, 0, 0);
                converted = FromInterleaved(redrawn, PixelFormat.Format32bppRgb, "bgrx", 4);
                if (converted == null)
                    throw new InvalidOperationException("The bitmap buffer layout is not supported.");
                return converted;
            }
        }

        private static HImage FromGray8(Bitmap bitmap)
        {
            int width = bitmap.Width;
            int height = bitmap.Height;
            BitmapData data = bitmap.LockBits(
                new Rectangle(0, 0, width, height),
                ImageLockMode.ReadOnly,
                PixelFormat.Format8bppIndexed);
            try
            {
                if (data.Stride <= 0) return null;
                HImage image = new HImage();
                try
                {
                    // gen_image1 copies the buffer, so the bitmap can be unlocked afterwards.
                    image.GenImage1("byte", data.Stride, height, data.Scan0);
                    if (data.Stride == width) return image;
                    HImage cropped = image.CropPart(0, 0, width, height);
                    image.Dispose();
                    return cropped;
                }
                catch
                {
                    image.Dispose();
                    throw;
                }
            }
            finally
            {
                bitmap.UnlockBits(data);
            }
        }

        private static HImage FromInterleaved(Bitmap bitmap, PixelFormat lockFormat, string colorFormat, int bytesPerPixel)
        {
            int width = bitmap.Width;
            int height = bitmap.Height;
            BitmapData data = bitmap.LockBits(
                new Rectangle(0, 0, width, height),
                ImageLockMode.ReadOnly,
                lockFormat);
            HImage interleaved = null;
            try
            {
                int packed = width * bytesPerPixel;
                int alignment;
                if (data.Stride == packed) alignment = -1;
                else if (data.Stride == ((packed + 3) / 4) * 4) alignment = 4;
                else return null;

                interleaved = new HImage();
                interleaved.GenImageInterleaved(
                    data.Scan0,
                    colorFormat,
                    width,
                    height,
                    alignment,
                    "byte",
                    width,
                    height,
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
                bitmap.UnlockBits(data);
            }
        }

        private static bool HasGrayPalette(Bitmap bitmap)
        {
            Color[] entries = bitmap.Palette.Entries;
            if (entries.Length < 256) return false;
            for (int index = 0; index < 256; index++)
            {
                Color color = entries[index];
                if (color.R != index || color.G != index || color.B != index) return false;
            }
            return true;
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
