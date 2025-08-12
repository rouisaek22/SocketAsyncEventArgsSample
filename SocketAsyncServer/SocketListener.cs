using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace SocketAsyncServer;

public sealed class SocketListener
{
    private const int MessageHeaderSize = 4;
    private int _receivedMessageCount = 0;  //for testing
    private Stopwatch _watch;  //for testing

    private BlockingCollection<MessageData> sendingQueue;
    private Thread sendMessageWorker;

    private static Mutex _mutex = new Mutex();
    private Socket _listenSocket;
    private int _bufferSize;
    private int _connectedSocketCount;
    private int _maxConnectionCount;
    private SocketAsyncEventArgsPool _socketAsyncReceiveEventArgsPool;
    private SocketAsyncEventArgsPool _socketAsyncSendEventArgsPool;
    private Semaphore _acceptedClientsSemaphore;
    private AutoResetEvent waitSendEvent;

    public SocketListener(int maxConnectionCount, int bufferSize)
    {
        _maxConnectionCount = maxConnectionCount;
        _bufferSize = bufferSize;
        _socketAsyncReceiveEventArgsPool = new SocketAsyncEventArgsPool(maxConnectionCount);
        _socketAsyncSendEventArgsPool = new SocketAsyncEventArgsPool(maxConnectionCount);
        _acceptedClientsSemaphore = new Semaphore(maxConnectionCount, maxConnectionCount);

        sendingQueue = new BlockingCollection<MessageData>();
        sendMessageWorker = new Thread(new ThreadStart(SendQueueMessage));

        for (int i = 0; i < maxConnectionCount; i++)
        {
            SocketAsyncEventArgs socketAsyncEventArgs = new SocketAsyncEventArgs();
            socketAsyncEventArgs.Completed += OnIOCompleted;
            socketAsyncEventArgs.SetBuffer(new byte[bufferSize], 0, bufferSize);
            _socketAsyncReceiveEventArgsPool.Push(socketAsyncEventArgs);
        }

        for (int i = 0; i < maxConnectionCount; i++)
        {
            SocketAsyncEventArgs socketAsyncEventArgs = new SocketAsyncEventArgs();
            socketAsyncEventArgs.Completed += OnIOCompleted;
            socketAsyncEventArgs.SetBuffer(new byte[bufferSize], 0, bufferSize);
            _socketAsyncSendEventArgsPool.Push(socketAsyncEventArgs);
        }

        waitSendEvent = new AutoResetEvent(false);
    }

