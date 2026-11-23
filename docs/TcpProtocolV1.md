# TCP 通讯协议 v1.1

> 完整业务流程与联调约定见 [上位机通讯交互协议与指令规范.md](上位机通讯交互协议与指令规范.md)。本文件为协议字段与错误码速查规范（已按 ZM910A 双 R 轴上位机补充需求升级至 `v1.1`）。

## 1. 传输、超时与数值约定

- 视觉工作站是 TCP Server，默认监听 `0.0.0.0:9100`，同一时间仅允许一个活动上位机连接。
- 每帧格式：`4-byte unsigned big-endian length + UTF-8 JSON`（长度仅含 JSON 字节数）。
- 默认最大帧长 `1 MiB`。长度为 `0`、超限、非合法 UTF-8 或非法 JSON 时直接断开连接并记录协议错误。
- 数字使用标准 JSON number（小数点固定为 `.`），禁止 `NaN`/`Infinity`；时间使用 UTC ISO-8601。
- **数值精度**：机械坐标（mm）保留 3 位小数，机械角度（deg）保留 3 位小数。
- **R 轴角度坐标系**：输出 `rOffset`、`targetR`、`pickR`、`blindPlaceR` 不做 `[-180, 180]` 或 `[0, 360]` 归一化，保持与输入 `currentRLeft`/`currentRRight` 同一连续坐标系，超行程由上位机判定。
- **错误码命名**：全协议所有错误码统一为全大写下划线（`UPPER_SNAKE_CASE`），无错误统一为 `"NONE"`。
- **时间参数**：
  - 心跳周期：`2000 ms`（标定等待运动期间同样须保持发送）；
  - 断线判定：任一方连续 `6000 ms` 未收到任何有效报文即判断线并重连；
  - `system.ack` 应答超时：`1000 ms`；
  - `inspection.result` 应答超时：`3000 ms`（视觉典型 CT `80~250 ms`，正常最大 `≤ 1500 ms`，生产采图超时收紧至 `≤ 2000 ms`）；
  - `teaching.result` 应答超时：`3000 ms`（示教采图超时收紧至 `≤ 2000 ms`）；
  - `calibration.started.ack` 应答超时：`3000 ms`；
  - 单点标定运动超时：默认 `30000 ms`（可配置）。

## 2. 包络

```json
{
  "version": "1.1",
  "messageId": "6f26388d0d214fbd9c8a37a6dd434270",
  "correlationId": null,
  "type": "system.hello",
  "timestampUtc": "2026-11-23T08:00:00.0000000Z",
  "data": {}
}
```

- `messageId` 由发送方生成，在同一次 TCP 连接内必须唯一；重复 `messageId` 不执行业务，返回 `system.error / DUPLICATE_MESSAGE`。
- 所有应答和结果报文必须把请求 `messageId` 写入 `correlationId`；主动发起的请求或推送为 `null` 或 `""`。
- `version`、`messageId` 和 `type` 必填；`version` 在 v1.1 为 `"1.1"`（视觉端兼容接收 `"1.0"`）。
- `type` 统一小写点分格式。

## 3. 连接、心跳与状态推送

### Host -> Vision: `system.hello`

```json
{
  "role": "host",
  "station": "ZM910A-01",
  "capabilities": ["motion-xy-r1r2", "calibration-session-v1.1", "dual-r-inspection"]
}
```

### Host -> Vision: `system.heartbeat`

上位机每 `2 s` 发送一次，`data` 为 `{}`。视觉在 `1000 ms` 内回复 `system.ack`（`correlationId` 等于请求 `messageId`），不触发拍照或运动，不受工作流互斥锁阻塞。

### Vision -> Host: `system.ack` / `system.status`

`system.ack` 用于应答 `system.hello` 和 `system.heartbeat`；`system.status`（`correlationId=null`）由视觉在 `cameraEnabled`、`cameraConnected`、`calibrationEnabled`、`calibrationReady`、`inspectionEnabled` 任一状态变化时主动推送（上位机无需应答）。两者 `data` 结构一致：

```json
{
  "role": "vision",
  "ready": true,
  "cameraEnabled": true,
  "cameraConnected": true,
  "calibrationEnabled": true,
  "calibrationReady": true,
  "inspectionEnabled": true,
  "protocolVersion": "1.1"
}
```

`calibrationReady` 为 `true` 表示左吸嘴（R1）标定、右吸嘴（R2）标定、左示教与右示教均已就绪。

## 4. 标定流程（双吸嘴独立 9 + 5 会话）

左吸嘴（`nozzle="Left"`, `axisR="R1"`）与右吸嘴（`nozzle="Right"`, `axisR="R2"`）分别执行独立的 14 点 9+5 标定并分别保存结果。任何异常中止或失败均**不得覆盖旧标定结果**。

