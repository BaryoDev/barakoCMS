using System.Globalization;
using System.Text.Json;
using barakoCMS.Models;
using Marten;

namespace BarakoCMS.Forms;

/// <summary>
/// The opening and closing time of a form, read from one entry of a singleton content type each time
/// it is asked, so editing the date moves the window with no restart.
/// </summary>
internal static class FormWindow
{
    /// <summary>What a submission may do right now, and the message for a refusal.</summary>
    internal sealed record State(bool IsOpen, string? Message);

    private static readonly State Open = new(true, null);

    /// <summary>Field types that can hold the moment a window opens or closes.</summary>
    public static bool IsDateField(FieldDefinition field) =>
        field.Type.Equals("date", StringComparison.OrdinalIgnoreCase)
        || field.Type.Equals("datetime", StringComparison.OrdinalIgnoreCase);

    public static bool IsConfigured(PublicForm form) =>
        !string.IsNullOrEmpty(form.WindowContentType)
        && (!string.IsNullOrEmpty(form.OpensAtField) || !string.IsNullOrEmpty(form.ClosesAtField));

    /// <summary>
    /// Open when the form has no window, when the singleton holds no entry or no date, and between
    /// the two dates. A date with no offset is read as UTC.
    /// </summary>
    public static async Task<State> CheckAsync(IQuerySession session, PublicForm form, DateTimeOffset now, CancellationToken ct)
    {
        if (!IsConfigured(form))
        {
            return Open;
        }

        var entry = await session.Query<Content>()
            .Where(c => c.ContentType == form.WindowContentType)
            .OrderByDescending(c => c.UpdatedAt)
            .FirstOrDefaultAsync(ct);
        if (entry is null)
        {
            return Open;
        }

        var opens = Read(entry, form.OpensAtField);
        var closes = Read(entry, form.ClosesAtField);

        if (opens is { } from && now < from)
        {
            return new State(false, "This form is not open yet.");
        }

        if (closes is { } until && now >= until)
        {
            return new State(false, "This form is closed.");
        }

        return Open;
    }

    private static DateTimeOffset? Read(Content entry, string? field)
    {
        if (string.IsNullOrEmpty(field))
        {
            return null;
        }

        var key = entry.Data.Keys.FirstOrDefault(k => k.Equals(field, StringComparison.OrdinalIgnoreCase));
        if (key is null)
        {
            return null;
        }

        var value = entry.Data[key];
        switch (value)
        {
            case DateTimeOffset offset:
                return offset;
            case DateTime moment:
                return new DateTimeOffset(DateTime.SpecifyKind(moment, moment.Kind == DateTimeKind.Unspecified ? DateTimeKind.Utc : moment.Kind));
            case JsonElement { ValueKind: JsonValueKind.String } element:
                return Parse(element.GetString());
            case string text:
                return Parse(text);
            default:
                return null;
        }
    }

    private static DateTimeOffset? Parse(string? text) =>
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
            ? parsed
            : null;
}
