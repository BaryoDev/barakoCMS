using System.Text.Json;
using System.Text.Json.Nodes;
using barakoCMS.Core.Interfaces;
using barakoCMS.Features.Config;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using Marten;
using Marten.Linq.MatchesSql;

namespace barakoCMS.Features.ContentType;

/// <summary>
/// The <c>contentTypes</c> section: each content type of the tenant under its name, with
/// <c>eventSourced</c> beside its definition.
/// </summary>
/// <remarks>
/// A type is written the way a Portability import writes one (<see cref="ContentTypeUpsert"/>), so
/// the two refuse the same things. A type the document leaves out is kept: the API has no route
/// that removes a content type, and an apply is not a way around that. Removing a field, changing a
/// field's type or what a reference points at loses the values entries hold, so each is destructive.
///
/// The field routes' 409s hold here too: a type with a field change running is refused, as every
/// <c>{name}</c> route refuses it, and so is leaving out a field a block binding names. Both answer
/// the apply with 409 and write nothing.
/// </remarks>
internal sealed class ContentTypeConfigProvider(
    IDocumentSession session,
    IContentTypeValidatorService validator,
    IContentSourcingPolicy sourcing,
    ICallerCapabilities capabilities,
    IConfiguration configuration,
    barakoCMS.Infrastructure.Multitenancy.TenantContext tenant) : IConfigProvider
{
    public string Section => "contentTypes";

    public int Order => 100;

    public string ReadCapability => SystemCapabilities.ManageContentTypes;

    public string WriteCapability => SystemCapabilities.ManageContentTypes;

    private static readonly string[] NotCarried = ["id", "name", "createdAt", "updatedAt"];

    /// <summary>The cap the Portability import puts on a bundle's types, so a bundle sent here is held to it too.</summary>
    public const int MaxPerApply = 500;

    public async Task<ConfigSectionExport?> ExportAsync(ConfigCaller caller, CancellationToken ct)
    {
        var types = (await session.Query<ContentTypeDefinition>().ToListAsync(ct)).ToList();

        // A stored field lists roles by id, and an id means nothing on the deployment a document is
        // applied to. The document carries names, which the apply matches against its own roles.
        await barakoCMS.Core.RoleReferences.ToNamesAsync(session, types.SelectMany(t => t.Fields ?? []), ct);

        var documents = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var type in types)
        {
            documents[type.Name] = Canonical(type, await sourcing.IsEventSourcedAsync(type.Name, ct));
        }

        return new ConfigSectionExport { Documents = documents };
    }

    public async Task<ConfigSectionPlan> PlanAsync(
        IReadOnlyDictionary<string, JsonElement> desired, ConfigCaller caller, CancellationToken ct)
    {
        var errors = new List<string>();
        var changes = new List<ConfigChange>();
        var disclosures = new List<(string Name, bool On)>();
        var stored = (await session.Query<ContentTypeDefinition>().ToListAsync(ct)).ToList();

        if (desired.Count > MaxPerApply)
        {
            errors.Add($"The document holds {desired.Count} content types, and an apply takes at most {MaxPerApply}.");
            return new ConfigSectionPlan { Section = Section, Errors = errors };
        }

        var busy = await LockStoredTypesAsync(desired.Keys, stored, ct);
        stored = (await session.Query<ContentTypeDefinition>().ToListAsync(ct)).ToList();
        var conflicts = new List<string>();

        foreach (var duplicate in desired.Keys.GroupBy(barakoCMS.Core.ContentTypeName.Normalize).Where(g => g.Count() > 1))
        {
            errors.Add($"{string.Join(", ", duplicate)} name the same content type '{duplicate.Key}'.");
        }

        foreach (var (key, document) in desired.OrderBy(d => d.Key, StringComparer.Ordinal))
        {
            if (ContentTypeUpsert.StoredMatch(stored, key) is { } held && busy.TryGetValue(held.Name.ToLower(), out var running))
            {
                conflicts.Add(running);
                continue;
            }

            var resolved = await ResolveAsync(key, document, stored, errors, ct);
            if (resolved is null)
            {
                continue;
            }

            var (type, match, _) = resolved.Value;
            if (match is null)
            {
                changes.Add(new ConfigChange
                {
                    Key = key, Kind = ConfigChangeKind.Create, Desired = document,
                    Summary = $"create content type '{barakoCMS.Core.ContentTypeName.Normalize(key)}' with {type.Fields.Count} fields",
                });
                continue;
            }

            if (match.IsPubliclyDeliverable != type.IsPubliclyDeliverable)
            {
                disclosures.Add((match.Name, type.IsPubliclyDeliverable));
            }

            conflicts.AddRange(await RemovedFieldsBindingsNameAsync(match, type, ct));
            if (Difference(match, type) is { } change)
            {
                changes.Add(new ConfigChange
                {
                    Key = key, Kind = change.Destructive ? ConfigChangeKind.Destructive : ConfigChangeKind.Change,
                    Desired = document, Summary = $"change content type '{match.Name}'", Details = change.Details,
                });
            }
        }

        var refusals = await DisclosureRulesAsync(disclosures, caller, errors, ct);
        return new ConfigSectionPlan { Section = Section, Changes = changes, Errors = errors, Refusals = refusals, Conflicts = conflicts };
    }

    /// <summary>
    /// Takes the field-change lock of each stored type the document updates, in one order, and
    /// returns the refusal the type's <c>{name}</c> routes give, by lowered name, for each with a
    /// field change running.
    /// </summary>
    /// <remarks>
    /// Held until the apply commits or rolls back, so no field change starts on a type between this
    /// check and the write, and no block binding is saved on one. The caller reads the types again
    /// after this, so a change that ran to completion before the lock is seen in what it read.
    /// </remarks>
    private async Task<Dictionary<string, string>> LockStoredTypesAsync(
        IEnumerable<string> keys, List<ContentTypeDefinition> stored, CancellationToken ct)
    {
        var busy = new Dictionary<string, string>(StringComparer.Ordinal);
        var names = keys
            .Select(k => ContentTypeUpsert.StoredMatch(stored, k)?.Name.ToLower())
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal);

        foreach (var name in names)
        {
            await barakoCMS.Infrastructure.Jobs.FieldChanges.LockTypeAsync(session, name, ct);
            if (await barakoCMS.Infrastructure.Jobs.FieldChanges.RunningAsync(session, name, ct) is { } running)
            {
                busy[name] = barakoCMS.Infrastructure.Jobs.FieldChanges.Busy(running);
            }
        }

        return busy;
    }

    /// <summary>
    /// The refusal the field change route gives for removing a field a block binding names, for each
    /// stored field <paramref name="type"/> leaves out that one does.
    /// </summary>
    private async Task<List<string>> RemovedFieldsBindingsNameAsync(
        ContentTypeDefinition stored, ContentTypeDefinition type, CancellationToken ct)
    {
        var conflicts = new List<string>();
        foreach (var removed in stored.Fields.Where(f => !type.Fields.Any(t => t.Name.Equals(f.Name, StringComparison.OrdinalIgnoreCase))))
        {
            var bindings = await barakoCMS.Features.Blocks.Bindings.BindingFields.NamingAsync(session, stored.Name, removed.Name, ct);
            if (bindings.Count > 0)
            {
                conflicts.Add($"'{stored.Name}': {barakoCMS.Features.Blocks.Bindings.BindingFields.Refusal(removed.Name, bindings)}");
            }
        }

        return conflicts;
    }

    /// <summary>
    /// Switching public delivery on or off for a stored type is the disclosure decision
    /// <c>PUT /api/content-types/{name}/public-delivery</c> makes, so it takes that route's
    /// capability, and its acknowledgement where the deployment asks for one.
    /// </summary>
    private async Task<List<string>> DisclosureRulesAsync(
        List<(string Name, bool On)> disclosures, ConfigCaller caller, List<string> errors, CancellationToken ct)
    {
        if (disclosures.Count == 0)
        {
            return [];
        }

        if (configuration.GetValue("PublicDelivery:RequireAcknowledgement", false))
        {
            errors.AddRange(disclosures.Where(d => d.On).Select(d =>
                $"'{d.Name}': this deployment asks for public delivery to be acknowledged. Turn it on with "
                + $"PUT /api/content-types/{d.Name}/public-delivery and acknowledgeExposure."));
        }

        return caller.Principal is { } principal && !await capabilities.HoldsAsync(principal, SystemCapabilities.ManagePublicDelivery, ct)
            ? [$"Switching public delivery for {string.Join(", ", disclosures.Select(d => $"'{d.Name}'"))} takes the "
               + $"'{SystemCapabilities.ManagePublicDelivery}' capability, which this caller is missing."]
            : [];
    }

    public async Task<IReadOnlyList<string>> ApplyAsync(ConfigSectionPlan plan, ConfigCaller caller, CancellationToken ct)
    {
        var errors = new List<string>();
        var stored = (await session.Query<ContentTypeDefinition>().ToListAsync(ct)).ToList();

        foreach (var change in plan.Changes)
        {
            if (await ResolveAsync(change.Key, change.Desired!.Value, stored, errors, ct) is not { } resolved)
            {
                continue;
            }

            var (type, match, eventSourced) = resolved;
            if (match is null)
            {
                var created = ContentTypeUpsert.Create(type);
                session.Store(created);
                stored.Add(created);
                await sourcing.DecideAsync(created.Name, eventSourced, ct);
            }
            else
            {
                var (delivered, route) = (match.IsPubliclyDeliverable, match.RouteTemplate);
                ContentTypeUpsert.MergeInto(match, type);
                session.Store(match);
                await AuditAsync(match, delivered, route, caller, ct);
            }
        }

        return errors;
    }

    /// <summary>The audit entries the public delivery and route template routes write, for the same changes.</summary>
    private async Task AuditAsync(ContentTypeDefinition type, bool deliveredBefore, string? routeBefore, ConfigCaller caller, CancellationToken ct)
    {
        Guid? actorId = Guid.TryParse(caller.Principal?.FindFirst("UserId")?.Value, out var id) ? id : null;
        var actor = caller.Principal?.FindFirst("Username")?.Value ?? "system";

        if (type.IsPubliclyDeliverable != deliveredBefore)
        {
            var name = type.Name;
            var published = await session.Query<barakoCMS.Models.Content>()
                .CountAsync(c => c.ContentType == name && c.Status == ContentStatus.Published, ct);
            await barakoCMS.Infrastructure.Audit.AuditLog.RecordAsync(session, tenant.Slug,
                type.IsPubliclyDeliverable ? "contenttype.publicdelivery.enabled" : "contenttype.publicdelivery.disabled",
                actorId, actor, targetType: "ContentType", targetId: type.Id.ToString(),
                metadata: new Dictionary<string, object>
                {
                    ["contentType"] = type.Name, ["enabled"] = type.IsPubliclyDeliverable,
                    ["publishedEntries"] = published, ["via"] = "config",
                }, ct: ct);
        }

        if (!string.Equals(type.RouteTemplate, routeBefore, StringComparison.Ordinal))
        {
            await barakoCMS.Infrastructure.Audit.AuditLog.RecordAsync(session, tenant.Slug, "contenttype.routetemplate.changed",
                actorId, actor, targetType: "ContentType", targetId: type.Id.ToString(),
                metadata: new Dictionary<string, object>
                {
                    ["contentType"] = type.Name, ["from"] = routeBefore ?? "none", ["to"] = type.RouteTemplate ?? "none", ["via"] = "config",
                }, ct: ct);
        }
    }

    /// <summary>
    /// The document read, brought to the stored shape and checked, with the stored type it updates.
    /// Null, with the reasons added to <paramref name="errors"/>, when it cannot be written.
    /// </summary>
    private async Task<(ContentTypeDefinition Type, ContentTypeDefinition? Match, bool EventSourced)?> ResolveAsync(
        string key, JsonElement document, List<ContentTypeDefinition> stored, List<string> errors, CancellationToken ct)
    {
        var problems = new List<string>();
        var type = ConfigJson.Read<ContentTypeDefinition>(key, document, problems);
        if (type is null)
        {
            errors.AddRange(problems);
            return null;
        }

        type.Name = key;
        var eventSourced = document.TryGetProperty("eventSourced", out var flag) && flag.ValueKind == JsonValueKind.True;
        var match = ContentTypeUpsert.StoredMatch(stored, key);
        var maxFields = ContentTypeFieldLimit.Resolve(configuration);

        if (ContentTypeUpsert.FieldCapError(type, match, maxFields) is { } tooMany)
        {
            problems.Add(tooMany);
        }
        else
        {
            await ContentTypeUpsert.PrepareAsync(session, type, match, ct);
            problems.AddRange(await ContentTypeUpsert.ErrorsAsync(
                validator, sourcing, session, type, match, maxFields, ContentTypeUpsert.Wording.Config, ct));
            problems.AddRange(await SourcingErrorsAsync(type, match, eventSourced, ct));
        }

        errors.AddRange(problems.Select(p => $"'{key}': {p}"));
        return problems.Count == 0 ? (type, match, eventSourced) : null;
    }

    /// <summary>The create endpoint's rules for <c>eventSourced</c>, which is decided once per name.</summary>
    private async Task<List<string>> SourcingErrorsAsync(
        ContentTypeDefinition type, ContentTypeDefinition? match, bool eventSourced, CancellationToken ct)
    {
        var errors = new List<string>();
        var name = match?.Name ?? barakoCMS.Core.ContentTypeName.Normalize(type.Name);
        var standing = await sourcing.GetAsync(name, ct);

        if (standing is not null && standing.EventSourced != eventSourced)
        {
            errors.Add($"'{name}' was decided with eventSourced set to {(standing.EventSourced ? "true" : "false")}, "
                       + "and that decision belongs to the name and cannot change.");
            return errors;
        }

        if (standing is null && eventSourced)
        {
            var entries = await session.Query<barakoCMS.Models.Content>().Where(c => c.MatchesSql(EntriesUnderNameSql, name)).CountAsync(ct);
            if (entries > 0)
            {
                errors.Add($"'{name}' already has entries written under document sourcing, so it cannot become event sourced.");
            }

            var nonPublic = type.Fields.Where(f => f.Sensitivity != SensitivityLevel.Public).Select(f => f.Name).ToList();
            if (nonPublic.Count > 0)
            {
                errors.Add($"an event-sourced type may not hold non-Public fields, and {string.Join(", ", nonPublic)} "
                           + $"{(nonPublic.Count == 1 ? "is" : "are")} not Public.");
            }
        }

        return errors;
    }

    /// <summary>The create endpoint's count of entries under a name, normalised the way the name is.</summary>
    private const string EntriesUnderNameSql =
        "replace(lower(btrim(d.data ->> 'ContentType', ' ' || chr(9) || chr(10) || chr(13))), ' ', '-') = ?";

    private sealed record Diff(bool Destructive, List<string> Details);

    /// <summary>What writing <paramref name="type"/> over <paramref name="stored"/> changes, or null for nothing.</summary>
    private static Diff? Difference(ContentTypeDefinition stored, ContentTypeDefinition type)
    {
        var merged = JsonSerializer.Deserialize<ContentTypeDefinition>(ConfigJson.ToElement(stored), ConfigJson.Options)!;
        ContentTypeUpsert.MergeInto(merged, type);

        var before = Canonical(stored, eventSourced: false);
        var after = Canonical(merged, eventSourced: false);
        if (JsonElement.DeepEquals(before, after))
        {
            return null;
        }

        var details = new List<string>();
        var destructive = false;

        foreach (var property in after.EnumerateObject().Where(p => p.Name != "fields"))
        {
            if (!before.TryGetProperty(property.Name, out var was) || !JsonElement.DeepEquals(was, property.Value))
            {
                details.Add($"{property.Name} changes");
            }
        }

        foreach (var field in stored.Fields)
        {
            var next = merged.Fields.FirstOrDefault(f => f.Name.Equals(field.Name, StringComparison.OrdinalIgnoreCase));
            if (next is null)
            {
                destructive = true;
                details.Add($"field '{field.Name}' is removed, with the values entries hold in it");
            }
            else if (!string.Equals(next.Type, field.Type, StringComparison.OrdinalIgnoreCase)
                     || !string.Equals(next.ReferenceType, field.ReferenceType, StringComparison.OrdinalIgnoreCase))
            {
                destructive = true;
                details.Add($"field '{field.Name}' changes type or target, from {Describe(field)} to {Describe(next)}");
            }
            else if (!ConfigJson.Same(field, next))
            {
                details.Add($"field '{field.Name}' changes");
            }
        }

        details.AddRange(merged.Fields
            .Where(f => !stored.Fields.Any(s => s.Name.Equals(f.Name, StringComparison.OrdinalIgnoreCase)))
            .Select(f => $"field '{f.Name}' is added"));

        return new Diff(destructive, details);
    }

    private static string Describe(FieldDefinition field) =>
        field.ReferenceType is { Length: > 0 } target ? $"{field.Type} of {target}" : field.Type;

    /// <summary>A type as its document: the definition without what is particular to one database.</summary>
    private static JsonElement Canonical(ContentTypeDefinition type, bool eventSourced)
    {
        var node = JsonSerializer.SerializeToNode(type, ConfigJson.Options)!.AsObject();
        foreach (var name in NotCarried)
        {
            node.Remove(name);
        }

        node["eventSourced"] = eventSourced;
        return JsonSerializer.SerializeToElement(node, ConfigJson.Options);
    }
}
