using System;
using System.IO;
using System.Text;
using System.Threading;

namespace NinePointRotationCalibration.Station.Services
{
    /// <summary>进程级滚动文件日志服务，并统一记录全局未捕获异常与退出事件。</summary>
    public static class StationLog
    {
        private const long MaximumFileBytes = 10 * 1024 * 1024;
        private const int BackupCount = 5;
        private static readonly object Sync = new object();
        private static int _initialized;
        private static string _logDirectory;

        /// <summary>本地日志目录路径（访问时自动完成初始化）。</summary>
        public static string LogDirectory
        {
            get { EnsureInitialized(); return _logDirectory; }
        }

        /// <summary>幂等初始化日志目录并挂载进程级异常与退出监听。</summary>
        public static void EnsureInitialized()
        {
            if (Interlocked.CompareExchange(ref _initialized, 1, 0) != 0) return;
            try
            {
                string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                _logDirectory = Path.Combine(local, "NinePointRotationCalibration", "logs");
                Directory.CreateDirectory(_logDirectory);
                AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
                AppDomain.CurrentDomain.ProcessExit += CurrentDomain_ProcessExit;
                System.Threading.Tasks.TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;
                System.Windows.Forms.Application.ThreadException += Application_ThreadException;
                Write("INFO", "startup", "Station process started. Version=" +
                    (System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "unknown") +
                    "; Machine=" + Environment.MachineName + "; OS=" + Environment.OSVersion +
                    "; 64bit=" + Environment.Is64BitProcess + "; LogDirectory=" + _logDirectory);
            }
            catch
            {
                Interlocked.Exchange(ref _initialized, 0);
            }
        }

        /// <summary>线程安全地写入一条格式化文本日志并在超限时自动轮转。</summary>
        public static void Write(string level, string source, string message)
        {
            try
            {
                if (Volatile.Read(ref _initialized) == 0)
                {
                    string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                    _logDirectory = Path.Combine(local, "NinePointRotationCalibration", "logs");
                    Directory.CreateDirectory(_logDirectory);
                }
                string line = string.Format(
                    System.Globalization.CultureInfo.InvariantCulture,
                    "{0:O} [{1}] [PID:{2}] [{3}] {4}{5}",
                    DateTimeOffset.Now,
                    string.IsNullOrWhiteSpace(level) ? "INFO" : level.Trim().ToUpperInvariant(),
                    System.Diagnostics.Process.GetCurrentProcess().Id,
                    string.IsNullOrWhiteSpace(source) ? "app" : source.Trim(),
                    message ?? string.Empty,
                    Environment.NewLine);
                lock (Sync)
                {
                    string path = Path.Combine(_logDirectory, "station.log");
                    RotateIfNeeded(path, Encoding.UTF8.GetByteCount(line));
                    File.AppendAllText(path, line, new UTF8Encoding(false));
                }
            }
            catch { }
        }

        /// <summary>记录异常的完整堆栈信息。</summary>
        public static void WriteException(string level, string source, Exception exception)
        {
            if (exception != null) Write(level, source, exception.ToString());
        }

        private static void RotateIfNeeded(string path, int incomingBytes)
        {
            if (!File.Exists(path) || new FileInfo(path).Length + incomingBytes <= MaximumFileBytes) return;
            // 超出单文件上限时按 .1 ~ .BackupCount 依次顺延归档旧日志文件
            string oldest = path + "." + BackupCount;
            if (File.Exists(oldest)) File.Delete(oldest);
            for (int index = BackupCount - 1; index >= 1; index--)
            {
                string source = path + "." + index;
                if (File.Exists(source)) File.Move(source, path + "." + (index + 1));
            }
            File.Move(path, path + ".1");
        }

        private static void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            Exception exception = e.ExceptionObject as Exception;
            WriteException("FATAL", "AppDomain.UnhandledException", exception);
            if (exception == null) Write("FATAL", "AppDomain.UnhandledException", Convert.ToString(e.ExceptionObject));
        }

        private static void TaskScheduler_UnobservedTaskException(object sender, System.Threading.Tasks.UnobservedTaskExceptionEventArgs e)
        {
            WriteException("ERROR", "TaskScheduler.UnobservedTaskException", e.Exception);
        }

        private static void Application_ThreadException(object sender, System.Threading.ThreadExceptionEventArgs e)
        {
            WriteException("ERROR", "WinForms.ThreadException", e.Exception);
        }

        private static void CurrentDomain_ProcessExit(object sender, EventArgs e)
        {
            Write("INFO", "shutdown", "Station process is exiting.");
        }
    }
}
