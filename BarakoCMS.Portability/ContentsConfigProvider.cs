using System.Text.Json;
using barakoCMS.Core.Interfaces;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using Marten;
using Marten.Linq.MatchesSql;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using barakoCMS.Modules;

namespace BarakoCMS.Portability;

/// <summary>
/// The <c>contents</c> section: a document's entries, so entries move with the types through
/// <c>/api/config/export</c>, <c>plan</c> and <c>apply</c>.
/// </summary>
/// <remarks>
/// <para>
/// Entries are content, not configuration, so this section is exported only when it is named
/// (<c>?sections=contentTypes,contents</c>), and as the content read endpoints show them: each entry
/// as the caller may read it, tokens left out, and an entry the caller may read nothing of withheld.
/// </para>
/// <para>
/// Every record is created under a new id, with references inside the document pointed at the
/// new ids, through the same write path as <c>POST /api/contents</c>. So it is not idempotent:
/// applying the same entries twice creates them twice, and a plan names every record as a create. It needs a signed-in
/// caller, since what a caller may write is decided for that caller.
/// </para>
/// </remarks>
internal sealed class ContentsConfigProvider(
    IDocumentSession session,
    IContentCreator creator,
    IPermissionResolver permissions,
    ISensitivityService sensitivity,
    IHttpContextAccessor http,
    IConfiguration configuration) : IConfigProvider
{
    // Enums as names, which is how an export writes them, and numbers still read.
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    private const string ContentTypeInSql = "lower(d.data ->> 'ContentType') = ANY(?)";

    public string Section => "contents";

    public int Order => 900;

    public bool PlanShowsStoredValues => false;

    public string ReadCapability => PortabilityCapabilities.ExportContent;

    public string WriteCapability => PortabilityCapabilities.ImportContent;

    public bool ExportsByDefault => false;

    public bool AppliesAsHost => false;

    public async Task<ConfigSectionExport?> ExportAsync(ConfigCaller caller, CancellationToken ct)
    {
        if (await CallerAsync(caller, ct) is not { } user || http.HttpContext is not { } context)
        {
            return new ConfigSectionExport { Documents = new Dictionary<string, JsonElement>(), Gaps = ["Entries are exported for a signed-in caller only."] };
        }

        var types = (await session.Query<ContentTypeDefinition>().ToListAsync(ct)).ToList();

        // No more than an apply takes, so what this exports can always be applied back, and read no
        // more than that either: one over the limit is enough to know there are more.
        var max = PortabilityLimits.MaxImportRecords(configuration);
        IQueryable<Content> query = session.Query<Content>();
        if (caller.ExportTypes is { } wanted)
        {
            // In the database, before the limit, so a narrowed export reads only those types' entries.
            var names = wanted.ToArray();
            query = query.Where(c => c.MatchesSql(ContentTypeInSql, new object[] { names }));
            types = types.Where(t => wanted.Contains(t.Name.ToLowerInvariant())).ToList();
        }

        var contents = await query.OrderBy(c => c.Id).Take(max + 1).ToListAsync(ct);
        var more = contents.Count > max;
        var (records, withheld) = await BundleEntries.ExportAsync(contents.Take(max).ToList(), types, user, context, permissions, sensitivity, ct);
        var gaps = withheld == 0 ? new List<string>() : [$"{withheld} entries are left out because this caller may read none of them."];
        if (more)
        {
            gaps.Add($"Only the first {max} entries are read, the most an apply takes, and more are stored. Export fewer types with ?types=, or raise Portability:MaxImportRecords.");
        }

        return new ConfigSectionExport
        {
            Documents = records.ToDictionary(r => r.Id!.Value.ToString(), r => JsonSerializer.SerializeToElement(r, Json), StringComparer.Ordinal),
            Gaps = gaps,
        };
    }

    public async Task<ConfigSectionPlan> PlanAsync(
        IReadOnlyDictionary<string, JsonElement> desired, ConfigCaller caller, CancellationToken ct)
    {
        var errors = new List<string>();
        var records = Read(desired, errors);

        if (caller.IsSystem)
        {
            errors.Add("contents is written for a signed-in caller, so it is applied with POST /api/config/apply.");
        }

        var maxRecords = PortabilityLimits.MaxImportRecords(configuration);
        if (records.Count > maxRecords)
        {
            errors.Add($"The document holds {records.Count} entries and at most {maxRecords} are applied at once. Raise Portability:MaxImportRecords, or split it.");
        }

        var existing = (await session.Query<ContentTypeDefinition>().ToListAsync(ct)).ToList();
        var prepared = BundleEntries.Prepare(records.Select(r => r.Record).ToList(), existing, new ContentCreateBatch(), new ImportReport());
        errors.AddRange(prepared.Errors.Select(e => $"{Key(records, e.PropertyName)}: {e.ErrorMessage}"));

        var changes = records.Select(r => new ConfigChange
        {
            Key = r.Key, Kind = ConfigChangeKind.Create, Desired = r.Document, Summary = $"create a '{r.Record.ContentType}' entry",
        }).ToList();

        return new ConfigSectionPlan { Section = Section, Changes = changes, Errors = errors };
    }

    public async Task<IReadOnlyList<string>> ApplyAsync(ConfigSectionPlan plan, ConfigCaller caller, CancellationToken ct)
    {
        var errors = new List<string>();
        if (await CallerAsync(caller, ct) is not { } user || http.HttpContext is not { } context)
        {
            return ["contents is written for a signed-in caller."];
        }

        var records = Read(plan.Changes.ToDictionary(c => c.Key, c => c.Desired!.Value, StringComparer.Ordinal), errors);
        var existing = (await session.Query<ContentTypeDefinition>().ToListAsync(ct)).ToList();

        // No singleton cap: a bundle is a restore and lands what it holds.
        var batch = new ContentCreateBatch { CapSingletons = false };
        foreach (var definition in existing)
        {
            batch.UseSchema(definition);
        }

        var prepared = BundleEntries.Prepare(records.Select(r => r.Record).ToList(), existing, batch, new ImportReport());
        var refused = await BundleEntries.WriteAsync(session, creator, prepared, user.Id, context, batch, ct);
        errors.AddRange(prepared.Errors.Concat(refused).Select(e => $"{Key(records, e.PropertyName)}: {e.ErrorMessage}"));
        return errors;
    }

    private static List<(string Key, ContentRecord Record, JsonElement Document)> Read(
        IReadOnlyDictionary<string, JsonElement> desired, List<string> errors)
    {
        var records = new List<(string, ContentRecord, JsonElement)>();
        foreach (var (key, document) in desired)
        {
            try
            {
                if (document.ValueKind == JsonValueKind.Object && document.Deserialize<ContentRecord>(Json) is { } record)
                {
                    records.Add((key, record, document));
                    continue;
                }

                errors.Add($"'{key}' is not a JSON object.");
            }
            catch (JsonException ex)
            {
                errors.Add($"'{key}' cannot be read: {ex.Message}");
            }
        }

        return records;
    }

    /// <summary>The document key a refusal names as <c>contents[i]</c>.</summary>
    private static string Key(List<(string Key, ContentRecord Record, JsonElement Document)> records, string property)
    {
        var open = property.IndexOf('[');
        var close = property.IndexOf(']');
        return open >= 0 && close > open && int.TryParse(property[(open + 1)..close], out var index) && index < records.Count
            ? $"'{records[index].Key}'"
            : property;
    }

    private async Task<User?> CallerAsync(ConfigCaller caller, CancellationToken ct) =>
        caller.Principal is { } principal && principal.TryGetUserId(out var id) ? await session.LoadAsync<User>(id, ct) : null;
}