### Vision -> Host: `calibration.started`

```json
{
  "sessionId": "cal-20261123-0001",
  "nozzle": "Left",
  "axisR": "R1",
  "totalPoints": 14,
  "stepX": 10.000,
  "stepY": 10.000,
  "stepR": 5.000
}
```

### Host -> Vision: `calibration.started.ack`

`correlationId` 等于 `calibration.started.messageId`。`accepted=true` 时上位机锁存此时实测绝对原点 `(originX, originY, originR)`；`accepted=false` 时视觉不下发 `move.request` 并在界面显示 `reason`。

```json
{
  "sessionId": "cal-20261123-0001",
  "accepted": true,
  "originX": 650.120,
  "originY": 210.450,
  "originR": 0.000,
  "reason": ""
}
```

### Vision -> Host: `calibration.move.request`

```json
{
  "sessionId": "cal-20261123-0001",
  "axisR": "R1",
  "pointNumber": 1,
  "kind": "translation",
  "coordinateMode": "calibration-origin-relative",
  "offsetX": -10.000,
  "offsetY": -10.000,
  "offsetR": 0.000,
  "sharedOrigin": false,
  "replyType": "motion.arrived"
}
```

`pointNumber` 为 `1..14`（1..9 平移，10..14 旋转；点 5 与点 12 的 `sharedOrigin=true`）。目标绝对位统一按 `started.ack` 锁存的 `(originX + offsetX, originY + offsetY, originR + offsetR)` 计算，禁止按上一点累加。

### Host -> Vision: `motion.arrived`

`correlationId` 等于 `calibration.move.request.messageId`。`x/y/r` 是对应 `axisR` 停稳后的实测绝对机械坐标。

```json
{
  "sessionId": "cal-20261123-0001",
  "pointNumber": 1,
  "x": 640.120,
  "y": 200.450,
  "r": 0.000
}
```

### Host -> Vision: `motion.failed`

作为 `calibration.move.request` 的异常应答（`correlationId` 等于该请求的 `messageId`）。视觉收到后立即终止会话，不下发后续点位，发送 `calibration.completed(success=false, errorCode="MOTION_FAILED")` 并保留旧标定结果。

```json
{
  "sessionId": "cal-20261123-0001",
  "pointNumber": 7,
  "code": "SOFT_LIMIT",
  "message": "Target X 905.000 exceeds soft limit 900.000"
}
```

`code` 标准取值：`SOFT_LIMIT`、`EMERGENCY_STOP`、`SERVO_ALARM`、`NOT_IN_CALIBRATION_MODE`、`SESSION_MISMATCH`、`SETTLE_TIMEOUT`。

### Host -> Vision: `calibration.cancel`

上位机主动中止当前标定（`correlationId=null`）。视觉立即停止下发，保留旧标定结果，回复 `calibration.completed(success=false, errorCode="CANCELLED")`（`correlationId` 等于本消息 `messageId`）。视觉界面点击“停止标定”时同样主动推送 `calibration.completed(success=false, errorCode="CANCELLED", correlationId=null)`。

```json
{
  "sessionId": "cal-20261123-0001",
  "reason": "Operator stop"
}
```

### Vision -> Host: `calibration.point.result`

`correlationId` 等于本点 `calibration.move.request.messageId`。

```json
{
  "sessionId": "cal-20261123-0001",
  "axisR": "R1",
  "pointNumber": 1,
  "success": true,
  "status": "Completed",
  "machineX": 640.120,
  "machineY": 200.450,
  "machineR": 0.000,
  "imageRow": 1012.44,
  "imageColumn": 812.31,
  "imageAngle": 0.18,
  "score": 0.910,
  "message": "Captured"
}
```

### Vision -> Host: `calibration.completed`

```json
{
  "sessionId": "cal-20261123-0001",
  "nozzle": "Left",
  "axisR": "R1",
  "success": true,
  "errorCode": "NONE",
  "message": "Calibration succeeded.",
  "resolutionX": 0.015200,
  "resolutionY": 0.015200,
  "rmsResidualPixels": 0.180
}
```

`errorCode` 统一全大写下划线：`NONE`、`MOTION_FAILED`、`CANCELLED`、`MOTION_TIMEOUT`、`SESSION_REJECTED`、`TEMPLATE_MISSING`、`CAMERA_CAPTURE_FAILED`、`MATCH_FAILED`、`CALIBRATION_SOLVE_FAILED`、`DISCONNECTED`。

## 5. 生产检测（支持双 R 轴、空料与缺板返回）

### Host -> Vision: `inspection.trigger`

```json
{
  "currentRLeft": 12.500,
  "currentRRight": -3.200,
  "expectedCount": 6,
  "archiveImage": false
}
```

