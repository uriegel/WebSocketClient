using System.IO.Compression;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Text.RegularExpressions;

namespace Client;




public class Client
{
	public bool IsConnected { get => isConnected && (tcpClient?.Connected ?? false); }

	public WebSockets.ProtocolExtensions ConfirmedExtensions { get; private set; } = new ProtocolExtensions();

	public Client(string url, Func<string, Task> onMessage, Action onClosed)
	{
		this.url = new UrlExtracts(url);
		this.onMessage = onMessage;
		this.onClosed = onClosed;
	}

	public async Task OpenAsync(Settings? settings = null)
	{
		Reset();

		synchronizationContext = SynchronizationContext.Current;

		await (tcpClient?.ConnectAsync(url.Host, url.Port) ?? Async(0));
		networkStream = tcpClient?.GetStream();

		if (url.TlsUsed && networkStream != null)
		{
			var secureStream = new SslStream(networkStream);
			await secureStream.AuthenticateAsClientAsync(url.Host, null, SslProtocols.Tls13 | SslProtocols.Tls12, false);
			networkStream = secureStream;
		}

		await InitializeWebSockets(settings);
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
					await (pongReceived?.Task ?? Async(false));
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

			var type = jsonObject.GetType();
			var jason = new DataContractJsonSerializer(type);
			var memStm = new MemoryStream();

			jason.WriteObject(memStm, jsonObject);
			memStm.Position = 0;
			var bytes = memStm.ToArray();
			bytes = EncodeWSBuffer(bytes);

			await (networkStream?.WriteAsync(bytes, 0, bytes.Length) ?? Async(0));
		}
		finally
		{
			writeLocker.Release();
		}
	}

	public override string ToString() => $"Url: {url}, connected: {IsConnected}";

	async Task InitializeWebSockets(Settings? settings)
	{
		await writeLocker.WaitAsync();
		try
		{
			var key = DateTime.Now.ToString();
			var base64Key = Convert.ToBase64String(Encoding.UTF8.GetBytes(key));
			var extensions = settings?.Extensions?.Watchdog ?? false 
				? "Sec-WebSocket-Extensions: watchdog"
				: "";
			var http =
$@"GET {url.Url} HTTP/1.1
Upgrade: websocket
Connection: Upgrade
Host: {url.Host}
{extensions}
Sec-WebSocket-Key: {base64Key}

";
			var bytes = Encoding.UTF8.GetBytes(http);
			await (networkStream?.WriteAsync(bytes, 0, bytes.Length) ?? Async(0));

			var buffer = new byte[2000];

			async Task<(bool finished, string answer)> readAnswer(int pos)
			{
                var read = await (networkStream?.ReadAsync(buffer, pos, Math.Min(1, buffer.Length - pos)) ?? Async(0));
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
				return int.Parse(body.Substring(pos, pos2 - pos));
			}
			var status = GetResult(answer);
			if (status != 101)
				throw new Exception($"Could not switch to web sockets: {answer}");

			var urlQuery = new UrlQueryComponents(answer);
			var headerParts = answer.Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries);
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
			var confirmedExtensions = confirmedExtensionsStr?.Split(new[] { ';' }) ?? new string[0];
			ConfirmedExtensions.Watchdog = confirmedExtensions.Any(n => n == "watchdog");
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
			await (networkStream?.WriteAsync(bytes, 0, bytes.Length) ?? Async(0));
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

	byte[] EncodeWSBuffer(byte[] bytes, OpCode opcode = OpCode.Text)
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

	readonly UrlExtracts url;
	readonly SemaphoreSlim writeLocker = new(1, 1);
	readonly Func<string, Task> onMessage;
	readonly Action onClosed;
	TcpClient? tcpClient;
	SynchronizationContext? synchronizationContext;
	Stream? networkStream;
	bool isConnected;
	TaskCompletionSource<bool>? pongReceived;
}


public class Settings
{
	public ProtocolExtensions? Extensions { get; set; }
}

public class ProtocolExtensions
{
	public bool Watchdog { get; set; }
}


namespace Caseris.Core.WebServiceTools
{
    /// <summary>
    /// Zerlegt eine Url in seine Bestandteile
    /// </summary>
    public struct UrlExtracts
    {
        public readonly string Url;
        public readonly string Scheme;
        public readonly bool TlsUsed;
        public readonly string Host;
        public readonly int Port;

        public UrlExtracts(string url)
        {
            var matches = regex.Match(url);
            if (!matches.Success)
                throw new UrlMismatchException();

            var scheme = matches.Groups["scheme"].Value;
            var secureScheme = string.Compare(scheme, "https", true) == 0 || string.Compare(scheme, "wss", true) == 0;

            Host = matches.Groups["server"].Value;
            if (!int.TryParse(matches.Groups["port"].Value, out Port))
                Port = secureScheme ? 443 : 80;
            TlsUsed = secureScheme || Port == 443;
            Url = matches.Groups["url"].Value;
            Scheme = !string.IsNullOrEmpty(scheme) ? scheme : (TlsUsed ? "https" : "http");
        }

