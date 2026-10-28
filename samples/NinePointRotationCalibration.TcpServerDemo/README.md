# TCP 服务端交互 Demo

该 demo 直接复用 Station 项目的 `TcpVisionServer` 和 `ProtocolJsonCodec`，用于让上位机 TCP 客户端连接并检查协议交互。

默认监听 `0.0.0.0:9100`：

```powershell
dotnet build samples\NinePointRotationCalibration.TcpServerDemo\NinePointRotationCalibration.TcpServerDemo.csproj --no-restore /p:BuildProjectReferences=false
.\samples\NinePointRotationCalibration.TcpServerDemo\bin\Debug\NinePointRotationCalibration.TcpServerDemo.exe
```

也可以指定地址和端口：

```powershell
.\samples\NinePointRotationCalibration.TcpServerDemo\bin\Debug\NinePointRotationCalibration.TcpServerDemo.exe --bind 127.0.0.1 --port 9100
```

客户端连入后，demo 默认自动应答 `system.hello`、`system.heartbeat`、`inspection.trigger` 和 `motion.arrived`。控制台命令：

- `hello` / `heartbeat`：主动下发系统消息。
- `move`：下发一个标定运动请求；收到客户端 `motion.arrived` 后回传模拟 `calibration.point.result`。
- `inspect [R]`：下发生产检测触发。
- `completed` / `error [CODE]`：下发标定完成或系统错误消息。
- `manual` / `auto`：关闭或开启自动应答。
- `test`：运行项目已有的编解码、拆包/粘包、断线重连自测。
- `quit`：停止 demo。

`--manual` 启动时关闭自动应答，只打印客户端发来的协议包，便于逐条检查客户端行为。
