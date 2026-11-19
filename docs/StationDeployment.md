# 工作站交付

在开发机运行 `scripts\package-station.ps1`，脚本重建 Release x64 解决方案，并在 `artifacts\NinePointRotationCalibration-Station-<时间>` 生成可拷贝目录和 ZIP。

- `Source` 包含解决方案、源代码、配置、文档和随项目提供的托管相机 SDK 文件，可直接用 Visual Studio 打开 `NinePointRotationCalibration.sln`。
- `Run` 包含 Station Release 可执行程序及其托管依赖。
- `交付说明.txt` 列出目标机器的环境准备事项。

运行日志分两路持久化保存：

- **本地详细调试日志**（推荐上机排查优先查看）：`<程序目录>\logs\station-yyyyMMdd.log`（UTF-8 BOM 编码，若程序目录不可写则自动回退至 `%LOCALAPPDATA%\NinePointRotationCalibration\logs`），按天及单文件 20 MB 自动切分，最多保留 30 份。记录启动配置快照、TCP 收发报文、标定/示教过程、逐板检测 ROI/Blob 参数与耗时拆分、相机操作及完整异常堆栈。
- **系统运行日志**：`%LOCALAPPDATA%\NinePointRotationCalibration\logs\station.log`，单文件达到 10 MB 后滚动，保留 5 份历史日志，记录应用启动/退出、运行时事件、相机切换、UI 操作异常和未处理异常。

目标机器需要安装 Visual Studio 的 .NET 桌面开发工作负载和 .NET Framework 4.7.2 Developer Pack 才能编辑/构建源码。图像检测和模板标定依赖有效授权、版本匹配的 64 位 HALCON Runtime；相机采集需要匹配型号的厂商 native SDK 与驱动。安装程序和许可证未打包。当前开发环境可提供的 HALCON Runtime 版本与项目托管接口版本不一致，因此交付脚本不会复制该不匹配 Runtime。
