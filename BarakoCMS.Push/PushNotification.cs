using System.Text;
using System.Text.Json;

namespace BarakoCMS.Push;

public sealed record PushNotification(string Title, string Body, string Url = "/", string? Tag = null)
{
    public const int MaxPayloadBytes = 3500;

    public string ToJson()
    {
        var json = JsonSerializer.Serialize(new { title = Title, body = Body, url = Url, tag = Tag });
        if (Encoding.UTF8.GetByteCount(json) > MaxPayloadBytes)
            throw new ArgumentException($"Push payload is over {MaxPayloadBytes} bytes.");
        return json;
    }

    public static string SafeUrl(string? url)
    {
        if (string.IsNullOrEmpty(url) || url[0] != '/') return "/";
        if (url.Length > 1 && url[1] is '/' or '\\') return "/";
        foreach (var c in url)
            if (c == '\\' || c < 0x20 || c == 0x7F) return "/";
        return url.Contains("%5c", StringComparison.OrdinalIgnoreCase) ? "/" : url;
    }
}
