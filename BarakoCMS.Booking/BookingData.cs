using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BarakoCMS.Booking;

/// <summary>Reads booking fields out of an entry's data, whatever shape the value was stored in.</summary>
/// <remarks>
/// A value arrives as a <see cref="JsonElement"/> when read back from the database and as a CLR value
/// when it came in on a request, so each reader accepts both. Field names match case-insensitively,
/// the same as schema validation.
/// </remarks>
internal static partial class BookingData
{
    public static object? Raw(IReadOnlyDictionary<string, object> data, string field) =>
        data.FirstOrDefault(kv => string.Equals(kv.Key, field, StringComparison.OrdinalIgnoreCase)).Value;

    public static string? String(IReadOnlyDictionary<string, object> data, string field) =>
        Raw(data, field) switch
        {
            null => null,
            JsonElement { ValueKind: JsonValueKind.String } je => je.GetString(),
            JsonElement => null,
            var v => v.ToString(),
        };

    public static Guid? Guid(IReadOnlyDictionary<string, object> data, string field) =>
        System.Guid.TryParse(String(data, field), out var id) ? id : null;

    /// <summary>
    /// An instant. A string must carry a UTC offset (<c>Z</c> or <c>+08:00</c>): a time with none
    /// names a different instant in every time zone, and a booking cannot guess which was meant.
    /// </summary>
    public static DateTimeOffset? Instant(IReadOnlyDictionary<string, object> data, string field)
    {
        switch (Raw(data, field))
        {
            case DateTimeOffset dto:
                return dto.ToUniversalTime();
            case DateTime { Kind: not DateTimeKind.Unspecified } dt:
                return new DateTimeOffset(dt.ToUniversalTime(), TimeSpan.Zero);
        }

        var text = String(data, field)?.Trim();
        if (text is null || !HasOffset().IsMatch(text))
        {
            return null;
        }

        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed.ToUniversalTime()
            : null;
    }

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}(:\d{2}(\.\d+)?)?(Z|[+-]\d{2}:?\d{2})$", RegexOptions.IgnoreCase)]
    private static partial Regex HasOffset();
}
