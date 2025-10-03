using System;
using System.Collections.Concurrent;
using System.Net;
using System.Threading;

namespace KcpSharp;

internal class KcpConnectionManager
{
    private readonly KcpListener _server;
    private int _increasedKcpConv = 0;
    private readonly ConcurrentDictionary<uint, KcpConnection> _connections = new ConcurrentDictionary<uint, KcpConnection>();
    
    public KcpConnectionManager(KcpListener server)
    {
        _server = server;
    }

    public bool TryGetConnection(uint id, out KcpConnection connection)
        => _connections.TryGetValue(id, out connection);

    public ServerKcpConnection AllocConnection(Guid guid, IPEndPoint remote)
    {
        while (true)
        {
            var conv = (uint)Interlocked.Increment(ref _increasedKcpConv);
            var connection = new ServerKcpConnection(_server, guid, conv, remote);
            if (_connections.TryAdd(conv, connection))
            {
                return connection;
            }
        }
    }

    public void RemoveConnection(KcpConnection connection)
    {
        _connections.TryRemove(connection.Conv, out _);
        Console.WriteLine($"移除连接: {connection.Conv}");
    }
}