using barakoCMS.Core.Interfaces;
using barakoCMS.Infrastructure.Multitenancy;
using barakoCMS.Models;
using Marten;

namespace barakoCMS.Infrastructure.Audit;

/// <summary>One entry of a read, and what in it was above Public.</summary>
/// <remarks>
/// <c>Fields</c> names the non-Public fields whose stored value the caller was sent. <c>Document</c>
/// is true when the entry itself is Sensitive or Hidden and its data was sent.
/// </remarks>
internal sealed record SensitiveRead(Guid EntryId, string ContentType, SensitivityLevel Level, IReadOnlyList<string> Fields, bool Document)
{
    public bool IsEmpty => Fields.Count == 0 && !Document;
}

/// <summary>
/// The audit row for a read that sent data above Public: who, which entries and which fields, never
/// a value. One row per request, so a list read is one row.
/// </summary>
/// <remarks>
/// What was sent is decided from the response data after the sensitivity scrub, not from the rules
/// that produced it. A field counts when its stored value is still the very object in the response:
/// a redacted or Last4 field holds a new string and a removed one is absent. That holds for any
/// <see cref="ISensitivityService"/> and for <c>Sensitivity:Mode=Off</c>, where nothing is masked
/// and every Sensitive value read is a read worth recording.
///
/// The row is written before the response is sent and in its own transaction. A read whose row
/// cannot be written fails rather than going out unrecorded.
/// </remarks>
internal static class SensitiveReadAudit
{
    public const string Action = "content.sensitive.read";

    /// <summary>Public and Members are audience rules, not secrets: staff read both as they read Public.</summary>
    private static bool IsAudience(SensitivityLevel level) =>
        level is SensitivityLevel.Public or SensitivityLevel.Members;

    /// <summary>The non-Public fields of <paramref name="definition"/> that <paramref name="shown"/> carries unmasked.</summary>
    public static List<string> FieldsShown(
        ContentTypeDefinition? definition,
        IReadOnlyDictionary<string, object> stored,
        IReadOnlyDictionary<string, object> shown)
    {
        if (definition?.Fields is not { Count: > 0 } fields || shown.Count == 0)
        {
            return [];
        }

        var names = new List<string>();
        foreach (var field in fields)
        {
            if (field is null || IsAudience(field.Sensitivity))
            {
                continue;
            }

            var sent = stored.Any(kv =>
                kv.Value is not null
                && string.Equals(kv.Key, field.Name, StringComparison.OrdinalIgnoreCase)
                && shown.TryGetValue(kv.Key, out var value)
                && ReferenceEquals(value, kv.Value));
            if (sent)
            {
                names.Add(field.Name);
            }
        }

        return names;
    }

    /// <summary>What one entry's read sent above Public, after <paramref name="shown"/> was scrubbed.</summary>
    public static async Task<SensitiveRead> DescribeAsync(
        Content entry,
        ContentTypeDefinition? definition,
        IReadOnlyDictionary<string, object> shown,
        ISensitivityService sensitivity,
        HttpContext http,
        CancellationToken ct)
    {
        var document = !IsAudience(entry.Sensitivity)
                       && await sensitivity.MaySeeDocumentAsync(entry.Sensitivity, http.User, ct);
        return new SensitiveRead(
            entry.Id, entry.ContentType, entry.Sensitivity, FieldsShown(definition, entry.Data, shown), document);
    }

    /// <summary>
    /// What a read of several versions of one entry sent above Public: every field any version
    /// carried unmasked, named once.
    /// </summary>
    public static async Task<SensitiveRead> DescribeVersionsAsync(
        Content entry,
        ContentTypeDefinition? definition,
        IReadOnlyList<(Dictionary<string, object> Stored, Dictionary<string, object> Shown)> versions,
        ISensitivityService sensitivity,
        HttpContext http,
        CancellationToken ct)
    {
        var fields = versions
            .SelectMany(v => FieldsShown(definition, v.Stored, v.Shown))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var document = versions.Count > 0
                       && !IsAudience(entry.Sensitivity)
                       && await sensitivity.MaySeeDocumentAsync(entry.Sensitivity, http.User, ct);
        return new SensitiveRead(entry.Id, entry.ContentType, entry.Sensitivity, fields, document);
    }

    /// <summary>Writes the request's row, or nothing when no entry sent anything above Public.</summary>
    public static async Task RecordAsync(HttpContext http, IReadOnlyList<SensitiveRead> reads, CancellationToken ct)
    {
        var kept = reads.Where(r => !r.IsEmpty).ToList();
        if (kept.Count == 0)
        {
            return;
        }

        var services = http.RequestServices;
        var session = services.GetRequiredService<IDocumentSession>();
        var tenant = services.GetRequiredService<TenantContext>();
        Guid? actor = Guid.TryParse(http.User.FindFirst("UserId")?.Value, out var id) ? id : null;

        var metadata = new Dictionary<string, object>
        {
            ["route"] = Clip(RouteOf(http)),
            ["entries"] = new Dictionary<string, object>
            {
                ["items"] = kept.Take(RoleAudit.MaxItems).Select(Describe).ToList(),
                ["count"] = kept.Count,
                ["truncated"] = kept.Count > RoleAudit.MaxItems,
            },
        };

        if (http.User.HasClaim("auth_method", "apikey")
            && http.User.FindFirst(barakoCMS.Infrastructure.Security.RateLimitAfterAuthentication.ApiKeyClaim)?.Value is { } keyId)
        {
            metadata["apiKeyId"] = Clip(keyId);
        }

        var single = kept.Count == 1 ? kept[0] : null;
        if (single is not null)
        {
            metadata["contentType"] = Clip(single.ContentType);
            metadata["fields"] = single.Fields.Take(RoleAudit.MaxItems).Select(Clip).ToList();
            metadata["sensitivity"] = single.Level.ToString();
        }

        await AuditLog.RecordAsync(
            session,
            tenant.Slug,
            Action,
            actor,
            http.User.FindFirst("Username")?.Value,
            targetType: "content",
            targetId: single?.EntryId.ToString(),
            metadata: metadata,
            ipAddress: barakoCMS.Infrastructure.DeviceContext.From(http).IpAddress,
            ct: ct);
        await session.SaveChangesAsync(ct);
    }

    /// <summary>
    /// The route template, not the path: a path holds the slug of a read by slug, and a slug field
    /// may itself be Sensitive.
    /// </summary>
    private static string RouteOf(HttpContext http)
    {
        var pattern = (http.GetEndpoint() as Microsoft.AspNetCore.Routing.RouteEndpoint)?.RoutePattern.RawText;
        if (string.IsNullOrEmpty(pattern))
        {
            return string.Empty;
        }

        return pattern.StartsWith('/') ? pattern : "/" + pattern;
    }

    private static Dictionary<string, object> Describe(SensitiveRead read) => new()
    {
        ["id"] = read.EntryId.ToString(),
        ["contentType"] = Clip(read.ContentType),
        ["sensitivity"] = read.Level.ToString(),
        ["fields"] = read.Fields.Take(RoleAudit.MaxItems).Select(Clip).ToList(),
    };

    private static string Clip(string? value) =>
        value is null ? string.Empty : value.Length <= RoleAudit.MaxLength ? value : value[..RoleAudit.MaxLength];
}
