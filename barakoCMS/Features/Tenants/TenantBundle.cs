using System.Text.Json;
using barakoCMS.Core.Interfaces;
using barakoCMS.Infrastructure.Multitenancy;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using Marten;
using Marten.Linq.MatchesSql;
using Microsoft.AspNetCore.Http;
using ContentDoc = barakoCMS.Models.Content;

namespace barakoCMS.Features.Tenants;

/// <summary>
/// The tenant bundle: one tenant's configuration and entries as a single JSON file, written by
/// <c>GET /api/tenants/{handle}/export</c> and read by <c>POST /api/tenants/import</c>.
/// </summary>
/// <remarks>
/// <para>
/// The configuration is the config engine's own export, so every section a provider writes
/// (content types, roles, rules, workflows, connectors, site settings, and a module's) travels
/// without a second description of it. The engine's rule holds here too: a credential is named at
/// most and never written, and an apply never stores one. Password hashes, API keys and the SMTP
/// settings are in no section, so none of them is in a bundle.
/// </para>
/// <para>
/// Entries are written one page at a time straight to the response, in the shape of the
/// Portability <c>contents</c> section, so a bundle's entries import through that section with its
/// id remapping and dependency order. The <c>site</c> entry is not repeated there: it is the
/// <c>siteSettings</c> section.
/// </para>
/// </remarks>
internal static class TenantBundle
{
    public const string Kind = "barako.tenant-bundle";

    public const int Version = 1;

    /// <summary>Entries read, and written to the response, in one round trip.</summary>
    public const int PageSize = 200;

    public const string MaxImportEntriesKey = "Tenants:MaxImportEntries";

    public const int DefaultMaxImportEntries = 5000;

    public const string ContentsSection = "contents";

    public static int MaxImportEntries(Microsoft.Extensions.Configuration.IConfiguration configuration) =>
        configuration.GetValue<int?>(MaxImportEntriesKey) is > 0 and var configured ? configured : DefaultMaxImportEntries;

    /// <summary>The entries of the tenant the scope is bound to, as the caller may read them, page by page.</summary>
    /// <returns>
    /// How many were written, how many were left out because the caller may read none of them, and how
    /// many were written with a field the caller may not read taken out or masked.
    /// </returns>
    public static async Task<(int Written, int Withheld, int Redacted)> WriteEntriesAsync(
        IServiceProvider scope, Utf8JsonWriter writer, Func<Task> flush, User caller, HttpContext http, CancellationToken ct)
    {
        var session = scope.GetRequiredService<IQuerySession>();
        var permissions = scope.GetRequiredService<IPermissionResolver>();
        var sensitivity = scope.GetRequiredService<ISensitivityService>();
        var types = (await session.Query<ContentTypeDefinition>().ToListAsync(ct)).ToList();

        const string siteType = barakoCMS.Features.Site.ShareLinks.ShareLinkKeys.SiteType;
        int written = 0, withheld = 0, redacted = 0;
        var last = Guid.Empty;
        while (true)
        {
            // Keyset paging: a write between two pages cannot repeat or skip an entry, and each page
            // is an index range rather than a count of every row before it.
            var after = last;
            var page = await session.Query<ContentDoc>()
                .Where(c => c.ContentType != siteType && c.MatchesSql("id > ?", after))
                .OrderBy(c => c.Id)
                .Take(PageSize)
                .ToListAsync(ct);
            if (page.Count == 0)
            {
                break;
            }

            last = page[^1].Id;
            foreach (var entry in page)
            {
                if (await RecordAsync(entry, types, caller, http, permissions, sensitivity, ct) is not { } record)
                {
                    withheld++;
                    continue;
                }

                JsonSerializer.Serialize(writer, record, barakoCMS.Features.Config.ConfigJson.Options);
                written++;
                redacted += record.MaskedFields.Count > 0 ? 1 : 0;
            }

            await flush();
        }

        return (written, withheld, redacted);
    }

