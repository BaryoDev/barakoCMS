using System.Globalization;
using System.Text.Json;

namespace BarakoCMS.Notifications;

internal static class NotificationRules
{
    internal const int MaxPayloadBytes = 8 * 1024;

    internal const int MaxTypeLength = 100;
    internal const int MaxTitleLength = 200;
    internal const int MaxBodyLength = 4000;
    internal const int MaxPayloadKeys = 50;

    internal static bool HasControlCharacter(string? text) => text is not null && text.Any(IsControlOrFormat);

    internal static bool TooLong(string? text, int max) => text is not null && text.Length > max;

    internal static bool TooManyPayloadKeys(Dictionary<string, object>? payload) =>
        payload is not null && payload.Count > MaxPayloadKeys;

    private static bool IsControlOrFormat(char c) =>
        char.IsControl(c) || CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.Format;

    internal static bool PayloadTooLarge(Dictionary<string, object>? payload) =>
        payload is not null && JsonSerializer.SerializeToUtf8Bytes(payload).Length > MaxPayloadBytes;
}
