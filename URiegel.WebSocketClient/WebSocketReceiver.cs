using URiegel.WebSocketClient.Enums;
using URiegel.WebSocketClient.Exceptions;

namespace URiegel.WebSocketClient;

class WebSocketReceiver
{
    #region Constructor	

    public WebSocketReceiver(Stream networkStream) => this.networkStream = networkStream;

    #endregion

    #region Methods	

    public Task<bool> StartMessageReceiving(Func<WsDecodedStream?, Exception?, Task> action, IWebSocketInternalSession internalSession)
        => StartMessageReceiving(action, null, internalSession);

    public async Task<bool> StartMessageReceiving(Func<WsDecodedStream?, Exception?, Task> action, Action<string>? onPong, IWebSocketInternalSession? internalSession)
    {
        try
        {
            var headerBuffer = new byte[14];
            this.internalSession = internalSession;
            var read = await networkStream.ReadAsync(headerBuffer, 0, 2);
            if (read == 1)
                read = Read(headerBuffer, 1, 1);
            if (read == 0)
                // TODO:
                throw new ConnectionClosedException();
            return await MessageReceiving(headerBuffer, action, onPong);
        }
        catch (ConnectionClosedException ce)
        {
            await action(null, ce);
            return false;
        }
        catch (IOException)
        {
            await action(null, new ConnectionClosedException());
            return false;
        }
        catch
        {
            // TODO:
            return false;
        }
    }

    async Task<bool> MessageReceiving(byte[] headerBuffer, Func<WsDecodedStream, Exception?, Task> action, Action<string>? onPong)
    {
        var read = 2;
        var fin = (byte)((byte)headerBuffer[0] & 0x80) == 0x80;
        var deflated = (byte)((byte)headerBuffer[0] & 0x40) == 0x40;
        var opcode = (OpCode)((byte)headerBuffer[0] & 0xf);
        switch (opcode)
        {
            case OpCode.Close:
                Close();
                break;
            case OpCode.Ping:
            case OpCode.Pong:
            case OpCode.Text:
            case OpCode.ContinuationFrame:
                break;
            default:
                {
                    Close();
                    break;
                }
        }
        var mask = (byte)(headerBuffer[1] >> 7);
        var length = (ulong)(headerBuffer[1] & ~0x80);

        //If the second byte minus 128 is between 0 and 125, this is the length of message. 
        if (length < 126)
        {
            if (mask == 1)
                read += Read(headerBuffer, read, 4);
        }
        else if (length == 126)
        {
            // If length is 126, the following 2 bytes (16-bit unsigned integer), if 127, the following 8 bytes (64-bit unsigned integer) are the length.
            read += Read(headerBuffer, read, mask == 1 ? 6 : 2);
            var ushortbytes = new byte[2];
            ushortbytes[0] = headerBuffer[3];
            ushortbytes[1] = headerBuffer[2];
            length = BitConverter.ToUInt16(ushortbytes, 0);
        }
        else if (length == 127)
        {
            // If length is 127, the following 8 bytes (64-bit unsigned integer) is the length of message
            read += Read(headerBuffer, read, mask == 1 ? 12 : 8);
            var ulongbytes = new byte[8];
            ulongbytes[0] = headerBuffer[9];
            ulongbytes[1] = headerBuffer[8];
            ulongbytes[2] = headerBuffer[7];
            ulongbytes[3] = headerBuffer[6];
            ulongbytes[4] = headerBuffer[5];
            ulongbytes[5] = headerBuffer[4];
            ulongbytes[6] = headerBuffer[3];
            ulongbytes[7] = headerBuffer[2];
            length = BitConverter.ToUInt64(ulongbytes, 0);
        }
        else if (length > 127)
            Close();
        if (length == 0)
        {
            //if (opcode == OpCode.Ping)
            // TODO: Send pong
            // await MessageReceivingAsync(action);
            return false;
        }

        byte[] key = Array.Empty<byte>();
        if (mask == 1)
            key = new byte[4] { headerBuffer[read - 4], headerBuffer[read - 3], headerBuffer[read - 2], headerBuffer[read - 1] };
        if (wsDecodedStream == null)
            wsDecodedStream = new WsDecodedStream(networkStream, (int)length, key, mask == 1, deflated);
        else
            wsDecodedStream.AddContinuation((int)length, key, mask == 1);
        if (fin)
        {
            var receivedStream = wsDecodedStream;
            wsDecodedStream = null;
            switch (opcode)
            {
                case OpCode.Ping:
                    internalSession?.SendPong(receivedStream?.Payload ?? "");
                    break;
                case OpCode.Pong:
                    onPong?.Invoke(receivedStream?.Payload ?? "");
                    break;
                default:
                    await action(receivedStream, null);
                    break;
            }
        }

        return true;
    }

    int Read(byte[] buffer, int offset, int length)
    {
        var result = networkStream.Read(buffer, offset, length);
        if (result == 0)
            throw new ConnectionClosedException();
        return result;
    }

    void Close()
    {
        try
        {
            networkStream.Close();
        }
        catch { }
        throw new ConnectionClosedException();
    }

    #endregion

    #region Fields	

    IWebSocketInternalSession? internalSession;
    Stream networkStream;
    WsDecodedStream? wsDecodedStream;

    #endregion
}
