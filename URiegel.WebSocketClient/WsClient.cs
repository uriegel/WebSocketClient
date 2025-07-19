using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Text.Json;
using CsTools.Extensions;
using URiegel.WebSocketClient.Enums;
using URiegel.WebSocketClient.Exceptions;

namespace URiegel.WebSocketClient;

public class WsClient(string url, Func<string, Task> onMessage, Action onClosed)
{
	public bool IsConnected { get => isConnected && (tcpClient?.Connected ?? false); }

    public async Task OpenAsync()
	{
		Reset();

		synchronizationContext = SynchronizationContext.Current;

		await (tcpClient?.ConnectAsync(url.Host, url.Port) ?? 0.ToAsync());
		networkStream = tcpClient?.GetStream();

		if (url.TlsUsed && networkStream != null)
		{
			var secureStream = new SslStream(networkStream);
			await secureStream.AuthenticateAsClientAsync(url.Host, null, SslProtocols.Tls13 | SslProtocols.Tls12, false);
			networkStream = secureStream;
		}

		await InitializeWebSockets();
		isConnected = true;
		StartMessageReceiving();
	}

	public void Close()
	{
		writeLocker.Wait();
		try
		{
			var end = new byte[2];
			end[0] = 0xf8;
			networkStream?.Write(end, 0, end.Length);
		}
		catch
		{
			try
			{
				networkStream?.Close();
			}
			catch { }
		}
		finally
		{
			writeLocker.Release();
		}
	}

	public void StartConnectionControlling(TimeSpan refreshCheck)
	{
		Check();

		async void Check()
		{
			try
			{
				while (true)
				{
					var startTime = DateTime.Now;
					pongReceived = new(false);
					SetTimeout(pongReceived);
					await SendAsync("check-connection", OpCode.Ping);
					await (pongReceived?.Task ?? false.ToAsync());
					await Task.Delay(refreshCheck);
				}
			}
			catch (OperationCanceledException)
			{
				Close();
				isConnected = false;
				if (synchronizationContext != null)
					synchronizationContext.Send(_ => onClosed(), null);
				else
					onClosed();
				pongReceived = null;
			}
			catch { }
		}

		async void SetTimeout(TaskCompletionSource<bool> tcs)
		{
			await Task.Delay(TimeSpan.FromSeconds(10));
			tcs.TrySetCanceled();
		}
	}


	public Task SendAsync(string payload) => SendAsync(payload, OpCode.Text);

	public async Task SendJsonAsync(object jsonObject)
	{
		await writeLocker.WaitAsync();
		try
		{
			if (!IsConnected)
				throw new ConnectionClosedException();

			var memStm = new MemoryStream();
			await JsonSerializer.SerializeAsync(memStm,  jsonObject);
			memStm.Position = 0;
			var bytes = memStm.ToArray();
			bytes = EncodeWSBuffer(bytes);

			await (networkStream?.WriteAsync(bytes, 0, bytes.Length) ?? 0.ToAsync());
		}
		finally
		{
			writeLocker.Release();
		}
	}

	public override string ToString() => $"Url: {url}, connected: {IsConnected}";

	async Task InitializeWebSockets()
	{
		await writeLocker.WaitAsync();
		try
		{
			var key = DateTime.Now.ToString();
			var base64Key = Convert.ToBase64String(Encoding.UTF8.GetBytes(key));
			var http = string.Join("\r\n",
			[
				$"GET {url.Url} HTTP/1.1",
				"Upgrade: websocket",
				"Connection: Upgrade",
				"Host: {url.Host}",
				$"Sec-WebSocket-Key: {base64Key}",
				"",
				""
			]);
			var bytes = Encoding.UTF8.GetBytes(http);
			await (networkStream?.WriteAsync(bytes, 0, bytes.Length) ?? 0.ToAsync());

			var buffer = new byte[2000];

			async Task<(bool finished, string answer)> readAnswer(int pos)
			{
                var read = await (networkStream?.ReadAsync(buffer, pos, Math.Min(1, buffer.Length - pos)) ?? 0.ToAsync());
				var result = read == 0;
				if (!result)
				{
					var text = Encoding.UTF8.GetString(buffer, 0, pos + read);
					result = text.Contains("\r\n\r\n");
					if (result)
						return (true, text);
				}
				else
					return (false, "");
				return await readAnswer(pos + read);
			}

			var (res, answer) = await readAnswer(0);
			if (!res)
				throw new Exception("Could not switch to web sockets");

			int GetResult(string body)
			{
				var pos = body.IndexOf(" ");
				var pos2 = body.IndexOf(" ", pos + 1);
				return int.Parse(body[pos..pos2]);
			}
			var status = GetResult(answer);
			if (status != 101)
				throw new Exception($"Could not switch to web sockets: {answer}");

			var urlQuery = new UrlQueryComponents(answer);
			var headerParts = answer.Split(["\r\n"], StringSplitOptions.RemoveEmptyEntries);
			var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
			var keyValues = headerParts.Skip(1).Select(s =>
			{
				try
				{
					return new KeyValuePair<string, string>(s.Substring(0, s.IndexOf(": ")), s[(s.IndexOf(": ") + 2)..]);
				}
				catch { return new KeyValuePair<string, string>("_OBSOLETE_", ""); }
			}).Where(n => n.Key != "_OBSOLETE_");
			foreach (var keyValue in keyValues)
				headers[keyValue.Key] = keyValue.Value;
			headers.TryGetValue("sec-websocket-extensions", out var confirmedExtensionsStr);
			var confirmedExtensions = confirmedExtensionsStr?.Split([';']) ?? [];
		}
		finally
		{
			writeLocker.Release();
		}
	}

