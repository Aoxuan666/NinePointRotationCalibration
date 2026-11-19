using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace NinePointRotationCalibration.Station.Services
{
    /// <summary>
    /// 本地详细诊断日志器：默认输出到程序目录下的 logs\station-yyyyMMdd.log（若不可写则回退至 LocalAppData），
    /// 使用异步队列写入以避免阻塞生产检测 CT 与 UI 线程，支持按日与单文件大小滚动、UTF-8 BOM 编码及异常堆栈落盘。
    /// </summary>
    public static class LocalDebugLog
    {
        private const long MaxPartBytes = 20L * 1024L * 1024L;
        private const int MaxRetainedFiles = 30;
        private static readonly Encoding Utf8WithBom = new UTF8Encoding(true);
        private static readonly object InitSync = new object();
        private static readonly object FileSync = new object();
        private static readonly ConcurrentQueue<string> PendingLines = new ConcurrentQueue<string>();
        private static readonly AutoResetEvent LineAvailable = new AutoResetEvent(false);
        private static readonly ManualResetEvent IdleEvent = new ManualResetEvent(true);
        private static readonly int ProcessId = Process.GetCurrentProcess().Id;

        private static bool _initialized;
        private static bool _hooksRegistered;
        private static string _customDirectory;
        private static string _resolvedDirectory;
        private static string _currentFilePath;
        private static Thread _writerThread;

        /// <summary>获取当前生效的本地调试日志目录。</summary>
        public static string LogDirectory
        {
            get
            {
                EnsureInitialized();
                lock (FileSync) return _resolvedDirectory;
            }
        }

        /// <summary>获取当前正在写入的本地调试日志文件路径。</summary>
        public static string CurrentLogFilePath
        {
            get
            {
                EnsureInitialized();
                lock (FileSync)
                {
                    return ResolveTargetFileLocked(DateTime.Now);
                }
            }
        }

        /// <summary>确保本地日志目录、后台写入线程与进程退出刷盘钩子已初始化。</summary>
        public static void EnsureInitialized()
        {
            lock (InitSync)
            {
                if (_initialized) return;

                lock (FileSync)
                {
                    _resolvedDirectory = ResolveWritableDirectory(_customDirectory);
                    _currentFilePath = ResolveTargetFileLocked(DateTime.Now);
                }

                if (!_hooksRegistered)
                {
                    _hooksRegistered = true;
                    AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
                    AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
                }

                _writerThread = new Thread(WriterLoop)
                {
                    Name = "StationLocalDebugLogWriter",
                    IsBackground = true
                };
                _writerThread.Start();
                _initialized = true;
                WriteHeaderLine();
            }
        }

        /// <summary>写入一条本地诊断日志。</summary>
        public static void Write(string level, string category, string message)
        {
            EnsureInitialized();
            string line = FormatLine(level, category, message);
            EnqueueLine(line);

            if (IsHighSeverity(level))
            {
                Flush(500);
            }
        }

        /// <summary>写入一条包含完整异常类型、内部异常与堆栈跟踪的本地诊断日志。</summary>
        public static void WriteException(string level, string category, string context, Exception exception)
        {
            EnsureInitialized();
            string detail = exception == null ? "<null exception>" : FormatException(exception);
            string message = string.IsNullOrWhiteSpace(context)
                ? detail
                : context + " | " + detail;
            EnqueueLine(FormatLine(string.IsNullOrWhiteSpace(level) ? "ERROR" : level, category, message));
            Flush(1000);
        }

        /// <summary>等待后台队列将当前积压日志全部刷入本地磁盘。</summary>
        public static void Flush(int timeoutMilliseconds = 2000)
        {
            if (!_initialized) return;
            if (PendingLines.IsEmpty && IdleEvent.WaitOne(0)) return;
            LineAvailable.Set();
            IdleEvent.WaitOne(Math.Max(50, timeoutMilliseconds));
        }

        /// <summary>供单元测试切换隔离日志目录并清空缓冲状态。</summary>
        internal static void SetLogDirectoryForTesting(string directory)
        {
            EnsureInitialized();
            Flush(2000);
            lock (InitSync)
            {
                lock (FileSync)
                {
                    _customDirectory = directory;
                    _resolvedDirectory = ResolveWritableDirectory(_customDirectory);
                    _currentFilePath = ResolveTargetFileLocked(DateTime.Now);
                }
            }
            WriteHeaderLine();
        }

        private static void EnqueueLine(string line)
        {
            if (string.IsNullOrEmpty(line)) return;
            IdleEvent.Reset();
            PendingLines.Enqueue(line);
            LineAvailable.Set();
        }

        private static void WriterLoop()
        {
            List<string> batch = new List<string>(64);
            while (true)
            {
                DrainBatch(batch);
                if (batch.Count > 0)
                {
                    WriteBatchToDisk(batch);
                    batch.Clear();
                    continue;
                }

                IdleEvent.Set();
                LineAvailable.WaitOne(1000);
            }
        }

        private static void DrainBatch(List<string> batch)
        {
            string line;
            while (batch.Count < 256 && PendingLines.TryDequeue(out line))
            {
                batch.Add(line);
            }
        }

        private static void WriteBatchToDisk(List<string> batch)
        {
            lock (FileSync)
            {
                try
                {
                    DateTime now = DateTime.Now;
                    string path = ResolveTargetFileLocked(now);
                    bool writeBom = !File.Exists(path);
                    using (FileStream stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                    using (StreamWriter writer = new StreamWriter(stream, writeBom ? Utf8WithBom : new UTF8Encoding(false)))
                    {
                        foreach (string line in batch)
                        {
                            writer.WriteLine(line);
                        }
                    }
                }
                catch
                {
                    // 日志写入失败不应反向拖垮业务主流程
                }
            }
        }

        private static string ResolveWritableDirectory(string preferredOverride)
        {
            List<string> candidates = new List<string>();
            if (!string.IsNullOrWhiteSpace(preferredOverride))
            {
                candidates.Add(Path.GetFullPath(preferredOverride.Trim()));
            }
            candidates.Add(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs"));
            candidates.Add(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "NinePointRotationCalibration",
                "logs"));

            foreach (string candidate in candidates)
            {
                if (TryEnsureWritableDirectory(candidate)) return candidate;
            }
            return Path.GetTempPath();
        }

        private static bool TryEnsureWritableDirectory(string directory)
        {
            try
            {
                Directory.CreateDirectory(directory);
                string probe = Path.Combine(directory, ".write-probe-" + Guid.NewGuid().ToString("N") + ".tmp");
                File.WriteAllText(probe, string.Empty, Utf8WithBom);
                File.Delete(probe);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static string ResolveTargetFileLocked(DateTime localTime)
        {
            if (string.IsNullOrWhiteSpace(_resolvedDirectory))
            {
                _resolvedDirectory = ResolveWritableDirectory(_customDirectory);
            }
            Directory.CreateDirectory(_resolvedDirectory);

            string datePrefix = "station-" + localTime.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
            string basePath = Path.Combine(_resolvedDirectory, datePrefix + ".log");
            if (!File.Exists(basePath) || new FileInfo(basePath).Length < MaxPartBytes)
            {
                if (!string.Equals(_currentFilePath, basePath, StringComparison.OrdinalIgnoreCase))
                {
                    _currentFilePath = basePath;
                    PruneOldLogsLocked();
                }
                return basePath;
            }

            for (int part = 1; part <= 999; part++)
            {
                string partPath = Path.Combine(
                    _resolvedDirectory,
                    string.Format(CultureInfo.InvariantCulture, "{0}.{1:000}.log", datePrefix, part));
                if (!File.Exists(partPath) || new FileInfo(partPath).Length < MaxPartBytes)
                {
                    if (!string.Equals(_currentFilePath, partPath, StringComparison.OrdinalIgnoreCase))
                    {
                        _currentFilePath = partPath;
                        PruneOldLogsLocked();
                    }
                    return partPath;
                }
            }

            return basePath;
        }

        private static void PruneOldLogsLocked()
        {
            try
            {
                List<FileInfo> files = new DirectoryInfo(_resolvedDirectory)
                    .GetFiles("station-*.log", SearchOption.TopDirectoryOnly)
                    .OrderByDescending(file => file.LastWriteTimeUtc)
                    .ToList();
                for (int index = MaxRetainedFiles; index < files.Count; index++)
                {
                    try { files[index].Delete(); } catch { }
                }
            }
            catch
            {
            }
        }

        private static void WriteHeaderLine()
        {
            string dir;
            string path;
            lock (FileSync)
            {
                dir = _resolvedDirectory;
                path = _currentFilePath;
            }

            EnqueueLine(FormatLine(
                "INFO",
                "log.init",
                string.Format(
                    CultureInfo.InvariantCulture,
                    "LocalDebugLog initialized. Directory={0}; File={1}; BaseDir={2}; OS={3}; Is64Bit={4}",
                    dir,
                    path,
                    AppDomain.CurrentDomain.BaseDirectory,
                    Environment.OSVersion,
                    Environment.Is64BitProcess)));
        }

        private static string FormatLine(string level, string category, string message)
        {
            string normalizedLevel = NormalizeLevel(level);
            string normalizedCategory = string.IsNullOrWhiteSpace(category) ? "general" : category.Trim();
            string normalizedMessage = NormalizeMessage(message);
            return string.Format(
                CultureInfo.InvariantCulture,
                "{0:yyyy-MM-dd HH:mm:ss.fff} [{1,-5}] [PID:{2}] [T:{3:000}] [{4}] {5}",
                DateTime.Now,
                normalizedLevel,
                ProcessId,
                Thread.CurrentThread.ManagedThreadId,
                normalizedCategory,
                normalizedMessage);
        }

        private static string NormalizeLevel(string level)
        {
            if (string.IsNullOrWhiteSpace(level)) return "INFO";
            string upper = level.Trim().ToUpperInvariant();
            return upper == "WARNING" ? "WARN" : upper;
        }

        private static string NormalizeMessage(string message)
        {
            if (string.IsNullOrEmpty(message)) return string.Empty;
            return message.Replace("\r\n", " \\n ").Replace('\r', ' ').Replace('\n', ' ');
        }

        private static bool IsHighSeverity(string level)
        {
            if (string.IsNullOrWhiteSpace(level)) return false;
            string upper = level.Trim().ToUpperInvariant();
            return upper == "ERROR" || upper == "FATAL";
        }

        private static string FormatException(Exception exception)
        {
            StringBuilder builder = new StringBuilder();
            Exception current = exception;
            int depth = 0;
            while (current != null && depth < 8)
            {
                if (depth > 0) builder.Append(" ---> ");
                builder.Append(current.GetType().FullName);
                builder.Append(": ");
                builder.Append(current.Message);
                if (!string.IsNullOrWhiteSpace(current.StackTrace))
                {
                    builder.Append(" @ ");
                    builder.Append(current.StackTrace);
                }
                current = current.InnerException;
                depth++;
            }
            return builder.ToString();
        }

        private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            Exception ex = e == null ? null : e.ExceptionObject as Exception;
            if (ex != null)
            {
                WriteException("FATAL", "unhandled", "IsTerminating=" + (e != null && e.IsTerminating), ex);
            }
            else
            {
                Write("FATAL", "unhandled", "Non-CLS unhandled exception: " + (e == null ? null : e.ExceptionObject));
            }
            Flush(2000);
        }

        private static void OnProcessExit(object sender, EventArgs e)
        {
            Write("INFO", "shutdown", "Process exiting; flushing LocalDebugLog.");
            Flush(2000);
        }
    }
}
