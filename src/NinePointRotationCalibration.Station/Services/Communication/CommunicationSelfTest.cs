using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using NinePointRotationCalibration.Halcon;
using NinePointRotationCalibration.Station.Devices;
using NinePointRotationCalibration.Station.Models;
using NinePointRotationCalibration.Station.Services.Calibration;
using NinePointRotationCalibration.Station.Services.Inspection;
using NinePointRotationCalibration.Station.Services.Teaching;

namespace NinePointRotationCalibration.Station.Services.Communication
{
    /// <summary>TCP 协议 v1.1 编解码、长度分帧、连接隔离、心跳断线、双 R 轴映射与标定会话中止保护自检工具。</summary>
    public static class CommunicationSelfTest
    {
        /// <summary>同步执行全部通信与协议 v1.1 链路自检。</summary>
        public static void Run()
        {
            RunAsync(CancellationToken.None).GetAwaiter().GetResult();
        }

        /// <summary>依次验证协议编解码、分帧、连接隔离、心跳断线、双 R 映射、标定会话与 StationRuntime v1.1 端到端交互。</summary>
        public static async Task RunAsync(CancellationToken cancellationToken)
        {
            ProtocolJsonCodec codec = new ProtocolJsonCodec();
            VerifyCodec(codec);
            await VerifyLengthFramingAsync(codec, cancellationToken).ConfigureAwait(false);
            await VerifyConnectionScopedReconnectAsync(codec, cancellationToken).ConfigureAwait(false);
            await VerifySmoothClientReplacementAsync(codec, cancellationToken).ConfigureAwait(false);
            await VerifyHeartbeatInactivityTimeoutAsync(codec, cancellationToken).ConfigureAwait(false);
            VerifyDualRTargetMapping();
            VerifyInspectionSemantics();
            await VerifyCalibrationSessionsAndPreservationAsync(cancellationToken).ConfigureAwait(false);
            await VerifyLowScoreCalibrationAbortAsync(cancellationToken).ConfigureAwait(false);
            await VerifyTwoStepTeachingAsync(cancellationToken).ConfigureAwait(false);
            await VerifyRuntimeProtocolV11EndToEndAsync(codec, cancellationToken).ConfigureAwait(false);
            await VerifyTeachingServiceAndProtocolAsync(codec, cancellationToken).ConfigureAwait(false);
        }

        private static void VerifyCodec(ProtocolJsonCodec codec)
        {
            // 验证 v1.1 版本号、驼峰字段输出、数值精度舍入、大小写不敏感字典访问及非法自定义 CLR 对象拦截
            ProtocolEnvelope outgoing = CreateMessage("self-test-roundtrip", 17);
            Assert(string.Equals(outgoing.Version, "1.1", StringComparison.Ordinal), "Default protocol version must be 1.1.");
            Assert(ProtocolNumber.Round3(12.3456) == 12.346, "Round3 must round to 3 decimal places.");
            Assert(ProtocolNumber.Round3(-0.0001) == 0.0, "Round3 must normalize -0.0 to 0.0.");

            outgoing.Data["items"] = new List<object>
            {
                new Dictionary<string, object>
                {
                    { "boardIndex", 1 },
                    { "valid", true },
                    { "pass", true }
                },
                null
            };
            outgoing.Data["labels"] = new[] { "left", "right" };

            string json = codec.SerializeToString(outgoing);
            Assert(json.Contains("\"version\":\"1.1\""), "Wire JSON must contain version 1.1.");
            Assert(json.Contains("\"messageId\""), "Wire JSON must contain camelCase messageId.");
            Assert(json.Contains("\"data\""), "Wire JSON must contain camelCase data.");
            Assert(!json.Contains("\"Version\""), "Wire JSON must not expose model property casing.");
            Assert(!json.Contains("__type"), "Wire JSON must not contain serializer type metadata.");

            ProtocolEnvelope incoming = codec.Deserialize(json);
            Assert(incoming.MessageId == outgoing.MessageId, "Codec round-trip changed messageId.");
            Assert(incoming.Type == outgoing.Type, "Codec round-trip changed message type.");
            Assert(Convert.ToInt32(incoming.Data["sequence"]) == 17, "Codec round-trip changed numeric data.");
            Assert(incoming.Data.ContainsKey("SEQUENCE"), "Top-level data lookup must remain case-insensitive.");

            outgoing.Data["unsupported"] = new UnsupportedPayload();
            bool rejected = false;
            try
            {
                codec.Serialize(outgoing);
            }
            catch (ProtocolSerializationException exception)
            {
                rejected = exception.Message.Contains(typeof(UnsupportedPayload).FullName);
            }

            Assert(rejected, "Codec must explicitly reject custom CLR payload objects.");
        }

