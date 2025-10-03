using System.Net;
using KcpSharp;

namespace KcpTestClient;

class Program
{
    static void Main(string[] args)
    {
        try
        {
            Console.WriteLine("Hello, World!");

            var remote = new IPEndPoint(IPAddress.Loopback, 12345);
            RunClientAsync(remote);
        
            Thread.CurrentThread.Join();
        }
        catch (Exception e)
        {
            Console.WriteLine(e);
        }
    }

    static async Task RunClientAsync(IPEndPoint remote)
    {
        var connection = await KcpConnection.Connect(remote);
        Console.WriteLine($"kcp Connected to {remote}, conv: {connection.Conv}");
        connection.OnReceive = (kcpConnection, bytes) =>
        {
            Console.WriteLine($"On Client received: {bytes.Length}");
        };

        var index = 0;
        while (true)
        {
            connection.SendAsync(System.Text.Encoding.UTF8.GetBytes($"hello {++index}"));
            if (index >= 5)
            {
                await connection.CloseAsync();
                Console.WriteLine("成功关闭");
                break;
            }
            
            await Task.Delay(1000);
        }
    }
}