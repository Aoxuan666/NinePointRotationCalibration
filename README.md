# 九点加旋转标定 SDK

面向上位机进程内调用的 C# / WinForms / HALCON 标定组件。上位机负责运动、停稳和拍照；SDK 负责模板示教与定位、样本管理、九点 XY 仿射求解、旋转方向/偏置/中心求解、Job 持久化和完整配置界面。

## 已实现

- HALCON Shape 与 NCC 模板示教、定位、多目标定位和模型重载。
- 旋转模板 ROI、搜索 ROI、Include/Exclude 区域、有序擦除/恢复笔画和掩膜反选。
- 模板参考中心数值编辑、鼠标拖动、锁定/解锁；匹配时按姿态输出调整后的中心。
- 图像 Fit、1:1、鼠标锚点缩放、中键平移、ROI 移动/缩放/旋转、固定屏幕尺寸句柄和运行 Overlay。
- 九点鲁棒仿射、RANSAC + MAD 离群剔除、正逆矩阵、解析度、轴夹角、条件数和逐点残差。
- 旋转正反向、角度偏置、XY 位移补偿后的旋转中心和质量阈值。
- 不修改调用方对象的 SDK 接口、结构化错误、ZIP Job 包、模型哈希校验和原子覆盖保存。

## 工程结构

```text
src/NinePointRotationCalibration.Core       DTO、验证、仿射与旋转求解
src/NinePointRotationCalibration.Halcon     HALCON 模板、掩膜、搜索域与定位
src/NinePointRotationCalibration.WinForms   图像画布、模板编辑器、标定配置器
src/NinePointRotationCalibration.Sdk        上位机 DLL 入口与 Job 保存/加载
samples/NinePointRotationCalibration.Demo   可运行的上位机调用示例
tests/                                      Core、HALCON、SDK 回归自测
docs/HostIntegration.md                     上位机接入与部署说明
```

技术基线为 `.NET Framework 4.7.2`、`x64`、WinForms、HALCON 19.11。详细设计见 [九点加旋转标定技术方案.md](九点加旋转标定技术方案.md)。

## 构建与打包

在 Visual Studio 2019（建议 16.11）中打开 `NinePointRotationCalibration.sln`，选择 `Release | x64`；或运行：

```powershell
.\scripts\build-release.ps1
.\scripts\package-sdk.ps1
```

部署输出位于 `artifacts\sdk\`。现场电脑必须安装与 `halcondotnet.dll` 匹配的 64 位 HALCON Runtime 并具有有效许可证。

### 无法打开解决方案

工程已转换为传统 .NET Framework 项目格式，适配 Visual Studio 2019/MSBuild 16.11。若系统文件关联异常，请双击 `scripts\open-vs2019.cmd`，或在命令行执行：

```powershell
& "C:\Program Files (x86)\Microsoft Visual Studio\2019\Professional\Common7\IDE\devenv.exe" `
  "C:\Users\智茂软件研发部\Documents\ChatGPT\九点标定工具开发\NinePointRotationCalibration.sln"
```

如果机器没有 VS2019，请安装“使用 .NET 的桌面开发”和“.NET Framework 4.7.2 targeting pack”工作负载。

## 最小调用

```csharp
using (var sdk = new CalibrationSdk())
{
    job = sdk.OpenTemplateDialog(referenceBitmap, job);
    job = sdk.AddSample(job, image, stageX, stageY);
    job = sdk.AddRotationSample(job, rotationImage, stageX, stageY, stageTheta);
    CalibrationResult result = sdk.Calculate(job);
}
```

九点位置循环调用 `AddSample`，旋转角度循环调用 `AddRotationSample`。每次都要接收方法返回的新 Job。

完整生命周期、线程约束、坐标定义和部署清单见 [上位机接入说明](docs/HostIntegration.md)。
