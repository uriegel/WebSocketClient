namespace URiegel.WebSocketClient;

interface IWebSocketInternalSession
{
    void SendPong(string payload);
}
