namespace URiegel.WebSocketClient;
struct UrlQueryComponents
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
                Parameters = [];
            }
        }
        else
        {
            Path = query ?? "";
            Parameters = [];
        }
    }
}
