using System;
using System.IO;

namespace NinePointRotationCalibration.Halcon
{
    /// <summary>通过临时文件在 HALCON 模型算子与字节数组之间中转序列化数据。</summary>
    internal static class HalconModelFile
    {
        /// <summary>将 HALCON 模型写入临时文件并读取为字节数组，结束后删除临时文件。</summary>
        public static byte[] Write(string extension, Action<string> writer)
        {
            if (writer == null)
            {
                throw new ArgumentNullException(nameof(writer));
            }

            // HALCON 19.11 的模型读写算子仅接受文件路径，这里通过唯一临时文件中转。
            string path = CreatePath(extension);
            try
            {
                writer(path);
                return File.ReadAllBytes(path);
            }
            finally
            {
                DeleteIfExists(path);
            }
        }

        /// <summary>将模型字节数组写入临时文件后交由 HALCON 读取回调加载。</summary>
        public static T Read<T>(byte[] data, string extension, Func<string, T> reader)
        {
            if (data == null || data.Length == 0)
            {
                throw new ArgumentException("HALCON model data is empty.", nameof(data));
            }

            if (reader == null)
            {
                throw new ArgumentNullException(nameof(reader));
            }

            string path = CreatePath(extension);
            try
            {
                File.WriteAllBytes(path, data);
                return reader(path);
            }
            finally
            {
                DeleteIfExists(path);
            }
        }

        private static string CreatePath(string extension)
        {
            string safeExtension = string.IsNullOrWhiteSpace(extension)
                ? ".model"
                : extension.StartsWith(".", StringComparison.Ordinal) ? extension : "." + extension;
            return Path.Combine(Path.GetTempPath(), "npcal-" + Guid.NewGuid().ToString("N") + safeExtension);
        }

        private static void DeleteIfExists(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
                // 尽力清理临时文件；清理失败不应掩盖主流程的 HALCON 结果或异常。
            }
        }
    }
}
