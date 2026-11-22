using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace NinePointRotationCalibration.Station.Services
{
    /// <summary>
    /// 第三方/外包 DLL 隔离加载器：在程序入口最早期配置托管程序集解析与原生 C++/GenTL 搜索路径，
    /// 使所有外部依赖（HALCON、大华、海康）统一存放于运行目录下的 libs\ 子目录，与自有业务代码产物彻底分离。
    /// </summary>
    internal static class ExternalRuntimeLoader
    {
        private static readonly string[] RelativeProbeDirs =
        {
            "libs",
            Path.Combine("libs", "halcon"),
            Path.Combine("libs", "dahua"),
            Path.Combine("libs", "hikvision")
        };

        private static bool _initialized;
        private static string[] _probeDirectories = new string[0];

        [ThreadStatic]
        private static HashSet<string> _resolvingAssemblies;

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

            // 若目标机已安装 HALCON，保留其原生与托管目录作为兜底搜索路径
            string halconRoot = Environment.GetEnvironmentVariable("HALCONROOT");
            if (!string.IsNullOrWhiteSpace(halconRoot))
            {
                string halconDotNetDir = Path.Combine(halconRoot, "bin", "dotnet35");
                if (Directory.Exists(halconDotNetDir)) dirs.Add(halconDotNetDir);
                string halconNativeDir = Path.Combine(halconRoot, "bin", "x64-win64");
                if (Directory.Exists(halconNativeDir)) dirs.Add(halconNativeDir);
            }

            _probeDirectories = dirs.ToArray();
            AppDomain.CurrentDomain.AssemblyResolve += OnAssemblyResolve;

            if (_probeDirectories.Length == 0) return;

            // 注意：不能调用 SetDefaultDllDirectories(LOAD_LIBRARY_SEARCH_DEFAULT_DIRS)，
            // 否则会禁用系统 PATH 搜索，导致系统已安装的 halcon.dll / 相机驱动原生库无法加载。
            // 仅通过进程级 PATH 与 GENICAM_GENTL64_PATH 前置注入，既优先查找 libs\ 又完整兼容系统环境。
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

            if (_resolvingAssemblies == null)
                _resolvingAssemblies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!_resolvingAssemblies.Add(shortName)) return null;
            try
            {
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
            finally
            {
                _resolvingAssemblies.Remove(shortName);
            }
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
