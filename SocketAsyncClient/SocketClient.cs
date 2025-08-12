using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace SocketAsyncClient;

public sealed class SocketClient : IDisposable
{
    private int bufferSize = 60000;
    private const int MessageHeaderSize = 4;

    private Socket clientSocket;
    private bool connected = false;
    private IPEndPoint hostEndPoint;
    private AutoResetEvent autoConnectEvent;
    private AutoResetEvent autoSendEvent;
    private SocketAsyncEventArgs sendEventArgs;
    private SocketAsyncEventArgs receiveEventArgs;
    private BlockingCollection<byte[]> sendingQueue;
    private BlockingCollection<byte[]> receivedMessageQueue;
    private Thread sendMessageWorker;
    private Thread processReceivedMessageWorker;

    public SocketClient(IPEndPoint hostEndPoint)
    {
        this.hostEndPoint = hostEndPoint;
        autoConnectEvent = new AutoResetEvent(false);
        autoSendEvent = new AutoResetEvent(false);
        sendingQueue = new BlockingCollection<byte[]>();
        receivedMessageQueue = new BlockingCollection<byte[]>();
        clientSocket = new Socket(this.hostEndPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        sendMessageWorker = new Thread(new ThreadStart(SendQueueMessage));
        processReceivedMessageWorker = new Thread(new ThreadStart(ProcessReceivedMessage));

        sendEventArgs = new SocketAsyncEventArgs();
        sendEventArgs.UserToken = clientSocket;
        sendEventArgs.RemoteEndPoint = this.hostEndPoint;
        sendEventArgs.Completed += new EventHandler<SocketAsyncEventArgs>(OnSend);

        receiveEventArgs = new SocketAsyncEventArgs();
        receiveEventArgs.UserToken = new AsyncUserToken(clientSocket);
        receiveEventArgs.RemoteEndPoint = this.hostEndPoint;
        receiveEventArgs.SetBuffer(new byte[bufferSize], 0, bufferSize);
        receiveEventArgs.Completed += new EventHandler<SocketAsyncEventArgs>(OnReceive);
    }

    public void Connect()
    {
        SocketAsyncEventArgs connectArgs = new SocketAsyncEventArgs();
        connectArgs.UserToken = clientSocket;
        connectArgs.RemoteEndPoint = hostEndPoint;
        connectArgs.Completed += new EventHandler<SocketAsyncEventArgs>(OnConnect);

        clientSocket.ConnectAsync(connectArgs);
        autoConnectEvent.WaitOne();

        SocketError errorCode = connectArgs.SocketError;
        if (errorCode != SocketError.Success)
        {
            throw new SocketException((int)errorCode);
        }
        sendMessageWorker.Start();
        processReceivedMessageWorker.Start();

        if (!clientSocket.ReceiveAsync(receiveEventArgs))
        {
            ProcessReceive(receiveEventArgs);
        }
    }

    public void Disconnect()
    {
        clientSocket.Disconnect(false);
    }
    public void Send(byte[] message)
    {
        sendingQueue.Add(message);
    }

    private void OnConnect(object sender, SocketAsyncEventArgs e)
    {
        autoConnectEvent.Set();
        connected = (e.SocketError == SocketError.Success);
    }
    private void OnSend(object sender, SocketAsyncEventArgs e)
    {
        autoSendEvent.Set();
    }
    private void SendQueueMessage()
    {
        while (true)
        {
            var message = sendingQueue.Take();
            if (message != null)
            {
                sendEventArgs.SetBuffer(message, 0, message.Length);
                clientSocket.SendAsync(sendEventArgs);
                autoSendEvent.WaitOne();
            }
        }
    }

    private void OnReceive(object sender, SocketAsyncEventArgs e)
    {
        ProcessReceive(e);
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
            ProcessError(e);
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
                ProcessMessage(messageData);

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
    private void ProcessMessage(byte[] messageData)
    {
        receivedMessageQueue.Add(messageData);
    }
    private void ProcessReceivedMessage()
    {
        while (true)
        {
            var message = receivedMessageQueue.Take();
            if (message != null)
            {
                var current = Interlocked.Increment(ref Program._receivedMessageCount);
                if (current == 1)
                {
                    Program._watch = Stopwatch.StartNew();
                }
                if (current % 1000 == 0)
                {
                    Console.WriteLine("received reply message, length:{0}, count:{1}, timeSpent:{2}", message.Length, current, Program._watch.ElapsedMilliseconds);
                }
            }
        }
    }

    private void ProcessError(SocketAsyncEventArgs e)
    {
        //Socket s = e.UserToken as Socket;
        //if (s.Connected)
        //{
        //    // close the socket associated with the client
        //    try
        //    {
        //        s.Shutdown(SocketShutdown.Both);
        //    }
        //    catch (Exception)
        //    {
        //        // throws if client process has already closed
        //    }
        //    finally
        //    {
        //        if (s.Connected)
        //        {
        //            s.Close();
        //        }
        //    }
        //}

        // Throw the SocketException
        throw new SocketException((int)e.SocketError);
    }

    #region IDisposable Members

    public void Dispose()
    {
        autoConnectEvent.Close();
        if (clientSocket.Connected)
        {
            clientSocket.Close();
        }
    }

    #endregion
}
