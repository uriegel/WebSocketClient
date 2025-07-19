// See https://aka.ms/new-console-template for more information
using CsTools.Extensions;
using URiegel.WebSocketClient;

Console.WriteLine("WebSocketClient will be created in a few secs...");
await Task.Delay(TimeSpan.FromSeconds(3));
Console.WriteLine("Creating WebSocketClient");

var client = new Client("ws://localhost:8080/events", msg => {
        Console.WriteLine(msg);
    return 0.ToAsync();
    }, () => Console.WriteLine("Closed"));

await client.OpenAsync();
Console.ReadLine();