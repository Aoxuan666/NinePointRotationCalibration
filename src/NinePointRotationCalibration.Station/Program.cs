using System;
using System.IO;
using System.Windows.Forms;
using NinePointRotationCalibration.Station.Services;
using NinePointRotationCalibration.Station.Views;

namespace NinePointRotationCalibration.Station
{
    internal static class Program
    {
        private static bool _reportingUiException;

        [STAThread]
        private static void Main()
        {
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += HandleThreadException;
            AppDomain.CurrentDomain.UnhandledException += HandleUnhandledException;
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

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
                MessageBox.Show(
                    "视觉工作站启动失败：" + exception.Message,
                    "启动失败",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
            finally
            {
                if (runtime != null) runtime.Dispose();
            }
        }

        private static void HandleThreadException(object sender, System.Threading.ThreadExceptionEventArgs eventArgs)
        {
            ReportUnhandledException(eventArgs == null ? null : eventArgs.Exception, true);
        }

        private static void HandleUnhandledException(object sender, UnhandledExceptionEventArgs eventArgs)
        {
            ReportUnhandledException(eventArgs == null ? null : eventArgs.ExceptionObject as Exception, false);
        }

        private static void ReportUnhandledException(Exception exception, bool showMessage)
        {
            Exception error = exception ?? new InvalidOperationException("Unknown application error.");
            string logPath = null;
            try
            {
                string logDirectory = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "NinePointRotationCalibration",
                    "logs");
                Directory.CreateDirectory(logDirectory);
                logPath = Path.Combine(logDirectory, "ui-errors.log");
                File.AppendAllText(
                    logPath,
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + Environment.NewLine +
                    error + Environment.NewLine + Environment.NewLine);
            }
            catch
            {
            }

            if (!showMessage || _reportingUiException) return;
            _reportingUiException = true;
            try
            {
                MessageBox.Show(
                    "本次页面操作未完成，界面已保持运行。" + Environment.NewLine +
                    error.Message +
                    (string.IsNullOrWhiteSpace(logPath) ? string.Empty : Environment.NewLine + "日志：" + logPath),
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
