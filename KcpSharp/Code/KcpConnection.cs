using System;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace KcpSharp;

public class KcpConnection
{
    private readonly object _kcpLock = new object();
    private KcpProject.KCP _kcp;
    private IPEndPoint _remote;
    private uint _conv;
    private Guid _guid;
    private TaskCompletionSource<bool> _closeWaiter;

    private long _lastSendDataTime;
    private long _lastReceiveDataTime;
    private int _keepLiveInterval = 30; // 心跳包30秒一次
    private int _idleTimeout = 60 * 3;  // 3分钟无数据断开连接
    
    public Action<KcpConnection, byte[]> OnReceive;
    public Action<KcpConnection> OnClose;
    
    private bool _isClosed;
    protected bool _isKcpClosed;
    
    public uint Conv => _conv;
    public Guid Guid => _guid;
    public IPEndPoint Remote => _remote;
    public bool IsClosed => _isClosed;
    internal bool IsKcpClosed => _isKcpClosed;
    
    /// 保活间隔，默认30秒，单位秒
    /// 设定0秒或负数时，不再发送保活消息
    public int KeepLiveInterval
    {
        get => _keepLiveInterval;
        set => _keepLiveInterval = System.Math.Max(value, 0);
    }

    /// 闲置断开时间 默认3分钟，单位秒
    /// 设定最低有效值5秒
    public int IdleTimeout
    {
        get => _idleTimeout;
        set => _idleTimeout = System.Math.Max(value, 10);
    }

    protected KcpConnection(Guid guid, uint conv, IPEndPoint remote)
    {
        _guid = guid;
        _conv = conv;
        _remote = remote;
        _kcp = new KcpProject.KCP(conv, KcpOutputCallback);
        _kcp.ReserveBytes(4);
        _kcp.SetMtu(1200);
        _kcp.WndSize(1024, 1024);
        _kcp.NoDelay(1, 20, 2, 1);
        
        _lastSendDataTime = _lastReceiveDataTime = DateTime.UtcNow.Ticks;
    }

    private void KcpOutputCallback(byte[] bytes, int length)
    {
        this.OutputKcpData(new ArraySegment<byte>(bytes, 0, length), _remote);
        _lastSendDataTime = DateTime.UtcNow.Ticks;
    }

    protected virtual void OnKcpClosed()
    {
    }

    protected virtual void OutputKcpData(ArraySegment<byte> data, IPEndPoint remote)
    {
    }

    public static async Task<KcpConnection> Connect(IPEndPoint remote)
    {
        var guid = Guid.NewGuid();
        var socket = new Socket(remote.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // Windows-specific constant for ignoring SIO_UDP_CONNRESET
            const int SIO_UDP_CONNRESET = -1744830452;
            socket.IOControl((IOControlCode)SIO_UDP_CONNRESET, new byte[] { 0, 0, 0, 0 }, null);
        }
        
        var buffer = new ArraySegment<byte>(new byte[4 + 16]);
        buffer[0] = 1;
        guid.TryWriteBytes(buffer.AsSpan(4));
        
        await socket.SendToAsync(buffer, SocketFlags.None, remote);
        var receiveCount = await UdpSocketReceiveFromAsync(socket, buffer, remote);
        if (receiveCount != 8)
        {
            throw new Exception($"KcpConnection.Connect: receiveCount: {receiveCount}");
        }

        var cmd = buffer[0];
        if (cmd != 1)
        {
            throw new Exception($"KcpConnection.Connect: error cmd: {cmd}");
        }

        var conv = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(4, 4));
        var connection = new ClientKcpConnection(socket, guid, conv, remote);
        connection.KcpUpdateLoopAsync();
        connection.ReceiveUdpDataLoopAsync();
        return connection;
        