- `currentRLeft`（R1）与 `currentRRight`（R2）为拍照时刻左右吸嘴当前角度（deg，3 位小数，有限数值）。启用标定且标定/示教就绪时两者同时必填；保留 `currentR` 仅作 v1.0 兼容（双值均存在时以双值为准；若仅传双值之一或含非有限数值，返回 `system.error / PROTOCOL_INVALID`）。
- 纯视觉检测模式（未启用标定或标定/示教未就绪）下可省略角度（默认 `0.000`），正常执行定位与瑕疵检测：空料返回 `NO_MATERIAL`（`pass=true`）；有板时返回 `COORDINATE_NOT_MAPPED`（`executed=true, success=false, pass=false, coordinateMapped=false, items[].valid=false, targets=[]`）。
- 未进入检测流程的拒绝条件统一返回 `system.error`：`INSPECTION_DISABLED`（`retryable=false`）、`CAMERA_DISABLED`（`retryable=false`）、`CAMERA_NOT_CONNECTED`（`retryable=true`）、`BUSY`（`retryable=true`）、`PROTOCOL_INVALID`（`retryable=false`）。

### Vision -> Host: `inspection.result`

```json
{
  "executed": true,
  "success": true,
  "pass": false,
  "coordinateMapped": true,
  "errorCode": "MANUAL_MARK_FOUND",
  "message": "Manual white mark detected.",
  "expectedCount": 6,
  "actualCount": 6,
  "hasManualMarks": true,
  "currentRLeft": 12.500,
  "currentRRight": -3.200,
  "currentR": 12.500,
  "elapsedMilliseconds": 83.7,
  "matchElapsedMilliseconds": 55.2,
  "blobElapsedMilliseconds": 21.4,
  "items": [
    {
      "boardIndex": 1,
      "valid": true,
      "row": 430.20,
      "column": 812.60,
      "angle": -1.80,
      "score": 0.940,
      "hasManualMark": true,
      "targets": [
        { "side": "Left",  "axis": "R1", "x": 120.100, "y": 30.200, "rOffset": -0.600, "targetR": 11.900, "pickR": 11.900, "blindPlaceR": 0.000 },
        { "side": "Right", "axis": "R2", "x": 142.300, "y": 30.000, "rOffset": -0.600, "targetR": -3.800, "pickR": -3.800, "blindPlaceR": 0.000 }
      ]
    }
  ]
}
```

- **双 R 轴独立映射**：`Left`（`axis="R1"`）相对 `currentRLeft` 及左标定/左示教计算（`Left.targetR = currentRLeft + Left.rOffset`）；`Right`（`axis="R2"`）相对 `currentRRight` 及右标定/右示教计算（`Right.targetR = currentRRight + Right.rOffset`）。
- **空料（`NO_MATERIAL`）**：模板匹配引擎正常执行且匹配结果为 `0` 块板时，返回 `executed=true, success=true, pass=true, errorCode="NO_MATERIAL", actualCount=0, hasManualMarks=false, items=[]`；只要定位到 `≥ 1` 块板即不得返回 `NO_MATERIAL`。
- **未映射 / 映射异常与 5 级优先级**：有板（`actualCount ≥ 1`）时按 `BOARD_COUNT_MISMATCH` > `COORDINATE_NOT_MAPPED` > `COORDINATE_MAPPING_FAILED` > `MANUAL_MARK_FOUND` > `NONE` 判定；**只要任一块板 `valid=false`（或 `coordinateMapped=false`），整帧 `pass` 必为 `false`**。即使单板 `valid=false`（或未做坐标映射），该板 `hasManualMark` 与像素位姿仍独立生成且**完全可信**。
- **数量不一致（`BOARD_COUNT_MISMATCH`）**：`actualCount >= 1 && actualCount != expectedCount` 时，`executed=true, success=false, pass=false, errorCode="BOARD_COUNT_MISMATCH"`，且 `items` 仍完整返回所有已成功定位的 `1..actualCount` 块板（含完整坐标、`targets`、`hasManualMark`）；若个别板无法给出可靠坐标，仅将该单板置 `valid=false`，不得整体置空 `items`。
- `boardIndex` 从 `1` 开始，严格按图像空间“从上到下分行、同行内从左到右”连续编号，上位机不得按 `score` 重排。

## 5.1 远程示教（`teaching.submit` / `teaching.result`）

### Host -> Vision: `teaching.submit`

上位机将板放到示教位并将吸嘴移开后发送（`correlationId=null`）：

```json
{
  "nozzle": "Left",
  "axisR": "R1",
  "x": 650.120,
  "y": 210.450,
  "r": 0.000
}
```

### Vision -> Host: `teaching.result`

`correlationId` 等于对应 `teaching.submit.messageId`。失败时完整保留旧示教数据；成功后若 `calibrationReady` 变化，随后推送 `system.status`。

