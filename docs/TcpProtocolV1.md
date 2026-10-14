# TCP 通讯协议 v1

## 1. 传输

- 视觉工作站是 TCP Server，默认监听 `0.0.0.0:9100`。
- 同一时间只有一个活动上位机连接。
- 每帧是 `4-byte unsigned big-endian length + UTF-8 JSON`。长度只包含 JSON 字节数。
- 默认最大帧长 1 MiB。长度为 0 或超限时断开连接并记录协议错误。
- 数字使用 JSON number，小数点固定为 `.`；时间使用 UTC ISO-8601。

## 2. 包络

```json
{
  "version": "1.0",
  "messageId": "6f26388d0d214fbd9c8a37a6dd434270",
  "correlationId": "",
  "type": "system.hello",
  "timestampUtc": "2026-10-12T04:00:00.0000000Z",
  "data": {}
}
```

- `messageId` 由发送方生成，在一次连接内必须唯一。
- 回复必须把请求的 `messageId` 写入 `correlationId`。
- `version`、`messageId` 和 `type` 必填；`correlationId` 在非回复消息中可为 `null` 或空字符串。
- 线上类型名统一使用文档中的小写形式；当前接收实现容忍大小写差异。
- 重复 `messageId` 不会再次执行业务，当前实现返回 `system.error / DUPLICATE_MESSAGE`。

## 3. 连接和心跳

### Host -> Vision: `system.hello`

上位机可在建连后发送。`data` 建议字段：

```json
{
  "role": "vision|host",
  "station": "vision-01",
  "capabilities": ["calibration-9x5", "teaching-left-right", "pcb-inspection"]
}
```

### `system.heartbeat`

接收方回复 `system.ack`，`correlationId` 对应心跳 `messageId`。心跳不会触发任何拍照或运动。
当前视觉端对 `system.hello` 也回复 `system.ack`，`data` 包含 `role="vision"`、`ready` 和 `protocolVersion="1.0"`。`ready` 当前表示相机已连接；相机停用、未连接或连接异常时为 `false`，不代表本地图检测不可用。

## 4. 标定流程

### Vision -> Host: `calibration.move.request`

```json
{
  "pointNumber": 1,
  "kind": "translation",
  "coordinateMode": "calibration-origin-relative",
  "offsetX": -10.0,
  "offsetY": -10.0,
  "offsetR": 0.0,
  "sharedOrigin": false,
  "replyType": "motion.arrived"
}
```

`pointNumber` 为 1..14。1..9 是平移点，10..14 是旋转点。偏移永远基于标定会话原点，不允许按上一点累加。
点 5 和点 12 的 `sharedOrigin=true`，两者都是各自点组的零偏移，并共用同一标定原点定义。

### Host -> Vision: `motion.arrived`

```json
{
  "version": "1.0",
  "messageId": "host-reply-id",
  "correlationId": "move-request-message-id",
  "type": "motion.arrived",
  "timestampUtc": "2026-10-12T04:00:01.0000000Z",
  "data": {
    "pointNumber": 1,
    "x": 102.345,
    "y": 55.120,
    "r": -10.0
  }
}
```

`x/y/r` 是停稳后的实际绝对机械坐标，不是目标坐标。只有收到该消息后视觉才会软触发相机。

### Vision -> Host: `calibration.point.result`

```json
{
  "pointNumber": 1,
  "success": true,
  "status": "Completed",
  "machineX": 102.345,
  "machineY": 55.120,
  "machineR": -10.0,
  "imageRow": 1012.44,
  "imageColumn": 812.31,
  "imageAngle": 0.18,
  "score": 0.91,
  "message": "Captured"
}
```

`correlationId` 等于本点 `calibration.move.request.messageId`。等待运动超时时，视觉端回复 `success=false`、`status="Failed"`，并在 `message` 中带 `MOTION_TIMEOUT`。

### Vision -> Host: `calibration.completed`

包含 `success/errorCode/message`、XY 解析度和像素残差摘要。完整矩阵保存在视觉 Job，上位机不应在未校验方向的情况下自行重建矩阵。
该消息由本地“计算并保存”或自动标定完成触发，当前不对应一条上位机请求，因此 `correlationId` 可为 `null`。

## 5. 生产检测

### Host -> Vision: `inspection.trigger`

```json
{
  "currentR": 12.5,
  "expectedCount": 6,
  "archiveImage": false
}
```

`currentR` 必填且必须是有限 JSON number；缺失或非法时返回 `system.error / PROTOCOL_INVALID`。`expectedCount` 可选，缺失或无法转换时使用工作站配置值。`archiveImage` 可选，覆盖工作站默认归档开关。

