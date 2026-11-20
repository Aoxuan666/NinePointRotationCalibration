using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace NinePointRotationCalibration.Station.Services
{
    /// <summary>
    /// 第三方/外包 DLL 隔离加载器：在程序入口最早期配置托管程序集解析与原生 C++/GenTL 搜索路径，
    /// 使所有外部依赖（HALCON、大华、海康）统一存放于运行目录下的 libs\ 子目录，与自有业务代码产物彻底分离。
    /// </summary>
    internal static class ExternalRuntimeLoader
    {
        private const uint LoadLibrarySearchDefaultDirs = 0x00001000;
        private static readonly string[] RelativeProbeDirs =
        {
            "libs",
            Path.Combine("libs", "halcon"),
            Path.Combine("libs", "dahua"),
            Path.Combine("libs", "hikvision")
        };

        private static bool _initialized;
        private static string[] _probeDirectories = new string[0];

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetDefaultDllDirectories(uint directoryFlags);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr AddDllDirectory(string newDirectory);

        /// <summary>初始化外部库解析器与原生 DLL/GenTL 搜索目录（必须在任何 HALCON/相机类型加载前调用）。</summary>
        public static void Initialize()
        {
            if (_initialized) return;
            _initialized = true;

            string baseDir = AppDomain.CurrentDomain.BaseDirectory ?? string.Empty;
            List<string> dirs = new List<string>();
            for (int i = 0; i < RelativeProbeDirs.Length; i++)
            {
                string full = Path.Combine(baseDir, RelativeProbeDirs[i]);
                if (Directory.Exists(full))
                {
                    dirs.Add(full);
                }
            }

            _probeDirectories = dirs.ToArray();
            AppDomain.CurrentDomain.AssemblyResolve += OnAssemblyResolve;

            if (_probeDirectories.Length == 0) return;

            try
            {
                SetDefaultDllDirectories(LoadLibrarySearchDefaultDirs);
                for (int i = 0; i < _probeDirectories.Length; i++)
                {
                    AddDllDirectory(_probeDirectories[i]);
                }
            }
            catch
            {
                // 旧版系统或受限环境下退回仅使用进程 PATH 注入
            }

            PrependEnvironmentPath("PATH", _probeDirectories);
            string dahuaDir = Path.Combine(baseDir, "libs", "dahua");
            if (Directory.Exists(dahuaDir))
            {
                PrependEnvironmentPath("GENICAM_GENTL64_PATH", new[] { dahuaDir });
            }
        }

        private static Assembly OnAssemblyResolve(object sender, ResolveEventArgs args)
        {
            if (args == null || string.IsNullOrWhiteSpace(args.Name)) return null;
            string shortName;
            try
            {
                shortName = new AssemblyName(args.Name).Name;
            }
            catch
            {
                return null;
            }
            if (string.IsNullOrWhiteSpace(shortName)) return null;

            for (int i = 0; i < _probeDirectories.Length; i++)
            {
                string candidate = Path.Combine(_probeDirectories[i], shortName + ".dll");
                if (File.Exists(candidate))
                {
                    return Assembly.LoadFrom(candidate);
                }
            }

            return null;
        }

        private static void PrependEnvironmentPath(string variableName, IEnumerable<string> directories)
        {
            string existing = Environment.GetEnvironmentVariable(variableName) ?? string.Empty;
            string prefix = string.Join(";", directories);
            if (string.IsNullOrWhiteSpace(prefix)) return;
            string updated = string.IsNullOrWhiteSpace(existing) ? prefix : (prefix + ";" + existing);
            Environment.SetEnvironmentVariable(variableName, updated);
        }
    }
}
