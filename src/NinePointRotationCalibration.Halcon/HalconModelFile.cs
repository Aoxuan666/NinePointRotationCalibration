using System;
using System.IO;

namespace NinePointRotationCalibration.Halcon
{
    internal static class HalconModelFile
    {
        public static byte[] Write(string extension, Action<string> writer)
        {
            if (writer == null)
            {
                throw new ArgumentNullException(nameof(writer));
            }

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
                // A failed best-effort cleanup must not hide the HALCON result/error.
            }
        }
    }
}