	async Task SendAsync(string payload, OpCode opcode)
	{
		await writeLocker.WaitAsync();
		try
		{
			if (!IsConnected)
				throw new ConnectionClosedException();
			var bytes = Encoding.UTF8.GetBytes(payload);
			bytes = EncodeWSBuffer(bytes, opcode);
			await (networkStream?.WriteAsync(bytes, 0, bytes.Length) ?? 0.ToAsync());
		}
		finally
		{
			writeLocker.Release();
		}
	}

	async void StartMessageReceiving()
	{
		if (networkStream != null)
		{
			var wsr = new WebSocketReceiver(networkStream);

			Func<WsDecodedStream?, Exception?, Task> action = async (wsDecodedStream, exception) =>
			{
				try
				{
					if (exception == null)
					{
						var payload = wsDecodedStream?.Payload ?? "";
						if (synchronizationContext != null)
							synchronizationContext.Send(_ => onMessage(payload), null);
						else
							await onMessage(payload);
					}
					else
					{
						Close();
						isConnected = false;
						if (synchronizationContext != null)
							synchronizationContext.Send(_ => onClosed(), null);
						else
							onClosed();
					}
				}
				catch { }
			};

			while (true)
				if (!await wsr.StartMessageReceiving(action, pong => pongReceived?.TrySetResult(true), null))
					break;
		}
	}

    static byte[] EncodeWSBuffer(byte[] bytes, OpCode opcode = OpCode.Text)
	{
		var result = Array.Empty<byte>();
		var key = new byte[] { 9, 8, 6, 5 };

		var bufferIndex = 0;
		if (bytes.Length < 126)
		{
			result = new byte[bytes.Length + 6];
			result[1] = (byte)(bytes.Length + 128); // wahrscheinlich mit bit 8 gesetzt, Masking

			result[2] = key[0]; // Maskierung des Clients
			result[3] = key[1]; // Maskierung des Clients
			result[4] = key[2]; // Maskierung des Clients
			result[5] = key[3]; // Maskierung des Clients
			bufferIndex = 6;
		}
		else if (bytes.Length <= ushort.MaxValue)
		{
			result = new byte[bytes.Length + 8];
			result[1] = (byte)(126 + 128); // wahrscheinlich mit bit 8 gesetzt, Masking
			var sl = (ushort)bytes.Length;
			var byteArray = BitConverter.GetBytes(sl);
			var eins = byteArray[0];
			result[2] = byteArray[1];
			result[3] = eins;

			result[4] = key[0]; // Maskierung des Clients
			result[5] = key[1]; // Maskierung des Clients
			result[6] = key[2]; // Maskierung des Clients
			result[7] = key[3]; // Maskierung des Clients
			bufferIndex = 8;
		}
		result[0] = (byte)(0x80 + opcode);  // 129;  Text, single frame

		for (var i = 0; i < bytes.Length; i++)
			result[i + bufferIndex] = (Byte)(bytes[i] ^ key[i % 4]);

		return result;
	}

	void Reset()
	{
		try
		{
			var stream = networkStream;
			stream?.Dispose();
			var zombie = tcpClient;
			zombie?.Close();
		}
		catch { }

		networkStream = null;
		tcpClient = new TcpClient();
	}

	readonly UrlExtracts url = new UrlExtracts(url);
	readonly SemaphoreSlim writeLocker = new(1, 1);
	readonly Func<string, Task> onMessage = onMessage;
	readonly Action onClosed = onClosed;
	TcpClient? tcpClient;
	SynchronizationContext? synchronizationContext;
	Stream? networkStream;
	bool isConnected;
	TaskCompletionSource<bool>? pongReceived;
}

