using System.Collections.Concurrent;
using System.Net.Sockets;

namespace SocketAsyncServer;

public sealed class SocketAsyncEventArgsPool
{
    ConcurrentQueue<SocketAsyncEventArgs> queue;

    public SocketAsyncEventArgsPool(int capacity)
    {
        queue = new ConcurrentQueue<SocketAsyncEventArgs>();
    }

    public SocketAsyncEventArgs Pop()
    {
        SocketAsyncEventArgs args;
        if (queue.TryDequeue(out args))
        {
            return args;
        }
        return null;
    }
    public void Push(SocketAsyncEventArgs item)
    {
        if (item == null) 
        { 
            throw new ArgumentNullException("Items added to a SocketAsyncEventArgsPool cannot be null"); 
        }
        queue.Enqueue(item);
    }
}
