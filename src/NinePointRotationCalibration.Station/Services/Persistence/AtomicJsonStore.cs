using System;
using System.Globalization;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;

namespace NinePointRotationCalibration.Station.Services.Persistence
{
    public sealed class AtomicJsonStore
    {
        private readonly object _sync = new object();

        public T Load<T>(string path) where T : class
        {
            string fullPath = NormalizePath(path);

            lock (_sync)
            {
                using (FileStream stream = new FileStream(
                    fullPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read | FileShare.Delete))
                {
                    T value = CreateSerializer<T>().ReadObject(stream) as T;
                    if (value == null)
                    {
                        throw new SerializationException("JSON document did not contain a " + typeof(T).FullName + ".");
                    }

                    return value;
                }
            }
        }

        public T LoadOrDefault<T>(string path, Func<T> defaultFactory) where T : class
        {
            if (defaultFactory == null) throw new ArgumentNullException(nameof(defaultFactory));
            string fullPath = NormalizePath(path);
            return File.Exists(fullPath) ? Load<T>(fullPath) : defaultFactory();
        }

        public bool TryLoad<T>(string path, out T value, out string error) where T : class
        {
            try
            {
                value = Load<T>(path);
                error = string.Empty;
                return true;
            }
            catch (Exception exception)
            {
                value = null;
                error = exception.Message;
                return false;
            }
        }

        public void Save<T>(string path, T value) where T : class
        {
            if (value == null) throw new ArgumentNullException(nameof(value));
            string fullPath = NormalizePath(path);
            string directory = Path.GetDirectoryName(fullPath);
            string tempPath = Path.Combine(
                directory,
                "." + Path.GetFileName(fullPath) + "." + Guid.NewGuid().ToString("N") + ".tmp");

            lock (_sync)
            {
                Directory.CreateDirectory(directory);
                try
                {
                    using (FileStream stream = new FileStream(
                        tempPath,
                        FileMode.CreateNew,
                        FileAccess.Write,
                        FileShare.None,
                        4096,
                        FileOptions.WriteThrough))
                    {
                        CreateSerializer<T>().WriteObject(stream, value);
                        stream.Flush(true);
                    }

                    Commit(tempPath, fullPath);
                }
                finally
                {
                    TryDelete(tempPath);
                }
            }
        }

        private static void Commit(string tempPath, string destinationPath)
        {
            if (!File.Exists(destinationPath))
            {
                File.Move(tempPath, destinationPath);
                return;
            }

            try
            {
                File.Replace(tempPath, destinationPath, null, true);
            }
            catch (FileNotFoundException)
            {
                // Another process removed the destination after the existence check.
                File.Move(tempPath, destinationPath);
            }
        }

        private static DataContractJsonSerializer CreateSerializer<T>()
        {
            return new DataContractJsonSerializer(
                typeof(T),
                new DataContractJsonSerializerSettings
                {
                    DateTimeFormat = new DateTimeFormat("O", CultureInfo.InvariantCulture),
                    UseSimpleDictionaryFormat = true
                });
        }

        private static string NormalizePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("A JSON file path is required.", nameof(path));
            return Path.GetFullPath(path);
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch
            {
                // A stale temp file is preferable to hiding the original save error.
            }
        }
    }
}