```json
{
  "nozzle": "Left",
  "axisR": "R1",
  "success": true,
  "errorCode": "NONE",
  "message": "Teaching saved.",
  "machineX": 650.120,
  "machineY": 210.450,
  "machineR": 0.000,
  "imageRow": 1024.15,
  "imageColumn": 1280.42,
  "imageAngle": 0.12,
  "score": 0.952
}
```

`teaching.result.errorCode`：`NONE`、`POSITIONING_TEMPLATE_MISSING`、`CAMERA_CAPTURE_FAILED`、`MATCH_FAILED`、`TEACHING_SAVE_FAILED`、`INTERNAL_ERROR`（前置条件不满足走 `system.error`：`CALIBRATION_DISABLED`、`CAMERA_DISABLED`、`CAMERA_NOT_CONNECTED`、`BUSY`、`PROTOCOL_INVALID`）。

## 6. 错误返回边界与错误码速查

- **未进入检测流程**（协议校验、去重、未知类型、使能关闭、相机未连、忙、通信框架异常）→ **`system.error`**。
- **已进入检测流程**（工艺配置缺失、空料、数量不符、人工标记、定位/Blob/算法异常）→ **`inspection.result`**。

### `system.error`

```json
{
  "code": "BUSY",
  "message": "Another vision workflow is already running.",
  "retryable": true
}
```

| 错误码 | `retryable` | 含义 |
| --- | --- | --- |
| `PROTOCOL_INVALID` | `false` | 业务字段缺失或非法（如单侧 R 角缺失、非有限数值等） |
| `INSPECTION_DISABLED` | `false` | 检测使能关闭，生产检测不可用 |
| `CAMERA_DISABLED` | `false` | 相机使能关闭，生产拍照不可用 |
| `CAMERA_NOT_CONNECTED` | `true` | 真实相机未连接，无法拍照 |
| `CALIBRATION_DISABLED` | `false` | 标定功能已关闭（仅用于标定相关请求） |
| `BUSY` | `true` | 正在标定/检测/示教，不能并发执行 |
| `DUPLICATE_MESSAGE` | `false` | 当前连接内 `messageId` 重复 |
| `MESSAGE_TYPE_UNSUPPORTED` | `false` | 未支持的 `type` |
| `INTERNAL_ERROR` | `true` | 处理请求时发生未分类通信/框架异常 |

### `inspection.result.errorCode`

| 错误码 | `executed` | `success` | `pass` | 含义 |
| --- | --- | --- | --- | --- |
| `NONE` | `true` | `true` | `true` | 检测完成，板数一致、全部 `valid=true` 且无人工标记 |
| `NO_MATERIAL` | `true` | `true` | `true` | 正常空料（定位结果为 0 块板，`items=[]`） |
| `MANUAL_MARK_FOUND` | `true` | `true` | `false` | 板数一致、坐标全部有效，但检出人工坏板标记 |
| `COORDINATE_MAPPING_FAILED` | `true` | `false` | `false` | 板数一致且已执行映射，但至少 1 块板 `valid=false` |
| `COORDINATE_NOT_MAPPED` | `true` | `false` | `false` | 有板（`actualCount ≥ 1`）但未执行坐标映射（`coordinateMapped=false`） |
| `BOARD_COUNT_MISMATCH` | `true` | `false` | `false` | 实际板数（≥1）与期望数不等；`items` 仍返回全部已定位板 |
| `POSITIONING_TEMPLATE_MISSING` | `false` | `false` | `false` | 定位模板未配置 |
| `CALIBRATION_MISSING` | `false` | `false` | `false` | 左或右吸嘴 9+5 标定结果不可用 |
| `TEACHING_MISSING` | `false` | `false` | `false` | 左或右示教数据不完整 |
| `CURRENT_R_INVALID` | `false` | `false` | `false` | 传入 R 轴角度非有限数值（内核兜底） |
| `DETECTION_PROFILE_INVALID` | `false` | `false` | `false` | 检测参数配置无效 |
| `DETECTION_ROI_MISSING` | `false` | `false` | `false` | 定位模板未配置模板 ROI 或检测 ROI |
| `EXPECTED_COUNT_INVALID` | `false` | `false` | `false` | 期望板数 `≤ 0` |
| `IMAGE_MISSING` | `false` | `false` | `false` | 未获得检测图像 |
| `POSITIONING_FAILED` | `true` | `false` | `false` | PCB 模板定位引擎失败或输出非法姿态 |
| `BLOB_ANALYSIS_FAILED` | `true` | `false` | `false` | Blob 分析未为每块板返回结果 |
| `INSPECTION_EXCEPTION` | `true` | `false` | `false` | 检测算法内部异常 |
