using FastEndpoints;
using Marten;
using barakoCMS.Core.Validation;
using barakoCMS.Infrastructure.Audit;
using barakoCMS.Infrastructure.Auth;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;

namespace barakoCMS.Features.ContentType.SetUniqueness;

/// <summary>
/// PUT /api/content-types/{name}/uniqueness, which sets the type's uniqueness rules: the values only
/// one entry may hold at a time.
/// </summary>
/// <remarks>
/// Its own endpoint because content types have no general update, and a type stored before rules
/// existed has no other way to get one.
///
/// No entry is read into memory or rewritten. For each rule added or changed, the entries that
/// already share their values with another entry the rule counts are counted in the database, and
/// the change is refused with 409 unless <c>force</c> is set. With it the rule is stored and those
/// entries stay as they are: each can still be edited, moved and erased, and only a write that
/// would bring an entry to values another entry holds is refused. The count is a reading taken
/// before the save, and a write that read the type before the save lands can still add to it.
/// </remarks>
internal class Endpoint(
    IDocumentSession session,
    IDocumentStore store,
    IContentTypeValidatorService validator,
    barakoCMS.Infrastructure.Multitenancy.TenantContext tenant) : Endpoint<Request, Response>
{
    public override void Configure()
    {
        Put("/api/content-types/{name}/uniqueness");
        // What a type's entries may hold is modelling, the same gate as creating the type.
        Definition.RequireCapability(SystemCapabilities.ManageContentTypes, "SuperAdmin", "Admin");
    }

    public override async Task HandleAsync(Request req, CancellationToken ct)
    {
        var name = barakoCMS.Core.ContentTypeName.Normalize(Route<string>("name") ?? string.Empty);

        var def = await session.Query<ContentTypeDefinition>()
            .FirstOrDefaultAsync(d => d.Name.ToLower() == name, ct);

        if (def is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var requested = req.Uniqueness is { Count: > 0 } ? req.Uniqueness : null;

        var (valid, errors) = validator.ValidateUniqueness(requested, def.Fields, def.Lifecycle);
        if (!valid)
        {
            foreach (var error in errors) AddError(error);
            await Send.ErrorsAsync(400, ct);
            return;
        }

        if (UniquenessRules.Same(def.Uniqueness, requested))
        {
            await Send.OkAsync(new Response { Name = def.Name, Uniqueness = def.Uniqueness }, ct);
            return;
        }

        var duplicates = await CountDuplicatesAsync(def, requested ?? [], ct);

        if (duplicates.Count > 0 && !req.Force)
        {
            foreach (var rule in duplicates)
            {
                AddError(
                    $"{rule.Entries} {(rule.Entries == 1 ? "entry shares its" : "entries share their")} values "
                    + $"under the rule '{rule.Rule}' with another entry. They are left as they are and can "
                    + "still be edited; a write that would bring another entry to values one of them holds is "
                    + $"refused. GET /api/content-types/{def.Name}/uniqueness/{rule.Rule}/duplicates lists them.");
            }

            AddError("Resend with force set to true to go ahead anyway.");
            await Send.ErrorsAsync(409, ct);
            return;
        }

        // The counts above read the type's entries, and the definition has no concurrency check. So
        // it is read again and only this member is changed on that copy, as the currency endpoint
        // does, and the rules are checked again against the fields and states it holds now.
        def = await session.LoadAsync<ContentTypeDefinition>(def.Id, ct);

        if (def is null || !validator.ValidateUniqueness(requested, def.Fields, def.Lifecycle).IsValid)
        {
            AddError("The content type changed while this ran. Nothing was written, so send it again.");
            await Send.ErrorsAsync(409, ct);
            return;
        }

        var before = Names(def.Uniqueness);
        def.Uniqueness = requested;
        def.UpdatedAt = DateTimeOffset.UtcNow;
        session.Store(def);

        var actorId = Guid.TryParse(User.FindFirst("UserId")?.Value, out var parsed) ? parsed : (Guid?)null;
        await AuditLog.RecordAsync(
            session,
            tenant.Slug,
            "contenttype.uniqueness.changed",
            actorId,
            User.FindFirst("Username")?.Value,
            targetType: "ContentType",
            targetId: def.Id.ToString(),
            metadata: new Dictionary<string, object>
            {
                ["contentType"] = def.Name,
                ["from"] = before,
                ["to"] = Names(def.Uniqueness),
                ["entriesSharingValues"] = duplicates.Sum(d => d.Entries),
            },
            ct: ct);

        await session.SaveChangesAsync(ct);

        await Send.OkAsync(new Response { Name = def.Name, Uniqueness = def.Uniqueness, Duplicates = duplicates }, ct);
    }

    /// <summary>The rules added or changed that entries already break, with how many entries each.</summary>
    private async Task<List<RuleDuplicates>> CountDuplicatesAsync(
        ContentTypeDefinition def, List<UniquenessRule> requested, CancellationToken ct)
    {
        var changed = requested
            .Where(rule => !(def.Uniqueness ?? []).Any(stored => stored is not null && UniquenessRules.Same(stored, rule)))
            .ToList();

        var resolved = UniquenessRules.Resolve(new ContentTypeDefinition
        {
            Name = def.Name,
            Fields = def.Fields,
            Lifecycle = def.Lifecycle,
            Uniqueness = changed,
        });

        var counts = new List<RuleDuplicates>();
        foreach (var rule in resolved.Usable)
        {
            var entries = await ContentUniqueness
                .Duplicates(session, store.Options.DatabaseSchemaName, def.Name, rule)
                .CountAsync(ct);

            if (entries > 0)
            {
                counts.Add(new RuleDuplicates { Rule = rule.Name, Entries = entries });
            }
        }

        return counts;
    }

    private static string Names(List<UniquenessRule>? rules) =>
        rules is { Count: > 0 } ? string.Join(", ", rules.Select(r => r.Name)) : "none";
}
