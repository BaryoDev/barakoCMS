using System.Text.Json;

namespace BarakoCMS.Notifications;

internal static class NotificationRules
{
    internal const int MaxPayloadBytes = 8 * 1024;

    internal static bool HasControlCharacter(string? text) => text is not null && text.Any(char.IsControl);

    internal static bool PayloadTooLarge(Dictionary<string, object>? payload) =>
        payload is not null && JsonSerializer.SerializeToUtf8Bytes(payload).Length > MaxPayloadBytes;
}
