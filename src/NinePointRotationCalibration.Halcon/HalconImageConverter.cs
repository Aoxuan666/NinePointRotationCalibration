using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using HalconDotNet;

namespace NinePointRotationCalibration.Halcon
{
    /// <summary>
    /// Converts caller-owned bitmaps to HALCON images without retaining or modifying the bitmap.
    /// </summary>
    public static class HalconImageConverter
    {
        public static HImage ToGrayHImage(Bitmap source)
        {
            if (source == null)
            {
                throw new ArgumentNullException(nameof(source));
            }

            if (source.Width <= 0 || source.Height <= 0)
            {
                throw new ArgumentException("The input image has no pixels.", nameof(source));
            }

            int width = source.Width;
            int height = source.Height;
            byte[] gray = CopyGrayPixels(source, width, height);
            GCHandle pinned = default(GCHandle);
            HImage borrowed = null;

            try
            {
                pinned = GCHandle.Alloc(gray, GCHandleType.Pinned);
                borrowed = new HImage();
                borrowed.GenImage1("byte", width, height, pinned.AddrOfPinnedObject());

                // gen_image1 borrows the supplied buffer. copy_image gives the caller an
                // independently owned HALCON image before the managed buffer is unpinned.
                return borrowed.CopyImage();
            }
            finally
            {
                if (borrowed != null)
                {
                    borrowed.Dispose();
                }

                if (pinned.IsAllocated)
                {
                    pinned.Free();
                }
            }
        }

        public static Bitmap ToGrayBitmap(HImage image)
        {
            if (image == null)
            {
                throw new ArgumentNullException(nameof(image));
            }

            int channels;
            using (HTuple channelCount = image.CountChannels())
            {
                channels = channelCount.I;
            }
            if (channels != 1)
            {
                throw new ArgumentException("Only single-channel HALCON images can be converted to a grayscale bitmap.", nameof(image));
            }

            string type;
            int width;
            int height;
            IntPtr pointer = image.GetImagePointer1(out type, out width, out height);
            if (!string.Equals(type, "byte", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("Only HALCON byte images can be converted to a grayscale bitmap.", nameof(image));
            }

            byte[] pixels = new byte[checked(width * height)];
            Marshal.Copy(pointer, pixels, 0, pixels.Length);

            Bitmap bitmap = new Bitmap(width, height, PixelFormat.Format24bppRgb);
            BitmapData data = null;
            try
            {
                data = bitmap.LockBits(
                    new Rectangle(0, 0, width, height),
                    ImageLockMode.WriteOnly,
                    PixelFormat.Format24bppRgb);

                int stride = Math.Abs(data.Stride);
                byte[] row = new byte[stride];
                for (int y = 0; y < height; y++)
                {
                    Array.Clear(row, 0, row.Length);
                    int sourceOffset = y * width;
                    for (int x = 0; x < width; x++)
                    {
                        byte value = pixels[sourceOffset + x];
                        int destinationOffset = x * 3;
                        row[destinationOffset] = value;
                        row[destinationOffset + 1] = value;
                        row[destinationOffset + 2] = value;
                    }

                    IntPtr destination = data.Stride >= 0
                        ? IntPtr.Add(data.Scan0, y * data.Stride)
                        : IntPtr.Add(data.Scan0, (height - 1 - y) * stride);
                    Marshal.Copy(row, 0, destination, stride);
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
                if (data != null)
                {
                    bitmap.UnlockBits(data);
                }
            }
        }

        private static byte[] CopyGrayPixels(Bitmap source, int width, int height)
        {
            using (Bitmap rgb = new Bitmap(width, height, PixelFormat.Format24bppRgb))
            {
                using (Graphics graphics = Graphics.FromImage(rgb))
                {
                    graphics.DrawImageUnscaled(source, 0, 0);
                }

                BitmapData data = null;
                try
                {
                    data = rgb.LockBits(
                        new Rectangle(0, 0, width, height),
                        ImageLockMode.ReadOnly,
                        PixelFormat.Format24bppRgb);

                    int stride = Math.Abs(data.Stride);
                    byte[] row = new byte[stride];
                    byte[] result = new byte[checked(width * height)];

                    for (int y = 0; y < height; y++)
                    {
                        IntPtr sourcePointer = data.Stride >= 0
                            ? IntPtr.Add(data.Scan0, y * data.Stride)
                            : IntPtr.Add(data.Scan0, (height - 1 - y) * stride);
                        Marshal.Copy(sourcePointer, row, 0, stride);

                        int destinationOffset = y * width;
                        for (int x = 0; x < width; x++)
                        {
                            int pixelOffset = x * 3;
                            int blue = row[pixelOffset];
                            int green = row[pixelOffset + 1];
                            int red = row[pixelOffset + 2];
                            result[destinationOffset + x] = (byte)((77 * red + 150 * green + 29 * blue + 128) >> 8);
                        }
                    }

                    return result;
                }
                finally
                {
                    if (data != null)
                    {
                        rgb.UnlockBits(data);
                    }
                }
            }
        }
    }
}