        private static async Task VerifyLengthFramingAsync(
            ProtocolJsonCodec codec,
            CancellationToken cancellationToken)
        {
            // 测试 TCP 分片拆包写入与多帧合并粘包写入下的 4 字节大端长度分帧解析
            int port = ReserveLoopbackPort();
            StationOptions options = new StationOptions
            {
                TcpBindAddress = "127.0.0.1",
                TcpPort = port,
                TcpMaxMessageBytes = 64 * 1024
            };

            using (CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            using (TcpVisionServer server = new TcpVisionServer(options, codec))
            using (TcpClient client = new TcpClient(AddressFamily.InterNetwork))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                TaskCompletionSource<bool> connected = NewCompletionSource<bool>();
                TaskCompletionSource<bool> receivedAll = NewCompletionSource<bool>();
                object receivedSync = new object();
                List<int> receivedSequences = new List<int>();

                server.Connected += delegate { connected.TrySetResult(true); };
                server.MessageReceived += delegate(object sender, ProtocolMessageEventArgs args)
                {
                    int sequence = Convert.ToInt32(args.Message.Data["sequence"]);
                    lock (receivedSync)
                    {
                        receivedSequences.Add(sequence);
                        if (receivedSequences.Count == 3) receivedAll.TrySetResult(true);
                    }
                };

                await server.StartAsync(timeout.Token).ConfigureAwait(false);
                await client.ConnectAsync(IPAddress.Loopback, port).ConfigureAwait(false);
                await AwaitAsync(connected.Task, timeout.Token).ConfigureAwait(false);

                NetworkStream stream = client.GetStream();
                byte[] fragmented = CreateFrame(codec.Serialize(CreateMessage("fragmented", 1)));
                await stream.WriteAsync(fragmented, 0, 2, timeout.Token).ConfigureAwait(false);
                await stream.FlushAsync(timeout.Token).ConfigureAwait(false);
                await Task.Delay(20, timeout.Token).ConfigureAwait(false);
                await stream.WriteAsync(fragmented, 2, 5, timeout.Token).ConfigureAwait(false);
                await stream.FlushAsync(timeout.Token).ConfigureAwait(false);
                await Task.Delay(20, timeout.Token).ConfigureAwait(false);
                await stream.WriteAsync(fragmented, 7, fragmented.Length - 7, timeout.Token).ConfigureAwait(false);

                byte[] second = CreateFrame(codec.Serialize(CreateMessage("coalesced-2", 2)));
                byte[] third = CreateFrame(codec.Serialize(CreateMessage("coalesced-3", 3)));
                byte[] coalesced = new byte[second.Length + third.Length];
                Buffer.BlockCopy(second, 0, coalesced, 0, second.Length);
                Buffer.BlockCopy(third, 0, coalesced, second.Length, third.Length);
                await stream.WriteAsync(coalesced, 0, coalesced.Length, timeout.Token).ConfigureAwait(false);
                await stream.FlushAsync(timeout.Token).ConfigureAwait(false);
                await AwaitAsync(receivedAll.Task, timeout.Token).ConfigureAwait(false);

                lock (receivedSync)
                {
                    Assert(
                        receivedSequences.Count == 3
                        && receivedSequences[0] == 1
                        && receivedSequences[1] == 2
                        && receivedSequences[2] == 3,
                        "TCP server did not preserve fragmented/coalesced frame order.");
                }

                ProtocolEnvelope reply = CreateMessage("server-reply", 99);
                await server.SendAsync(reply, timeout.Token).ConfigureAwait(false);
                ProtocolEnvelope receivedReply = await ReadEnvelopeAsync(stream, codec, options.TcpMaxMessageBytes, timeout.Token).ConfigureAwait(false);
                Assert(receivedReply.MessageId == reply.MessageId, "Server response frame changed its payload.");

                await server.StopAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }

        private static async Task VerifyConnectionScopedReconnectAsync(
            ProtocolJsonCodec codec,
            CancellationToken cancellationToken)
        {
            // 测试断线重连后按 ConnectionId 路由发送：发往已断开旧连接的消息必须被拒绝且不能串发到新连接
            int port = ReserveLoopbackPort();
            StationOptions options = new StationOptions
            {
                TcpBindAddress = "127.0.0.1",
                TcpPort = port,
                TcpMaxMessageBytes = 64 * 1024
            };

            using (CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            using (TcpVisionServer server = new TcpVisionServer(options, codec))
            using (TcpClient oldClient = new TcpClient(AddressFamily.InterNetwork))
            using (TcpClient newClient = new TcpClient(AddressFamily.InterNetwork))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                TaskCompletionSource<string> oldConnected = NewCompletionSource<string>();
                TaskCompletionSource<string> newConnected = NewCompletionSource<string>();
                TaskCompletionSource<string> oldRequestReceived = NewCompletionSource<string>();
                TaskCompletionSource<string> oldDisconnected = NewCompletionSource<string>();
                int connectionCount = 0;

                server.Connected += delegate(object sender, TcpConnectionEventArgs args)
                {
                    if (Interlocked.Increment(ref connectionCount) == 1)
                        oldConnected.TrySetResult(args.ConnectionId);
                    else
                        newConnected.TrySetResult(args.ConnectionId);
                };
                server.Disconnected += delegate(object sender, TcpDisconnectedEventArgs args)
                {
                    oldDisconnected.TrySetResult(args.ConnectionId);
                };
                server.MessageReceived += delegate(object sender, ProtocolMessageEventArgs args)
                {
                    if (string.Equals(args.Message.MessageId, "old-connection-request", StringComparison.Ordinal))
                        oldRequestReceived.TrySetResult(args.ConnectionId);
                };

                await server.StartAsync(timeout.Token).ConfigureAwait(false);
                await oldClient.ConnectAsync(IPAddress.Loopback, port).ConfigureAwait(false);
                string oldConnectionId = await AwaitAsync(oldConnected.Task, timeout.Token).ConfigureAwait(false);
                Assert(!string.IsNullOrWhiteSpace(oldConnectionId), "Accepted clients must have a connection ID.");

                await WriteEnvelopeAsync(oldClient.GetStream(), codec, CreateMessage("old-connection-request", 1), timeout.Token).ConfigureAwait(false);
                string inboundConnectionId = await AwaitAsync(oldRequestReceived.Task, timeout.Token).ConfigureAwait(false);
                Assert(
                    string.Equals(oldConnectionId, inboundConnectionId, StringComparison.Ordinal),
                    "Inbound protocol messages must expose their exact connection ID.");

                oldClient.Close();
                string disconnectedConnectionId = await AwaitAsync(oldDisconnected.Task, timeout.Token).ConfigureAwait(false);
                Assert(
                    string.Equals(oldConnectionId, disconnectedConnectionId, StringComparison.Ordinal),
                    "Disconnect events must retain the closed connection ID.");

                await newClient.ConnectAsync(IPAddress.Loopback, port).ConfigureAwait(false);
                string newConnectionId = await AwaitAsync(newConnected.Task, timeout.Token).ConfigureAwait(false);
                Assert(
                    !string.Equals(oldConnectionId, newConnectionId, StringComparison.Ordinal),
                    "Every accepted client must receive a unique connection ID.");

                ProtocolEnvelope oldResult = CreateMessage("old-connection-result", 2);
                bool rejected = false;
                try
                {
                    await server.SendAsync(oldConnectionId, oldResult, timeout.Token).ConfigureAwait(false);
                }
                catch (InvalidOperationException)
                {
                    rejected = true;
                }
                Assert(rejected, "A response targeted at a closed connection must be rejected.");

                ProtocolEnvelope newResult = CreateMessage("new-connection-result", 3);
                await server.SendAsync(newConnectionId, newResult, timeout.Token).ConfigureAwait(false);

                ProtocolEnvelope delivered = await ReadEnvelopeAsync(newClient.GetStream(), codec, options.TcpMaxMessageBytes, timeout.Token).ConfigureAwait(false);
                Assert(
                    string.Equals(delivered.MessageId, newResult.MessageId, StringComparison.Ordinal),
                    "A result for the old client leaked onto the replacement connection.");

                await server.StopAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }

        private static async Task VerifySmoothClientReplacementAsync(
            ProtocolJsonCodec codec,
            CancellationToken cancellationToken)
        {
            // 验证协议 v1.1 §2.1：旧连接尚未断开时新客户端接入，服务端须平滑关闭旧连接并接管新连接，且禁止跨连接串包
            int port = ReserveLoopbackPort();
            StationOptions options = new StationOptions
            {
                TcpBindAddress = "127.0.0.1",
                TcpPort = port,
                TcpMaxMessageBytes = 64 * 1024
            };

            using (CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            using (TcpVisionServer server = new TcpVisionServer(options, codec))
            using (TcpClient oldClient = new TcpClient(AddressFamily.InterNetwork))
            using (TcpClient newClient = new TcpClient(AddressFamily.InterNetwork))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                TaskCompletionSource<string> oldConnected = NewCompletionSource<string>();
                TaskCompletionSource<string> newConnected = NewCompletionSource<string>();
                TaskCompletionSource<string> oldDisconnected = NewCompletionSource<string>();
                int connectCount = 0;

                server.Connected += delegate(object sender, TcpConnectionEventArgs args)
                {
                    if (Interlocked.Increment(ref connectCount) == 1)
                        oldConnected.TrySetResult(args.ConnectionId);
                    else
                        newConnected.TrySetResult(args.ConnectionId);
                };
                server.Disconnected += delegate(object sender, TcpDisconnectedEventArgs args)
                {
                    oldDisconnected.TrySetResult(args.ConnectionId);
                };

                await server.StartAsync(timeout.Token).ConfigureAwait(false);
                await oldClient.ConnectAsync(IPAddress.Loopback, port).ConfigureAwait(false);
                string oldId = await AwaitAsync(oldConnected.Task, timeout.Token).ConfigureAwait(false);

                // 旧连接仍保持打开，直接建立新连接
                await newClient.ConnectAsync(IPAddress.Loopback, port).ConfigureAwait(false);
                string newId = await AwaitAsync(newConnected.Task, timeout.Token).ConfigureAwait(false);
                string disconnectedId = await AwaitAsync(oldDisconnected.Task, timeout.Token).ConfigureAwait(false);

                Assert(string.Equals(disconnectedId, oldId, StringComparison.Ordinal),
                    "Smooth replacement must close and report disconnect for the previous connection.");
                Assert(!string.Equals(oldId, newId, StringComparison.Ordinal),
                    "Replacement client must receive a distinct connection ID.");
                Assert(string.Equals(server.ActiveConnectionId, newId, StringComparison.Ordinal),
                    "ActiveConnectionId must point to the newly accepted client.");

                // 发往被替换旧连接的消息必须被拒绝，不得串发到新连接
                bool oldSendRejected = false;
                try
                {
                    await server.SendAsync(oldId, CreateMessage("stale-after-replace", 1), timeout.Token).ConfigureAwait(false);
                }
                catch (InvalidOperationException)
                {
                    oldSendRejected = true;
                }
                Assert(oldSendRejected, "SendAsync targeting a replaced connection must be rejected.");

                // 新连接应正常收发
                ProtocolEnvelope freshReply = CreateMessage("fresh-after-replace", 2);
                await server.SendAsync(newId, freshReply, timeout.Token).ConfigureAwait(false);
                ProtocolEnvelope received = await ReadEnvelopeAsync(newClient.GetStream(), codec, options.TcpMaxMessageBytes, timeout.Token).ConfigureAwait(false);
                Assert(string.Equals(received.MessageId, freshReply.MessageId, StringComparison.Ordinal),
                    "Replacement client did not receive its expected message.");

                await server.StopAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }

        private static async Task VerifyHeartbeatInactivityTimeoutAsync(
            ProtocolJsonCodec codec,
            CancellationToken cancellationToken)
        {
            // 验证需求六：连续超过心跳断线判定时间未收到有效报文时，服务端主动判定断线并关闭连接
            int port = ReserveLoopbackPort();
            StationOptions options = new StationOptions
            {
                TcpBindAddress = "127.0.0.1",
                TcpPort = port,
                TcpMaxMessageBytes = 64 * 1024,
                HeartbeatTimeoutMilliseconds = 450
            };

            using (CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            using (TcpVisionServer server = new TcpVisionServer(options, codec))
            using (TcpClient client = new TcpClient(AddressFamily.InterNetwork))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                TaskCompletionSource<bool> connected = NewCompletionSource<bool>();
                TaskCompletionSource<string> disconnected = NewCompletionSource<string>();
                server.Connected += delegate { connected.TrySetResult(true); };
                server.Disconnected += delegate(object sender, TcpDisconnectedEventArgs args)
                {
                    disconnected.TrySetResult(args.Reason ?? string.Empty);
                };

                await server.StartAsync(timeout.Token).ConfigureAwait(false);
                await client.ConnectAsync(IPAddress.Loopback, port).ConfigureAwait(false);
                await AwaitAsync(connected.Task, timeout.Token).ConfigureAwait(false);

                // 发送一次心跳刷新活跃计时，确认未提前断开
                await Task.Delay(200, timeout.Token).ConfigureAwait(false);
                await WriteEnvelopeAsync(client.GetStream(), codec, CreateMessage("hb-keepalive", 1), timeout.Token).ConfigureAwait(false);
                await Task.Delay(200, timeout.Token).ConfigureAwait(false);
                Assert(!disconnected.Task.IsCompleted, "Client was disconnected prematurely while heartbeat was active.");

                // 停止心跳，验证超时后服务端自动断线
                string reason = await AwaitAsync(disconnected.Task, timeout.Token).ConfigureAwait(false);
                Assert(
                    reason.IndexOf("timeout", StringComparison.OrdinalIgnoreCase) >= 0,
                    "Expected heartbeat/inactivity timeout disconnect reason, got: " + reason);

                await server.StopAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }

        private static void VerifyDualRTargetMapping()
        {
            // 验证需求一 & 需求六(8)：左/右吸嘴独立标定与独立 R1/R2 计算，且目标 R 不做 [-180,180] 归一化
            CalibrationResult leftCal = CreateSyntheticCalibration(+1.0, 2.0, 1.0);
            CalibrationResult rightCal = CreateSyntheticCalibration(-1.0, -3.0, 2.0);
            TeachingData teaching = new TeachingData
            {
                Left = new TeachingPose
                {
                    Side = TeachingSide.Left,
                    IsValid = true,
                    ImageRow = 100.0,
                    ImageColumn = 200.0,
                    ImageAngleDegrees = 0.0,
                    MachineX = 120.0,
                    MachineY = 30.0,
                    MachineR = 180.0
                },
                Right = new TeachingPose
                {
                    Side = TeachingSide.Right,
                    IsValid = true,
                    ImageRow = 100.0,
                    ImageColumn = 200.0,
                    ImageAngleDegrees = 0.0,
                    MachineX = 142.0,
                    MachineY = 30.0,
                    MachineR = -180.0
                }
            };
            TemplateMatchResult match = new TemplateMatchResult
            {
                Success = true,
                Anchor = new ImagePoint(100.0, 200.0),
                ModelPose = new ImagePose(100.0, 200.0, 5.0),
                Score = 0.95
            };

            TargetPoseMapper mapper = new TargetPoseMapper();
            List<MappedTargetPose> poses = mapper.Map(leftCal, rightCal, teaching, match, 178.0, -178.0);
            Assert(poses.Count == 2, "Dual-R mapping must return Left and Right poses.");

            MappedTargetPose left = poses.First(p => p.Side == TeachingSide.Left);
            MappedTargetPose right = poses.First(p => p.Side == TeachingSide.Right);
            Assert(string.Equals(left.Axis, "R1", StringComparison.Ordinal), "Left target axis must be R1.");
            Assert(string.Equals(right.Axis, "R2", StringComparison.Ordinal), "Right target axis must be R2.");
            // Left: pickR = 180 + (+1)*5 = 185, currentRLeft = 178 -> rOffset = +7, targetR = 185 (不归一化到 -175)
            Assert(Math.Abs(left.ROffset - 7.0) < 1e-6, "Left rOffset mismatch: " + left.ROffset);
            Assert(Math.Abs(left.TargetR - 185.0) < 1e-6, "Left targetR must stay in continuous frame (185), got " + left.TargetR);
            // Right: pickR = -180 + (-1)*5 = -185, currentRRight = -178 -> rOffset = -7, targetR = -185 (不归一化到 +175)
            Assert(Math.Abs(right.ROffset - (-7.0)) < 1e-6, "Right rOffset mismatch: " + right.ROffset);
            Assert(Math.Abs(right.TargetR - (-185.0)) < 1e-6, "Right targetR must stay in continuous frame (-185), got " + right.TargetR);
        }

        private static void VerifyInspectionSemantics()
        {
            // 验证需求四 & 需求五：NO_MATERIAL 与 POSITIONING_FAILED 区分、BOARD_COUNT_MISMATCH 仍返回全部已定位板及单板 valid=false
            FakeSelfTestTemplateEngine engine = new FakeSelfTestTemplateEngine();
            using (NinePointRotationCalibrationSdk sdk = new NinePointRotationCalibrationSdk(engine))
            using (Bitmap frame = new Bitmap(256, 256, System.Drawing.Imaging.PixelFormat.Format24bppRgb))
            {
                BoardInspectionService service = new BoardInspectionService(sdk);
                CalibrationJob positioningJob = new CalibrationJob
                {
                    Name = "SelfTestPositioning",
                    Template = new TemplateDefinition
                    {
                        Revision = 1,
                        ModelType = TemplateModelType.Shape,
                        ModelData = new byte[] { 1, 2, 3, 4 },
                        ModelHash = "dummy-hash",
                        IsModelDirty = false,
                        ReferenceImageWidth = 256,
                        ReferenceImageHeight = 256,
                        ReferenceAnchor = new ImagePoint(64.0, 64.0)
                    }
                };
                DetectionProfile profile = new DetectionProfile { TemplateMatchingEnabled = true };
                CalibrationResult leftCal = CreateSyntheticCalibration(1.0, 1.0, 0.0);
                CalibrationResult rightCal = CreateSyntheticCalibration(-1.0, -1.0, 0.0);
                TeachingData teaching = new TeachingData
                {
                    Left = new TeachingPose
                    {
                        Side = TeachingSide.Left,
                        IsValid = true,
                        ImageRow = 64.0,
                        ImageColumn = 10.0,
                        ImageAngleDegrees = 0.0,
                        MachineX = 100.0,
                        MachineY = 50.0,
                        MachineR = 10.0
                    },
                    Right = new TeachingPose
                    {
                        Side = TeachingSide.Right,
                        IsValid = true,
                        ImageRow = 64.0,
                        ImageColumn = 10.0,
                        ImageAngleDegrees = 0.0,
                        MachineX = 120.0,
                        MachineY = 50.0,
                        MachineR = -10.0
                    }
                };

                // 1. 正常空料 -> NO_MATERIAL (executed=true, success=true, pass=true, actualCount=0, items=[])
                engine.NextCollectionResult = TemplateMatchCollectionResult.NoMatch("No PCB in FOV.");
                InspectionResult emptyRes = service.Inspect(frame, positioningJob, leftCal, rightCal, teaching, profile, 6, 12.5, -3.2);
                Assert(emptyRes.Executed && emptyRes.Success && emptyRes.Pass, "NO_MATERIAL must set executed=true, success=true, pass=true.");
                Assert(string.Equals(emptyRes.ErrorCode, ProtocolErrorCodes.NoMaterial, StringComparison.Ordinal), "Expected NO_MATERIAL.");
                Assert(emptyRes.ActualCount == 0 && emptyRes.Items.Count == 0 && !emptyRes.HasManualMarks && !emptyRes.CoordinateMappingApplied,
                    "NO_MATERIAL must have actualCount=0, items=[], hasManualMarks=false, coordinateMapped=false.");

                // 2. 模板引擎失败 -> POSITIONING_FAILED (不能误报 NO_MATERIAL)
                engine.NextCollectionResult = TemplateMatchCollectionResult.Failure(CalibrationErrorCode.TemplateMatchFailed, "Engine fault.");
                InspectionResult failRes = service.Inspect(frame, positioningJob, leftCal, rightCal, teaching, profile, 6, 12.5, -3.2);
                Assert(failRes.Executed && !failRes.Success && !failRes.Pass, "Engine fault must fail.");
                Assert(string.Equals(failRes.ErrorCode, "POSITIONING_FAILED", StringComparison.Ordinal), "Expected POSITIONING_FAILED.");

                // 3. 数量不一致 (expected=6, actual=5) -> BOARD_COUNT_MISMATCH，但 items 完整返回全部 5 块板及双 R 目标
                engine.NextCollectionResult = new TemplateMatchCollectionResult
                {
                    Success = true,
                    ErrorCode = CalibrationErrorCode.None,
                    Message = "Matched 5 boards.",
                    Matches = new List<TemplateMatchResult>
                    {
                        new TemplateMatchResult { Success = true, Row = 64.0, Column = 40.0, AngleDegrees = 2.0, Score = 0.96 },
                        new TemplateMatchResult { Success = true, Row = 64.0, Column = 110.0, AngleDegrees = 2.0, Score = 0.95 },
                        new TemplateMatchResult { Success = true, Row = 64.0, Column = 180.0, AngleDegrees = 2.0, Score = 0.94 },
                        new TemplateMatchResult { Success = true, Row = 150.0, Column = 40.0, AngleDegrees = -1.0, Score = 0.93 },
                        new TemplateMatchResult { Success = true, Row = 150.0, Column = 110.0, AngleDegrees = -1.0, Score = 0.92 }
                    }
                };
                InspectionResult mismatchRes = service.Inspect(frame, positioningJob, leftCal, rightCal, teaching, profile, 6, 12.5, -3.2);
                Assert(mismatchRes.Executed && !mismatchRes.Success && !mismatchRes.Pass, "BOARD_COUNT_MISMATCH must set executed=true, success=false, pass=false.");
                Assert(string.Equals(mismatchRes.ErrorCode, "BOARD_COUNT_MISMATCH", StringComparison.Ordinal), "Expected BOARD_COUNT_MISMATCH.");
                Assert(mismatchRes.ExpectedCount == 6 && mismatchRes.ActualCount == 5 && mismatchRes.Items.Count == 5,
                    "BOARD_COUNT_MISMATCH must return all 5 located boards.");
                for (int i = 0; i < mismatchRes.Items.Count; i++)
                {
                    BoardInspectionItem item = mismatchRes.Items[i];
                    Assert(item.BoardIndex == i + 1, "boardIndex must be contiguous 1..actualCount.");
                    Assert(item.Valid, "Located board must be marked valid=true.");
                    Assert(item.Targets.Count == 2, "Each located board must contain Left(R1) and Right(R2) targets.");
                    Assert(item.Targets.Any(t => t.Side == TeachingSide.Left && t.Axis == "R1"), "Missing Left/R1 target.");
                    Assert(item.Targets.Any(t => t.Side == TeachingSide.Right && t.Axis == "R2"), "Missing Right/R2 target.");
                }

                // 4. 单板映射溢出异常 -> 仅该板置 valid=false 且 targets=[]，其余板正常保留，整帧必为 COORDINATE_MAPPING_FAILED 且 pass=false
                CalibrationResult overflowLeftCal = CreateSyntheticCalibration(1.0, 0.0, 0.0);
                overflowLeftCal.PixelToStageMatrix = new AffineMatrix2D(1e306, 0.0, 0.0, 0.0, -1.0, 0.0);
                engine.NextCollectionResult = new TemplateMatchCollectionResult
                {
                    Success = true,
                    ErrorCode = CalibrationErrorCode.None,
                    Message = "Matched 2 boards.",
                    Matches = new List<TemplateMatchResult>
                    {
                        new TemplateMatchResult { Success = true, Row = 64.0, Column = 10.0, AngleDegrees = 0.0, Score = 0.95 },
                        new TemplateMatchResult { Success = true, Row = 160.0, Column = 200.0, AngleDegrees = 0.0, Score = 0.94 }
                    }
                };
                InspectionResult partialValidRes = service.Inspect(frame, positioningJob, overflowLeftCal, rightCal, teaching, profile, 2, 10.0, -10.0);
                Assert(partialValidRes.Executed && !partialValidRes.Success && !partialValidRes.Pass,
                    "COORDINATE_MAPPING_FAILED must set executed=true, success=false, pass=false.");
                Assert(string.Equals(partialValidRes.ErrorCode, ProtocolErrorCodes.CoordinateMappingFailed, StringComparison.Ordinal),
                    "Expected COORDINATE_MAPPING_FAILED, got " + partialValidRes.ErrorCode);
                Assert(partialValidRes.Items.Count == 2, "Single-board mapping failure must not clear items.");
                Assert(partialValidRes.Items[0].Valid && partialValidRes.Items[0].Targets.Count == 2, "Board #1 must remain valid with 2 targets.");
                Assert(!partialValidRes.Items[1].Valid && partialValidRes.Items[1].Targets.Count == 0, "Board #2 must be marked valid=false with empty targets.");

                // 5. 有板但未执行坐标映射 -> COORDINATE_NOT_MAPPED (executed=true, success=false, pass=false, valid=false, targets=[])
                InspectionResult unmappedRes = service.InspectWithoutMapping(frame, positioningJob, profile, 2);
                Assert(unmappedRes.Executed && !unmappedRes.Success && !unmappedRes.Pass && !unmappedRes.CoordinateMappingApplied,
                    "COORDINATE_NOT_MAPPED must set executed=true, success=false, pass=false, coordinateMapped=false.");
                Assert(string.Equals(unmappedRes.ErrorCode, ProtocolErrorCodes.CoordinateNotMapped, StringComparison.Ordinal),
                    "Expected COORDINATE_NOT_MAPPED, got " + unmappedRes.ErrorCode);
                Assert(unmappedRes.Items.Count == 2 && unmappedRes.Items.All(item => !item.Valid && item.Targets.Count == 0),
                    "Unmapped boards must return valid=false and empty targets.");

                // 5b. 未映射模式下空料（actualCount=0）仍返回 NO_MATERIAL (pass=true)
                engine.NextCollectionResult = TemplateMatchCollectionResult.NoMatch("No PCB in FOV.");
                InspectionResult unmappedEmptyRes = service.InspectWithoutMapping(frame, positioningJob, profile, 2);
                Assert(unmappedEmptyRes.Executed && unmappedEmptyRes.Success && unmappedEmptyRes.Pass &&
                       string.Equals(unmappedEmptyRes.ErrorCode, ProtocolErrorCodes.NoMaterial, StringComparison.Ordinal),
                    "Empty FOV without mapping must still return NO_MATERIAL with pass=true.");

                // 6. 构造第 2 块板带人工白标点的图像与 ROI，验证 5 级错误码优先级与 valid=false 板 hasManualMark 可信性
                CalibrationJob roiJob = positioningJob.DeepClone();
                roiJob.Template.ModelReference = new ImagePose(64.0, 64.0, 0.0);
                roiJob.Template.DetectionRoi = new RegionDefinition
                {
                    Kind = RegionKind.Rectangle2,
                    CenterRow = 64.0,
                    CenterColumn = 64.0,
                    PhiRadians = 0.0,
                    Length1 = 18.0,
                    Length2 = 18.0
                };
                DetectionProfile markProfile = new DetectionProfile
                {
                    TemplateMatchingEnabled = true,
                    FollowTemplateTransform = true,
                    AutoThreshold = false,
                    DetectDarkForeground = false,
                    BrightThreshold = 128,
                    MinimumBlobArea = 10.0,
                    MaximumBlobArea = 5000.0,
                    RectangleFilterEnabled = false
                };
                using (Bitmap markedFrame = new Bitmap(256, 256, System.Drawing.Imaging.PixelFormat.Format24bppRgb))
                {
                    using (Graphics g = Graphics.FromImage(markedFrame))
                    {
                        g.Clear(Color.Black);
                        // 在第 2 块板中心 (col=200, row=160) 绘制交叉白标记，避免矩形过滤干扰并产生明确 Blob
                        g.FillRectangle(Brushes.White, 198, 152, 4, 16);
                        g.FillRectangle(Brushes.White, 192, 158, 16, 4);
                    }
                    engine.NextCollectionResult = new TemplateMatchCollectionResult
                    {
                        Success = true,
                        ErrorCode = CalibrationErrorCode.None,
                        Message = "Matched 2 boards.",
                        Matches = new List<TemplateMatchResult>
                        {
                            new TemplateMatchResult { Success = true, Row = 64.0, Column = 10.0, ModelPose = new ImagePose(64.0, 10.0, 0.0), AngleDegrees = 0.0, Score = 0.95 },
                            new TemplateMatchResult { Success = true, Row = 160.0, Column = 200.0, ModelPose = new ImagePose(160.0, 200.0, 0.0), AngleDegrees = 0.0, Score = 0.94 }
                        }
                    };

                    // 优先级 1 > 2：BOARD_COUNT_MISMATCH 优先于 COORDINATE_NOT_MAPPED
                    InspectionResult prioMismatchUnmapped = service.InspectWithoutMapping(markedFrame, roiJob, markProfile, 3);
                    Assert(string.Equals(prioMismatchUnmapped.ErrorCode, ProtocolErrorCodes.BoardCountMismatch, StringComparison.Ordinal) &&
                           !prioMismatchUnmapped.Pass && prioMismatchUnmapped.Items[1].HasManualMark,
                        "BOARD_COUNT_MISMATCH must take precedence over COORDINATE_NOT_MAPPED.");

                    // 优先级 1 > 3：BOARD_COUNT_MISMATCH 优先于 COORDINATE_MAPPING_FAILED
                    InspectionResult prioMismatchInvalid = service.Inspect(markedFrame, roiJob, overflowLeftCal, rightCal, teaching, markProfile, 3, 10.0, -10.0);
                    Assert(string.Equals(prioMismatchInvalid.ErrorCode, ProtocolErrorCodes.BoardCountMismatch, StringComparison.Ordinal) &&
                           !prioMismatchInvalid.Pass && !prioMismatchInvalid.Items[1].Valid && prioMismatchInvalid.Items[1].HasManualMark,
                        "BOARD_COUNT_MISMATCH must take precedence over COORDINATE_MAPPING_FAILED.");

                    // 优先级 2 > 4：COORDINATE_NOT_MAPPED 优先于 MANUAL_MARK_FOUND，且 hasManualMark 仍可信
                    InspectionResult prioUnmappedMark = service.InspectWithoutMapping(markedFrame, roiJob, markProfile, 2);
                    Assert(string.Equals(prioUnmappedMark.ErrorCode, ProtocolErrorCodes.CoordinateNotMapped, StringComparison.Ordinal) &&
                           !prioUnmappedMark.Success && !prioUnmappedMark.Pass &&
                           !prioUnmappedMark.Items[0].HasManualMark && prioUnmappedMark.Items[1].HasManualMark,
                        "COORDINATE_NOT_MAPPED must beat MANUAL_MARK_FOUND and preserve trustworthy hasManualMark.");

                    // 优先级 3 > 4：COORDINATE_MAPPING_FAILED 优先于 MANUAL_MARK_FOUND，且 valid=false 板 hasManualMark 仍可信
                    InspectionResult prioInvalidMark = service.Inspect(markedFrame, roiJob, overflowLeftCal, rightCal, teaching, markProfile, 2, 10.0, -10.0);
                    Assert(string.Equals(prioInvalidMark.ErrorCode, ProtocolErrorCodes.CoordinateMappingFailed, StringComparison.Ordinal) &&
                           !prioInvalidMark.Success && !prioInvalidMark.Pass &&
                           prioInvalidMark.Items[0].Valid && !prioInvalidMark.Items[0].HasManualMark &&
                           !prioInvalidMark.Items[1].Valid && prioInvalidMark.Items[1].HasManualMark,
                        "COORDINATE_MAPPING_FAILED must beat MANUAL_MARK_FOUND and preserve trustworthy hasManualMark on valid=false board.");
                }
            }
        }

        private static async Task VerifyCalibrationSessionsAndPreservationAsync(CancellationToken cancellationToken)
        {
            // 验证需求二 & 需求三：calibration.started 握手、拒绝、motion.failed、calibration.cancel、界面停止标定，以及保留旧标定不覆盖
            CalibrationJob committedLeft = CreateJobWithDummyTemplateAndSample("Calibration-Left", "EXISTING_L1");
            CalibrationJob committedRight = CreateJobWithDummyTemplateAndSample("Calibration-Right", "EXISTING_R1");

            int saveCallCount = 0;
            int captureCallCount = 0;
            List<ProtocolEnvelope> sent = new List<ProtocolEnvelope>();
            object sentSync = new object();

            CalibrationCoordinator coordinator = null;
            Func<ProtocolEnvelope, CancellationToken, Task> responder = null;

            using (NinePointRotationCalibrationSdk sdk = new NinePointRotationCalibrationSdk())
            using (coordinator = new CalibrationCoordinator(
                sdk,
                async (envelope, ct) =>
                {
                    lock (sentSync) sent.Add(envelope);
                    if (responder != null) await responder(envelope, ct).ConfigureAwait(false);
                },
                ct =>
                {
                    Interlocked.Increment(ref captureCallCount);
                    throw new InvalidOperationException("Capture should not run when motion fails or session is rejected.");
                },
                (side, job) =>
                {
                    Interlocked.Increment(ref saveCallCount);
                    return Task.CompletedTask;
                },
                new StationOptions { CalibrationStartAckTimeoutMilliseconds = 2000, MotionTimeoutMilliseconds = 2000 },
                committedLeft,
                committedRight))
            {
                // 场景 A：上位机拒绝 calibration.started.ack(accepted=false) -> 不发 move.request，回 SESSION_REJECTED，保留旧标定
                responder = (msg, ct) =>
                {
                    if (msg.Type == ProtocolMessageTypes.CalibrationStarted)
                    {
                        Assert(string.Equals(ProtocolValue.String(msg.Data, "nozzle"), "Left", StringComparison.Ordinal), "Expected Left nozzle.");
                        Assert(string.Equals(ProtocolValue.String(msg.Data, "axisR"), "R1", StringComparison.Ordinal), "Expected R1 axis.");
                        Assert(ProtocolValue.Integer(msg.Data, "totalPoints") == 14, "Expected totalPoints=14.");
                        ProtocolEnvelope ack = ProtocolEnvelope.Create(ProtocolMessageTypes.CalibrationStartedAck);
                        ack.CorrelationId = msg.MessageId;
                        ack.Data["sessionId"] = ProtocolValue.String(msg.Data, "sessionId");
                        ack.Data["accepted"] = false;
                        ack.Data["reason"] = "Machine in production";
                        coordinator.HandleProtocolMessage(ack);
                    }
                    return Task.CompletedTask;
                };

                bool rejectedThrown = false;
                try
                {
                    await coordinator.RunAutomaticAsync(TeachingSide.Left, cancellationToken).ConfigureAwait(false);
                }
                catch (CalibrationWorkflowException ex)
                {
                    rejectedThrown = ex.ErrorCode == ProtocolErrorCodes.SessionRejected &&
                                     ex.Message.Contains("Machine in production");
                }
                Assert(rejectedThrown, "Rejected calibration.started.ack must fail with SESSION_REJECTED and host reason.");
                Assert(!sent.Any(m => m.Type == ProtocolMessageTypes.MoveRequest), "No move.request may be sent after accepted=false.");
                Assert(sent.Any(m => m.Type == ProtocolMessageTypes.CalibrationCompleted &&
                                     ProtocolValue.String(m.Data, "errorCode") == ProtocolErrorCodes.SessionRejected),
                    "calibration.completed(SESSION_REJECTED) expected.");
                Assert(saveCallCount == 0 && coordinator.GetJobSnapshot(TeachingSide.Left).Samples.Count == 1,
                    "Old Left calibration must remain untouched after rejection.");

                // 场景 B：右吸嘴 R2 标定期间收到 motion.failed -> 立即终止，不拍照不下发后续点，发 MOTION_FAILED，保留旧标定
                sent.Clear();
                string capturedSessionId = null;
                responder = (msg, ct) =>
                {
                    if (msg.Type == ProtocolMessageTypes.CalibrationStarted)
                    {
                        capturedSessionId = ProtocolValue.String(msg.Data, "sessionId");
                        Assert(string.Equals(ProtocolValue.String(msg.Data, "nozzle"), "Right", StringComparison.Ordinal), "Expected Right nozzle.");
                        Assert(string.Equals(ProtocolValue.String(msg.Data, "axisR"), "R2", StringComparison.Ordinal), "Expected R2 axis.");
                        ProtocolEnvelope ack = ProtocolEnvelope.Create(ProtocolMessageTypes.CalibrationStartedAck);
                        ack.CorrelationId = msg.MessageId;
                        ack.Data["sessionId"] = capturedSessionId;
                        ack.Data["accepted"] = true;
                        ack.Data["originX"] = 650.120;
                        ack.Data["originY"] = 210.450;
                        ack.Data["originR"] = 0.000;
                        coordinator.HandleProtocolMessage(ack);
                    }
                    else if (msg.Type == ProtocolMessageTypes.MoveRequest)
                    {
                        Assert(string.Equals(ProtocolValue.String(msg.Data, "sessionId"), capturedSessionId, StringComparison.Ordinal), "move.request must carry sessionId.");
                        Assert(string.Equals(ProtocolValue.String(msg.Data, "axisR"), "R2", StringComparison.Ordinal), "move.request must carry axisR=R2.");
                        ProtocolEnvelope failed = ProtocolEnvelope.Create(ProtocolMessageTypes.MotionFailed);
                        failed.CorrelationId = msg.MessageId;
                        failed.Data["sessionId"] = capturedSessionId;
                        failed.Data["pointNumber"] = ProtocolValue.Integer(msg.Data, "pointNumber");
                        failed.Data["code"] = MotionFailedCodes.SoftLimit;
                        failed.Data["message"] = "Target X 905.000 exceeds soft limit 900.000";
                        coordinator.HandleProtocolMessage(failed);
                    }
                    return Task.CompletedTask;
                };

                bool motionFailedThrown = false;
                try
                {
                    await coordinator.RunAutomaticAsync(TeachingSide.Right, cancellationToken).ConfigureAwait(false);
                }
                catch (CalibrationWorkflowException ex)
                {
                    motionFailedThrown = ex.ErrorCode == ProtocolErrorCodes.MotionFailed &&
                                         ex.Message.Contains(MotionFailedCodes.SoftLimit);
                }
                Assert(motionFailedThrown, "motion.failed must abort calibration with MOTION_FAILED.");
                Assert(sent.Count(m => m.Type == ProtocolMessageTypes.MoveRequest) == 1, "No subsequent move.request may be sent after motion.failed.");
                Assert(captureCallCount == 0, "Camera capture must not run after motion.failed.");
                ProtocolEnvelope motionFailedDone = sent.LastOrDefault(m => m.Type == ProtocolMessageTypes.CalibrationCompleted);
                Assert(motionFailedDone != null &&
                       ProtocolValue.String(motionFailedDone.Data, "errorCode") == ProtocolErrorCodes.MotionFailed &&
                       ProtocolValue.String(motionFailedDone.Data, "axisR") == "R2" &&
                       ProtocolValue.String(motionFailedDone.Data, "sessionId") == capturedSessionId,
                    "calibration.completed(MOTION_FAILED) with sessionId and axisR=R2 expected.");
                Assert(saveCallCount == 0 && coordinator.GetJobSnapshot(TeachingSide.Right).Samples.Count == 1,
                    "Old Right calibration must remain untouched after motion.failed.");

                // 场景 C：上位机主动发 calibration.cancel -> 立即停止并回 calibration.completed(CANCELLED, corrId=cancel.messageId)
                sent.Clear();
                string cancelMessageId = "host-cancel-test-001";
                responder = (msg, ct) =>
                {
                    if (msg.Type == ProtocolMessageTypes.CalibrationStarted)
                    {
                        capturedSessionId = ProtocolValue.String(msg.Data, "sessionId");
                        ProtocolEnvelope ack = ProtocolEnvelope.Create(ProtocolMessageTypes.CalibrationStartedAck);
                        ack.CorrelationId = msg.MessageId;
                        ack.Data["sessionId"] = capturedSessionId;
                        ack.Data["accepted"] = true;
                        ack.Data["originX"] = 650.0;
                        ack.Data["originY"] = 210.0;
                        ack.Data["originR"] = 0.0;
                        coordinator.HandleProtocolMessage(ack);
                    }
                    else if (msg.Type == ProtocolMessageTypes.MoveRequest)
                    {
                        ProtocolEnvelope cancel = ProtocolEnvelope.Create(ProtocolMessageTypes.CalibrationCancel);
                        cancel.MessageId = cancelMessageId;
                        cancel.Data["sessionId"] = capturedSessionId;
                        cancel.Data["reason"] = "Operator stop";
                        _ = coordinator.HandleCalibrationCancelAsync(cancel);
                    }
                    return Task.CompletedTask;
                };

                bool canceledThrown = false;
                try
                {
                    await coordinator.RunAutomaticAsync(TeachingSide.Left, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    canceledThrown = true;
                }
                Assert(canceledThrown, "Host calibration.cancel must cancel automatic calibration.");
                ProtocolEnvelope cancelDone = sent.LastOrDefault(m => m.Type == ProtocolMessageTypes.CalibrationCompleted);
                Assert(cancelDone != null &&
                       ProtocolValue.String(cancelDone.Data, "errorCode") == ProtocolErrorCodes.Cancelled &&
                       string.Equals(cancelDone.CorrelationId, cancelMessageId, StringComparison.Ordinal),
                    "calibration.completed(CANCELLED) must correlate to calibration.cancel.messageId.");

                // 场景 D：视觉界面停止手动标定 -> 发 calibration.completed(CANCELLED, corrId=null)
                sent.Clear();
                responder = (msg, ct) =>
                {
                    if (msg.Type == ProtocolMessageTypes.CalibrationStarted)
                    {
                        capturedSessionId = ProtocolValue.String(msg.Data, "sessionId");
                        ProtocolEnvelope ack = ProtocolEnvelope.Create(ProtocolMessageTypes.CalibrationStartedAck);
                        ack.CorrelationId = msg.MessageId;
                        ack.Data["sessionId"] = capturedSessionId;
                        ack.Data["accepted"] = true;
                        ack.Data["originX"] = 650.0;
                        ack.Data["originY"] = 210.0;
                        ack.Data["originR"] = 0.0;
                        coordinator.HandleProtocolMessage(ack);
                    }
                    return Task.CompletedTask;
                };
                string manualSessionId = await coordinator.StartManualSessionAsync(TeachingSide.Right, cancellationToken).ConfigureAwait(false);
                Assert(manualSessionId == capturedSessionId, "Manual session ID mismatch.");
                await coordinator.CancelCalibrationAsync("Operator stopped on vision UI").ConfigureAwait(false);
                ProtocolEnvelope uiCancelDone = sent.LastOrDefault(m => m.Type == ProtocolMessageTypes.CalibrationCompleted);
                Assert(uiCancelDone != null &&
                       ProtocolValue.String(uiCancelDone.Data, "errorCode") == ProtocolErrorCodes.Cancelled &&
                       string.IsNullOrWhiteSpace(uiCancelDone.CorrelationId) &&
                       ProtocolValue.String(uiCancelDone.Data, "axisR") == "R2",
                    "Vision UI cancel must send calibration.completed(CANCELLED, corrId=null, axisR=R2).");
            }
        }

        private static async Task VerifyLowScoreCalibrationAbortAsync(CancellationToken cancellationToken)
        {
            // 验证需求：标定期间任一点位模板匹配分数低于设定最低匹配分数时立即中断并报 NG（自动与手动模式均适用，且保留旧标定）
            CalibrationJob committedLeft = CreateJobWithDummyTemplateAndSample("Calibration-Left", "EXISTING_L1");
            committedLeft.Template.MatchParameters.MinScore = 0.75;
            CalibrationJob committedRight = CreateJobWithDummyTemplateAndSample("Calibration-Right", "EXISTING_R1");
            committedRight.Template.MatchParameters.MinScore = 0.75;

            FakeSelfTestTemplateEngine engine = new FakeSelfTestTemplateEngine();
            using (NinePointRotationCalibrationSdk sdk = new NinePointRotationCalibrationSdk(engine))
            {
                // 1. SDK 直接校验分数低于 MinScore
                TemplateMatchResult lowMatch = new TemplateMatchResult
                {
                    Success = true,
                    Row = 500.0,
                    Column = 600.0,
                    ModelPose = new ImagePose(500.0, 600.0, 1.0),
                    AngleDegrees = 1.0,
                    Score = 0.52
                };
                SampleCaptureResult sdkReject = sdk.AddSampleFromMatch(committedLeft, lowMatch, 100.0, 50.0, 0.0, "P1", CalibrationSampleKind.Translation);
                Assert(!sdkReject.Success && sdkReject.ErrorCode == CalibrationErrorCode.TemplateMatchFailed,
                    "SDK AddSampleFromMatch must reject match score below Template.MatchParameters.MinScore.");

                // 2. 自动标定：点 1 成功 (0.94 >= 0.75)，点 2 低分 (0.52 < 0.75) -> 立即中断不下发点 3，回传 MATCH_FAILED 并保留旧标定
                int saveCount = 0;
                int captureCount = 0;
                List<ProtocolEnvelope> sent = new List<ProtocolEnvelope>();
                object sentSync = new object();
                CalibrationCoordinator coordinator = null;
                Func<ProtocolEnvelope, CancellationToken, Task> responder = null;

                using (coordinator = new CalibrationCoordinator(
                    sdk,
                    async (envelope, ct) =>
                    {
                        lock (sentSync) sent.Add(envelope);
                        if (responder != null) await responder(envelope, ct).ConfigureAwait(false);
                    },
                    ct =>
                    {
                        int index = Interlocked.Increment(ref captureCount);
                        if (index == 1)
                        {
                            engine.NextCollectionResult = new TemplateMatchCollectionResult
                            {
                                Success = true,
                                ErrorCode = CalibrationErrorCode.None,
                                Matches = new List<TemplateMatchResult>
                                {
                                    new TemplateMatchResult
                                    {
                                        Success = true,
                                        Row = 500.0,
                                        Column = 600.0,
                                        ModelPose = new ImagePose(500.0, 600.0, 0.0),
                                        AngleDegrees = 0.0,
                                        Score = 0.94
                                    }
                                }
                            };
                        }
                        else
                        {
                            engine.NextCollectionResult = new TemplateMatchCollectionResult
                            {
                                Success = true,
                                ErrorCode = CalibrationErrorCode.None,
                                Matches = new List<TemplateMatchResult>
                                {
                                    new TemplateMatchResult
                                    {
                                        Success = true,
                                        Row = 520.0,
                                        Column = 620.0,
                                        ModelPose = new ImagePose(520.0, 620.0, 0.0),
                                        AngleDegrees = 0.0,
                                        Score = 0.52
                                    }
                                }
                            };
                        }
                        return Task.FromResult(new CameraFrame(new Bitmap(64, 64), index, DateTime.UtcNow));
                    },
                    (side, job) =>
                    {
                        Interlocked.Increment(ref saveCount);
                        return Task.CompletedTask;
                    },
                    new StationOptions { CalibrationStartAckTimeoutMilliseconds = 2000, MotionTimeoutMilliseconds = 2000 },
                    committedLeft,
                    committedRight))
                {
                    string sessionId = null;
                    responder = (msg, ct) =>
                    {
                        if (msg.Type == ProtocolMessageTypes.CalibrationStarted)
                        {
                            sessionId = ProtocolValue.String(msg.Data, "sessionId");
                            ProtocolEnvelope ack = ProtocolEnvelope.Create(ProtocolMessageTypes.CalibrationStartedAck);
                            ack.CorrelationId = msg.MessageId;
                            ack.Data["sessionId"] = sessionId;
                            ack.Data["accepted"] = true;
                            ack.Data["originX"] = 100.0;
                            ack.Data["originY"] = 50.0;
                            ack.Data["originR"] = 0.0;
                            coordinator.HandleProtocolMessage(ack);
                        }
                        else if (msg.Type == ProtocolMessageTypes.MoveRequest)
                        {
                            int ptNum = ProtocolValue.Integer(msg.Data, "pointNumber", 0);
                            ProtocolEnvelope arrived = ProtocolEnvelope.Create(ProtocolMessageTypes.MotionArrived);
                            arrived.CorrelationId = msg.MessageId;
                            arrived.Data["sessionId"] = sessionId;
                            arrived.Data["pointNumber"] = ptNum;
                            arrived.Data["x"] = 100.0 + ProtocolValue.Double(msg.Data, "offsetX");
                            arrived.Data["y"] = 50.0 + ProtocolValue.Double(msg.Data, "offsetY");
                            arrived.Data["r"] = ProtocolValue.Double(msg.Data, "offsetR");
                            coordinator.HandleProtocolMessage(arrived);
                        }
                        return Task.CompletedTask;
                    };

                    bool autoAbortedNg = false;
                    try
                    {
                        await coordinator.RunAutomaticAsync(TeachingSide.Left, cancellationToken).ConfigureAwait(false);
                    }
                    catch (CalibrationWorkflowException ex)
                    {
                        autoAbortedNg = ex.ErrorCode == ProtocolErrorCodes.MatchFailed && ex.Message.Contains("NG");
                    }
                    Assert(autoAbortedNg, "Automatic calibration must immediately abort with MATCH_FAILED / NG when score < MinScore.");
                    Assert(sent.Count(m => m.Type == ProtocolMessageTypes.MoveRequest) == 2,
                        "Automatic calibration must stop at point 2 and never request point 3.");
                    ProtocolEnvelope autoDone = sent.LastOrDefault(m => m.Type == ProtocolMessageTypes.CalibrationCompleted);
                    Assert(autoDone != null &&
                           ProtocolValue.String(autoDone.Data, "errorCode") == ProtocolErrorCodes.MatchFailed,
                        "calibration.completed(MATCH_FAILED) expected on low score abort.");
                    IReadOnlyList<CalibrationPointRecord> records = coordinator.GetRecordsSnapshot(TeachingSide.Left);
                    Assert(records.First(r => r.PointNumber == 1).Status == CalibrationPointStatus.Completed,
                        "Point 1 should remain Completed before Point 2 low-score abort.");
                    Assert(records.First(r => r.PointNumber == 2).Status == CalibrationPointStatus.Failed,
                        "Point 2 should be marked Failed (NG) on low score.");
                    Assert(saveCount == 0 && coordinator.GetJobSnapshot(TeachingSide.Left).Samples.Count == 1,
                        "Committed calibration must be preserved untouched after low-score abort.");

                    // 3. 手动标定：单点匹配分数低于 MinScore 同样立即中断会话并报 NG
                    sent.Clear();
                    captureCount = 10; // ensure index > 1 -> score 0.52 < 0.75
                    bool manualAbortedNg = false;
                    try
                    {
                        await coordinator.CapturePointAsync(TeachingSide.Right, 1, cancellationToken).ConfigureAwait(false);
                    }
                    catch (CalibrationWorkflowException ex)
                    {
                        manualAbortedNg = ex.ErrorCode == ProtocolErrorCodes.MatchFailed && ex.Message.Contains("NG");
                    }
                    Assert(manualAbortedNg, "Manual calibration point must immediately abort session with NG when score < MinScore.");
                    Assert(!coordinator.HasActiveSession, "Manual calibration session must close immediately on low-score NG.");
                    Assert(sent.Any(m => m.Type == ProtocolMessageTypes.CalibrationCompleted &&
                                         ProtocolValue.String(m.Data, "errorCode") == ProtocolErrorCodes.MatchFailed),
                        "Manual low-score NG must send calibration.completed(MATCH_FAILED).");
                }
            }
        }

        private static async Task VerifyTwoStepTeachingAsync(CancellationToken cancellationToken)
        {
            // 验证需求：示教分两步——步骤 1 仅记录轴坐标（不触发拍照），步骤 2 移轴后触发拍照并用定位模板匹配图像坐标并合并保存
            FakeSelfTestTemplateEngine engine = new FakeSelfTestTemplateEngine();
            CalibrationJob positioningJob = CreateJobWithDummyTemplateAndSample("SelfTestPositioning", "P1");
            positioningJob.Template.MatchParameters.MinScore = 0.60;
            int captureCount = 0;
            int saveCount = 0;
            TeachingData lastSaved = null;
            List<List<ImagePoint>> receivedContours = null;

            using (NinePointRotationCalibrationSdk sdk = new NinePointRotationCalibrationSdk(engine))
            using (TeachingService teaching = new TeachingService(
                sdk,
                ct =>
                {
                    int seq = Interlocked.Increment(ref captureCount);
                    return Task.FromResult(new CameraFrame(new Bitmap(64, 64), seq, DateTime.UtcNow));
                },
                () => positioningJob,
                data =>
                {
                    Interlocked.Increment(ref saveCount);
                    lastSaved = data;
                    return Task.CompletedTask;
                },
                new TeachingData()))
            {
                teaching.TeachingChanged += (s, e) =>
                {
                    if (e != null) receivedContours = e.ContourSegments;
                };

                // 步骤 1：记录左吸嘴机械轴坐标 -> 严禁触发拍照，状态为半完成 (HasMachinePose=true, HasImagePose=false, IsValid=false)
                TeachingPose step1 = await teaching.RecordMachinePoseAsync(
                    TeachingSide.Left,
                    new MachinePose(650.125, 210.450, 0.0),
                    cancellationToken).ConfigureAwait(false);
                Assert(captureCount == 0, "RecordMachinePoseAsync must not trigger camera capture while axis blocks FOV.");
                Assert(step1.HasMachinePose && !step1.HasImagePose && !step1.IsValid,
                    "After step 1, HasMachinePose must be true while HasImagePose and IsValid stay false.");
                Assert(Math.Abs(step1.MachineX - 650.125) < 1e-6 && saveCount == 1,
                    "Step 1 machine coordinates must be persisted.");

                // 步骤 2A：移轴后触发模板匹配，若分数低于 MinScore -> 报 MATCH_FAILED 且保留步骤 1 轴坐标
                engine.NextCollectionResult = new TemplateMatchCollectionResult
                {
                    Success = true,
                    ErrorCode = CalibrationErrorCode.None,
                    Matches = new List<TemplateMatchResult>
                    {
                        new TemplateMatchResult
                        {
                            Success = true,
                            Row = 400.0,
                            Column = 500.0,
                            ModelPose = new ImagePose(400.0, 500.0, 0.5),
                            AngleDegrees = 0.5,
                            Score = 0.35
                        }
                    }
                };
                bool lowScoreRejected = false;
                try
                {
                    await teaching.LocateImagePoseAsync(TeachingSide.Left, cancellationToken).ConfigureAwait(false);
                }
                catch (TeachingWorkflowException ex)
                {
                    lowScoreRejected = ex.ErrorCode == ProtocolErrorCodes.MatchFailed;
                }
                Assert(lowScoreRejected, "LocateImagePoseAsync must reject match score below MinScore.");
                Assert(captureCount == 1, "LocateImagePoseAsync must trigger camera capture.");
                Assert(teaching.GetSnapshot().Left.HasMachinePose && !teaching.GetSnapshot().Left.IsValid,
                    "Step 1 machine pose must stay preserved after failed match.");

                // 步骤 2B：移轴后模板匹配成功 -> 触发拍照并合并步骤 1 轴坐标生成完整有效示教，回传轮廓用于界面绘制
                engine.NextCollectionResult = new TemplateMatchCollectionResult
                {
                    Success = true,
                    ErrorCode = CalibrationErrorCode.None,
                    Matches = new List<TemplateMatchResult>
                    {
                        new TemplateMatchResult
                        {
                            Success = true,
                            Row = 412.5,
                            Column = 528.25,
                            ModelPose = new ImagePose(412.5, 528.25, 1.25),
                            AngleDegrees = 1.25,
                            Score = 0.93,
                            ContourSegments = new List<List<ImagePoint>>
                            {
                                new List<ImagePoint> { new ImagePoint(400, 500), new ImagePoint(420, 540) }
                            }
                        }
                    }
                };
                TeachingPose step2 = await teaching.LocateImagePoseAsync(TeachingSide.Left, cancellationToken).ConfigureAwait(false);
                Assert(captureCount == 2, "Step 2B must trigger fresh camera capture.");
                Assert(step2.IsValid && step2.HasMachinePose && step2.HasImagePose,
                    "Step 2B must complete teaching with IsValid=true.");
                Assert(Math.Abs(step2.MachineX - 650.125) < 1e-6 && Math.Abs(step2.ImageRow - 412.5) < 1e-6 &&
                       Math.Abs(step2.ImageColumn - 528.25) < 1e-6 && Math.Abs(step2.MatchScore - 0.93) < 1e-6,
                    "Step 2B must combine recorded machine pose with located image pose.");
                Assert(receivedContours != null && receivedContours.Count == 1,
                    "TeachingChanged must carry matched contour segments for viewport overlay.");

                // 反向顺序（右吸嘴先模板匹配定位，再记录轴坐标）同样自动合并为有效示教
                TeachingPose rightImgFirst = await teaching.LocateImagePoseAsync(TeachingSide.Right, cancellationToken).ConfigureAwait(false);
                Assert(!rightImgFirst.IsValid && !rightImgFirst.HasMachinePose && rightImgFirst.HasImagePose,
                    "Locating image first without recorded machine pose must stay IsValid=false.");
                TeachingPose rightDone = await teaching.RecordMachinePoseAsync(
                    TeachingSide.Right,
                    new MachinePose(672.0, 210.45, 0.0),
                    cancellationToken).ConfigureAwait(false);
                Assert(rightDone.IsValid && rightDone.HasMachinePose && rightDone.HasImagePose &&
                       Math.Abs(rightDone.ImageColumn - 528.25) < 1e-6,
                    "Recording machine pose after image locate must merge into IsValid=true.");
                Assert(lastSaved != null && lastSaved.HasBothNozzles, "Both nozzles should now be valid.");
            }
        }

        private static async Task VerifyRuntimeProtocolV11EndToEndAsync(
            ProtocolJsonCodec codec,
            CancellationToken cancellationToken)
        {
            // 端到端验证 StationRuntime：system.hello -> system.ack(1.1)、system.status 主动推送、
            // 单侧 R 拦截 PROTOCOL_INVALID、采图失败返回 inspection.result(IMAGE_MISSING)、无模板配置返回 POSITIONING_TEMPLATE_MISSING、
            // 关闭检测返回 system.error(INSPECTION_DISABLED)
            string tempDir = Path.Combine(Path.GetTempPath(), "nprcal-selftest-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            int port = ReserveLoopbackPort();
            StationOptions options = new StationOptions
            {
                TcpBindAddress = "127.0.0.1",
                TcpPort = port,
                TcpMaxMessageBytes = 64 * 1024,
                CameraEnabled = true,
                CalibrationEnabled = false,
                InspectionEnabled = true,
                LiveAcquisitionEnabled = false,
                CameraTimeoutMilliseconds = 500,
                HeartbeatTimeoutMilliseconds = 6000
            };
            options.ApplyConfigDirectory(tempDir);
            string optionsPath = Path.Combine(tempDir, "station.json");
            FakeSelfTestCamera camera = new FakeSelfTestCamera();

            using (CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            using (StationRuntime runtime = StationRuntime.CreateForSelfTest(optionsPath, options, camera))
            using (TcpClient client = new TcpClient(AddressFamily.InterNetwork))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(15));
                try
                {
                    await runtime.StartAsync(timeout.Token).ConfigureAwait(false);
                    await client.ConnectAsync(IPAddress.Loopback, port).ConfigureAwait(false);
                    NetworkStream stream = client.GetStream();

                    // 1. system.hello -> system.ack (version 1.1)
                    ProtocolEnvelope hello = ProtocolEnvelope.Create(ProtocolMessageTypes.Hello);
                    hello.Data["role"] = "host";
                    await WriteEnvelopeAsync(stream, codec, hello, timeout.Token).ConfigureAwait(false);
                    ProtocolEnvelope ack = await ReadEnvelopeAsync(stream, codec, options.TcpMaxMessageBytes, timeout.Token).ConfigureAwait(false);
                    Assert(ack.Type == ProtocolMessageTypes.Acknowledge, "Expected system.ack.");
                    Assert(ack.CorrelationId == hello.MessageId, "system.ack correlationId mismatch.");
                    Assert(ProtocolValue.String(ack.Data, "protocolVersion") == "1.1", "Expected protocolVersion=1.1.");
                    bool ready;
                    Assert(ProtocolValue.TryBool(ack.Data, "ready", out ready) && ready, "Expected ready=true with fake camera connected.");

                    // 2. 切换检测使能 -> 主动推送 system.status
                    await runtime.ApplyEnablementAsync(true, false, false, timeout.Token).ConfigureAwait(false);
                    ProtocolEnvelope statusPush = await ReadEnvelopeAsync(stream, codec, options.TcpMaxMessageBytes, timeout.Token).ConfigureAwait(false);
                    Assert(statusPush.Type == ProtocolMessageTypes.SystemStatus, "Expected system.status push on enablement change.");
                    Assert(string.IsNullOrWhiteSpace(statusPush.CorrelationId), "system.status correlationId must be null/empty.");
                    bool inspEnabled;
                    Assert(ProtocolValue.TryBool(statusPush.Data, "inspectionEnabled", out inspEnabled) && !inspEnabled,
                        "system.status must report inspectionEnabled=false.");

                    // 3. 检测关闭时触发 inspection.trigger -> system.error / INSPECTION_DISABLED
                    ProtocolEnvelope trigDisabled = ProtocolEnvelope.Create(ProtocolMessageTypes.InspectionTrigger);
                    trigDisabled.Data["currentRLeft"] = 12.5;
                    trigDisabled.Data["currentRRight"] = -3.2;
                    await WriteEnvelopeAsync(stream, codec, trigDisabled, timeout.Token).ConfigureAwait(false);
                    ProtocolEnvelope errDisabled = await ReadEnvelopeAsync(stream, codec, options.TcpMaxMessageBytes, timeout.Token).ConfigureAwait(false);
                    Assert(errDisabled.Type == ProtocolMessageTypes.Error &&
                           ProtocolValue.String(errDisabled.Data, "code") == ProtocolErrorCodes.InspectionDisabled,
                        "Expected system.error / INSPECTION_DISABLED.");

                    // 恢复检测使能（消费推送的 system.status）
                    await runtime.ApplyEnablementAsync(true, false, true, timeout.Token).ConfigureAwait(false);
                    ProtocolEnvelope statusRestored = await ReadEnvelopeAsync(stream, codec, options.TcpMaxMessageBytes, timeout.Token).ConfigureAwait(false);
                    Assert(statusRestored.Type == ProtocolMessageTypes.SystemStatus, "Expected second system.status push.");

                    // 4. 仅传 currentRLeft 不传 currentRRight -> system.error / PROTOCOL_INVALID
                    ProtocolEnvelope trigHalfR = ProtocolEnvelope.Create(ProtocolMessageTypes.InspectionTrigger);
                    trigHalfR.Data["currentRLeft"] = 12.5;
                    await WriteEnvelopeAsync(stream, codec, trigHalfR, timeout.Token).ConfigureAwait(false);
                    ProtocolEnvelope errHalfR = await ReadEnvelopeAsync(stream, codec, options.TcpMaxMessageBytes, timeout.Token).ConfigureAwait(false);
                    Assert(errHalfR.Type == ProtocolMessageTypes.Error &&
                           ProtocolValue.String(errHalfR.Data, "code") == ProtocolErrorCodes.ProtocolInvalid,
                        "Half dual-R trigger must return system.error / PROTOCOL_INVALID.");

                    // 5. 采图异常（已进入检测流程）-> 走 inspection.result(executed=false, errorCode=IMAGE_MISSING)，而非 system.error
                    camera.FailCapture = true;
                    ProtocolEnvelope trigCaptureFail = ProtocolEnvelope.Create(ProtocolMessageTypes.InspectionTrigger);
                    trigCaptureFail.Data["currentRLeft"] = 12.5004;
                    trigCaptureFail.Data["currentRRight"] = -3.2004;
                    trigCaptureFail.Data["expectedCount"] = 6;
                    await WriteEnvelopeAsync(stream, codec, trigCaptureFail, timeout.Token).ConfigureAwait(false);
                    ProtocolEnvelope resCaptureFail = await ReadEnvelopeAsync(stream, codec, options.TcpMaxMessageBytes, timeout.Token).ConfigureAwait(false);
                    Assert(resCaptureFail.Type == ProtocolMessageTypes.InspectionResult, "Capture failure must return inspection.result.");
                    Assert(ProtocolValue.String(resCaptureFail.Data, "errorCode") == "IMAGE_MISSING", "Expected IMAGE_MISSING.");
                    Assert(Math.Abs(ProtocolValue.Double(resCaptureFail.Data, "currentRLeft") - 12.5) < 1e-6, "Expected rounded currentRLeft echo.");
                    Assert(Math.Abs(ProtocolValue.Double(resCaptureFail.Data, "currentRRight") - (-3.2)) < 1e-6, "Expected rounded currentRRight echo.");

                    // 6. 正常采图但无定位模板 -> inspection.result(executed=false, errorCode=POSITIONING_TEMPLATE_MISSING)
                    camera.FailCapture = false;
                    ProtocolEnvelope trigNoTpl = ProtocolEnvelope.Create(ProtocolMessageTypes.InspectionTrigger);
                    trigNoTpl.Data["currentRLeft"] = 12.5;
                    trigNoTpl.Data["currentRRight"] = -3.2;
                    trigNoTpl.Data["expectedCount"] = 6;
                    await WriteEnvelopeAsync(stream, codec, trigNoTpl, timeout.Token).ConfigureAwait(false);
                    ProtocolEnvelope resNoTpl = await ReadEnvelopeAsync(stream, codec, options.TcpMaxMessageBytes, timeout.Token).ConfigureAwait(false);
                    Assert(resNoTpl.Type == ProtocolMessageTypes.InspectionResult &&
                           ProtocolValue.String(resNoTpl.Data, "errorCode") == "POSITIONING_TEMPLATE_MISSING",
                        "Missing positioning template must return inspection.result / POSITIONING_TEMPLATE_MISSING.");

                    await runtime.StopAsync().ConfigureAwait(false);
                }
                finally
                {
                    try { Directory.Delete(tempDir, true); } catch { }
                }
            }
        }

        private static async Task VerifyTeachingServiceAndProtocolAsync(
            ProtocolJsonCodec codec,
            CancellationToken cancellationToken)
        {
            // 1. 单元测试：保存示教失败时抛出 TEACHING_SAVE_FAILED，且必须完整保留旧示教数据
            using (NinePointRotationCalibrationSdk unitSdk = new NinePointRotationCalibrationSdk(new FakeSelfTestTemplateEngine
            {
                NextCollectionResult = new TemplateMatchCollectionResult
                {
                    Success = true,
                    ErrorCode = CalibrationErrorCode.None,
                    Matches = new List<TemplateMatchResult>
                    {
                        new TemplateMatchResult { Success = true, Row = 100.0, Column = 200.0, AngleDegrees = 1.5, Score = 0.96 }
                    }
                }
            }))
            {
                bool failSave = false;
                TeachingData initial = new TeachingData
                {
                    Left = new TeachingPose
                    {
                        Side = TeachingSide.Left,
                        IsValid = true,
                        ImageRow = 88.0,
                        ImageColumn = 99.0,
                        ImageAngleDegrees = 0.5,
                        MatchScore = 0.91,
                        MachineX = 500.0,
                        MachineY = 200.0,
                        MachineR = 0.0
                    }
                };
                using (TeachingService teachingService = new TeachingService(
                    unitSdk,
                    ct => Task.FromResult(new CameraFrame(new Bitmap(64, 64, System.Drawing.Imaging.PixelFormat.Format24bppRgb), 1, DateTime.UtcNow)),
                    () => CreateJobWithDummyTemplateAndSample("Pos", "P"),
                    data => failSave ? Task.FromException(new IOException("Disk full")) : Task.CompletedTask,
                    initial))
                {
                    failSave = true;
                    bool threwSaveFailed = false;
                    try
                    {
                        await teachingService.CaptureAsync(TeachingSide.Left, new MachinePose(610.0, 220.0, 1.0), 1000, cancellationToken).ConfigureAwait(false);
                    }
                    catch (TeachingWorkflowException ex)
                    {
                        threwSaveFailed = ex.ErrorCode == ProtocolErrorCodes.TeachingSaveFailed;
                    }
                    Assert(threwSaveFailed, "Save failure must throw TeachingWorkflowException(TEACHING_SAVE_FAILED).");
                    TeachingPose preserved = teachingService.GetSnapshot().Left;
                    Assert(preserved.IsValid && Math.Abs(preserved.MachineX - 500.0) < 1e-6 && Math.Abs(preserved.ImageRow - 88.0) < 1e-6,
                        "Old teaching data must be preserved when persistence fails.");
                }
            }

            // 2. 端到端 TCP 验证：teaching.submit / teaching.result、system.status 顺序推送、错误保留旧示教，及未就绪 COORDINATE_NOT_MAPPED
            string tempDir = Path.Combine(Path.GetTempPath(), "nprcal-teach-selftest-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            int port = ReserveLoopbackPort();
            StationOptions options = new StationOptions
            {
                TcpBindAddress = "127.0.0.1",
                TcpPort = port,
                TcpMaxMessageBytes = 64 * 1024,
                CameraEnabled = true,
                CalibrationEnabled = false,
                InspectionEnabled = true,
                LiveAcquisitionEnabled = false,
                CameraTimeoutMilliseconds = 500,
                HeartbeatTimeoutMilliseconds = 6000
            };
            options.ApplyConfigDirectory(tempDir);
            string optionsPath = Path.Combine(tempDir, "station.json");

            using (NinePointRotationCalibrationSdk prepSdk = new NinePointRotationCalibrationSdk())
            {
                CalibrationJob leftJob = CreateValid9Plus5CalibrationJob("Calibration-Left", 100.0, 50.0);
                CalibrationJob rightJob = CreateValid9Plus5CalibrationJob("Calibration-Right", 122.0, 50.0);
                CalibrationJob posJob = CreateJobWithDummyTemplateAndSample("Positioning", "POS");
                posJob.Template.ModelHash = TemplateDefinition.ComputeModelHash(posJob.Template.ModelData);
                posJob.Template.ReferenceImageWidth = 256;
                posJob.Template.ReferenceImageHeight = 256;
                posJob.Template.ReferenceAnchor = new ImagePoint(100.0, 100.0);
                posJob.Template.DetectionRoi = new RegionDefinition
                {
                    Kind = RegionKind.Rectangle2,
                    CenterRow = 100.0,
                    CenterColumn = 100.0,
                    PhiRadians = 0.0,
                    Length1 = 16.0,
                    Length2 = 16.0
                };
                Assert(prepSdk.TrySaveJob(options.CalibrationJobPath, leftJob).Success, "Failed to seed left calibration job.");
                Assert(prepSdk.TrySaveJob(options.RightCalibrationJobPath, rightJob).Success, "Failed to seed right calibration job.");
                Assert(prepSdk.TrySaveJob(options.PositioningJobPath, posJob).Success, "Failed to seed positioning job.");
            }

            FakeSelfTestCamera camera = new FakeSelfTestCamera();
            FakeSelfTestTemplateEngine fakeEngine = new FakeSelfTestTemplateEngine();
            using (CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            using (StationRuntime runtime = StationRuntime.CreateForSelfTest(optionsPath, options, camera, fakeEngine))
            using (TcpClient client = new TcpClient(AddressFamily.InterNetwork))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(15));
                try
                {
                    Assert(runtime.IsLeftCalibrationValid && runtime.IsRightCalibrationValid,
                        "Seeded 9+5 calibration files must validate on runtime startup.");
                    await runtime.StartAsync(timeout.Token).ConfigureAwait(false);
                    await client.ConnectAsync(IPAddress.Loopback, port).ConfigureAwait(false);
                    NetworkStream stream = client.GetStream();

                    ProtocolEnvelope hello = ProtocolEnvelope.Create(ProtocolMessageTypes.Hello);
                    hello.Data["role"] = "host";
                    await WriteEnvelopeAsync(stream, codec, hello, timeout.Token).ConfigureAwait(false);
                    ProtocolEnvelope ack = await ReadEnvelopeAsync(stream, codec, options.TcpMaxMessageBytes, timeout.Token).ConfigureAwait(false);
                    bool readyFlag;
                    Assert(ack.Type == ProtocolMessageTypes.Acknowledge &&
                           ProtocolValue.TryBool(ack.Data, "calibrationReady", out readyFlag) && !readyFlag,
                        "Initial calibrationReady must be false before left/right teaching.");

                    // A. 标定未启用且有 2 块板 -> inspection.result 必为 COORDINATE_NOT_MAPPED, pass=false, valid=false, targets=[]
                    fakeEngine.NextCollectionResult = new TemplateMatchCollectionResult
                    {
                        Success = true,
                        ErrorCode = CalibrationErrorCode.None,
                        Matches = new List<TemplateMatchResult>
                        {
                            new TemplateMatchResult { Success = true, Row = 1000.0, Column = 1000.0, AngleDegrees = 0.0, Score = 0.95 },
                            new TemplateMatchResult { Success = true, Row = 1000.0, Column = 1100.0, AngleDegrees = 0.0, Score = 0.94 }
                        }
                    };
                    ProtocolEnvelope trigUnmapped = ProtocolEnvelope.Create(ProtocolMessageTypes.InspectionTrigger);
                    trigUnmapped.Data["expectedCount"] = 2;
                    await WriteEnvelopeAsync(stream, codec, trigUnmapped, timeout.Token).ConfigureAwait(false);
                    ProtocolEnvelope resUnmapped = await ReadEnvelopeAsync(stream, codec, options.TcpMaxMessageBytes, timeout.Token).ConfigureAwait(false);
                    Assert(resUnmapped.Type == ProtocolMessageTypes.InspectionResult &&
                           ProtocolValue.String(resUnmapped.Data, "errorCode") == ProtocolErrorCodes.CoordinateNotMapped,
                        "Expected COORDINATE_NOT_MAPPED when calibration is disabled and boards exist.");
                    bool resSuccess;
                    bool resPass;
                    bool resMapped;
                    Assert(ProtocolValue.TryBool(resUnmapped.Data, "success", out resSuccess) && !resSuccess &&
                           ProtocolValue.TryBool(resUnmapped.Data, "pass", out resPass) && !resPass &&
                           ProtocolValue.TryBool(resUnmapped.Data, "coordinateMapped", out resMapped) && !resMapped,
                        "COORDINATE_NOT_MAPPED must set success=false, pass=false, coordinateMapped=false.");
                    System.Collections.IList unmappedItems = resUnmapped.Data["items"] as System.Collections.IList;
                    Assert(unmappedItems != null && unmappedItems.Count == 2, "Expected 2 items in COORDINATE_NOT_MAPPED.");
                    foreach (object rawItem in unmappedItems)
                    {
                        IDictionary<string, object> itemDict = rawItem as IDictionary<string, object>;
                        bool itemValid;
                        Assert(itemDict != null && ProtocolValue.TryBool(itemDict, "valid", out itemValid) && !itemValid,
                            "COORDINATE_NOT_MAPPED items must mark valid=false.");
                        System.Collections.IList targets = itemDict["targets"] as System.Collections.IList;
                        Assert(targets != null && targets.Count == 0, "COORDINATE_NOT_MAPPED items must have targets=[].");
                    }

                    // B. 标定关闭时发送 teaching.submit -> system.error / CALIBRATION_DISABLED
                    ProtocolEnvelope teachDisabled = ProtocolEnvelope.Create(ProtocolMessageTypes.TeachingSubmit);
                    teachDisabled.Data["nozzle"] = "Left";
                    teachDisabled.Data["axisR"] = "R1";
                    teachDisabled.Data["x"] = 650.120;
                    teachDisabled.Data["y"] = 210.450;
                    teachDisabled.Data["r"] = 0.0;
                    await WriteEnvelopeAsync(stream, codec, teachDisabled, timeout.Token).ConfigureAwait(false);
                    ProtocolEnvelope errTeachDisabled = await ReadEnvelopeAsync(stream, codec, options.TcpMaxMessageBytes, timeout.Token).ConfigureAwait(false);
                    Assert(errTeachDisabled.Type == ProtocolMessageTypes.Error &&
                           ProtocolValue.String(errTeachDisabled.Data, "code") == ProtocolErrorCodes.CalibrationDisabled,
                        "teaching.submit with calibration disabled must return CALIBRATION_DISABLED.");

                    // 开启标定（消费主动推送的 system.status）
                    await runtime.ApplyEnablementAsync(true, true, true, timeout.Token).ConfigureAwait(false);
                    ProtocolEnvelope statusEnabled = await ReadEnvelopeAsync(stream, codec, options.TcpMaxMessageBytes, timeout.Token).ConfigureAwait(false);
                    Assert(statusEnabled.Type == ProtocolMessageTypes.SystemStatus, "Expected system.status after enabling calibration.");

                    // C. nozzle 与 axisR 冲突 -> system.error / PROTOCOL_INVALID
                    ProtocolEnvelope teachConflict = ProtocolEnvelope.Create(ProtocolMessageTypes.TeachingSubmit);
                    teachConflict.Data["nozzle"] = "Left";
                    teachConflict.Data["axisR"] = "R2";
                    teachConflict.Data["x"] = 650.120;
                    teachConflict.Data["y"] = 210.450;
                    teachConflict.Data["r"] = 0.0;
                    await WriteEnvelopeAsync(stream, codec, teachConflict, timeout.Token).ConfigureAwait(false);
                    ProtocolEnvelope errTeachConflict = await ReadEnvelopeAsync(stream, codec, options.TcpMaxMessageBytes, timeout.Token).ConfigureAwait(false);
                    Assert(errTeachConflict.Type == ProtocolMessageTypes.Error &&
                           ProtocolValue.String(errTeachConflict.Data, "code") == ProtocolErrorCodes.ProtocolInvalid,
                        "Conflicting nozzle/axisR must return PROTOCOL_INVALID.");

                    // D. 左吸嘴 (Left/R1) teaching.submit 成功
                    fakeEngine.NextCollectionResult = new TemplateMatchCollectionResult
                    {
                        Success = true,
                        ErrorCode = CalibrationErrorCode.None,
                        Matches = new List<TemplateMatchResult>
                        {
                            new TemplateMatchResult { Success = true, Row = 1024.154, Column = 1280.424, AngleDegrees = 0.124, Score = 0.9524 }
                        }
                    };
                    ProtocolEnvelope teachLeft = ProtocolEnvelope.Create(ProtocolMessageTypes.TeachingSubmit);
                    teachLeft.Data["nozzle"] = "Left";
                    teachLeft.Data["axisR"] = "R1";
                    teachLeft.Data["x"] = 650.1204;
                    teachLeft.Data["y"] = 210.4506;
                    teachLeft.Data["r"] = -0.0001;
                    await WriteEnvelopeAsync(stream, codec, teachLeft, timeout.Token).ConfigureAwait(false);
                    ProtocolEnvelope teachLeftRes = await ReadEnvelopeAsync(stream, codec, options.TcpMaxMessageBytes, timeout.Token).ConfigureAwait(false);
                    Assert(teachLeftRes.Type == ProtocolMessageTypes.TeachingResult &&
                           teachLeftRes.CorrelationId == teachLeft.MessageId,
                        "Expected teaching.result correlated to Left teaching.submit.");
                    bool teachOk;
                    Assert(ProtocolValue.TryBool(teachLeftRes.Data, "success", out teachOk) && teachOk &&
                           ProtocolValue.String(teachLeftRes.Data, "errorCode") == ProtocolErrorCodes.None &&
                           ProtocolValue.String(teachLeftRes.Data, "nozzle") == "Left" &&
                           ProtocolValue.String(teachLeftRes.Data, "axisR") == "R1" &&
                           Math.Abs(ProtocolValue.Double(teachLeftRes.Data, "machineX") - 650.12) < 1e-6 &&
                           Math.Abs(ProtocolValue.Double(teachLeftRes.Data, "machineY") - 210.451) < 1e-6 &&
                           Math.Abs(ProtocolValue.Double(teachLeftRes.Data, "imageRow") - 1024.15) < 1e-6 &&
                           Math.Abs(ProtocolValue.Double(teachLeftRes.Data, "imageColumn") - 1280.42) < 1e-6 &&
                           Math.Abs(ProtocolValue.Double(teachLeftRes.Data, "imageAngle") - 0.12) < 1e-6 &&
                           Math.Abs(ProtocolValue.Double(teachLeftRes.Data, "score") - 0.952) < 1e-6,
                        "Left teaching.result fields or rounding mismatch.");
                    Assert(!runtime.IsCalibrationReady, "calibrationReady must stay false before Right teaching completes.");

                    // E. 左吸嘴采图失败 / 匹配失败 -> 返回对应 errorCode 且保留旧示教数据
                    camera.FailCapture = true;
                    ProtocolEnvelope teachCapFail = ProtocolEnvelope.Create(ProtocolMessageTypes.TeachingSubmit);
                    teachCapFail.Data["nozzle"] = "Left";
                    teachCapFail.Data["axisR"] = "R1";
                    teachCapFail.Data["x"] = 999.0;
                    teachCapFail.Data["y"] = 999.0;
                    teachCapFail.Data["r"] = 5.0;
                    await WriteEnvelopeAsync(stream, codec, teachCapFail, timeout.Token).ConfigureAwait(false);
                    ProtocolEnvelope teachCapFailRes = await ReadEnvelopeAsync(stream, codec, options.TcpMaxMessageBytes, timeout.Token).ConfigureAwait(false);
                    Assert(teachCapFailRes.Type == ProtocolMessageTypes.TeachingResult &&
                           ProtocolValue.TryBool(teachCapFailRes.Data, "success", out teachOk) && !teachOk &&
                           ProtocolValue.String(teachCapFailRes.Data, "errorCode") == ProtocolErrorCodes.CameraCaptureFailed,
                        "Expected teaching.result / CAMERA_CAPTURE_FAILED.");
                    Assert(Math.Abs(runtime.GetTeachingSnapshot().Left.MachineX - 650.1204) < 1e-6,
                        "Old Left teaching must remain intact after CAMERA_CAPTURE_FAILED.");

                    camera.FailCapture = false;
                    fakeEngine.NextCollectionResult = TemplateMatchCollectionResult.NoMatch("No board.");
                    ProtocolEnvelope teachMatchFail = ProtocolEnvelope.Create(ProtocolMessageTypes.TeachingSubmit);
                    teachMatchFail.Data["nozzle"] = "Left";
                    teachMatchFail.Data["axisR"] = "R1";
                    teachMatchFail.Data["x"] = 888.0;
                    teachMatchFail.Data["y"] = 888.0;
                    teachMatchFail.Data["r"] = 3.0;
                    await WriteEnvelopeAsync(stream, codec, teachMatchFail, timeout.Token).ConfigureAwait(false);
                    ProtocolEnvelope teachMatchFailRes = await ReadEnvelopeAsync(stream, codec, options.TcpMaxMessageBytes, timeout.Token).ConfigureAwait(false);
                    Assert(teachMatchFailRes.Type == ProtocolMessageTypes.TeachingResult &&
                           ProtocolValue.TryBool(teachMatchFailRes.Data, "success", out teachOk) && !teachOk &&
                           ProtocolValue.String(teachMatchFailRes.Data, "errorCode") == ProtocolErrorCodes.MatchFailed,
                        "Expected teaching.result / MATCH_FAILED.");
                    Assert(Math.Abs(runtime.GetTeachingSnapshot().Left.MachineX - 650.1204) < 1e-6,
                        "Old Left teaching must remain intact after MATCH_FAILED.");

                    // F. 右吸嘴 (Right/R2) teaching.submit 成功 -> 先收到 teaching.result，再收到 system.status(calibrationReady=true)
                    fakeEngine.NextCollectionResult = new TemplateMatchCollectionResult
                    {
                        Success = true,
                        ErrorCode = CalibrationErrorCode.None,
                        Matches = new List<TemplateMatchResult>
                        {
                            new TemplateMatchResult { Success = true, Row = 1024.15, Column = 1280.42, AngleDegrees = 0.12, Score = 0.948 }
                        }
                    };
                    ProtocolEnvelope teachRight = ProtocolEnvelope.Create(ProtocolMessageTypes.TeachingSubmit);
                    teachRight.Data["nozzle"] = "Right";
                    teachRight.Data["axisR"] = "R2";
                    teachRight.Data["x"] = 672.340;
                    teachRight.Data["y"] = 210.450;
                    teachRight.Data["r"] = 0.0;
                    await WriteEnvelopeAsync(stream, codec, teachRight, timeout.Token).ConfigureAwait(false);
                    ProtocolEnvelope teachRightRes = await ReadEnvelopeAsync(stream, codec, options.TcpMaxMessageBytes, timeout.Token).ConfigureAwait(false);
                    Assert(teachRightRes.Type == ProtocolMessageTypes.TeachingResult &&
                           ProtocolValue.TryBool(teachRightRes.Data, "success", out teachOk) && teachOk &&
                           ProtocolValue.String(teachRightRes.Data, "nozzle") == "Right" &&
                           ProtocolValue.String(teachRightRes.Data, "axisR") == "R2",
                        "teaching.result must arrive before system.status push when Right teaching completes.");

                    ProtocolEnvelope statusReady = await ReadEnvelopeAsync(stream, codec, options.TcpMaxMessageBytes, timeout.Token).ConfigureAwait(false);
                    bool calReadyAfter;
                    Assert(statusReady.Type == ProtocolMessageTypes.SystemStatus &&
                           ProtocolValue.TryBool(statusReady.Data, "calibrationReady", out calReadyAfter) && calReadyAfter,
                        "Expected system.status(calibrationReady=true) immediately after Right teaching.result.");

                    await runtime.StopAsync().ConfigureAwait(false);
                }
                finally
                {
                    try { Directory.Delete(tempDir, true); } catch { }
                }
            }
        }

        private static CalibrationJob CreateValid9Plus5CalibrationJob(string name, double originX, double originY)
        {
            CalibrationJob job = CreateJobWithDummyTemplateAndSample(name, "P1");
            job.Template.ModelHash = TemplateDefinition.ComputeModelHash(job.Template.ModelData);
            job.Samples.Clear();
            IReadOnlyList<CalibrationPlanPoint> plan = CalibrationPlan.Create(10.0, 10.0, 6.0);
            foreach (CalibrationPlanPoint pt in plan)
            {
                double sx = originX + pt.OffsetX;
                double sy = originY + pt.OffsetY;
                double sr = pt.OffsetR;
                double row;
                double col;
                double imgAngle;
                if (pt.Kind == CalibrationSampleKind.Translation)
                {
                    col = 1000.0 + (10.0 * pt.OffsetX);
                    row = 1000.0 - (10.0 * pt.OffsetY);
                    imgAngle = 0.0;
                }
                else
                {
                    double rad = pt.OffsetR * Math.PI / 180.0;
                    col = 1000.0 + (50.0 * Math.Cos(rad));
                    row = 1000.0 - (50.0 * Math.Sin(rad));
                    imgAngle = pt.OffsetR;
                }

                job.Samples.Add(new CalibrationSample
                {
                    SampleId = Guid.NewGuid().ToString("N"),
                    Tag = pt.Tag,
                    Kind = pt.Kind,
                    Enabled = true,
                    TemplateRevision = 1,
                    MachinePose = new MachinePose(sx, sy, sr),
                    ImagePose = new ImagePose(row, col, imgAngle),
                    MatchScore = 0.99
                });
            }
            return job;
        }

        private static CalibrationResult CreateSyntheticCalibration(
            double directionMultiplier,
            double rotCenterOffsetX,
            double rotCenterOffsetY)
        {
            return new CalibrationResult
            {
                Success = true,
                TranslationSuccess = true,
                ErrorCode = CalibrationErrorCode.None,
                PixelToStageMatrix = new AffineMatrix2D(1.0, 0.0, 0.0, 0.0, -1.0, 0.0),
                StageToPixelMatrix = new AffineMatrix2D(1.0, 0.0, 0.0, 0.0, -1.0, 0.0),
                ResolutionX = 1.0,
                ResolutionY = 1.0,
                Rotation = new RotationCalibrationResult
                {
                    IsAvailable = true,
                    Success = true,
                    Direction = directionMultiplier > 0 ? RotationDirection.Same : RotationDirection.Opposite,
                    ReferenceStage = new StagePoint(100.0, 50.0),
                    RotationCenterStage = new StagePoint(100.0 + rotCenterOffsetX, 50.0 + rotCenterOffsetY)
                }
            };
        }

        private static CalibrationJob CreateJobWithDummyTemplateAndSample(string name, string sampleTag)
        {
            CalibrationJob job = new CalibrationJob
            {
                Name = name,
                Template = new TemplateDefinition
                {
                    Revision = 1,
                    ModelType = TemplateModelType.Shape,
                    ModelData = new byte[] { 1, 2, 3, 4 },
                    ModelHash = "dummy-hash",
                    IsModelDirty = false
                }
            };
            job.Samples.Add(new CalibrationSample
            {
                SampleId = Guid.NewGuid().ToString("N"),
                Tag = sampleTag,
                Kind = CalibrationSampleKind.Translation,
                Enabled = true,
                TemplateRevision = 1,
                MachinePose = new MachinePose(100.0, 50.0, 0.0),
                ImagePose = new ImagePose(200.0, 300.0, 0.0),
                MatchScore = 0.99
            });
            return job;
        }

        private static ProtocolEnvelope CreateMessage(string messageId, int sequence)
        {
            ProtocolEnvelope envelope = ProtocolEnvelope.Create(ProtocolMessageTypes.Heartbeat);
            envelope.MessageId = messageId;
            envelope.Data["sequence"] = sequence;
            return envelope;
        }

        private static int ReserveLoopbackPort()
        {
            TcpListener reservation = new TcpListener(IPAddress.Loopback, 0);
            reservation.Start();
            try
            {
                return ((IPEndPoint)reservation.LocalEndpoint).Port;
            }
            finally
            {
                reservation.Stop();
            }
        }

        private static async Task WriteEnvelopeAsync(
            Stream stream,
            ProtocolJsonCodec codec,
            ProtocolEnvelope envelope,
            CancellationToken cancellationToken)
        {
            byte[] frame = CreateFrame(codec.Serialize(envelope));
            await stream.WriteAsync(frame, 0, frame.Length, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        private static async Task<ProtocolEnvelope> ReadEnvelopeAsync(
            Stream stream,
            ProtocolJsonCodec codec,
            int maxBytes,
            CancellationToken cancellationToken)
        {
            byte[] header = new byte[TcpVisionServer.FrameHeaderSize];
            await ReadExactAsync(stream, header, cancellationToken).ConfigureAwait(false);
            int length = ParseFrameLength(header);
            Assert(length > 0 && length <= maxBytes, "Invalid frame length received: " + length);
            byte[] payload = new byte[length];
            await ReadExactAsync(stream, payload, cancellationToken).ConfigureAwait(false);
            return codec.Deserialize(payload);
        }

        private static byte[] CreateFrame(byte[] payload)
        {
            byte[] frame = new byte[TcpVisionServer.FrameHeaderSize + payload.Length];
            frame[0] = (byte)((payload.Length >> 24) & 0xff);
            frame[1] = (byte)((payload.Length >> 16) & 0xff);
            frame[2] = (byte)((payload.Length >> 8) & 0xff);
            frame[3] = (byte)(payload.Length & 0xff);
            Buffer.BlockCopy(payload, 0, frame, TcpVisionServer.FrameHeaderSize, payload.Length);
            return frame;
        }

        private static int ParseFrameLength(byte[] header)
        {
            uint length = ((uint)header[0] << 24)
                | ((uint)header[1] << 16)
                | ((uint)header[2] << 8)
                | header[3];
            if (length > int.MaxValue) throw new InvalidDataException("Frame length exceeds Int32.MaxValue.");
            return (int)length;
        }

        private static async Task ReadExactAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
        {
            int offset = 0;
            while (offset < buffer.Length)
            {
                int read = await stream.ReadAsync(
                    buffer,
                    offset,
                    buffer.Length - offset,
                    cancellationToken).ConfigureAwait(false);
                if (read == 0) throw new EndOfStreamException("Stream ended during a self-test frame.");
                offset += read;
            }
        }

        private static async Task AwaitAsync(Task task, CancellationToken cancellationToken)
        {
            Task canceled = Task.Delay(Timeout.Infinite, cancellationToken);
            Task completed = await Task.WhenAny(task, canceled).ConfigureAwait(false);
            if (completed == canceled) cancellationToken.ThrowIfCancellationRequested();
            await task.ConfigureAwait(false);
        }

        private static async Task<T> AwaitAsync<T>(Task<T> task, CancellationToken cancellationToken)
        {
            Task canceled = Task.Delay(Timeout.Infinite, cancellationToken);
            Task completed = await Task.WhenAny(task, canceled).ConfigureAwait(false);
            if (completed == canceled) cancellationToken.ThrowIfCancellationRequested();
            return await task.ConfigureAwait(false);
        }

        private static TaskCompletionSource<T> NewCompletionSource<T>()
        {
            return new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Communication self-test failed: " + message);
        }

        private sealed class UnsupportedPayload
        {
            public int Value { get; set; }
        }

        private sealed class FakeSelfTestCamera : ICameraDevice
        {
            private long _sequence;

            public string ProviderName { get { return "SelfTestCamera"; } }
            public bool IsConnected { get; private set; }
            public string ConnectedDeviceId { get; private set; }
            public bool FailCapture { get; set; }

            public Task<IReadOnlyList<CameraDescriptor>> EnumerateAsync(CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                IReadOnlyList<CameraDescriptor> list = new[]
                {
                    new CameraDescriptor { Id = "SELFTEST-01", DisplayName = "SelfTest Camera" }
                };
                return Task.FromResult(list);
            }

            public Task ConnectAsync(string deviceId, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                IsConnected = true;
                ConnectedDeviceId = string.IsNullOrWhiteSpace(deviceId) ? "SELFTEST-01" : deviceId;
                return Task.CompletedTask;
            }

            public Task DisconnectAsync()
            {
                IsConnected = false;
                ConnectedDeviceId = string.Empty;
                return Task.CompletedTask;
            }

            public Task SetExposureAsync(double exposureMicroseconds, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            }

            public Task<CameraFrame> CaptureAsync(CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (FailCapture) throw new IOException("Simulated camera capture failure.");
                Bitmap bmp = new Bitmap(64, 64, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
                return Task.FromResult(new CameraFrame(bmp, Interlocked.Increment(ref _sequence), DateTime.UtcNow));
            }

            public void Dispose()
            {
                IsConnected = false;
                ConnectedDeviceId = string.Empty;
            }
        }

        private sealed class FakeSelfTestTemplateEngine : ITemplateMatchingEngine
        {
            public TemplateMatchCollectionResult NextCollectionResult { get; set; }

            public TemplateBuildResult BuildTemplate(Bitmap referenceImage, TemplateDefinition definition)
            {
                throw new NotSupportedException();
            }

            public TemplateMatchResult Locate(Bitmap image, TemplateDefinition definition, bool returnOverlay)
            {
                if (NextCollectionResult != null && NextCollectionResult.Matches != null && NextCollectionResult.Matches.Count > 0)
                    return NextCollectionResult.Matches[0];
                return TemplateMatchResult.Failure(CalibrationErrorCode.TemplateMatchFailed, "No match.");
            }

            public TemplateMatchCollectionResult LocateAll(Bitmap image, TemplateDefinition definition, bool returnOverlay)
            {
                return NextCollectionResult ?? TemplateMatchCollectionResult.NoMatch("No match.");
            }

            public TemplateRegionPreviewResult PreviewEffectiveRegion(TemplateDefinition definition, int imageWidth, int imageHeight)
            {
                throw new NotSupportedException();
            }

            public void ClearCache()
            {
            }

            public void Dispose()
            {
            }
        }
    }
}
