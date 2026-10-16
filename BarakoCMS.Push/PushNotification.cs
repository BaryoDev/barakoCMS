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

    public static string SafeUrl(string? url) =>
        !string.IsNullOrEmpty(url) && url[0] == '/' && !url.StartsWith("//", StringComparison.Ordinal)
            ? url
            : "/";
}
