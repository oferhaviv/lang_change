using System.Net.Http;
using System.Text.Json;

internal sealed class SharedConfig
{
    public ConnectionConfig Connection { get; set; } = new();
    public Dictionary<string, string> LanguageMap { get; set; } = new(); // "040D" -> "heb"
}

internal sealed class ConnectionConfig
{
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 47655;
}

internal static class RestNotifier
{
    private static readonly HttpClient Http = new HttpClient
    {
        Timeout = TimeSpan.FromSeconds(2)
    };

    public static SharedConfig LoadConfig(string folderPath)
    {
        var path = Path.Combine(folderPath, "config.json");
        var json = File.ReadAllText(path);
        var cfg = JsonSerializer.Deserialize<SharedConfig>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });
        if (cfg is null) throw new InvalidOperationException("Failed to parse config.json");
        return cfg;
    }

    public static async Task SendAsync(SharedConfig cfg, string endpoint)
    {
        var url = $"http://{cfg.Connection.Host}:{cfg.Connection.Port}/{endpoint}";
        using var resp = await Http.PostAsync(url, content: null);
        resp.EnsureSuccessStatusCode();
    }

    public static bool TryMapLangIdToEndpoint(SharedConfig cfg, ushort langId, out string endpoint)
    {
        // langId 0x040D -> "040D"
        var key = langId.ToString("X4");
        return cfg.LanguageMap.TryGetValue(key, out endpoint!);
    }
}
