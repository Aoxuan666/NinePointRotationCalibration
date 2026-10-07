# 上位机接入与部署说明

## 1. 对接边界

本项目采用与参考 `ResolutionCalibration` 一致的进程内 DLL 方式。上位机负责轴运动、到位判断、延时停稳、相机触发、图像生命周期和流程互锁；SDK 不直接控制相机、PLC 或运动卡。

标定顺序建议如下：

1. 采集清晰参考图，调用 `OpenTemplateDialog` 完成模板。
2. 上位机依次运动到九个覆盖 X/Y 行程的非共线位置；每次停稳、拍照后调用 `AddSample`。
3. 保持旋转中心附近的工况，采集至少三个且跨度足够的角度；调用 `AddRotationSample`。
4. 调用 `Calculate`；根据 `Success`、错误码和残差决定是否放行。
5. 如需文件持久化，使用高级入口的 `SaveJob` / `LoadJob` 保存和加载 `.nprcal`。

## 2. 引用与平台

上位机项目要求：

- `.NET Framework 4.7.2` 或兼容的更高 .NET Framework。
- 平台固定 `x64`，不要使用 `Any CPU + Prefer 32-bit`。
- 直接引用 `NinePointRotationCalibration.Sdk.dll`。
- 输出目录同时部署 `NinePointRotationCalibration.Core.dll`、`NinePointRotationCalibration.Halcon.dll`、`NinePointRotationCalibration.WinForms.dll` 和 `halcondotnet.dll`。
- 安装与托管 DLL 完全匹配的 64 位 HALCON Runtime，并配置 `HALCONROOT`、`HALCONARCH=x64-win64` 和许可证。

配置窗体必须从 STA 线程调用。WinForms 主 UI 线程默认是 STA；`Locate`、`CaptureSample`、`Calculate` 和保存加载可从工作线程调用。同一个 SDK 实例内部串行保护 HALCON 模型句柄，长期复用并在退出时 `Dispose`。

`src` 目录是源码工程，不建议上位机直接引用源码目录。发布时引用 `NinePointRotationCalibration.Sdk.dll`，并把 `artifacts\sdk` 中的托管 DLL 放在上位机 exe 同目录：

```text
NinePointRotationCalibration.Sdk.dll
NinePointRotationCalibration.Core.dll
NinePointRotationCalibration.Halcon.dll
NinePointRotationCalibration.WinForms.dll
halcondotnet.dll
```

其中 `halcondotnet.dll` 只是 HALCON 的 .NET 接口，机器仍必须安装匹配版本的 64 位 HALCON Runtime、许可证和运行环境变量。上位机工程可以只把 `Sdk.dll` 作为主要引用，但编译器和运行时仍需要上述依赖程序集可解析。

## 3. 公开入口

普通上位机只使用 `CalibrationSdk` 的四个方法：

- `OpenTemplateDialog`：打开页面制作模板，确认后自动清空旧样本。
- `AddSample`：传入图像和 X/Y 轴坐标，添加一个九点标定样本。
- `AddRotationSample`：传入图像和 X/Y/角度，添加一个旋转样本。
- `Calculate`：生成正逆映射矩阵、旋转中心、角度关系和残差。

`NinePointRotationCalibrationSdk` 是高级入口，提供结构化错误、无 UI 建模和预定位复用：

- `OpenSetupDialog`：完整模板、采样、计算配置器。
- `OpenTemplateDialog`：只打开模板编辑器。
- `BuildTemplate`：无 UI 自动示教。
- `Locate` / `LocateAll`：单目标或多目标定位。
- `CaptureSample`：定位并返回追加样本后的新 Job，失败不改原 Job。
- `AddSampleFromMatch`：复用上位机已经获得的匹配结果，避免重复定位。
- `AddSample`：兼容简洁调用风格；失败时抛异常，生产代码优先用 `CaptureSample`。
- `RemoveSample` / `ClearSamples`：返回新 Job，不改入参。
- `Validate` / `Calculate`：校验和求解。
- `SaveJob` / `TrySaveJob` / `LoadJob` / `TryLoadJob`：持久化。

## 4. 典型代码

推荐流程就是“先制作模板，再复用同一个 Job 采集样本，最后计算”。九个平移样本用于生成 XY 仿射矩阵；旋转样本用于在该矩阵基础上计算旋转方向、角度偏置和旋转中心。

```csharp
private CalibrationSdk _calibration = new CalibrationSdk();
private CalibrationJob _job;

private void Configure(Bitmap reference)
{
    _job = _calibration.OpenTemplateDialog(reference, _job, this);
}

private void CaptureNinePoint(Bitmap image, double x, double y)
{
    _job = _calibration.AddSample(_job, image, x, y);
}

private void CaptureRotation(Bitmap image, double x, double y, double angle)
{
    _job = _calibration.AddRotationSample(_job, image, x, y, angle);
}

private CalibrationResult Solve()
{
    return _calibration.Calculate(_job);
}
```

## 5. 坐标与结果

- 图像点使用 HALCON 约定：`Row` 向下、`Column` 向右。
- 机械点使用 `X/Y`；线性单位由 Job 指定，默认 mm。
- SDK 公共角度使用 degree；HALCON 边界内部转换为 radian。
- `StageToPixelMatrix` 输入 `(X,Y)`，输出笛卡尔图像 `(Column,Row)`。
- `PixelToStageMatrix` 输入 `(Column,Row)`，输出 `(X,Y)`。
- `ResolutionX/Y` 是相应机械轴每像素的机械单位。
- 定位输出的 `Anchor` 是用户设置的模板参考中心，不一定是 HALCON 模型原点。
- 修改模板 ROI、掩膜或建模参数会增加模板 Revision；旧样本会被判定为 stale，必须重采。

生产放行至少检查 `CalibrationResult.Success`、`RmsResidualPixels`、`MaxResidualPixels`、`ConditionNumber`、内点数量以及旋转角度/中心残差。阈值均位于 `CalibrationSolverOptions`，应按相机分辨率、平台重复精度和工艺容差配置。

## 6. Job 文件

`.nprcal` 是 ZIP 容器：

- `job.json` 保存参数、ROI、掩膜、中心、样本、阈值和版本；它不直接保存本次 `Calculate` 返回的矩阵结果。
- `model.bin` 保存 HALCON 原生模型。
- 加载时校验 SchemaVersion 和 SHA-256；损坏或不支持的文件会返回 `SerializationError`。

不要在不同 HALCON 大版本之间直接复用模型。升级 Runtime 后应重新示教并完成整套标定验收。

## 7. 现场验收

- 确认机械 X/Y 正方向、角度正方向、单位和相机是否随旋转轴运动。
- 九点必须同时覆盖 X/Y 行程，不能共线或集中在很小区域。
- 旋转样本建议包含负角、零度、正角，总跨度不低于 Job 阈值。
- 用独立验证点检查正变换与逆变换，不要只验证参与拟合的九点。
- 连续运行模板定位与模型重载，观察时间、句柄和内存稳定性。
- 用真实遮挡、亮度波动和边缘工况验证最低分数、搜索 ROI 和掩膜。
