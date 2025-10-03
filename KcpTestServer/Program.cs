using KcpSharp;

namespace KcpTestServer;

class Program
{
    static void Main(string[] args)
    {
        Console.WriteLine("Hello, World!");

        var kcpListener = new KcpListener(12345);
        kcpListener.OnClientConnected = OnClientConnected;
        kcpListener.RunAsync();

        Thread.CurrentThread.Join();
    }

    static void OnClientConnected(KcpListener server, KcpConnection connection)
    {
        Console.WriteLine($"On Client connected: {connection.Remote}");
        connection.OnClose = kcpConnection =>
        {
            Console.WriteLine($"On Client disconnected: {kcpConnection.Remote}");
        };
        
        connection.OnReceive = (kcpConnection, bytes) =>
        {
            if (bytes.Length == 0)
            {
                Console.WriteLine($"Server receive empty");
                return;
            }

            Console.WriteLine($"Server receive: {System.Text.Encoding.UTF8.GetString(bytes, 0, bytes.Length)}");
        };
    }
}