`inspection.trigger` 是生产实时拍照指令，只有相机使能与标定使能同时打开时才允许执行。收到触发后，视觉串行执行一次软触发、多模板定位、逐板 ROI/Blob 检测和左右坐标映射。

- 相机使能关闭：返回 `system.error / CAMERA_DISABLED`。
- 相机使能打开但标定使能关闭：返回 `system.error / CALIBRATION_DISABLED`。
- 两者都关闭：优先返回 `CAMERA_DISABLED`。

上述错误的 `retryable=false`。上位机应等待操作员恢复使能，不能自动重试。UI 中“导入图片/检测当前图”是独立的本地调试流程，TCP 触发永远不会读取或复用操作员导入的图像。

### Vision -> Host: `inspection.result`

```json
{
  "executed": true,
  "success": true,
  "pass": false,
  "errorCode": "MANUAL_MARK_FOUND",
  "message": "Manual white mark detected.",
  "expectedCount": 6,
  "actualCount": 6,
  "hasManualMarks": true,
  "currentR": 12.5,
  "elapsedMilliseconds": 83.7,
  "matchElapsedMilliseconds": 55.2,
  "blobElapsedMilliseconds": 21.4,
  "items": [
    {
      "boardIndex": 1,
      "row": 430.2,
      "column": 812.6,
      "angle": -1.8,
      "score": 0.94,
      "hasManualMark": true,
      "targets": [
        { "side": "Left", "x": 120.1, "y": 30.2, "rOffset": -0.6, "targetR": 11.9 },
        { "side": "Right", "x": 142.3, "y": 30.0, "rOffset": -0.6, "targetR": 11.9 }
      ]
    }
  ]
}
```

`success` 表示流程执行成功且板数正确；`pass` 是工艺判定。检出白色人工标记时 `success=true, pass=false`。板数不等时 `success=false, pass=false, errorCode=BOARD_COUNT_MISMATCH`。
`elapsedMilliseconds` 是检测算法总 CT；`matchElapsedMilliseconds` 和 `blobElapsedMilliseconds` 分别是模板定位与 Blob 分析耗时，便于现场区分性能瓶颈。
`inspection.result.correlationId` 等于对应的 `inspection.trigger.messageId`。
`boardIndex` 从 1 开始，按板件在图像中的空间行优先顺序编号：先从上到下分行，同一行内再从左到右。编号不依赖模板匹配分数，上位机不得按 `score` 重新排序。

## 6. 错误

### `system.error`

```json
{
  "code": "BUSY",
  "message": "Another vision workflow is already running.",
  "retryable": true
}
```

系统错误使用对应请求的 `correlationId`。上位机只对 `retryable=true` 的错误自动重试，并且每次重试使用新 `messageId`。帧长、UTF-8 或 JSON 包络无效时，传输层直接断开连接，不保证能发出 `system.error`。

### `system.error` 错误码

| 错误码 | 含义 |
| --- | --- |
| `PROTOCOL_INVALID` | 业务字段无效，当前用于 `inspection.trigger.currentR` |
| `CAMERA_DISABLED` | 相机使能关闭，生产拍照不可用；`retryable=false` |
| `CALIBRATION_DISABLED` | 标定与坐标换算关闭，生产目标坐标不可用；`retryable=false` |
| `BUSY` | 正在标定/检测，不能并发拍照 |
| `DUPLICATE_MESSAGE` | 当前连接内的 `messageId` 重复 |
| `MESSAGE_TYPE_UNSUPPORTED` | 未支持的 `type` |
| `INTERNAL_ERROR` | 处理请求时发生未分类异常 |

### `inspection.result.errorCode`

| 错误码 | 含义 |
| --- | --- |
| `NONE` | 检测完成且无人工标记 |
| `MANUAL_MARK_FOUND` | 板数正确，但检出白色人工标记 |
| `BOARD_COUNT_MISMATCH` | 定位数与期望数不等 |
| `POSITIONING_TEMPLATE_MISSING` | 定位模板未配置 |
| `CALIBRATION_MISSING` | XY 或旋转标定结果不可用 |
| `TEACHING_MISSING` | 左或右示教数据不完整 |
| `DETECTION_PROFILE_INVALID` | 检测 ROI 或参数无效 |
| `EXPECTED_COUNT_INVALID` | 期望板数不大于 0 |
| `POSITIONING_FAILED` | PCB 模板定位失败 |
| `IMAGE_MISSING` | 未获得检测图像 |
| `INSPECTION_EXCEPTION` | 检测算法内部异常 |
