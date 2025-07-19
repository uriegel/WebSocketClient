using WebServerLight;

using static System.Console;

var server =
    WebServer
        .New()
        .Http(8080)
        .WebSocket(WebSocket)
        .Build();
    
server.Start();
ReadLine();
server.Stop();

static async void WebSocket(IWebSocket webSocket)
{
    var i = 0;
    while (true)
    {
        await Task.Delay(5000);
        await webSocket.SendString($"Web socket text: {++i}");
    }
}