    public void Start(IPEndPoint localEndPoint)
    {
        _listenSocket = new Socket(localEndPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        _listenSocket.ReceiveBufferSize = _bufferSize;
        _listenSocket.SendBufferSize = _bufferSize;
        _listenSocket.Bind(localEndPoint);
        _listenSocket.Listen(_maxConnectionCount);
        sendMessageWorker.Start();
        StartAccept(null);
        _mutex.WaitOne();
    }
    public void Stop()
    {
        try
        {
            _listenSocket.Close();
        }
        catch { }
        _mutex.ReleaseMutex();
    }

    private void OnIOCompleted(object sender, SocketAsyncEventArgs e)
    {
        switch (e.LastOperation)
        {
            case SocketAsyncOperation.Receive:
                ProcessReceive(e);
                break;
            case SocketAsyncOperation.Send:
                ProcessSend(e);
                break;
            default:
                throw new ArgumentException("The last operation completed on the socket was not a receive or send");
        }
    }
    private void StartAccept(SocketAsyncEventArgs acceptEventArg)
    {
        if (acceptEventArg == null)
        {
            acceptEventArg = new SocketAsyncEventArgs();
            acceptEventArg.Completed += (sender, e) => ProcessAccept(e);
        }
        else
        {
            acceptEventArg.AcceptSocket = null;
        }

        _acceptedClientsSemaphore.WaitOne();
        if (!_listenSocket.AcceptAsync(acceptEventArg))
        {
            ProcessAccept(acceptEventArg);
        }
    }
    private void ProcessAccept(SocketAsyncEventArgs e)
    {
        try
        {
            SocketAsyncEventArgs readEventArgs = _socketAsyncReceiveEventArgsPool.Pop();
            if (readEventArgs != null)
            {
                readEventArgs.UserToken = new AsyncUserToken(e.AcceptSocket);
                Interlocked.Increment(ref _connectedSocketCount);
                Console.WriteLine("Client connection accepted. There are {0} clients connected to the server", _connectedSocketCount);
                if (!e.AcceptSocket.ReceiveAsync(readEventArgs))
                {
                    ProcessReceive(readEventArgs);
                }
            }
            else
            {
                Console.WriteLine("There are no more available sockets to allocate.");
            }
        }
        catch (SocketException ex)
        {
            AsyncUserToken token = e.UserToken as AsyncUserToken;
            Console.WriteLine("Error when processing data received from {0}:\r\n{1}", token.Socket.RemoteEndPoint, ex.ToString());
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex.ToString());
        }

        // Accept the next connection request.
        StartAccept(e);
    }
    private void ProcessReceive(SocketAsyncEventArgs e)
    {
        if (e.BytesTransferred > 0 && e.SocketError == SocketError.Success)
        {
            AsyncUserToken token = e.UserToken as AsyncUserToken;

            //�������յ�������
            ProcessReceivedData(token.DataStartOffset, token.NextReceiveOffset - token.DataStartOffset + e.BytesTransferred, 0, token, e);

            //������һ��Ҫ�������ݵ���ʼλ��
            token.NextReceiveOffset += e.BytesTransferred;

            //����ﵽ�������Ľ�β����NextReceiveOffset��λ����������ʼλ�ã���Ǩ�ƿ�����ҪǨ�Ƶ�δ����������
            if (token.NextReceiveOffset == e.Buffer.Length)
            {
                //��NextReceiveOffset��λ����������ʼλ��
                token.NextReceiveOffset = 0;

                //�������δ���������ݣ������Щ����Ǩ�Ƶ����ݻ���������ʼλ��
                if (token.DataStartOffset < e.Buffer.Length)
                {
                    var notYesProcessDataSize = e.Buffer.Length - token.DataStartOffset;
                    Buffer.BlockCopy(e.Buffer, token.DataStartOffset, e.Buffer, 0, notYesProcessDataSize);

                    //����Ǩ�Ƶ���������ʼλ�ú���Ҫ�ٴθ���NextReceiveOffset
                    token.NextReceiveOffset = notYesProcessDataSize;
                }

                token.DataStartOffset = 0;
            }

            //���½������ݵĻ������´ν������ݵ���ʼλ�ú����ɽ������ݵĳ���
            e.SetBuffer(token.NextReceiveOffset, e.Buffer.Length - token.NextReceiveOffset);

            //���պ���������
            if (!token.Socket.ReceiveAsync(e))
            {
                ProcessReceive(e);
            }
        }
        else
        {
            CloseClientSocket(e);
        }
    }
    private void ProcessReceivedData(int dataStartOffset, int totalReceivedDataSize, int alreadyProcessedDataSize, AsyncUserToken token, SocketAsyncEventArgs e)
    {
        if (alreadyProcessedDataSize >= totalReceivedDataSize)
        {
            return;
        }

        if (token.MessageSize == null)
        {
            //���֮ǰ���յ������ݼ��ϵ�ǰ���յ������ݴ�����Ϣͷ�Ĵ�С������Խ�����Ϣͷ
            if (totalReceivedDataSize > MessageHeaderSize)
            {
                //������Ϣ����
                var headerData = new byte[MessageHeaderSize];
                Buffer.BlockCopy(e.Buffer, dataStartOffset, headerData, 0, MessageHeaderSize);
                var messageSize = BitConverter.ToInt32(headerData, 0);

                token.MessageSize = messageSize;
                token.DataStartOffset = dataStartOffset + MessageHeaderSize;

                //�ݹ鴦��
                ProcessReceivedData(token.DataStartOffset, totalReceivedDataSize, alreadyProcessedDataSize + MessageHeaderSize, token, e);
            }
            //���֮ǰ���յ������ݼ��ϵ�ǰ���յ���������Ȼû�д�����Ϣͷ�Ĵ�С������Ҫ�������պ������ֽ�
            else
            {
                //���ﲻ��Ҫ��ʲô����
            }
        }
        else
        {
            var messageSize = token.MessageSize.Value;
            //�жϵ�ǰ�ۼƽ��յ����ֽ�����ȥ�Ѿ��������ֽ����Ƿ������Ϣ�ĳ��ȣ�������ڣ���˵�����Խ�����Ϣ��
            if (totalReceivedDataSize - alreadyProcessedDataSize >= messageSize)
            {
                var messageData = new byte[messageSize];
                Buffer.BlockCopy(e.Buffer, dataStartOffset, messageData, 0, messageSize);
                ProcessMessage(messageData, token, e);

                //��Ϣ���������Ҫ����token���Ա������һ����Ϣ
                token.DataStartOffset = dataStartOffset + messageSize;
                token.MessageSize = null;

                //�ݹ鴦��
                ProcessReceivedData(token.DataStartOffset, totalReceivedDataSize, alreadyProcessedDataSize + messageSize, token, e);
            }
            //˵��ʣ�µ��ֽ���������ת��Ϊ��Ϣ������Ҫ�������պ������ֽ�
            else
            {
                //���ﲻ��Ҫ��ʲô����
            }
        }
    }
    private void ProcessMessage(byte[] messageData, AsyncUserToken token, SocketAsyncEventArgs e)
    {
        var current = Interlocked.Increment(ref _receivedMessageCount);
        if (current == 1)
        {
            _watch = Stopwatch.StartNew();
        }
        if (current % 10000 == 0)
        {
            Console.WriteLine("received message, length:{0}, count:{1}, timeSpent:{2}", messageData.Length, current, _watch.ElapsedMilliseconds);
        }
        sendingQueue.Add(new MessageData { Message = messageData, Token = token });
    }
    private void ProcessSend(SocketAsyncEventArgs e)
    {
        _socketAsyncSendEventArgsPool.Push(e);
        waitSendEvent.Set();
    }
    private void SendQueueMessage()
    {
        while (true)
        {
            var messageData = sendingQueue.Take();
            if (messageData != null)
            {
                SendMessage(messageData, BuildMessage(messageData.Message));
            }
        }
    }
    private void SendMessage(MessageData messageData, byte[] message)
    {
        var sendEventArgs = _socketAsyncSendEventArgsPool.Pop();
        if (sendEventArgs != null)
        {
            sendEventArgs.SetBuffer(message, 0, message.Length);
            sendEventArgs.UserToken = messageData.Token;
            messageData.Token.Socket.SendAsync(sendEventArgs);
        }
        else
        {
            waitSendEvent.WaitOne();
            SendMessage(messageData, message);
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
    private void CloseClientSocket(SocketAsyncEventArgs e)
    {
        var token = e.UserToken as AsyncUserToken;
        token.Dispose();
        _acceptedClientsSemaphore.Release();
        Interlocked.Decrement(ref _connectedSocketCount);
        Console.WriteLine("A client has been disconnected from the server. There are {0} clients connected to the server", _connectedSocketCount);
        _socketAsyncReceiveEventArgsPool.Push(e);
    }
}
