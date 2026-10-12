using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using barakoCMS.Core.Validation;
using barakoCMS.Infrastructure.Caching;
using barakoCMS.Infrastructure.Security;
using barakoCMS.Models;
using FastEndpoints;
using Marten;
using Marten.Linq.MatchesSql;
using ContentDoc = barakoCMS.Models.Content;

namespace barakoCMS.Features.Public.Lookup;

/// <summary>
/// POST /api/public/{type}/lookup: one entry, found by a reference plus an identifier, with only the
/// fields the type names.
/// </summary>
/// <remarks>
/// <para>
/// Every way of not finding an entry answers the same empty 404: a type that does not exist or
/// answers no lookup, a reference nobody holds, and a reference whose identifier does not match. So
/// the route cannot be used to learn that a reference exists. The match runs in Postgres on the
/// reference alone and the identifier is compared afterwards, so both misses cost the same query.
/// </para>
/// <para>
/// A POST, so the identifier, a last name or an email, is in the body and not in a URL a proxy logs.
/// Throttled twice: per tenant and visitor by <see cref="RateLimitSetup.LookupPolicy"/>, and per
/// reference by <see cref="LookupReferenceThrottle"/>, which is taken before anything is read.
/// </para>
/// <para>
/// Any entry of the type but an Archived one is found, whatever its status, because the records a
/// lookup is for (registrations, orders) are rarely Published. The answer carries only fields that
/// are Public and not tokens at the moment of the read, plus the reveal fields while the setting is
/// on, and is never cached.
/// </para>
/// </remarks>
internal sealed class Endpoint(
    IQuerySession session,
    LookupReferenceThrottle throttle,
    barakoCMS.Infrastructure.Multitenancy.TenantContext tenant) : Endpoint<Request, Response>
{
    /// <summary>How many entries holding one reference are checked. A reference is meant to be unique.</summary>
    private const int MaxCandidates = 5;

    public override void Configure()
    {
        Post("/api/public/{type}/lookup");
        AllowAnonymous();
        Options(x => x.RequireRateLimiting(RateLimitSetup.LookupPolicy));
    }

    public override async Task HandleAsync(Request req, CancellationToken ct)
    {
        DeliveryCache.NoStore(HttpContext);

        var type = Route<string>("type") ?? string.Empty;

        if (!throttle.TryAcquire(tenant.Slug, type, req.Reference))
        {
            HttpContext.Response.Headers.RetryAfter = "60";
            await Send.StringAsync("Too many requests. Please try again later.", StatusCodes.Status429TooManyRequests, cancellation: ct);
            return;
        }

        var def = await session.Query<ContentTypeDefinition>().FirstOrDefaultAsync(d => d.Name == type, ct);
        var lookup = def?.Lookup;
        if (def is null || lookup is null || LookupRules.Field(def, lookup.MatchField) is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var (sql, parameters) = DeliveryQuery.FieldEqualsIgnoreCaseSql(lookup.MatchField, req.Reference.Trim());
        var candidates = await session.Query<ContentDoc>()
            .Where(c => c.ContentType == def.Name
                        && c.Status != ContentStatus.Archived
                        && c.MatchesSql(sql, parameters))
            .OrderBy(c => c.CreatedAt)
            .ThenBy(c => c.Id)
            .Take(MaxCandidates)
            .ToListAsync(ct);

        var found = candidates.FirstOrDefault(c => Verifies(c, lookup.VerifyFields, req.Verify));
        if (found is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var data = Pick(found, def, lookup.ReturnFields, LookupRules.IsReturnable);

        if (lookup.Reveal is { } reveal && await RevealOpenAsync(reveal, ct))
        {
            foreach (var (name, value) in Pick(found, def, reveal.Fields, LookupRules.IsRevealable))
            {
                data[name] = value;
            }
        }

        await Send.OkAsync(new Response { Data = data }, ct);
    }

    /// <summary>Whether any verify field holds the identifier, compared without case, spaces or length showing in the time taken.</summary>
    private static bool Verifies(ContentDoc entry, IEnumerable<string> verifyFields, string identifier)
    {
        var expected = Digest(identifier);
        var any = false;

        foreach (var field in verifyFields)
        {
            var stored = Text(Value(entry, field));
            if (stored is null) continue;

            any |= CryptographicOperations.FixedTimeEquals(Digest(stored), expected);
        }

        return any;
    }

    private static byte[] Digest(string value) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(value.Trim().ToLowerInvariant()));

    /// <summary>The named fields of the entry that the rule allows, under the names the type spells them with.</summary>
    private static Dictionary<string, object> Pick(
        ContentDoc entry, ContentTypeDefinition def, IEnumerable<string> names, Func<FieldDefinition, bool> allowed)
    {
        var data = new Dictionary<string, object>();

        foreach (var name in names)
        {
            // Read off the definition as it is now: a field made Sensitive since the lookup was set
            // is left out here, whatever the stored lookup says.
            if (LookupRules.Field(def, name) is not { } field || !allowed(field)) continue;
            if (Value(entry, field.Name) is { } value) data[field.Name] = value;
        }

        return data;
    }

    private async Task<bool> RevealOpenAsync(LookupReveal reveal, CancellationToken ct)
    {
        var setting = await session.Query<ContentDoc>()
            .Where(c => c.ContentType == reveal.SettingType && c.Status == ContentStatus.Published)
            .OrderBy(c => c.CreatedAt)
            .ThenBy(c => c.Id)
            .FirstOrDefaultAsync(ct);

        return Value(setting, reveal.SettingField) switch
        {
            bool on => on,
            JsonElement { ValueKind: JsonValueKind.True } => true,
            _ => false,
        };
    }

    private static object? Value(ContentDoc? entry, string field)
    {
        if (entry is null) return null;

        foreach (var (key, value) in entry.Data)
        {
            if (string.Equals(key, field, StringComparison.OrdinalIgnoreCase)) return value;
        }

        return null;
    }

    private static string? Text(object? value) => value switch
    {
        string text => text,
        JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
        JsonElement { ValueKind: JsonValueKind.Number } element => element.GetRawText(),
        int or long or decimal or double => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture),
        _ => null,
    };
}