    /// <summary>
    /// The entry as the caller may read it, or null when they may read none of it. A field the
    /// caller's read rules masked or removed is named in <c>MaskedFields</c> so an import leaves it
    /// out, and a generated token is left out for everyone: a bundle is a file, and a file gets passed on.
    /// </summary>
    private static async Task<EntryRecord?> RecordAsync(
        ContentDoc entry, List<ContentTypeDefinition> types, User caller, HttpContext http,
        IPermissionResolver permissions, ISensitivityService sensitivity, CancellationToken ct)
    {
        if (!await permissions.CanPerformActionAsync(caller, entry.ContentType, "read", entry, ct))
        {
            return null;
        }

        var data = new Dictionary<string, object>(entry.Data);
        var hidden = await sensitivity.ApplyAsync(entry.ContentType, entry.Sensitivity, data, http, ct);
        if (hidden || (data.Count == 0 && entry.Data.Count > 0))
        {
            return null;
        }

        var masked = entry.Data.Keys
            .Where(k => !data.TryGetValue(k, out var seen) || !ReferenceEquals(seen, entry.Data[k]))
            .ToList();

        var definition = types.FirstOrDefault(t => string.Equals(t.Name, entry.ContentType, StringComparison.OrdinalIgnoreCase));
        foreach (var field in (definition?.Fields ?? []).Where(f => f is not null && barakoCMS.Core.Validation.FieldTypeRegistry.IsServerGenerated(f)))
        {
            foreach (var key in data.Keys.Where(k => string.Equals(k, field.Name, StringComparison.OrdinalIgnoreCase)).ToList())
            {
                data.Remove(key);
            }

            masked.RemoveAll(k => string.Equals(k, field.Name, StringComparison.OrdinalIgnoreCase));
        }

        return new EntryRecord
        {
            Id = entry.Id,
            ContentType = entry.ContentType,
            Data = data,
            Status = entry.Status.ToString(),
            Sensitivity = entry.Sensitivity,
            MaskedFields = masked,
        };
    }

    /// <summary>One entry in the shape of a Portability <c>ContentRecord</c>.</summary>
    internal sealed class EntryRecord
    {
        public Guid Id { get; init; }
        public string ContentType { get; init; } = string.Empty;
        public Dictionary<string, object> Data { get; init; } = [];
        public string Status { get; init; } = nameof(ContentStatus.Published);
        public SensitivityLevel Sensitivity { get; init; }
        public List<string> MaskedFields { get; init; } = [];
    }

    /// <summary>
    /// A bundle read into the config document an apply takes, or the reasons it cannot be. The
    /// envelope keys of the export (<c>exportedAt</c>, <c>gaps</c>, <c>withheld</c>) are dropped
    /// because a config document refuses a key it does not know, and the entries become the
    /// <c>contents</c> section, keyed by their exported id.
    /// </summary>
    public static JsonElement? ToConfigDocument(JsonElement bundle, int maxEntries, List<string> errors)
    {
        if (EnvelopeError(bundle) is { } envelope)
        {
            errors.Add(envelope);
            return null;
        }

        if (!bundle.TryGetProperty("config", out var config) || config.ValueKind != JsonValueKind.Object)
        {
            errors.Add("bundle holds no config object.");
            return null;
        }

        var document = new System.Text.Json.Nodes.JsonObject();
        foreach (var property in config.EnumerateObject().Where(p => p.Name is not ("exportedAt" or "gaps" or "withheld")))
        {
            document[property.Name] = System.Text.Json.Nodes.JsonNode.Parse(property.Value.GetRawText());
        }

        if (!bundle.TryGetProperty("contents", out var contents) || contents.ValueKind != JsonValueKind.Array)
        {
            errors.Add("bundle holds no contents array.");
            return null;
        }

        if (contents.GetArrayLength() > maxEntries)
        {
            errors.Add($"bundle holds {contents.GetArrayLength()} entries and at most {maxEntries} are imported at once. "
                       + $"Raise {MaxImportEntriesKey}.");
            return null;
        }

        if (contents.GetArrayLength() > 0)
        {
            var entries = new System.Text.Json.Nodes.JsonObject();
            var index = 0;
            foreach (var entry in contents.EnumerateArray())
            {
                var key = entry.ValueKind == JsonValueKind.Object && entry.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String
                    ? id.GetString()!
                    : $"#{index}";
                if (!entries.TryAdd(key, System.Text.Json.Nodes.JsonNode.Parse(entry.GetRawText())))
                {
                    errors.Add($"contents holds the id '{key}' more than once.");
                }

                index++;
            }

            document[ContentsSection] = entries;
        }

        return errors.Count > 0 ? null : JsonSerializer.SerializeToElement(document);
    }

    private static string? EnvelopeError(JsonElement bundle)
    {
        if (bundle.ValueKind != JsonValueKind.Object)
        {
            return "bundle must be the JSON object GET /api/tenants/{handle}/export returns.";
        }

        if (!bundle.TryGetProperty("kind", out var kind) || kind.ValueKind != JsonValueKind.String || kind.GetString() != Kind)
        {
            return $"bundle is not a tenant bundle: its kind must be \"{Kind}\".";
        }

        if (!bundle.TryGetProperty("bundleVersion", out var version) || !version.TryGetInt32(out var number) || number < 1)
        {
            return "bundle declares no bundleVersion.";
        }

        return number > Version
            ? $"bundle is written for version {number} of the format, and this server reads up to version {Version}."
            : null;
    }
}
