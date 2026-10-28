using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NinePointRotationCalibration.Station.Models;
using NinePointRotationCalibration.Station.Services.Communication;

namespace NinePointRotationCalibration.TcpServerDemo
{
    internal static class Program
    {
        private static readonly object ConsoleSync = new object();
        private static readonly object MessageIdSync = new object();
        private static readonly Dictionary<string, HashSet<string>> SeenMessageIdsByConnection = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        private static readonly ProtocolJsonCodec Codec = new ProtocolJsonCodec();
        private static TcpVisionServer _server;
        private static string _lastMoveRequestId;
        private static int _nextPointNumber = 1;
        private static bool _autoReply = true;

        private static void Main(string[] args)
        {
            RunAsync(args ?? new string[0]).GetAwaiter().GetResult();
        }

        private static async Task RunAsync(string[] args)
        {
            string bindAddress = GetOption(args, "--bind", "0.0.0.0");
            int port = ParsePort(GetOption(args, "--port", "9100"));
            _autoReply = !HasFlag(args, "--manual");

            StationOptions options = new StationOptions
            {
                TcpBindAddress = bindAddress,
                TcpPort = port,
                TcpMaxMessageBytes = 1024 * 1024
            };

            using (CancellationTokenSource lifetime = new CancellationTokenSource())
            using (_server = new TcpVisionServer(options, Codec))
            {
                AttachHandlers(lifetime.Token);
                Console.CancelKeyPress += (sender, eventArgs) =>
                {
                    eventArgs.Cancel = true;
                    lifetime.Cancel();
                };

                try
                {
                    await _server.StartAsync(lifetime.Token).ConfigureAwait(false);
                    WriteLine("TCP 服务端 demo 已启动：" + bindAddress + ":" + port);
                    WriteLine("自动应答：" + (_autoReply ? "开启" : "关闭（仅打印收到的消息）"));
                    PrintHelp();
                    await CommandLoopAsync(lifetime.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
                finally
                {
                    await _server.StopAsync(CancellationToken.None).ConfigureAwait(false);
                    WriteLine("TCP 服务端 demo 已停止。");
                }
            }
        }

        private static void AttachHandlers(CancellationToken cancellationToken)
        {
            _server.Log += (sender, args) => WriteLine("[" + args.Level + "] " + args.Message);
            _server.Connected += (sender, args) =>
                WriteLine("[CONNECTED] " + args.RemoteEndPoint + " connectionId=" + args.ConnectionId);
            _server.Disconnected += (sender, args) =>
            {
                lock (MessageIdSync) SeenMessageIdsByConnection.Remove(args.ConnectionId);
                WriteLine("[DISCONNECTED] " + args.RemoteEndPoint + " reason=" + args.Reason);
            };
            _server.MessageReceived += (sender, args) =>
            {
                WriteLine("[RECEIVED] " + FormatEnvelope(args.Message));
                _ = HandleMessageSafeAsync(args.ConnectionId, args.Message, cancellationToken);
            };
        }

        private static async Task HandleMessageSafeAsync(
            string connectionId,
            ProtocolEnvelope message,
            CancellationToken cancellationToken)
        {
            try
            {
                await HandleMessageAsync(connectionId, message, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exception)
            {
                WriteLine("[AUTO-REPLY ERROR] " + exception.Message);
            }
        }

        private static async Task HandleMessageAsync(
            string connectionId,
            ProtocolEnvelope message,
            CancellationToken cancellationToken)
        {
            if (!_autoReply) return;

            if (string.IsNullOrWhiteSpace(message.MessageId) || !TryRememberMessageId(connectionId, message.MessageId))
            {
                await SendErrorAsync(connectionId, message.MessageId, "DUPLICATE_MESSAGE", "messageId 在当前连接内重复。", false, cancellationToken).ConfigureAwait(false);
                return;
            }

            switch ((message.Type ?? string.Empty).ToLowerInvariant())
            {
                case ProtocolMessageTypes.Hello:
                case ProtocolMessageTypes.Heartbeat:
                    await SendAckAsync(connectionId, message, cancellationToken).ConfigureAwait(false);
                    break;
                case ProtocolMessageTypes.InspectionTrigger:
                    await HandleInspectionTriggerAsync(connectionId, message, cancellationToken).ConfigureAwait(false);
                    break;
                case ProtocolMessageTypes.MotionArrived:
                    await HandleMotionArrivedAsync(connectionId, message, cancellationToken).ConfigureAwait(false);
                    break;
                default:
                    await SendErrorAsync(connectionId, message.MessageId, "MESSAGE_TYPE_UNSUPPORTED", "demo 未实现此消息类型。", false, cancellationToken).ConfigureAwait(false);
                    break;
            }
        }

        private static bool TryRememberMessageId(string connectionId, string messageId)
        {
            if (string.IsNullOrWhiteSpace(messageId)) return false;
            lock (MessageIdSync)
            {
                HashSet<string> ids;
                if (!SeenMessageIdsByConnection.TryGetValue(connectionId, out ids))
                {
                    ids = new HashSet<string>(StringComparer.Ordinal);
                    SeenMessageIdsByConnection[connectionId] = ids;
                }

                return ids.Add(messageId);
            }
        }

        private static Task SendAckAsync(string connectionId, ProtocolEnvelope request, CancellationToken cancellationToken)
        {
            ProtocolEnvelope ack = ProtocolEnvelope.Create(ProtocolMessageTypes.Acknowledge);
            ack.CorrelationId = request.MessageId;
            ack.Data["role"] = "vision";
            ack.Data["ready"] = false;
            ack.Data["protocolVersion"] = "1.0";
            return SendAsync(connectionId, ack, cancellationToken);
        }

        private static async Task HandleInspectionTriggerAsync(
            string connectionId,
            ProtocolEnvelope request,
            CancellationToken cancellationToken)
        {
            double currentR;
            object rawCurrentR;
            if (!request.Data.TryGetValue("currentR", out rawCurrentR) ||
                !double.TryParse(Convert.ToString(rawCurrentR, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out currentR) ||
                double.IsNaN(currentR) || double.IsInfinity(currentR))
            {
                await SendErrorAsync(connectionId, request.MessageId, "PROTOCOL_INVALID", "inspection.trigger.currentR 必须是有限数字。", false, cancellationToken).ConfigureAwait(false);
                return;
            }

            int expectedCount = ReadInt(request.Data, "expectedCount", 6);
            ProtocolEnvelope result = ProtocolEnvelope.Create(ProtocolMessageTypes.InspectionResult);
            result.CorrelationId = request.MessageId;
            result.Data["executed"] = true;
            result.Data["success"] = true;
            result.Data["pass"] = true;
            result.Data["errorCode"] = "NONE";
            result.Data["message"] = "TCP demo synthetic inspection result.";
            result.Data["expectedCount"] = expectedCount;
            result.Data["actualCount"] = expectedCount;
            result.Data["hasManualMarks"] = false;
            result.Data["currentR"] = currentR;
            result.Data["elapsedMilliseconds"] = 1.0;
            result.Data["matchElapsedMilliseconds"] = 0.5;
            result.Data["blobElapsedMilliseconds"] = 0.5;
            result.Data["items"] = new List<object>();
            await SendAsync(connectionId, result, cancellationToken).ConfigureAwait(false);
        }

        private static Task HandleMotionArrivedAsync(
            string connectionId,
            ProtocolEnvelope request,
            CancellationToken cancellationToken)
        {
            ProtocolEnvelope result = ProtocolEnvelope.Create(ProtocolMessageTypes.CalibrationPointResult);
            result.CorrelationId = string.IsNullOrWhiteSpace(request.CorrelationId)
                ? _lastMoveRequestId
                : request.CorrelationId;
            result.Data["pointNumber"] = ReadInt(request.Data, "pointNumber", _nextPointNumber);
            result.Data["success"] = true;
            result.Data["status"] = "Completed";
            result.Data["machineX"] = ReadDouble(request.Data, "x", 100.0);
            result.Data["machineY"] = ReadDouble(request.Data, "y", 50.0);
            result.Data["machineR"] = ReadDouble(request.Data, "r", 0.0);
            result.Data["imageRow"] = 1000.0;
            result.Data["imageColumn"] = 800.0;
            result.Data["imageAngle"] = 0.0;
            result.Data["score"] = 0.99;
            result.Data["message"] = "TCP demo synthetic calibration point.";
            return SendAsync(connectionId, result, cancellationToken);
        }

        private static async Task CommandLoopAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                string command = await Task.Run(() => Console.ReadLine(), cancellationToken).ConfigureAwait(false);
                if (command == null) break;
                command = command.Trim();
                if (command.Length == 0) continue;

                try
                {
                    if (command.Equals("q", StringComparison.OrdinalIgnoreCase) || command.Equals("quit", StringComparison.OrdinalIgnoreCase)) break;
                    if (command.Equals("help", StringComparison.OrdinalIgnoreCase) || command == "?") { PrintHelp(); continue; }
                    if (command.Equals("auto", StringComparison.OrdinalIgnoreCase)) { _autoReply = true; WriteLine("自动应答已开启。"); continue; }
                    if (command.Equals("manual", StringComparison.OrdinalIgnoreCase)) { _autoReply = false; WriteLine("自动应答已关闭。"); continue; }
                    if (command.Equals("test", StringComparison.OrdinalIgnoreCase)) { await CommunicationSelfTest.RunAsync(cancellationToken).ConfigureAwait(false); WriteLine("项目自带通信自测通过。"); continue; }
                    await ExecuteSendCommandAsync(command, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    WriteLine("[COMMAND ERROR] " + exception.Message);
                }
            }
        }

        private static async Task ExecuteSendCommandAsync(string command, CancellationToken cancellationToken)
        {
            string[] parts = command.Split(new[] { ' ' }, 2, StringSplitOptions.RemoveEmptyEntries);
            string key = parts[0].ToLowerInvariant();
            ProtocolEnvelope message;
            switch (key)
            {
                case "hello":
                    message = ProtocolEnvelope.Create(ProtocolMessageTypes.Hello);
                    message.Data["role"] = "vision";
                    message.Data["station"] = "tcp-demo";
                    message.Data["capabilities"] = new[] { "calibration-9x5", "pcb-inspection" };
                    break;
                case "heartbeat":
                    message = ProtocolEnvelope.Create(ProtocolMessageTypes.Heartbeat);
                    break;
                case "move":
                    message = ProtocolEnvelope.Create(ProtocolMessageTypes.MoveRequest);
                    message.Data["pointNumber"] = _nextPointNumber;
                    message.Data["kind"] = _nextPointNumber <= 9 ? "translation" : "rotation";
                    message.Data["coordinateMode"] = "calibration-origin-relative";
                    message.Data["offsetX"] = _nextPointNumber <= 9 ? (_nextPointNumber - 5) * 10.0 : 0.0;
                    message.Data["offsetY"] = _nextPointNumber <= 9 ? (_nextPointNumber - 5) * 10.0 : 0.0;
                    message.Data["offsetR"] = _nextPointNumber <= 9 ? 0.0 : (_nextPointNumber - 12) * 5.0;
                    message.Data["sharedOrigin"] = _nextPointNumber == 5 || _nextPointNumber == 12;
                    message.Data["replyType"] = ProtocolMessageTypes.MotionArrived;
                    _lastMoveRequestId = message.MessageId;
                    _nextPointNumber = _nextPointNumber == 14 ? 1 : _nextPointNumber + 1;
                    break;
                case "inspect":
                    message = ProtocolEnvelope.Create(ProtocolMessageTypes.InspectionTrigger);
                    message.Data["currentR"] = parts.Length > 1 ? ParseDouble(parts[1]) : 12.5;
                    message.Data["expectedCount"] = 6;
                    message.Data["archiveImage"] = false;
                    break;
                case "completed":
                    message = ProtocolEnvelope.Create(ProtocolMessageTypes.CalibrationCompleted);
                    message.Data["success"] = true;
                    message.Data["errorCode"] = "NONE";
                    message.Data["message"] = "TCP demo calibration completed.";
                    message.Data["resolutionX"] = 0.01;
                    message.Data["resolutionY"] = 0.01;
                    break;
                case "error":
                    message = ProtocolEnvelope.Create(ProtocolMessageTypes.Error);
                    message.Data["code"] = parts.Length > 1 ? parts[1] : "BUSY";
                    message.Data["message"] = "TCP demo synthetic error.";
                    message.Data["retryable"] = true;
                    break;
                default:
                    throw new InvalidOperationException("命令不支持。输入 help 查看命令。");
            }

            await SendAsync(null, message, cancellationToken).ConfigureAwait(false);
        }

        private static Task SendAsync(string connectionId, ProtocolEnvelope message, CancellationToken cancellationToken)
        {
            Task send = string.IsNullOrWhiteSpace(connectionId)
                ? _server.SendAsync(message, cancellationToken)
                : _server.SendAsync(connectionId, message, cancellationToken);
            return LogSendAsync(send, message);
        }

        private static async Task LogSendAsync(Task send, ProtocolEnvelope message)
        {
            await send.ConfigureAwait(false);
            WriteLine("[SENT] " + FormatEnvelope(message));
        }

        private static Task SendErrorAsync(string connectionId, string correlationId, string code, string text, bool retryable, CancellationToken cancellationToken)
        {
            ProtocolEnvelope error = ProtocolEnvelope.Create(ProtocolMessageTypes.Error);
            error.CorrelationId = correlationId;
            error.Data["code"] = code;
            error.Data["message"] = text;
            error.Data["retryable"] = retryable;
            return SendAsync(connectionId, error, cancellationToken);
        }

        private static void PrintHelp()
        {
            WriteLine("命令：hello | heartbeat | move | inspect [R] | completed | error [CODE] | test | auto | manual | help | quit");
            WriteLine("说明：客户端连入后可自动应答 hello/heartbeat/inspection.trigger/motion.arrived；move 会主动请求客户端运动。");
        }

        private static string FormatEnvelope(ProtocolEnvelope message)
        {
            return message.Type + " messageId=" + message.MessageId + " correlationId=" + (message.CorrelationId ?? "") + " data=" + Codec.SerializeToString(message);
        }

        private static int ReadInt(IDictionary<string, object> data, string key, int fallback)
        {
            object value;
            int result;
            return data != null && data.TryGetValue(key, out value) && int.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out result) ? result : fallback;
        }

        private static double ReadDouble(IDictionary<string, object> data, string key, double fallback)
        {
            object value;
            double result;
            return data != null && data.TryGetValue(key, out value) && double.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out result) ? result : fallback;
        }

        private static double ParseDouble(string value)
        {
            double result;
            if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out result)) throw new FormatException("R 必须是数字。");
            return result;
        }

        private static int ParsePort(string value)
        {
            int port;
            if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out port) || port < 1 || port > 65535) throw new ArgumentException("端口必须在 1..65535 范围内。", "--port");
            return port;
        }

        private static string GetOption(string[] args, string name, string fallback)
        {
            for (int index = 0; index + 1 < args.Length; index++) if (string.Equals(args[index], name, StringComparison.OrdinalIgnoreCase)) return args[index + 1];
            return fallback;
        }

        private static bool HasFlag(string[] args, string flag)
        {
            return args.Any(item => string.Equals(item, flag, StringComparison.OrdinalIgnoreCase));
        }

        private static void WriteLine(string text)
        {
            lock (ConsoleSync) Console.WriteLine(DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) + " " + text);
        }
    }
}
