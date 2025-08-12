using System.Net;

namespace SocketAsyncServer;

static class Program
{
    private const int DEFAULT_PORT = 9900, DEFAULT_MAX_NUM_CONNECTIONS = 1000, DEFAULT_BUFFER_SIZE = 60000;

    static void Main()
    {
        try
        {
            SocketListener listener = new(DEFAULT_MAX_NUM_CONNECTIONS, DEFAULT_BUFFER_SIZE);
            listener.Start(new IPEndPoint(IPAddress.Parse("127.0.0.1"), DEFAULT_PORT));

            Console.WriteLine("Server listening on port {0}. Press any key to terminate the server process...", DEFAULT_PORT);
            Console.Read();

            listener.Stop();
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex.ToString());
        }
    }
}
