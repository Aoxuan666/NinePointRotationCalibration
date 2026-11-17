using System;
using System.Windows.Forms;

namespace NinePointRotationCalibration.Demo
{
    /// <summary>九点加旋转标定 WinForms 示例程序入口。</summary>
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }
}
