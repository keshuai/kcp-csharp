using System;
using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace KcpSharp;

public class KcpListener
{
    private KcpConnectionManager _connectionManager;
    private Socket _udpSocket;
    
    public Action<KcpListener, KcpConnection> OnClientConnected;
    
    private readonly ushort _listenPort;
    
    public KcpListener(ushort listenPort)
    {
        _connectionManager = new KcpConnectionManager(this);
        _listenPort = listenPort;
        _udpSocket = CreateUdpSocket(_listenPort);
        Console.WriteLine($"KcpListener listen on port: {listenPort}");
    }

    public void RunAsync()
    {
        Task.Run(this.ReceiveUdpDataLoopAsync);
    }

    private async void ReceiveUdpDataLoopAsync()
    {
        var buffer = new ArraySegment<byte>(new byte[2048]);
        var anyRemote = new IPEndPoint(IPAddress.IPv6Any, 0);
        while (true)
        {
            try
            {
                var result = await _udpSocket.ReceiveFromAsync(buffer, SocketFlags.None, anyRemote);
            
                var data = buffer.Slice(0, result.ReceivedBytes);
                var remote = result.RemoteEndPoint as IPEndPoint;
                if (remote == null)
                {
                    continue;
                }
            
                this.OnReceivedUdpData(data, remote);
            }
            catch (Exception e)
            {
                Console.WriteLine($"ReceiveUdpDataLoopAsync: {e.Message}");
            }
        }
    }

    internal void OutputUdpData(ArraySegment<byte> data, IPEndPoint remote)
    {
        _udpSocket.SendToAsync(data, SocketFlags.None, remote);
    }

    private void OnReceivedUdpData(ArraySegment<byte> data, IPEndPoint remote)
    {
        var cmd = data[0];

        if (cmd == 0)
        {
            this.OnReceiveKcpData(data.Slice(4), remote);
        }
        else if (cmd == 1)
        {
            this.OnReceiveKcpConnect(data.Slice(4), remote);
        }
        else
        {
            Console.WriteLine($"OnReceivedUdpData cmd: {cmd}");
        }
    }

    private void OnReceiveKcpConnect(ArraySegment<byte> data, IPEndPoint remote)
    {
        var guid = new Guid(data);
        var connection = _connectionManager.AllocConnection(guid, remote);
        connection.KcpUpdateLoopAsync();
        this.OnClientConnected?.Invoke(this, connection);
        
        var buffer = new ArraySegment<byte>(new byte[8]);
        buffer[0] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(4, 4), connection.Conv);
        this.OutputUdpData(buffer, remote);
    }
    
    private void OnReceiveKcpData(ArraySegment<byte> data, IPEndPoint remote)
    {
        var conv = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(0, 4));

        if (this._connectionManager.TryGetConnection(conv, out KcpConnection connection))
        {
            connection.InputKcpData(data, remote);
        }
        else
        {
            Console.WriteLine($"OnReceiveKcpData, conv not found: {conv}");
            var buffer = new ArraySegment<byte>(new byte[8]);
            buffer[0] = 2;
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(4, 4), conv);
            this.OutputUdpData(buffer, remote);
        }
    }

    internal void RemoveConnection(KcpConnection connection)
    {
        _connectionManager.RemoveConnection(connection);
    }
    
    private static Socket CreateUdpSocket(ushort listenPort)
    {
        Socket socket;
        
        if (HasIPv6())
        {
            socket = new Socket(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp);
            socket.DualMode = true;
            socket.Bind(new IPEndPoint(IPAddress.IPv6Any, listenPort));
        }
        else
        {
            socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Bind(new IPEndPoint(IPAddress.Any, listenPort));
        }
        
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // Windows-specific constant for ignoring SIO_UDP_CONNRESET
            const int SIO_UDP_CONNRESET = -1744830452;
            socket.IOControl((IOControlCode)SIO_UDP_CONNRESET, new byte[] { 0, 0, 0, 0 }, null);
        }
        
        return socket;
    }


    private static bool HasIPv6()
    {
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus == OperationalStatus.Up)
            {
                foreach (var ip in ni.GetIPProperties().UnicastAddresses)
                {
                    if (ip.Address.AddressFamily == AddressFamily.InterNetworkV6)
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }
}