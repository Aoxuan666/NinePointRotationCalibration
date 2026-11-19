using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;
using NinePointRotationCalibration.Station.Services;
using NinePointRotationCalibration.Station.Views;

namespace NinePointRotationCalibration.Station
{
    /// <summary>视觉工作站应用程序入口与全局 UI/后台未捕获异常兜底处理。</summary>
    internal static class Program
    {
        private static bool _reportingUiException;

        [STAThread]
        private static void Main()
        {
            // 尽早初始化详细日志并挂接全局未捕获异常处理器（UI 线程 / AppDomain / 未观察 Task）
            StationLog.EnsureInitialized();
            LocalDebugLog.EnsureInitialized();
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += HandleThreadException;
            AppDomain.CurrentDomain.UnhandledException += HandleUnhandledException;
            TaskScheduler.UnobservedTaskException += HandleUnobservedTaskException;
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            LocalDebugLog.Write("INFO", "app.main", "Application starting. LogFile=" + LocalDebugLog.CurrentLogFilePath);
            StationRuntime runtime = null;
            try
            {
                runtime = StationRuntime.CreateDefault();
                using (MainForm form = new MainForm())
                using (MainFormRuntimeBinder binder = new MainFormRuntimeBinder(form, runtime))
                {
                    Application.Run(form);
                }
            }
            catch (Exception exception)
            {
                StationLog.WriteException("FATAL", "app.startup", exception);
                LocalDebugLog.WriteException("FATAL", "app.startup", "Station failed to start or crashed during main loop", exception);
                LocalDebugLog.Flush(2000);
                MessageBox.Show(
                    "视觉工作站启动失败：" + exception.Message + Environment.NewLine +
                    "详细日志：" + LocalDebugLog.CurrentLogFilePath,
                    "启动失败",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
            finally
            {
                if (runtime != null)
                {
                    try { runtime.Dispose(); }
                    catch (Exception ex) { LocalDebugLog.WriteException("WARN", "app.shutdown", "StationRuntime.Dispose threw an exception", ex); }
                }
                LocalDebugLog.Write("INFO", "app.main", "Application exited.");
                LocalDebugLog.Flush(2000);
            }
        }

        private static void HandleThreadException(object sender, System.Threading.ThreadExceptionEventArgs eventArgs)
        {
            ReportUnhandledException("WinForms.ThreadException", eventArgs == null ? null : eventArgs.Exception, true);
        }

        private static void HandleUnhandledException(object sender, UnhandledExceptionEventArgs eventArgs)
        {
            ReportUnhandledException(
                "AppDomain.UnhandledException",
                eventArgs == null ? null : eventArgs.ExceptionObject as Exception,
                false);
        }

        private static void HandleUnobservedTaskException(object sender, UnobservedTaskExceptionEventArgs eventArgs)
        {
            if (eventArgs == null) return;
            eventArgs.SetObserved();
            ReportUnhandledException("TaskScheduler.UnobservedTaskException", eventArgs.Exception, false);
        }

        private static void ReportUnhandledException(string source, Exception exception, bool showMessage)
        {
            Exception error = exception ?? new InvalidOperationException("Unknown application error.");
            try
            {
                StationLog.WriteException("ERROR", source, error);
                LocalDebugLog.WriteException("ERROR", source, "Global unhandled exception caught", error);
                LocalDebugLog.Flush(1000);
            }
            catch
            {
            }

            string logPath = null;
            try
            {
                logPath = LocalDebugLog.CurrentLogFilePath;
                string fallbackDirectory = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "NinePointRotationCalibration",
                    "logs");
                Directory.CreateDirectory(fallbackDirectory);
                File.AppendAllText(
                    Path.Combine(fallbackDirectory, "ui-errors.log"),
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " [" + source + "]" + Environment.NewLine +
                    error + Environment.NewLine + Environment.NewLine);
            }
            catch
            {
            }

            // 避免弹窗期间再次触发 UI 异常导致递归弹窗
            if (!showMessage || _reportingUiException) return;
            _reportingUiException = true;
            try
            {
                MessageBox.Show(
                    "本次页面操作未完成，界面已保持运行。" + Environment.NewLine +
                    error.Message +
                    (string.IsNullOrWhiteSpace(logPath) ? string.Empty : Environment.NewLine + "详细日志：" + logPath),
                    "页面操作异常",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
            finally
            {
                _reportingUiException = false;
            }
        }
    }
}