        static async Task<int> UdpSocketReceiveFromAsync(Socket socket, ArraySegment<byte> buffer, IPEndPoint remote, int timeout = 3000)
        {
            using var cts = new CancellationTokenSource(timeout);
            var token = cts.Token;
            
            var receiveTask = socket.ReceiveFromAsync(buffer, SocketFlags.None, remote);
            var completedTask = await Task.WhenAny(receiveTask, Task.Delay(Timeout.Infinite, token));
                
            if (completedTask == receiveTask)
            {
                var result = await receiveTask;
                return result.ReceivedBytes;
            }
            
            //else if (completedTask.IsCanceled || token.IsCancellationRequested)
            {
                throw new TimeoutException();
            }
        }
    }
    
    /// 最好异步等待关闭
    /// 如果是客户端，且直接无等待关闭，服务段可能无法识别到客户端已关闭，就会等待idle超时后，才关闭这个连接。
    public Task CloseAsync()
    {
        if (_isClosed)
        {
            return Task.CompletedTask;
        }
        
        lock (_kcpLock)
        {
            if (_isClosed)
            {
                return Task.CompletedTask;
            }
        
            _isClosed = true;
            this.SendAsyncInternal(ArraySegment<byte>.Empty);
            _closeWaiter = new TaskCompletionSource<bool>();
            
            this.NotifyClosedEvent();
            return _closeWaiter.Task;
        }
    }

    public void SendAsync(ArraySegment<byte> data)
    {
        // 心跳直接使用kcp本身，简单实现，外部不禁用此消息
        if (data.Count == 1 && data[0] == byte.MaxValue)
        {
            throw new Exception("send message is same as keepalive message");
        }

        lock (_kcpLock)
        {
            this.SendAsyncInternal(data);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void SendAsyncInternal(ArraySegment<byte> data)
    {
        _kcp.Send(data.Array, data.Offset, data.Count);
        _kcp.Flush(false);
    }

    // public ValueTask<ArraySegment<byte>> ReceiveAsync()
    // {
    //     throw new NotImplementedException();
    // }

    internal void InputKcpData(ArraySegment<byte> data, IPEndPoint remote)
    {
        _remote = remote;
        this.InputKcpData(data);
    }
    
    internal void InputKcpData(ArraySegment<byte> data)
    {
        _lastReceiveDataTime = DateTime.UtcNow.Ticks;
        
        lock (_kcpLock)
        {
            _kcp.Input(data.Array, data.Offset, data.Count, true, false);
            this.CheckReceive();
        }
    }

    private void CheckReceive()
    {
        var peekSize = _kcp.PeekSize();
        if (peekSize < 0)
        {
            return;
        }

        if (peekSize == 0)
        {
            if (!_isClosed) // 如果不是己方主动关闭的，同样以空包通知对方我方已关闭
            {
                _isClosed = true;
                this.SendAsyncInternal(ArraySegment<byte>.Empty);
            }

            // 空包
            // this.OnReceive?.Invoke(this, Array.Empty<byte>()); // 非协程模式，无需给应用层抛空包
            this.NotifyClosedEvent();
            return;
        }
        
        var buffer = new byte[peekSize];
        var count = _kcp.Recv(buffer);
        if (count != peekSize)
        {
            throw new Exception($"receive count error {count} != {peekSize}");
        }

        // 心跳包拦截
        if (count == 1 && buffer[0] == byte.MaxValue)
        {
            return;
        }

        this.OnReceive?.Invoke(this, buffer);
    }

    protected void NotifyClosedEvent()
    {
        if (this.OnClose != null)
        {
            var onClose = this.OnClose;
            this.OnClose = null;
            onClose?.Invoke(this);
        }
    }

    internal async void KcpUpdateLoopAsync()
    {
        while (true)
        {
            try
            {
                await Task.Delay(50);

                if (_isKcpClosed)
                {
                    return;
                }

                lock (_kcpLock)
                {
                    if (_isClosed)
                    {
                        //Console.WriteLine($"kcp.WaitSnd: {_kcp.WaitSnd}");
                        if (_kcp.WaitSnd == 0)
                        {
                            _isKcpClosed = true;
                            this.OnKcpClosed();
                            
                            // notify closed success
                            if (_closeWaiter != null)
                            {
                                try
                                {
                                    var waiter = _closeWaiter;
                                    _closeWaiter = null;
                                    waiter.SetResult(true);
                                }
                                catch (Exception e)
                                {
                                    Console.WriteLine($"notify closed error {e.Message}");
                                }
                            }

                            return;
                        }
                    }
                    else
                    {
                        // 检查 限制断开连接
                        if (this.GetIdleSeconds() > _idleTimeout)
                        {
                            this.CloseAsync();
                            return;
                        }
                        // 检查保活时间
                        else if (this.GetLastSendIntervalSeconds() > _keepLiveInterval)
                        {
                            this.SendAsyncInternal(KeepAliveMessage);
                        }
                    }

                    _kcp.Update();
                }
            }
            catch (Exception e)
            {
                Console.WriteLine($"kcp update error: {e}");
            }
        }
    }
    
    private long GetIdleSeconds() =>(DateTime.UtcNow.Ticks - _lastReceiveDataTime) / 10000000;
    private long GetLastSendIntervalSeconds() => (DateTime.UtcNow.Ticks - _lastSendDataTime) / 10000000;
    private static ArraySegment<byte> KeepAliveMessage = new ArraySegment<byte>(new byte[1]{byte.MaxValue});
}

internal class ClientKcpConnection : KcpConnection
{
    private readonly Socket _socket;
    public ClientKcpConnection(Socket socket, Guid guid, uint conv, IPEndPoint remote)
        : base(guid, conv, remote)
    {
        _socket = socket;
    }

    internal async void ReceiveUdpDataLoopAsync()
    {
        var buffer = new ArraySegment<byte>(new byte[2048]);
        while (true)
        {
            try
            {
                if (this.IsKcpClosed)
                {
                    return;
                }
                
                var result = await _socket.ReceiveFromAsync(buffer, SocketFlags.None, this.Remote);
                var data = buffer.Slice(0, result.ReceivedBytes);

                var cmd = data[0];
                if (cmd == 2)
                {
                    var conv = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(4, 4));
                    if (conv == this.Conv)
                    {
                        // 服务器连接不存在
                        Console.WriteLine("服务器连接不存在");
                        _isKcpClosed = true;
                        this.NotifyClosedEvent();
                    }
                    
                    return;
                }

                this.InputKcpData(data.Slice(4));
            }
            catch (Exception e)
            {
                Console.WriteLine($"KcpConnection.ReceiveUdpDataLoopAsync: {e}");
            }
        }
    }

    protected override void OutputKcpData(ArraySegment<byte> data, IPEndPoint remote)
    {
        _socket.SendToAsync(data, SocketFlags.None, remote);
    }
}

internal class ServerKcpConnection : KcpConnection
{
    private KcpListener _server;
    public ServerKcpConnection(KcpListener server, Guid guid, uint conv, IPEndPoint remote)
     : base(guid, conv, remote)
    {
        _server = server;
    }

    protected override void OnKcpClosed()
    {
        _server.RemoveConnection(this);
    }

    protected override void OutputKcpData(ArraySegment<byte> data, IPEndPoint remote)
    {
        _server.OutputUdpData(data, remote);
    }
}