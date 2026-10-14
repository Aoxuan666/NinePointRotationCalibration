# 九点标定与 PCB 视觉检测工作站

当前主产品是 `NinePointRotationCalibration.Station`：一个独立运行的 C# / WinForms / HALCON 视觉工作站，不再作为上位机进程内插件。

- 视觉工作站独占相机，负责连接、软触发、取图、模板定位、标定、示教、PCB 检测和结果映射。
- 上位机独立负责运动、停稳、安全互锁和复归，不再直接操作相机。
- 双方通过 TCP 交互。视觉端是 Server，默认监听 `0.0.0.0:9100`。

## 已实现

- 手动和自动 9 + 5 标定，点 5 与旋转点 12 共用原点定义。
- 对每个标定点展示图像坐标、机械 X/Y/R、匹配分数和流程状态。
- 独立的标定模板和 PCB 定位模板，支持 HALCON Shape/NCC 示教、定位、多目标匹配和 Job 重载。
- 左/右示教，返回每块板的两侧映射 X/Y、`rOffset` 和 `targetR`。
- 按定位位姿变换每块板的检测 ROI，使用 Blob 检测白叉/白点，支持矩形标签过滤。
- 期望板数校验，`boardIndex` 按空间行优先顺序稳定编号，生产结果通过 TCP 返回。
- Mock 相机用于无硬件开发，大华 IMV 适配器用于现场软触发取图。
- 可分别停用相机和标定，直接导入本地图像调试 PCB 定位、数量、ROI、Blob 和矩形过滤。
- 图像窗口采用纯黑背景，支持 Fit、1:1、鼠标锚点缩放、中键平移、坐标显示和结果 Overlay；相机连接状态由独立状态点显示。

## 系统边界

```text
上位机（运动 + 安全）
          |
          | TCP v1, default 9100
          v
视觉工作站（相机唯一拥有者）
  |-- 9 + 5 标定
  |-- 左/右示教
  |-- PCB 定位 + ROI/Blob 检测
  `-- Job、配置和图像归档
```

TCP 每帧使用 `4-byte unsigned big-endian length + UTF-8 JSON`，一次只允许一个上位机 Client 连接。具体消息和错误码见 [TCP 通讯协议](docs/TcpProtocolV1.md)。

## 运行模式

| 相机使能 | 标定使能 | 适用场景 | 检测结果 |
| --- | --- | --- | --- |
| 开 | 开 | 正常生产、标定和左右示教 | 相机取图，输出视觉结果及左右机械坐标/R 修正 |
| 关 | 开 | 用已标定工位的历史图复现问题 | 只读本地图；标定和示教有效时仍可输出映射坐标 |
| 开 | 关 | 在线取图调模板、ROI、Blob 参数 | 只做视觉检测，`targets` 为空 |
| 关 | 关 | 完全离线算法调试 | 只做本地图视觉检测，`targets` 为空 |

程序启动后始终先启动 TCP 监听。相机使能打开时会自动枚举设备，优先连接保存的序列号，否则连接第一台设备，并设置曝光、启动预览；关闭时不会枚举、连接或取图。“导入图片”和“检测当前图”始终是本地调试路径，不会暗中触发相机。

生产 TCP 流程只允许“相机使能 + 标定使能”同时打开。否则 `inspection.trigger` 返回不可重试的 `CAMERA_DISABLED` 或 `CALIBRATION_DISABLED`，不会把操作员导入的图片当成生产图像。

## 工程结构

```text
src/NinePointRotationCalibration.Station    当前主程序：UI、TCP、相机、流程、检测和示教
src/NinePointRotationCalibration.Core       复用基础：DTO、验证、仿射与旋转求解
src/NinePointRotationCalibration.Halcon     复用基础：HALCON 模板、掩膜、搜索域与定位
src/NinePointRotationCalibration.WinForms   复用基础：图像画布和模板编辑器
src/NinePointRotationCalibration.Sdk        兼容组件：原进程内 SDK 与 Job 保存/加载入口
tests/NinePointRotationCalibration.Station.Tests  Station 协议、流程和检测自测
tests/                                      Core、HALCON、SDK 回归自测
samples/NinePointRotationCalibration.Demo   兼容 SDK 的进程内调用示例
docs/                                       架构、操作和 TCP 联调文档
```

`Core` / `Halcon` / `WinForms` / `Sdk` 继续作为 Station 的算法与编辑基础，也保留旧设备的进程内 SDK 兼容能力。新产线应使用 Station + TCP，不应再让上位机通过 SDK 控制拍照。

## 开发、设计器与运行

技术基线为 `.NET Framework 4.7.2`、C# 7.3、`x64`、WinForms、HALCON 19.11，建议使用 Visual Studio 2019 16.11。

1. 打开 `NinePointRotationCalibration.sln`，选择 `Debug | x64`。
2. 将 `NinePointRotationCalibration.Station` 设为启动项目。
3. 生成解决方案，按 `F5` 启动。

Developer PowerShell 中可直接生成：

```powershell
msbuild .\NinePointRotationCalibration.sln /t:Build /p:Configuration=Debug /p:Platform=x64 /m
```

Debug 启动文件：

```text
src\NinePointRotationCalibration.Station\bin\Debug\NinePointRotationCalibration.Station.exe
```

主界面位于 `src\NinePointRotationCalibration.Station\Views\MainForm.cs`。它有无参数构造和独立 `.Designer.cs`，可在 VS2019 中直接“查看设计器”；设计时不连相机、不启动 TCP。

如果无法打开解决方案，可运行 `scripts\open-vs2019.cmd`。需要安装 Visual Studio 的“使用 .NET 的桌面开发”工作负载和 .NET Framework 4.7.2 targeting pack。

## 运行时依赖

- HALCON：`lib\halcondotnet.dll` 用于编译。现场机必须安装与之匹配的 64 位 HALCON 19.11 Runtime 并具有有效许可。
- Mock 相机：默认模式，适合无硬件开发、UI 检查和 TCP 联调。
- 大华相机：需安装 MV Viewer/IMV x64 驱动与 native runtime。工程优先引用 `lib\Dahua\x64\MVSDK_Net.dll`，否则查找 `C:\Program Files\HuarayTech\MV Viewer\Development\DOTNET DLL\IMV\DOTNET_4.0\x64\MVSDK_Net.dll`。

首次启动会在 `<程序目录>\data` 生成 `station.json`。相机/标定使能状态、标定 Job、定位 Job、示教数据、检测配置和归档图像的默认路径也指向该目录。

## 测试与兼容 SDK 打包

解决方案包含 Station、Core、HALCON 和 SDK 的控制台自测项目。生成后可直接运行各 `tests\*\bin\Debug\*.Tests.exe`。

`scripts\build-release.ps1` 用于重建整个解决方案；`scripts\package-sdk.ps1` 仅保留给兼容 SDK 发布流程，输出位于 `artifacts\sdk\`。`artifacts\sdk\` 不是新 Station 工作站的部署目录。

## 文档入口

- [视觉工作站架构](docs/VisionStationArchitecture.md)
- [操作与联调指南](docs/OperatorGuide.md)
- [TCP 通讯协议 v1](docs/TcpProtocolV1.md)
- [九点加旋转标定技术方案](九点加旋转标定技术方案.md)
- [原进程内 SDK 接入说明（兼容用）](docs/HostIntegration.md)
