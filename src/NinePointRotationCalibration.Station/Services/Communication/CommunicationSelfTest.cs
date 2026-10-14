using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using NinePointRotationCalibration.Station.Models;

namespace NinePointRotationCalibration.Station.Services.Communication
{
    public static class CommunicationSelfTest
    {
        public static void Run()
        {
            RunAsync(CancellationToken.None).GetAwaiter().GetResult();
        }

        public static async Task RunAsync(CancellationToken cancellationToken)
        {
            ProtocolJsonCodec codec = new ProtocolJsonCodec();
            VerifyCodec(codec);
            await VerifyLengthFramingAsync(codec, cancellationToken).ConfigureAwait(false);
            await VerifyConnectionScopedReconnectAsync(codec, cancellationToken).ConfigureAwait(false);
        }

        private static void VerifyCodec(ProtocolJsonCodec codec)
        {
            ProtocolEnvelope outgoing = CreateMessage("self-test-roundtrip", 17);
            outgoing.Data["items"] = new List<object>
            {
                new Dictionary<string, object>
                {
                    { "boardIndex", 1 },
                    { "pass", true }
                },
                null
            };
            outgoing.Data["labels"] = new[] { "left", "right" };

            string json = codec.SerializeToString(outgoing);
            Assert(json.Contains("\"version\""), "Wire JSON must contain camelCase version.");
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
                byte[] header = new byte[TcpVisionServer.FrameHeaderSize];
                await ReadExactAsync(stream, header, timeout.Token).ConfigureAwait(false);
                int payloadLength = ParseFrameLength(header);
                Assert(payloadLength > 0 && payloadLength <= options.TcpMaxMessageBytes, "Server emitted an invalid frame length.");
                byte[] payload = new byte[payloadLength];
                await ReadExactAsync(stream, payload, timeout.Token).ConfigureAwait(false);
                ProtocolEnvelope receivedReply = codec.Deserialize(payload);
                Assert(receivedReply.MessageId == reply.MessageId, "Server response frame changed its payload.");

                await server.StopAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }

        private static async Task VerifyConnectionScopedReconnectAsync(
            ProtocolJsonCodec codec,
            CancellationToken cancellationToken)
        {
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

                byte[] oldRequest = CreateFrame(codec.Serialize(CreateMessage("old-connection-request", 1)));
                NetworkStream oldStream = oldClient.GetStream();
                await oldStream.WriteAsync(oldRequest, 0, oldRequest.Length, timeout.Token).ConfigureAwait(false);
                await oldStream.FlushAsync(timeout.Token).ConfigureAwait(false);
                string inboundConnectionId = await AwaitAsync(
                    oldRequestReceived.Task,
                    timeout.Token).ConfigureAwait(false);
                Assert(
                    string.Equals(oldConnectionId, inboundConnectionId, StringComparison.Ordinal),
                    "Inbound protocol messages must expose their exact connection ID.");

                oldClient.Close();
                string disconnectedConnectionId = await AwaitAsync(
                    oldDisconnected.Task,
                    timeout.Token).ConfigureAwait(false);
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

                NetworkStream newStream = newClient.GetStream();
                byte[] header = new byte[TcpVisionServer.FrameHeaderSize];
                await ReadExactAsync(newStream, header, timeout.Token).ConfigureAwait(false);
                byte[] payload = new byte[ParseFrameLength(header)];
                await ReadExactAsync(newStream, payload, timeout.Token).ConfigureAwait(false);
                ProtocolEnvelope delivered = codec.Deserialize(payload);
                Assert(
                    string.Equals(delivered.MessageId, newResult.MessageId, StringComparison.Ordinal),
                    "A result for the old client leaked onto the replacement connection.");

                await server.StopAsync(CancellationToken.None).ConfigureAwait(false);
            }
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
    }
}
