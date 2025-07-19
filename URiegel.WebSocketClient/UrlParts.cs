namespace URiegel.WebSocketClient;

class UrlParts
{
    /// <summary>
    /// Der Anteil der Url, der den Serverpart darstellt, z.B.: HTTPS://uriegel.de:443 oder uriegel.de
    /// </summary>
    public string ServerPart { get; internal set; } = "";
    /// <summary>
    /// Der Anteil der Url, der unabhängig vom Serv ist, also z.B. / oder /scripts/ccf.dll/proxy
    /// </summary>
    public string ServerIndependant { get; internal set; } = "";
    /// <summary>
    /// Teile der Url, zerlegt als Strings
    /// </summary>
    public string[] ResourceParts { get; internal set; } = [];
    /// <summary>
    /// Die Parameter einer WEB-Service-Anfrage.
    /// <remarks>Die Parameter lassen sich mit <see cref="UrlUtility.GetParameters"/></remarks> als Key-Value-Pairs weiter zerlegen
    /// </summary>
    public string Parameters { get; internal set; } = "";
}