        public override string ToString()
            => $"{Scheme}://{Host}{((TlsUsed && Port != 443) || (!TlsUsed && Port != 80) ? $":{Port}" : "")}{Url}";

        static readonly Regex regex = new Regex(@"((?<scheme>\w+)://)?(?<server>[^/:]+)(:(?<port>\d+))?(?<url>/.+)?", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    }
}


public class WebSocketReceiver
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


	public class WsDecodedStream : Stream
	{
		#region Properties	

		public int DataPosition { get; protected set; }
		public string? Payload { get; protected set; }

		#endregion

		#region Constructor	

		public WsDecodedStream(Stream stream, int length, byte[] key, bool encode, bool isDeflated)
		{
			this.stream = stream;
			this.length = length;
			this.key = key;
			this.encode = encode;
			buffer = new byte[length];
			this.isDeflated = isDeflated;
			ReadStream(0);
		}

		protected WsDecodedStream()
		{
		}

		#endregion

		#region Stream	

		public override bool CanRead { get { return true; } }

		public override bool CanSeek { get { return false; } }

		public override bool CanWrite { get { return false; } }

		public override long Length { get { return length - DataPosition; } }

		public override long Position
		{
			get { return _Position; }
			set
			{
				if (value > Length)
					throw new IndexOutOfRangeException();
				_Position = value;
			}
		}
		long _Position;

		public override void Flush()
		{
		}

		public override int Read(byte[] buffer, int offset, int count)
		{
			if (Position + count > length - DataPosition)
				count = (int)length - DataPosition - (int)Position;
			if (count == 0)
				return 0;

			Array.Copy(this.buffer, offset + DataPosition + Position, buffer, offset, count);
			Position += count;

			return count;
		}

		public override long Seek(long offset, SeekOrigin origin) => throw new NotImplementedException();

		public override void SetLength(long value) => throw new NotImplementedException();

		public override void Write(byte[] buffer, int offset, int count) => throw new NotImplementedException();

		public virtual int WriteHeaderToAnswer(byte[] bytes, int position)
		{
			Array.Copy(buffer, 0, bytes, position, DataPosition);
			return DataPosition;
		}

		void ReadStream(int position)
		{
			var read = 0;
			while (read < length - position)
			{
				var newlyRead = stream?.Read(buffer, read + position, (int)length - position - read) ?? 0;
				if (newlyRead == 0)
					throw new ConnectionClosedException();
				read += newlyRead;
			}

			if (encode)
				for (var i = 0; i < length - position; i++)
					buffer[i + position] = (Byte)(buffer[i + position] ^ key[i % 4]);

			if (position == 0)
			{
				if (isDeflated)
				{
					var ms = new MemoryStream(buffer, 0, (int)length);
					var outputStream = new MemoryStream();
					var compressedStream = new DeflateStream(ms, CompressionMode.Decompress, true);
					compressedStream.CopyTo(outputStream);
					compressedStream.Close();
					outputStream.Capacity = (int)outputStream.Length;
					var deflatedBuffer = outputStream.GetBuffer();
					Payload = Encoding.UTF8.GetString(deflatedBuffer, 0, deflatedBuffer.Length);
				}
				else
					Payload = Encoding.UTF8.GetString(buffer, 0, (int)length);
				DataPosition = Payload.Length + 1;
			}
		}

		#endregion

		#region Methods

		public void AddContinuation(int length, byte[] key, bool encode)
		{
			var oldLength = buffer.Length;
			Array.Resize<byte>(ref buffer, oldLength + length);
			this.key = key;
			this.encode = encode;
			this.length += length;
			ReadStream(oldLength);
		}

		#endregion

		#region Fields

		readonly Stream? stream;
		byte[] buffer = Array.Empty<byte>();
		long length;
		byte[] key = Array.Empty<byte>();
		bool encode;
		readonly bool isDeflated;

