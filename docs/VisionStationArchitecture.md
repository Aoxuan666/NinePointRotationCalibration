# 独立视觉工作站架构

## 1. 边界

新架构把视觉程序从上位机进程中拆出，部署为独立 WinForms 工作站。

- 相机使能时视觉工作站独占相机，负责连接、软触发、取图、模板匹配、标定、检测和结果存档；相机停用时可只处理操作员导入的本地图像。
- 上位机独占运动控制，负责移动、停稳、安全互锁和复归。
- 双方仅通过 TCP 交换有关联 ID 的结构化消息。视觉程序不直接调用运动卡，上位机不再操作相机。

```text
Upper machine
  motion + safety
       |
       | TCP v1
       v
Vision Station
  workflow coordinator
    |-- camera acquisition (single owner)
    |-- calibration SDK (HALCON)
    |-- board locate + transformed ROI + blob
    |-- teaching and target mapping
    `-- recipe / image / job persistence
```

## 2. 工程分层

| 工程 | 职责 |
| --- | --- |
| `NinePointRotationCalibration.Core` | 坐标、Job、仿射和旋转求解 |
| `NinePointRotationCalibration.Halcon` | Shape/NCC 模板、多目标定位 |
| `NinePointRotationCalibration.WinForms` | 现有模板编辑器和标定画布 |
| `NinePointRotationCalibration.Sdk` | 稳定的标定/定位/Job 保存入口 |
| `NinePointRotationCalibration.Station` | 独立程序、TCP、相机、流程、检测、示教和主界面 |

Station 内部不使用全局静态相机或共享帧缓冲。`CameraAcquisitionService` 是设备的唯一拥有者，标定、检测和手动拍照都通过同一个串行门进入，防止大华 SDK 共享帧缓冲竞态。

## 3. 9 + 5 标定

平移点使用 3 x 3 蛇形顺序，点 5 是标定原点。旋转点为 `-2dR, -dR, 0, +dR, +2dR`，点 12 与点 5 共用同一个原点定义。

手动和自动标定共用同一状态机：

```text
Pending -> Moving -> Capturing -> Locating -> Completed
                  \-> timeout / camera / match error -> Failed
```

1. 视觉发送 `calibration.move.request`，点位是相对“标定原点”的偏移，不是相对上一点的增量。
2. 上位机停稳后返回 `motion.arrived`，必须携带实际 `X/Y/R` 和请求的 `correlationId`。
3. 视觉内部触发相机，立即刷新图像窗口，用标定模板定位并记录图像坐标和机械坐标。
4. 单点结果通过 `calibration.point.result` 回传；14 点完成后求解并发送 `calibration.completed`。

重采某点时按 Tag 替换旧样本，不会把重复点静默追加到求解集。

## 4. 模板、示教和检测

标定模板和定位模板是两个独立 Job：

- 标定模板只用于 9 + 5 采点。
- 定位模板用于生产拍照中的多 PCB 板定位，并拥有该板的检测 ROI。定位模板编辑器在同一张参考图上编辑模板和检测 ROI，支持鼠标创建、移动、缩放和旋转；确认后两者一起写入定位 Job。
- 检测页不再保存另一套 Row/Column/Phi/半宽/半高。`FollowTemplateTransform=true` 时，检测 ROI 从定位 Job 读取，并按每个匹配板的中心和角度做刚体变换，因此定位到 N 块板会得到 N 个随板移动、旋转的检测区域。
- `FollowTemplateTransform=false` 时忽略自定义检测 ROI，对每个匹配板分别将定位模板区域换算为轴对齐外接矩形；定位到 N 块板仍得到 N 个独立检测区域，不存在整幅图共用的全局 ROI。
- 每个变换后的 ROI 独立做高亮 Blob 分析，归属不依赖全局距离猜测。
- “矩形过滤”打开时，填充率超过阈值的矩形标签 Blob 会被标记为已过滤，不作为白色叉/点。
- 定位数与期望数不等时，检测结果返回 `BOARD_COUNT_MISMATCH`。
- 检测调试窗口可在锁定的离线图像上显示 ROI 裁剪后的完整阈值分割，支持黑底白目标和白底黑目标两种极性，并对阈值/面积/矩形度变化做防抖、取消和 latest-wins 更新；调试结束只回写检测参数，不替换生产帧。
- `TemplateMatchingEnabled=false` 是离线调试旁路：跳过定位模板，对整幅图执行一次 Blob 分析，结果不生成机械目标；生产 `inspection.trigger` 默认保持 `true`。

左/右示教各保存一组模板图像姿态和机械姿态。每块板的左/右目标 XY 使用完整 `PixelToStage` 仿射矩阵映射：

```text
targetXY = teachMachineXY
         + PixelToStage(boardAnchor)
         - PixelToStage(teachImageAnchor)
