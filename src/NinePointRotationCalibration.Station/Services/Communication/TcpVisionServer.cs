using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using NinePointRotationCalibration.Station.Models;

namespace NinePointRotationCalibration.Station.Services.Communication
{
    /// <summary>TCP 上位机连接建立事件参数。</summary>
    public sealed class TcpConnectionEventArgs : EventArgs
    {
        public TcpConnectionEventArgs(string remoteEndPoint)
            : this(string.Empty, remoteEndPoint)
        {
        }

        public TcpConnectionEventArgs(string connectionId, string remoteEndPoint)
        {
            ConnectionId = connectionId ?? string.Empty;
            RemoteEndPoint = remoteEndPoint ?? string.Empty;
        }

        public string ConnectionId { get; private set; }
        public string RemoteEndPoint { get; private set; }
    }

    /// <summary>TCP 上位机连接断开事件参数。</summary>
    public sealed class TcpDisconnectedEventArgs : EventArgs
    {
        public TcpDisconnectedEventArgs(string remoteEndPoint, string reason, Exception exception)
            : this(string.Empty, remoteEndPoint, reason, exception)
        {
        }

        public TcpDisconnectedEventArgs(
            string connectionId,
            string remoteEndPoint,
            string reason,
            Exception exception)
        {
            ConnectionId = connectionId ?? string.Empty;
            RemoteEndPoint = remoteEndPoint ?? string.Empty;
            Reason = reason ?? string.Empty;
            Exception = exception;
        }

        public string ConnectionId { get; private set; }
        public string RemoteEndPoint { get; private set; }
        public string Reason { get; private set; }
        public Exception Exception { get; private set; }
    }

    /// <summary>接收到上位机协议消息的事件参数（附带源连接 ID）。</summary>
    public sealed class ProtocolMessageEventArgs : EventArgs
    {
        public ProtocolMessageEventArgs(ProtocolEnvelope message, string remoteEndPoint)
            : this(message, string.Empty, remoteEndPoint)
        {
        }

        public ProtocolMessageEventArgs(
            ProtocolEnvelope message,
            string connectionId,
            string remoteEndPoint)
        {
            Message = message ?? throw new ArgumentNullException(nameof(message));
            ConnectionId = connectionId ?? string.Empty;
            RemoteEndPoint = remoteEndPoint ?? string.Empty;
        }

        public ProtocolEnvelope Message { get; private set; }
        public ProtocolEnvelope Envelope { get { return Message; } }
        public string ConnectionId { get; private set; }
        public string RemoteEndPoint { get; private set; }
    }

    /// <summary>单客户端长度前缀 TCP 协议服务端（4 字节大端长度头 + UTF-8 JSON 负载）。</summary>
    public sealed class TcpVisionServer : IDisposable
    {
        public const int FrameHeaderSize = 4;

        private readonly object _stateSync = new object();
        private readonly string _bindAddress;
        private readonly int _port;
        private readonly int _maxMessageBytes;
        private readonly ProtocolJsonCodec _codec;
        private readonly SemaphoreSlim _lifecycleGate = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim _sendGate = new SemaphoreSlim(1, 1);
        private readonly List<Task> _receiveTasks = new List<Task>();

        private TcpListener _listener;
        private CancellationTokenSource _lifetimeCancellation;
        private Task _acceptLoopTask;
        private ClientConnection _activeConnection;
        private bool _running;
        private bool _disposed;

        public TcpVisionServer(StationOptions options)
            : this(options, null)
        {
        }

        public TcpVisionServer(StationOptions options, ProtocolJsonCodec codec)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            if (options.TcpPort < 1 || options.TcpPort > 65535)
            {
                throw new ArgumentOutOfRangeException(nameof(options), "TCP port must be between 1 and 65535.");
            }

            if (options.TcpMaxMessageBytes < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(options), "TCP maximum message size must be positive.");
            }

