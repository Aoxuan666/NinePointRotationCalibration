using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using HalconDotNet;

namespace NinePointRotationCalibration.Halcon
{
    /// <summary>
    /// 将调用方拥有的 Bitmap 转换为 HALCON 图像，不持有或修改调用方对象。
    /// </summary>
    public static class HalconImageConverter
    {
        /// <summary>将调用方拥有的 Bitmap 转换为单通道 HALCON byte 图像。</summary>
        public static HImage ToGrayHImage(Bitmap source)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (source.Width <= 0 || source.Height <= 0)
                throw new ArgumentException("The input image has no pixels.", nameof(source));

            // 常用格式直接读取 Bitmap 缓冲区；只有特殊格式或非灰度索引图像才回退到 GDI+ 重绘。
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
                    // gen_image1 会复制像素缓冲区，因此随后即可安全释放 Bitmap 锁；若存在行对齐填充则裁剪为精确宽度。
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
                // HALCON 要求指定紧致(-1)或 4 字节对齐的扫描行步长，不匹配则回退到 GDI+ 重绘。
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

                // rgb1_to_gray 会生成独立内存的 HALCON 灰度图，确保在解锁 Bitmap 前不悬空指针。
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

        /// <summary>将单通道 HALCON byte 图像复制为由本方法拥有的灰度 Bitmap。</summary>
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
                // 兼容自下而上存储（Stride 为负）与带行填充的 Bitmap 扫描行写入。
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
