using System.Diagnostics;
using System.Net;

namespace SocketAsyncClient;

static class Program
{
    public static int _receivedMessageCount = 0;  //for testing
    public static Stopwatch _watch;  //for testing

    static void Main(string[] args)
    {
        try
        {
            int iterations = 25000;
            int clientCount = 40;
            int messageSize = 1024 * 1;
            var data = new byte[messageSize];
            var message = BuildMessage(data);

            var action = new Action<SocketClient>((client) =>
            {
                for (var i = 0; i < iterations; i++)
                {
                    client.Send(message);
                }
            });

            for (var index = 0; index < clientCount; index++)
            {
                var client = new SocketClient(new IPEndPoint(IPAddress.Parse("127.0.0.1"), 9900));
                client.Connect();
                Task.Factory.StartNew(() => action(client));
            }

            Console.WriteLine("Press any key to terminate the client process...");
            Console.Read();
        }
        catch (Exception ex)
        {
            Console.WriteLine("ERROR: " + ex.Message);
            Console.Read();
        }
    }
    static byte[] BuildMessage(byte[] data)
    {
        var header = BitConverter.GetBytes(data.Length);
        var message = new byte[header.Length + data.Length];
        header.CopyTo(message, 0);
        data.CopyTo(message, header.Length);
        return message;
    }
}