            _bindAddress = options.TcpBindAddress;
            _port = options.TcpPort;
            _maxMessageBytes = options.TcpMaxMessageBytes;
            _codec = codec ?? new ProtocolJsonCodec();
        }

        public event EventHandler<TcpConnectionEventArgs> Connected;
        public event EventHandler<TcpDisconnectedEventArgs> Disconnected;
        public event EventHandler<ProtocolMessageEventArgs> MessageReceived;
        public event EventHandler<LogEventArgs> Log;

        public bool IsRunning
        {
            get
            {
                lock (_stateSync) return _running;
            }
        }

        public bool IsClientConnected
        {
            get
            {
                lock (_stateSync) return _activeConnection != null;
            }
        }

        public string ActiveConnectionId
        {
            get
            {
                lock (_stateSync)
                {
                    return _activeConnection == null ? null : _activeConnection.ConnectionId;
                }
            }
        }

        public int MaxMessageBytes { get { return _maxMessageBytes; } }

        public IPEndPoint LocalEndPoint
        {
            get
            {
                lock (_stateSync)
                {
                    return _listener == null ? null : _listener.LocalEndpoint as IPEndPoint;
                }
            }
        }

        /// <summary>启动 TCP 监听并开始接受上位机连接。</summary>
        public Task StartAsync()
        {
            return StartAsync(CancellationToken.None);
        }

        /// <summary>启动 TCP 监听并开始接受上位机连接。</summary>
        public async Task StartAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            IPEndPoint startedEndPoint = null;

            try
            {
                ThrowIfDisposed();
                lock (_stateSync)
                {
                    if (_running) return;
                }

                IPAddress address = ResolveBindAddress(_bindAddress);
                TcpListener listener = new TcpListener(address, _port);
                CancellationTokenSource lifetime = new CancellationTokenSource();

                try
                {
                    listener.Start(8);
                    startedEndPoint = listener.LocalEndpoint as IPEndPoint;
                }
                catch
                {
                    listener.Stop();
                    lifetime.Dispose();
                    throw;
                }

                lock (_stateSync)
                {
                    _listener = listener;
                    _lifetimeCancellation = lifetime;
                    _running = true;
                    _acceptLoopTask = AcceptLoopAsync(listener, lifetime.Token);
                }
            }
            finally
            {
                _lifecycleGate.Release();
            }

            RaiseLog("INFO", "TCP vision server listening on " + startedEndPoint + ".");
        }

        /// <summary>停止 TCP 监听并关闭当前客户端连接。</summary>
        public Task StopAsync()
        {
            return StopAsync(CancellationToken.None);
        }

        /// <summary>停止 TCP 监听，关闭连接并等待后台收发任务退出。</summary>
        public async Task StopAsync(CancellationToken cancellationToken)
        {
            await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            TcpListener listener;
            CancellationTokenSource lifetime;
            Task acceptLoop;
            ClientConnection connection;

            try
            {
                lock (_stateSync)
                {
                    if (!_running) return;
                    _running = false;
                    listener = _listener;
                    lifetime = _lifetimeCancellation;
                    acceptLoop = _acceptLoopTask;
                    connection = _activeConnection;
                }

                try
                {
                    if (listener != null) listener.Stop();
                }
                catch (SocketException)
                {
                    // 监听套接字已关闭。
                }

                CloseConnection(connection, "Server stopped.", null);
                if (lifetime != null) lifetime.Cancel();

                await IgnoreExpectedShutdownAsync(acceptLoop).ConfigureAwait(false);

                Task[] receiveTasks;
                lock (_stateSync) receiveTasks = _receiveTasks.ToArray();
                await IgnoreExpectedShutdownAsync(Task.WhenAll(receiveTasks)).ConfigureAwait(false);

                lock (_stateSync)
                {
                    if (ReferenceEquals(_listener, listener)) _listener = null;
                    if (ReferenceEquals(_lifetimeCancellation, lifetime)) _lifetimeCancellation = null;
                    if (ReferenceEquals(_acceptLoopTask, acceptLoop)) _acceptLoopTask = null;
                }

                if (lifetime != null) lifetime.Dispose();
            }
            finally
            {
                _lifecycleGate.Release();
            }

            RaiseLog("INFO", "TCP vision server stopped.");
        }

        /// <summary>向当前活跃的上位机客户端发送协议消息。</summary>
        public Task SendAsync(ProtocolEnvelope message)
        {
            return SendAsync(message, CancellationToken.None);
        }

        /// <summary>向当前活跃的上位机客户端发送协议消息（支持取消）。</summary>
        public Task SendAsync(ProtocolEnvelope message, CancellationToken cancellationToken)
        {
            return SendCoreAsync(null, false, message, cancellationToken);
        }

        /// <summary>向指定连接 ID 的上位机客户端发送协议消息，连接已变更时拒绝发送。</summary>
        public Task SendAsync(string connectionId, ProtocolEnvelope message)
        {
            return SendAsync(connectionId, message, CancellationToken.None);
        }

        /// <summary>向指定连接 ID 的上位机客户端发送协议消息，防止旧连接结果串发至重连新连接。</summary>
        public Task SendAsync(
            string connectionId,
            ProtocolEnvelope message,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(connectionId))
                throw new ArgumentException("A target connection ID is required.", nameof(connectionId));
            return SendCoreAsync(connectionId, true, message, cancellationToken);
        }

        private async Task SendCoreAsync(
            string connectionId,
            bool requireTarget,
            ProtocolEnvelope message,
            CancellationToken cancellationToken)
        {
            if (message == null) throw new ArgumentNullException(nameof(message));
            ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();

            byte[] payload = _codec.Serialize(message);
            if (payload.Length > _maxMessageBytes)
            {
                throw new InvalidDataException(
                    "Protocol message contains " + payload.Length + " bytes; the configured maximum is " +
                    _maxMessageBytes + " bytes.");
            }

            byte[] header = CreateFrameHeader(payload.Length);
            await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            ClientConnection connection = null;

            try
            {
                lock (_stateSync)
                {
                    if (!_running) throw new InvalidOperationException("TCP vision server is not running.");
                    ClientConnection active = _activeConnection;
                    if (active == null) throw new InvalidOperationException("No upper-computer client is connected.");
                    if (requireTarget && !string.Equals(
                        active.ConnectionId,
                        connectionId,
                        StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException(
                            "The target TCP connection is no longer active: " + connectionId + ".");
                    }

                    connection = active;
                }

                // 将长度头与负载合并为单次 WriteAsync 发送，避免 NoDelay 下产生独立小包。
                byte[] frame = new byte[header.Length + payload.Length];
                Buffer.BlockCopy(header, 0, frame, 0, header.Length);
                Buffer.BlockCopy(payload, 0, frame, header.Length, payload.Length);
                await connection.Stream.WriteAsync(frame, 0, frame.Length, cancellationToken).ConfigureAwait(false);
                await connection.Stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                if (connection != null)
                {
                    CloseConnection(connection, "Message send was canceled; the partially written stream was closed.", null);
                }

                throw;
            }
            catch (Exception exception)
            {
                if (connection != null) CloseConnection(connection, "Message send failed.", exception);
                if (exception is InvalidOperationException || exception is InvalidDataException) throw;
                throw new IOException("Failed to send a TCP protocol message.", exception);
            }
            finally
            {
                _sendGate.Release();
            }
        }

        /// <summary>停止 TCP 监听并释放全部底层网络与同步资源。</summary>
        public void Dispose()
        {
            lock (_stateSync)
            {
                if (_disposed) return;
                _disposed = true;
            }

            StopAsync(CancellationToken.None).GetAwaiter().GetResult();
        }

        private async Task AcceptLoopAsync(TcpListener listener, CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync().ConfigureAwait(false);
                }
                catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested || !IsRunning)
                {
                    break;
                }
                catch (SocketException exception) when (cancellationToken.IsCancellationRequested || !IsRunning)
                {
                    RaiseLog("DEBUG", "TCP accept loop stopped: " + exception.Message);
                    break;
                }
                catch (SocketException exception)
                {
                    RaiseLog("WARN", "TCP accept failed and will be retried: " + exception.Message);
                    try
                    {
                        await Task.Delay(250, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }

                    continue;
                }

                ConfigureClient(client);
                ClientConnection connection = new ClientConnection(client, cancellationToken);
                bool accepted;

                // 本服务仅维持单个上位机主连接，已有活跃连接时直接拒绝多余客户端
                lock (_stateSync)
                {
                    accepted = _running && _activeConnection == null;
                    if (accepted) _activeConnection = connection;
                }

                if (!accepted)
                {
                    string rejectedEndPoint = connection.RemoteEndPoint;
                    connection.Dispose();
                    RaiseLog("WARN", "Rejected additional TCP client " + rejectedEndPoint + "; one client is already active.");
                    continue;
                }

                RaiseConnected(connection.ConnectionId, connection.RemoteEndPoint);
                Task receiveTask = ReceiveLoopAsync(connection);
                TrackReceiveTask(receiveTask);
            }
        }

        private async Task ReceiveLoopAsync(ClientConnection connection)
        {
            string reason = "Remote client closed the connection.";
            Exception error = null;

            try
            {
                byte[] header = new byte[FrameHeaderSize];
                while (!connection.Token.IsCancellationRequested)
                {
                    bool hasFrame = await ReadExactAsync(
                        connection.Stream,
                        header,
                        header.Length,
                        true,
                        connection.Token).ConfigureAwait(false);
                    if (!hasFrame) break;

                    int payloadLength = ParseFrameLength(header);
                    if (payloadLength < 1 || payloadLength > _maxMessageBytes)
                    {
                        throw new InvalidDataException(
                            "Protocol frame length " + payloadLength + " is outside the allowed range 1.." +
                            _maxMessageBytes + ".");
                    }

                    byte[] payload = new byte[payloadLength];
                    await ReadExactAsync(
                        connection.Stream,
                        payload,
                        payload.Length,
                        false,
                        connection.Token).ConfigureAwait(false);

                    ProtocolEnvelope message = _codec.Deserialize(payload);
                    RaiseMessageReceived(message, connection.ConnectionId, connection.RemoteEndPoint);
                }
            }
            catch (OperationCanceledException) when (connection.Token.IsCancellationRequested)
            {
                reason = "Connection canceled.";
            }
            catch (IOException exception) when (connection.Token.IsCancellationRequested)
            {
                reason = "Connection closed.";
                error = exception;
            }
            catch (ObjectDisposedException) when (connection.Token.IsCancellationRequested)
            {
                reason = "Connection closed.";
            }
            catch (Exception exception)
            {
                reason = "Connection terminated because of a protocol or transport error.";
                error = exception;
                RaiseLog("ERROR", reason + " " + exception.Message);
            }
            finally
            {
                CloseConnection(connection, reason, error);
            }
        }

        private static async Task<bool> ReadExactAsync(
            Stream stream,
            byte[] buffer,
            int count,
            bool allowCleanEndOfStream,
            CancellationToken cancellationToken)
        {
            int offset = 0;
            while (offset < count)
            {
                int read = await stream.ReadAsync(buffer, offset, count - offset, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    if (offset == 0 && allowCleanEndOfStream) return false;
                    throw new EndOfStreamException("TCP stream ended in the middle of a protocol frame.");
                }

                offset += read;
            }

            return true;
        }

        private void CloseConnection(ClientConnection connection, string reason, Exception exception)
        {
            // 通过原子标记确保每条客户端连接仅释放并触发一次 Disconnected 事件
            if (connection == null || !connection.TryMarkClosed()) return;

            lock (_stateSync)
            {
                if (ReferenceEquals(_activeConnection, connection)) _activeConnection = null;
            }

            connection.Dispose();
            RaiseDisconnected(connection.ConnectionId, connection.RemoteEndPoint, reason, exception);
        }

        private void TrackReceiveTask(Task receiveTask)
        {
            lock (_stateSync) _receiveTasks.Add(receiveTask);
            receiveTask.ContinueWith(
                completedTask =>
                {
                    lock (_stateSync) _receiveTasks.Remove(completedTask);
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        private static byte[] CreateFrameHeader(int length)
        {
            return new[]
            {
                (byte)((length >> 24) & 0xff),
                (byte)((length >> 16) & 0xff),
                (byte)((length >> 8) & 0xff),
                (byte)(length & 0xff)
            };
        }

        private static int ParseFrameLength(byte[] header)
        {
            uint length = ((uint)header[0] << 24)
                | ((uint)header[1] << 16)
                | ((uint)header[2] << 8)
                | header[3];
            return length > int.MaxValue ? int.MaxValue : (int)length;
        }

        private static IPAddress ResolveBindAddress(string address)
        {
            if (string.IsNullOrWhiteSpace(address) || address == "*" || address == "0.0.0.0")
            {
                return IPAddress.Any;
            }

            if (string.Equals(address, "localhost", StringComparison.OrdinalIgnoreCase))
            {
                return IPAddress.Loopback;
            }

            IPAddress parsed;
            if (IPAddress.TryParse(address, out parsed)) return parsed;

            IPAddress[] resolved = Dns.GetHostAddresses(address);
            IPAddress selected = resolved.FirstOrDefault(item => item.AddressFamily == AddressFamily.InterNetwork)
                ?? resolved.FirstOrDefault();
            if (selected == null)
            {
                throw new ArgumentException("TCP bind address could not be resolved: " + address, nameof(address));
            }

            return selected;
        }

        private static void ConfigureClient(TcpClient client)
        {
            client.NoDelay = true;
            client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
        }

        private static async Task IgnoreExpectedShutdownAsync(Task task)
        {
            if (task == null) return;
            try
            {
                await task.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        }

        private void ThrowIfDisposed()
        {
            lock (_stateSync)
            {
                if (_disposed) throw new ObjectDisposedException(nameof(TcpVisionServer));
            }
        }

        private void RaiseConnected(string connectionId, string remoteEndPoint)
        {
            EventHandler<TcpConnectionEventArgs> handlers = Connected;
            if (handlers == null) return;
            TcpConnectionEventArgs args = new TcpConnectionEventArgs(connectionId, remoteEndPoint);
            foreach (EventHandler<TcpConnectionEventArgs> handler in handlers.GetInvocationList())
            {
                try
                {
                    handler(this, args);
                }
                catch (Exception exception)
                {
                    RaiseLog("ERROR", "Connected event handler failed: " + exception.Message);
                }
            }
        }

        private void RaiseDisconnected(
            string connectionId,
            string remoteEndPoint,
            string reason,
            Exception exception)
        {
            EventHandler<TcpDisconnectedEventArgs> handlers = Disconnected;
            if (handlers == null) return;
            TcpDisconnectedEventArgs args = new TcpDisconnectedEventArgs(
                connectionId,
                remoteEndPoint,
                reason,
                exception);
            foreach (EventHandler<TcpDisconnectedEventArgs> handler in handlers.GetInvocationList())
            {
                try
                {
                    handler(this, args);
                }
                catch (Exception handlerException)
                {
                    RaiseLog("ERROR", "Disconnected event handler failed: " + handlerException.Message);
                }
            }
        }

        private void RaiseMessageReceived(
            ProtocolEnvelope message,
            string connectionId,
            string remoteEndPoint)
        {
            EventHandler<ProtocolMessageEventArgs> handlers = MessageReceived;
            if (handlers == null) return;
            ProtocolMessageEventArgs args = new ProtocolMessageEventArgs(message, connectionId, remoteEndPoint);
            foreach (EventHandler<ProtocolMessageEventArgs> handler in handlers.GetInvocationList())
            {
                try
                {
                    handler(this, args);
                }
                catch (Exception exception)
                {
                    RaiseLog("ERROR", "MessageReceived event handler failed: " + exception.Message);
                }
            }
        }

        private void RaiseLog(string level, string message)
        {
            EventHandler<LogEventArgs> handlers = Log;
            if (handlers == null) return;
            LogEventArgs args = new LogEventArgs(level, message);
            foreach (EventHandler<LogEventArgs> handler in handlers.GetInvocationList())
            {
                try
                {
                    handler(this, args);
                }
                catch
                {
                    // 日志回调异常不得中断底层网络收发循环。
                }
            }
        }

        private sealed class ClientConnection : IDisposable
        {
            private readonly CancellationTokenSource _cancellation;
            private readonly CancellationToken _token;
            private int _closed;

            public ClientConnection(TcpClient client, CancellationToken serverToken)
            {
                Client = client ?? throw new ArgumentNullException(nameof(client));
                ConnectionId = Guid.NewGuid().ToString("N");
                Stream = client.GetStream();
                RemoteEndPoint = GetRemoteEndPoint(client);
                _cancellation = CancellationTokenSource.CreateLinkedTokenSource(serverToken);
                _token = _cancellation.Token;
            }

            public TcpClient Client { get; private set; }
            public string ConnectionId { get; private set; }
            public NetworkStream Stream { get; private set; }
            public string RemoteEndPoint { get; private set; }
            public CancellationToken Token { get { return _token; } }

            public bool TryMarkClosed()
            {
                return Interlocked.Exchange(ref _closed, 1) == 0;
            }

            public void Dispose()
            {
                try
                {
                    _cancellation.Cancel();
                }
                catch (ObjectDisposedException)
                {
                }

                try
                {
                    Stream.Dispose();
                }
                catch (ObjectDisposedException)
                {
                }

                try
                {
                    Client.Close();
                }
                catch (ObjectDisposedException)
                {
                }

                _cancellation.Dispose();
            }

            private static string GetRemoteEndPoint(TcpClient client)
            {
                try
                {
                    EndPoint endpoint = client.Client.RemoteEndPoint;
                    return endpoint == null ? string.Empty : endpoint.ToString();
                }
                catch (SocketException)
                {
                    return string.Empty;
                }
                catch (ObjectDisposedException)
                {
                    return string.Empty;
                }
            }
        }
    }
}
