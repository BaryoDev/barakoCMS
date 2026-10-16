using System.Text.Json;
using barakoCMS.Core.Interfaces;
using barakoCMS.Features.Config;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using Marten;
using ContentDoc = barakoCMS.Models.Content;

namespace barakoCMS.Features.Site;

/// <summary>The tenant's <c>site</c> entry as its document: its status and its values.</summary>
internal sealed record SiteConfig(string? Status, Dictionary<string, object>? Data);

/// <summary>
/// The <c>siteSettings</c> section: the tenant's <c>site</c> entry, under the one key <c>site</c>.
/// </summary>
/// <remarks>
/// <para>
/// The site entry is content, so it is read and written the way an entry is, for the caller: their
/// read and write rules on the <c>site</c> type, the field sensitivity that masks what they may not
/// see, validation against the type, and the type's lifecycle hooks. A field masked for the caller
/// is neither exported nor changed. That is why this section needs a signed-in caller and the seeder
/// cannot apply it.
/// </para>
/// <para>
/// The entry is never removed from here. A field the stored entry holds and the document leaves out
/// is cleared, destructively. The <c>site</c> type itself is in <c>contentTypes</c>.
/// </para>
/// </remarks>
internal sealed class SiteSettingsConfigProvider(
    IDocumentSession session,
    IHttpContextAccessor http,
    IPermissionResolver permissions,
    ISensitivityService sensitivity,
    IContentCreator creator,
    IContentValidatorService validator,
    IContentLifecycleRunner lifecycle,
    IContentWriter writer,
    barakoCMS.Infrastructure.Multitenancy.BatchTransaction batch) : IConfigProvider
{
    public const string Key = "site";

    private const string SiteType = barakoCMS.Features.Site.ShareLinks.ShareLinkKeys.SiteType;

    public string Section => "siteSettings";

    public int Order => 600;

    public string ReadCapability => SystemCapabilities.ManageConfig;

    public string WriteCapability => SystemCapabilities.ManageConfig;

    public bool AppliesAsHost => false;

    public async Task<ConfigSectionExport?> ExportAsync(ConfigCaller caller, CancellationToken ct)
    {
        var gaps = new List<string>();
        var documents = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var entry = await EntryAsync(ct);

        if (entry is not null && await VisibleAsync(entry, caller, gaps, ct) is { } data)
        {
            documents[Key] = ConfigJson.ToElement(new SiteConfig(entry.Status.ToString(), data));
        }

        return new ConfigSectionExport { Documents = documents, Gaps = gaps };
    }

    public async Task<ConfigSectionPlan> PlanAsync(
        IReadOnlyDictionary<string, JsonElement> desired, ConfigCaller caller, CancellationToken ct)
    {
        var errors = new List<string>();
        var refusals = new List<string>();
        var changes = new List<ConfigChange>();

        foreach (var other in desired.Keys.Where(k => k != Key))
        {
            errors.Add($"'{other}' is not a key of siteSettings, which holds one document, '{Key}'.");
        }

        if (!desired.TryGetValue(Key, out var document) || Read(document, errors) is not { } config)
        {
            return new ConfigSectionPlan { Section = Section, Errors = errors };
        }

        if (caller.IsSystem || await CallerAsync(caller, ct) is not { } user)
        {
            errors.Add("siteSettings is written as an entry, for a signed-in caller, so it is applied with POST /api/config/apply.");
            return new ConfigSectionPlan { Section = Section, Errors = errors };
        }

        if (!await session.Query<ContentTypeDefinition>().AnyAsync(d => d.Name == SiteType, ct))
        {
            errors.Add($"this tenant has no '{SiteType}' content type. Add it to contentTypes, or apply the site blueprint.");
            return new ConfigSectionPlan { Section = Section, Errors = errors };
        }

        var entry = await EntryAsync(ct);
        if (!await permissions.CanPerformActionAsync(user, SiteType, entry is null ? "create" : "update", entry, ct))
        {
            refusals.Add($"This caller may not {(entry is null ? "create" : "update")} the {SiteType} entry.");
        }

        if (await PlanChangeAsync(entry, config, document, caller, ct) is { } change)
        {
            changes.Add(change);
        }

        return new ConfigSectionPlan { Section = Section, Changes = changes, Errors = errors, Refusals = refusals };
    }

    public async Task<IReadOnlyList<string>> ApplyAsync(ConfigSectionPlan plan, ConfigCaller caller, CancellationToken ct)
    {
        var errors = new List<string>();
        if (plan.Changes.SingleOrDefault() is not { Desired: { } document }
            || Read(document, errors) is not { } config
            || await CallerAsync(caller, ct) is not { } user)
        {
            return errors;
        }

        if (http.HttpContext is not { } context)
        {
            return ["siteSettings is written the way the content routes write an entry, which reads the request, and there is none."];
        }

        var data = new Dictionary<string, object>(config.Data ?? [], StringComparer.OrdinalIgnoreCase);
        var entry = await EntryAsync(ct);
        if (entry is null)
        {
            await CreateAsync(user, data, Status(config) ?? ContentStatus.Published, context, errors, ct);
        }
        else
        {
            await UpdateAsync(user, entry, data, Status(config), context, errors, ct);
        }

        return errors;
    }

    private async Task<ConfigChange?> PlanChangeAsync(
        ContentDoc? entry, SiteConfig config, JsonElement document, ConfigCaller caller, CancellationToken ct)
    {
        if (entry is null)
        {
            return new ConfigChange { Key = Key, Kind = ConfigChangeKind.Create, Desired = document, Summary = $"create the {SiteType} entry" };
        }

        var visible = await VisibleAsync(entry, caller, [], ct) ?? [];
        var data = config.Data ?? [];
        var status = Status(config) ?? entry.Status;
        if (status == entry.Status && ConfigJson.Same(visible, data))
        {
            return null;
        }

        var details = new List<string>();
        var cleared = visible.Keys.Where(k => !data.Keys.Contains(k, StringComparer.OrdinalIgnoreCase)).Order(StringComparer.Ordinal).ToList();
        details.AddRange(data.Keys
            .Where(k => !visible.TryGetValue(k, out var held) || !ConfigJson.Same(held, data[k]))
            .Order(StringComparer.Ordinal)
            .Select(k => $"'{k}' is set"));
        details.AddRange(cleared.Select(k => $"'{k}' is cleared"));
        if (status != entry.Status)
        {
            details.Add($"status changes from {entry.Status} to {status}");
        }

        return new ConfigChange
        {
            Key = Key, Kind = cleared.Count > 0 ? ConfigChangeKind.Destructive : ConfigChangeKind.Change,
            Desired = document, Summary = $"change the {SiteType} entry", Details = details,
        };
    }

    private async Task CreateAsync(
        User user, Dictionary<string, object> data, ContentStatus status, HttpContext context, List<string> errors, CancellationToken ct)
    {
        var request = new ContentCreateRequest { ContentType = SiteType, Data = data, Status = status };

        errors.AddRange(await creator.CheckAsync(request, user.Id, context, batch: null, ct));
        if (errors.Count == 0 && !await permissions.AllowsCreatedEntryAsync(user, request, ct))
        {
            errors.Add($"This caller's create rule on {SiteType} does not hold for the entry as it would be stored.");
        }

        if (errors.Count == 0)
        {
            await creator.StageAsync(request, user.Id, batch: null, ct);
        }
    }

    /// <summary>The update route's checks and write, for the entry the tenant holds.</summary>
    private async Task UpdateAsync(
        User user, ContentDoc entry, Dictionary<string, object> data, ContentStatus? status, HttpContext context,
        List<string> errors, CancellationToken ct)
    {
        // A field the caller may not see keeps its stored value, and one they may not set is refused.
        await sensitivity.ApplyWriteAsync(entry, data, context, ct);
        if (!await permissions.AllowsWrittenEntryAsync(user, entry, "update", data, ct))
        {
            errors.Add($"This caller's update rule on {SiteType} does not hold for the entry as it would be stored.");
            return;
        }

        var (valid, problems) = await validator.ValidateAsync(SiteType, data, entry, context.User);
        errors.AddRange(valid ? [] : problems);
        errors.AddRange(await lifecycle.RunBeforeSaveAsync(SiteType, entry.Id, data, entry.Data, user.Id, ct));
        if (errors.Count > 0)
        {
            return;
        }

        var events = new List<object> { new barakoCMS.Events.ContentUpdated(entry.Id, data, user.Id, await SearchTextAsync(data, ct), DateTime.UtcNow) };
        if (status is { } next && next != entry.Status)
        {
            events.Add(new barakoCMS.Events.ContentStatusChanged(entry.Id, next, user.Id, DateTime.UtcNow));
        }

        var version = (await session.Events.FetchStreamStateAsync(entry.Id, ct))?.Version;
        if (batch.DefersStreams)
        {
            // Appended at the commit, never before it: an event written early in the apply's long
            // transaction would hold its sequence open, and a plan, which rolls back, writes none.
            batch.DeferAppend((_, token) => writer.AppendAsync(entry, events, version, token));
            return;
        }

        try
        {
            await writer.AppendAsync(entry, events, version, ct);
        }
        catch (StaleContentException ex)
        {
            errors.Add(ex.Message);
        }
    }

    private async Task<string> SearchTextAsync(Dictionary<string, object> data, CancellationToken ct)
    {
        var definition = await session.Query<ContentTypeDefinition>().FirstOrDefaultAsync(d => d.Name == SiteType, ct);
        var publicFields = (definition?.Fields ?? [])
            .Where(f => f.Sensitivity == SensitivityLevel.Public && !barakoCMS.Core.Validation.InlineImageFields.Is(f.Type) && string.IsNullOrEmpty(f.ShownWhenListedIn))
            .Select(f => f.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return string.Join(' ', data.Where(kv => publicFields.Contains(kv.Key)).Select(kv => kv.Value?.ToString()).Where(v => !string.IsNullOrWhiteSpace(v)));
    }

    /// <summary>
    /// The entry's values as this caller may read them, with what the server generates left out, or
    /// null when the caller may read none of it. What is left out is said in <paramref name="gaps"/>.
    /// </summary>
    private async Task<Dictionary<string, object>?> VisibleAsync(ContentDoc entry, ConfigCaller caller, List<string> gaps, CancellationToken ct)
    {
        var data = new Dictionary<string, object>(entry.Data, StringComparer.OrdinalIgnoreCase);
        var definition = await session.Query<ContentTypeDefinition>().FirstOrDefaultAsync(d => d.Name == entry.ContentType, ct);
        foreach (var field in (definition?.Fields ?? []).Where(f => barakoCMS.Core.Validation.FieldTypeRegistry.IsServerGenerated(f)))
        {
            data.Remove(field.Name);
        }

        if (caller.IsSystem)
        {
            return data;
        }

        if (await CallerAsync(caller, ct) is not { } user
            || http.HttpContext is not { } context
            || !await permissions.CanPerformActionAsync(user, entry.ContentType, "read", entry, ct)
            || await sensitivity.ApplyAsync(entry, data, context, ct))
        {
            gaps.Add($"The {SiteType} entry is not readable by this caller, so it is not exported.");
            return null;
        }

        foreach (var masked in data.Where(kv => entry.Data.TryGetValue(kv.Key, out var raw) && !ReferenceEquals(raw, kv.Value)).Select(kv => kv.Key).ToList())
        {
            data.Remove(masked);
            gaps.Add($"'{masked}' is masked for this caller, so it is left out.");
        }

        return data;
    }

    private async Task<ContentDoc?> EntryAsync(CancellationToken ct) =>
        await session.Query<ContentDoc>().Where(c => c.ContentType == SiteType).OrderBy(c => c.CreatedAt).FirstOrDefaultAsync(ct);

    private async Task<User?> CallerAsync(ConfigCaller caller, CancellationToken ct) =>
        Guid.TryParse(caller.Principal?.FindFirst("UserId")?.Value, out var id) ? await session.LoadAsync<User>(id, ct) : null;

    private static SiteConfig? Read(JsonElement document, List<string> errors)
    {
        if (ConfigJson.Read<SiteConfig>(Key, document, errors) is not { } config)
        {
            return null;
        }

        if (config.Status is not null && Status(config) is null)
        {
            errors.Add($"'{Key}': status is one of {string.Join(", ", Enum.GetNames<ContentStatus>())}.");
            return null;
        }

        return config;
    }

    private static ContentStatus? Status(SiteConfig config) =>
        Enum.TryParse<ContentStatus>(config.Status, ignoreCase: true, out var status) && Enum.IsDefined(status) ? status : null;
}
