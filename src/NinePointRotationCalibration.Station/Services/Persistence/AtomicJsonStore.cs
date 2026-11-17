using System;
using System.Globalization;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;

namespace NinePointRotationCalibration.Station.Services.Persistence
{
    /// <summary>线程安全的原子化 JSON 存储器，先刷盘写入临时文件再替换目标文件，防止异常中断损坏配置。</summary>
    public sealed class AtomicJsonStore
    {
        private readonly object _sync = new object();

        /// <summary>从指定路径读取并反序列化 JSON 对象。</summary>
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

        /// <summary>加载指定 JSON 文件；文件不存在时调用默认工厂方法创建实例。</summary>
        public T LoadOrDefault<T>(string path, Func<T> defaultFactory) where T : class
        {
            if (defaultFactory == null) throw new ArgumentNullException(nameof(defaultFactory));
            string fullPath = NormalizePath(path);
            return File.Exists(fullPath) ? Load<T>(fullPath) : defaultFactory();
        }

        /// <summary>尝试加载 JSON 文件，失败时不抛异常并返回错误信息。</summary>
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

        /// <summary>将对象以 WriteThrough 刷盘写入临时文件后原子替换目标文件。</summary>
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
                // 存在性检查后若目标文件被其他进程删除，则退回直接重命名移动。
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
                // 宁可残留临时文件也不掩盖主保存流程的原始异常。
            }
        }
    }
}