```

R 轴使用标定出的方向系数修正图像角度差，同时返回 `rOffset` 和 `targetR`，避免上位机再猜测符号。

## 5. 相机适配

`MockCameraDevice` 是默认驱动，用于无硬件开发和 TCP 联调。`DahuaCameraDevice` 直接封装大华 `MVSDK_Net`，严格执行枚举、创建、打开、软触发、取帧、释放帧和销毁流程。选择哪个驱动在运行时组合根创建时决定，所以修改相机驱动后必须重启工作站。

工作站启动时始终先启动 TCP Server。`CameraEnabled=true` 时随后自动枚举相机，优先连接 `CameraSerialNumber`，未配置时连接枚举到的第一台设备，再设置曝光并启动预览。自动连接失败只把相机状态置为异常，不终止 TCP 服务。运行中关闭相机使能会先停预览并断开设备；重新打开时会自动尝试连接。

## 6. 使能与图像来源

`CameraEnabled` 与 `CalibrationEnabled` 是相互独立且持久化的运行开关：

| 相机 | 标定 | 本地图检测 | 生产 TCP 检测 |
| --- | --- | --- | --- |
| 开 | 开 | 完整视觉检测和 XY/R/左右目标映射 | 允许，相机软触发并返回完整结果 |
| 关 | 开 | 不访问相机；已有标定和左右示教有效时可映射坐标 | 拒绝，返回 `CAMERA_DISABLED` |
| 开 | 关 | 只做定位、数量、ROI、Blob；每块板 `Targets` 为空 | 拒绝，返回 `CALIBRATION_DISABLED` |
| 关 | 关 | 只做定位、数量、ROI、Blob；每块板 `Targets` 为空 | 拒绝，优先返回 `CAMERA_DISABLED` |

本地图路径由 UI 保有当前图像，再调用 `RunOfflineInspectionAsync`。它与生产检测共用同一个工作流互斥门和检测算法，但不调用 `CameraAcquisitionService.CaptureAsync`，也不读取任何相机缓冲。关闭标定时使用 `InspectWithoutMapping`，不要求 XY/R 标定和左右示教，且绝不生成猜测的机械坐标。

`inspection.trigger` 只代表实时生产拍照，不能复用 UI 当前图；这样可避免上位机误把调试图片的结果用于运动。`CAMERA_DISABLED` 和 `CALIBRATION_DISABLED` 均不可自动重试，必须由操作员恢复对应使能。

## 7. UI 与线程

主窗体保持一个纯黑的大图像区，参数分成标定、示教、模板、检测、相机、通讯/文件页。顶部不显示产品大标题，只保留运行状态、“导入图片”和“检测当前图”等常用操作；相机和标定开关集中在相机页。相机状态点使用绿色表示已连接，黄色表示连接中，红色表示异常，灰色表示未连接或已停用。TCP 使用同样的圆点表达状态：绿色表示上位机已连接，黄色表示正在监听并等待连接，灰色表示未监听。

`MainForm` 有无参数构造和独立 `.Designer.cs`，运行时服务由外部 Binder 绑定；设计器加载时不连相机、不启动 TCP、不进入 HALCON 模板流程。空图像视口只绘制黑色背景，不显示网格、说明文字或装饰标记。图像窗口下方只保留图像信息和光标坐标，不显示进度条；Mock 图像也不再绘制底部绿色装饰条。

设备 I/O、运行时定位、标定计算和检测不阻塞 UI 线程；模板编辑是用户确认后才返回的模态界面流程。图像事件只传递自有 Bitmap 副本，控件替换图像时释放上一帧，不在预览路径中累积 GDI 句柄。

## 8. 配置与文件

默认数据目录是 `<程序目录>\data`，首次启动会自动创建。

| 文件 | 用途 |
| --- | --- |
| `station.json` | 相机/标定使能、TCP、相机、超时、步距和路径 |
| `calibration.nprcal` | 标定模板、样本和求解参数 |
| `positioning.nprcal` | PCB 多目标定位模板及检测 ROI 几何 |
| `teaching.json` | 左/右示教数据 |
| `detection-profile.json` | 模板匹配开关、ROI 使用模式、亮度/面积阈值、矩形过滤；不保存当前 ROI 几何 |

配置使用临时文件 + 原子替换保存。Job 继续使用现有带模型 hash 校验的 `.nprcal` 包。
相机驱动、TCP 监听地址和端口在组合根创建时生效，修改并保存后需重启程序；默认 TCP 端口为 `9100`。

## 9. 现场验收

1. 确认上位机的 X/Y/R 单位、方向和标定原点生命周期。
2. 用模拟相机和 TCP 模拟器跑完手动/自动 14 点、超时、断线和重连。
3. 用实机固定曝光后验证 1000 次软触发无丢帧、无帧缓冲泄漏。
4. 采集黑板/蓝板、白叉、白点和矩形标签样本，锁定 Blob 阈值和矩形过滤阈值。
5. 用旋转正反角样件验证左/右目标 `rOffset` 符号，再冻结为现场协议。
6. 分别验证四种使能组合，确认离线检测不触发相机，生产 TCP 在禁用模式下返回约定错误码。
