using System.Text.RegularExpressions;
using URiegel.WebSocketClient.Exceptions;

namespace URiegel.WebSocketClient;

struct UrlExtracts
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

    static readonly Regex regex = new(@"((?<scheme>\w+)://)?(?<server>[^/:]+)(:(?<port>\d+))?(?<url>/.+)?", RegexOptions.Compiled | RegexOptions.IgnoreCase);
}