		#endregion
	}
}
	public enum OpCode : byte
	{
		/// <summary>
		/// Diese Nachricht muss an die vorherige angehängt werden. Wenn der fin-Wert 0 ist, folgen weitere Fragmente, 
		/// bei fin=1 ist die Nachricht komplett verarbeitet.
		/// </summary>
		ContinuationFrame = 0,
		Text,
		Binary,
		Close = 8,
		/// <summary>
		/// Ping erhalten, direkt einen Pong zurücksenden mit denselben payload-Daten
		/// </summary>
		Ping,
		/// <summary>
		/// Wird serverseitig ignoriert
		/// </summary>
		Pong
	}

	public struct UrlQueryComponents
	{
		public string Path;
		public Dictionary<string, string> Parameters;

		public UrlQueryComponents(string query)
		{
			Path = "";

			if (!string.IsNullOrEmpty(query) && query.Contains('?'))
			{
				var pos = query.IndexOf('?');
				if (pos >= 0)
				{
					Path = query.Substring(0, pos) ?? "";
					Parameters = UrlUtility.GetParameters(query).ToDictionary(n => n.Key, n => n.Value);
				}
				else
				{
					Path = query ?? "";
					Parameters = new Dictionary<string, string>();
				}
			}
			else
			{
				Path = query ?? "";
				Parameters = new Dictionary<string, string>();
			}
		}
	}

	static public class UrlUtility
	{
		#region Methods

		/// <summary>
		/// Zerlegung einer Url in einzelne Url-Teile und Parametern, die sich mit <see cref="GetParameters"/> weiter zerlegen lassen
		/// </summary>
		/// <param name="url">Die zu untersuchende Url</param>
		/// <returns>Die in die Bestandteile zerlegete Url</returns>
		public static UrlParts GetUrlParts(string url)
		{
			var urlParts = new UrlParts();
			if (string.IsNullOrEmpty(url))
				return urlParts;
			int firstSlash;
			var doubleSlash = url.IndexOf("//");
			var relative = url.StartsWith("http") == false;
            if (doubleSlash != -1)
				firstSlash = url.IndexOf("/", doubleSlash + 2);
			else
				firstSlash = url.IndexOf("/");
			if (firstSlash == -1)
				urlParts.ServerPart = url ?? "";
			else
			{
				urlParts.ServerPart = relative ? "" : url.Substring(0, firstSlash) ?? "";
				urlParts.ServerIndependant = relative ? url : url.Substring(firstSlash) ?? "";
			}

			var match = urlPartsRegex.Match(url ?? "");
			if (!match.Success)
				return urlParts;

			if (firstSlash != -1 && url?.Length > firstSlash + 1)
				urlParts.ResourceParts = match.Groups["ResourcePart"].Captures.OfType<Capture>().Select(n => n.Value).ToArray();
			urlParts.Parameters = match.Groups["Parameters"].Value;
			return urlParts;
		}

		/// <summary>
		/// Zerlegung des Parameters-Teils einer URL
		/// </summary>
		/// <param name="urlParameterString">Der zu untersuchende Parameter-Teil einer Url</param>
		/// <returns>Die Parameter als Array von Key-Value-Pairs (Parametername, Parameterwert)</returns>
		public static KeyValuePair<string, string>[] GetParameters(string urlParameterString)
		{
			// Unnötig, bzw. falsch:
			//            urlParameterString = Uri.UnescapeDataString(urlParameterString);
			var mc = urlParameterRegex.Matches(urlParameterString);
			return mc.OfType<Match>().Select(n => new KeyValuePair<string, string>(n.Groups["key"].Value,
				Uri.UnescapeDataString(UnescapeSpaces(n.Groups["value"].Value)))).ToArray();
		}

		/// <summary>
		/// Aus Parametern die in der URL übergeben worden sind ein JSON-String erzeugen, welches sich in ein C#-Objekt umwandeln lassen kann
		/// </summary>
		/// <param name="parameters"></param>
		/// <returns></returns>
		public static string GetJsonFromUrlParameters(IEnumerable<KeyValuePair<string, string>> parameters)
			=> string.Format("{0}{1}{2}", '{', string.Join(",", parameters.Select(n => string.Format(@"""{0}"":""{1}""", n.Key, n.Value))), '}');

		static string UnescapeSpaces(string uri) => uri.Replace('+', ' ');

		#endregion

		#region Fields

		static readonly Regex urlPartsRegex = new Regex(@"(http://[^/]+)?(?:/(?<ResourcePart>[^<>?&/#\""]+))+(?:\?(?<Parameters>.+))?", RegexOptions.Compiled);
		static readonly Regex urlParameterRegex = new Regex(@"(?<key>[^&?]*?)=(?<value>[^&?]*)", RegexOptions.Compiled);

		#endregion
	}

	public class UrlParts
	{
		/// <summary>
		/// Der Anteil der Url, der den Serverpart darstellt, z.B.: HTTPS://www.caseris.de:443 oder www.caseris.de
		/// </summary>
		public string ServerPart { get; internal set; } = "";
		/// <summary>
		/// Der Anteil der Url, der unabhängig vom Serv ist, also z.B. / oder /scripts/ccf.dll/proxy
		/// </summary>
		public string ServerIndependant { get; internal set; } = "";
		/// <summary>
		/// Teile der Url, zerlegt als Strings
		/// </summary>
		public string[] ResourceParts { get; internal set; } = new string[0];
		/// <summary>
		/// Die Parameter einer WEB-Service-Anfrage.
		/// <remarks>Die Parameter lassen sich mit <see cref="UrlUtility.GetParameters"/></remarks> als Key-Value-Pairs weiter zerlegen
		/// </summary>
		public string Parameters { get; internal set; } = "";
	}


	public interface IWebSocketInternalSession
	{
		void SendPong(string payload);
	}
