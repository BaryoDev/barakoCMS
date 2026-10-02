using barakoCMS.Models;
using Marten;
using Marten.Linq.MatchesSql;

namespace barakoCMS.Infrastructure.Services;

/// <summary>
/// Counts the entries of a type that already hold a value under a name a new token field would
/// take, for the routes that add a field to a type: create, add field and a bundle import.
/// </summary>
/// <remarks>
/// Entry data can hold keys no field declares, so a caller could write a value under a name before
/// a token field of that name exists. The writer keeps a stored token, so such a value would become
/// the token: chosen by a caller and possibly shared by two entries. A route adding a token field
/// refuses while any entry holds one. Public because the Portability module adds fields too.
/// </remarks>
public static class TokenFieldEntries
{
    // The type name normalised the way ContentTypeName.Normalize does it, so an entry stored
    // under another spelling of the name is counted. The key is matched ignoring case, as the
    // entry validator reads one, and a JSON null is no value.
    private const string HoldingSql =
        "replace(lower(btrim(d.data ->> 'ContentType', ' ' || chr(9) || chr(10) || chr(13))), ' ', '-') = ? "
        + "AND EXISTS (SELECT 1 FROM jsonb_each(d.data -> 'Data') e "
        + "WHERE lower(e.key) = lower(?) AND jsonb_typeof(e.value) <> 'null')";

    /// <summary>Entries of the type, of any status, holding a value under <paramref name="field"/>.</summary>
    public static async Task<int> HoldingAsync(
        IQuerySession session, string contentType, string field, CancellationToken ct)
    {
        object[] parameters = [barakoCMS.Core.ContentTypeName.Normalize(contentType), field];
        return await session.Query<Content>()
            .Where(c => c.MatchesSql(HoldingSql, parameters))
            .CountAsync(ct);
    }

    /// <summary>The refusal a route answers with, naming the count and never a value.</summary>
    public static string Refusal(string contentType, string field, int entries) =>
        $"{entries} {(entries == 1 ? "entry" : "entries")} of '{contentType}' already "
        + $"{(entries == 1 ? "holds" : "hold")} a value under '{field}'. A token field cannot take over "
        + "values callers wrote. Choose another name, or remove those values first.";
}
