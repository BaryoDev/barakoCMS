using System.Text.Json;
using System.Text.Json.Nodes;
using barakoCMS.Core.Interfaces;
using barakoCMS.Events;
using barakoCMS.Features.Public;
using barakoCMS.Infrastructure.Audit;
using barakoCMS.Infrastructure.Multitenancy;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using FastEndpoints;
using Marten;
using Marten.Linq.MatchesSql;
using Microsoft.AspNetCore.Http.Metadata;
using ContentDoc = barakoCMS.Models.Content;

namespace barakoCMS.Features.Collections.Push;

/// <summary>
/// <c>POST /api/collections/{type}/push</c>: the source that owns a collection's entries writes
/// them, keyed by slug, in one transaction.
/// </summary>
/// <remarks>
/// Every entry goes through what <c>POST /api/contents</c> and <c>PUT /api/contents/{id}</c> run:
/// the type's create or update permission, the write-path sensitivity rule, the schema validator
/// and the lifecycle hooks. A push is a faster way in, never a looser one.
///
/// Nothing is written until every entry has passed, and then everything is written by one
/// <c>SaveChangesAsync</c>, archiving included. Workflows and their webhooks fire from the committed
/// events, as for any other write, so each created or changed entry fires once and an unchanged one,
/// which appends nothing, fires nothing.
/// </remarks>
internal sealed class Endpoint(
    IDocumentSession session,
    IContentValidatorService validator,
    IPermissionResolver permissions,
    IContentWriter writer,
    IContentSourcingPolicy sourcing,
    TenantContext tenant) : Endpoint<Request, Response>
{
    public override void Configure()
    {
        Post("/api/collections/{type}/push");
        Claims("UserId");

        // Lower than the server-wide limit, never higher: metadata replaces the server's value
        // for this route, so a deployment that set a smaller one keeps it.
        var serverLimit = Config.GetValue<long?>("RequestLimits:MaxBodyBytes") ?? 10L * 1024 * 1024;
        Options(b => b.WithMetadata(new BodyLimit(Math.Min(Limits.MaxBodyBytes, serverLimit))));
    }

    private sealed record BodyLimit(long? MaxRequestBodySize) : IRequestSizeLimitMetadata;

    private sealed record Plan(int Index, Dictionary<string, object> Data, ContentDoc? Existing);

    public override async Task HandleAsync(Request req, CancellationToken ct)
    {
        if (!Guid.TryParse(User.FindFirst("UserId")?.Value, out var userId)
            || await session.LoadAsync<User>(userId, ct) is not { } user)
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        var definition = await session.Query<ContentTypeDefinition>()
            .FirstOrDefaultAsync(d => d.Name == req.Type, ct);
        if (definition is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var slugField = PublicDelivery.SlugField(definition);
        if (slugField is null)
        {
            ThrowError($"'{req.Type}' has no slug field, so a push has nothing to key its entries on.");
        }

        if (req.ArchiveMissing && definition.Lifecycle is not null)
        {
            ThrowError($"'{req.Type}' moves between states by named transitions, so archiveMissing cannot archive its entries.");
        }

        var slugs = req.Entries.Select(e => SlugOf(e, slugField)).ToList();
        var existingBySlug = await ExistingAsync(req.Type, slugField, slugs.OfType<string>(), ct);

        var sensitivity = Resolve<ISensitivityService>();
        var hooks = Resolve<IContentLifecycleRunner>();

        var plans = new List<Plan>();
        var errors = new List<EntryError>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool? mayCreate = null;

        for (var i = 0; i < req.Entries.Count; i++)
        {
            var data = req.Entries[i];
            var slug = slugs[i];

            if (slug is null)
            {
                errors.Add(Error(i, null, $"The entry has no value for the slug field '{slugField}'."));
                continue;
            }

            if (!seen.Add(slug))
            {
                errors.Add(Error(i, slug, $"The slug '{slug}' appears more than once in this push."));
                continue;
            }

            var existing = existingBySlug.GetValueOrDefault(slug);

            var allowed = existing is null
                ? mayCreate ??= await permissions.CanPerformActionAsync(user, req.Type, "create", null, ct)
                : await permissions.CanPerformActionAsync(user, existing.ContentType, "update", existing, ct);
            if (!allowed)
            {
                await Send.ForbiddenAsync(ct);
                return;
            }

            await sensitivity.ApplyWriteAsync(req.Type, data, existing?.Data, HttpContext, ct);

            var (valid, messages) = await validator.ValidateAsync(req.Type, data, existing);
            if (valid)
            {
                messages = [.. await hooks.RunBeforeSaveAsync(req.Type, existing?.Id, data, existing?.Data, userId, ct)];
            }

            if (messages.Count > 0)
            {
                errors.Add(new EntryError { Index = i, Slug = slug, Messages = messages });
                continue;
            }

            plans.Add(new Plan(i, data, existing));
        }

        // The validator counts what is stored, and inside one push nothing is stored yet, so every
        // new entry of a singleton would pass it. The import endpoint applies the same rule.
        if (definition.IsSingleton)
        {
            var holders = plans.Count(p => p.Existing is not null);
            foreach (var extra in plans.Where(p => p.Existing is null).Skip(holders > 0 ? 0 : 1).ToList())
            {
                errors.Add(Error(extra.Index, slugs[extra.Index], $"'{definition.DisplayName}' holds a single entry."));
                plans.Remove(extra);
            }
        }

        if (errors.Count > 0)
        {
            await Send.ResponseAsync(new Response { Errors = [.. errors.OrderBy(e => e.Index)] }, 400, ct);
            return;
        }

        var missing = new List<ContentDoc>();
        if (req.ArchiveMissing)
        {
            var (sql, parameters) = DeliveryQuery.FieldNotInIgnoreCaseSql(slugField, seen);
            missing = [.. await session.Query<ContentDoc>()
                .Where(c => c.ContentType == req.Type
                            && c.Status == ContentStatus.Published
                            && c.MatchesSql(sql, parameters))
                .OrderBy(c => c.CreatedAt)
                .ThenBy(c => c.Id)
                .Take(Limits.MaxArchived + 1)
                .ToListAsync(ct)];

            if (missing.Count > Limits.MaxArchived)
            {
                ThrowError($"This push would archive more than {Limits.MaxArchived} entries. Archive them in smaller steps.");
            }

            foreach (var entry in missing)
            {
                if (!await permissions.CanPerformActionAsync(user, entry.ContentType, "update", entry, ct))
                {
                    await Send.ForbiddenAsync(ct);
                    return;
                }
            }
        }

        var response = new Response();
        var searchable = definition.Fields
            .Where(f => f.Sensitivity == SensitivityLevel.Public)
            .Select(f => f.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var eventSourced = await sourcing.IsEventSourcedAsync(req.Type, ct);

        try
        {
            foreach (var plan in plans)
            {
                if (plan.Existing is not { } existing)
                {
                    var created = await writer.CreateAsync(
                        new ContentCreated(Guid.NewGuid(), req.Type, plan.Data, req.Status, userId,
                            SearchText(plan.Data, searchable), SensitivityLevel.Public, DateTime.UtcNow),
                        ct);

                    if (definition.Lifecycle is { } lifecycle)
                    {
                        created.LifecycleState = lifecycle.InitialState;
                        session.Store(created);
                    }

                    response.Created++;
                    continue;
                }

                var events = new List<object>();
                if (!SameData(existing.Data, plan.Data))
                {
                    events.Add(new ContentUpdated(existing.Id, plan.Data, userId, SearchText(plan.Data, searchable), DateTime.UtcNow));
                }

                if (existing.Status != req.Status)
                {
                    events.Add(new ContentStatusChanged(existing.Id, req.Status, userId, DateTime.UtcNow));
                }

                if (events.Count == 0)
                {
                    response.Unchanged++;
                    continue;
                }

                // The same binding the update endpoint makes for a caller that sent no version: the
                // write is tied to what this request read, so a concurrent edit refuses the push
                // rather than being overwritten by it.
                var stream = await session.Events.FetchStreamStateAsync(existing.Id, ct);
                var document = eventSourced ? null : await session.MetadataForAsync(existing, ct);

                await writer.AppendAsync(existing, events, eventSourced ? stream?.Version : null, ct);

                if (document is not null)
                {
                    session.UpdateExpectedVersion(existing, document.CurrentVersion);
                }

                response.Updated++;
            }

            foreach (var entry in missing)
            {
                await writer.AppendOptimisticAsync(
                    entry, [new ContentStatusChanged(entry.Id, ContentStatus.Archived, userId, DateTime.UtcNow)], ct);
                await AuditLog.RecordAsync(session, tenant.Slug, "content.archived", userId, user.Username,
                    targetType: entry.ContentType, targetId: entry.Id.ToString(), ct: ct);
                response.Archived++;
            }

            if (response.Created + response.Updated + response.Archived > 0)
            {
                await AuditLog.RecordAsync(session, tenant.Slug, "collection.pushed", userId, user.Username,
                    targetType: nameof(ContentTypeDefinition), targetId: req.Type,
                    metadata: new Dictionary<string, object>
                    {
                        ["created"] = response.Created,
                        ["updated"] = response.Updated,
                        ["unchanged"] = response.Unchanged,
                        ["archived"] = response.Archived,
                    },
                    ct: ct);
            }

            await session.SaveChangesAsync(ct);
        }
        catch (StaleContentException ex)
        {
            ThrowError(ex.Message, 409);
        }
        catch (Exception ex) when (ex is JasperFx.ConcurrencyException
            || ex.GetType().Name.Contains("Concurrency")
            || ex.GetType().Name.Contains("UnexpectedMaxEventId"))
        {
            ThrowError("An entry in this push was changed by another writer while it ran. Nothing was written; push again.", 409);
        }

        await Send.OkAsync(response, ct);
    }

    /// <summary>The stored entry each slug names, the oldest where more than one holds it.</summary>
    /// <remarks>
    /// Oldest first for the reason the slug routes give: a slug is unique going forward (#717), so
    /// older data can hold duplicates, and the first holder is the one its links point at. One
    /// query for the whole push rather than one per entry.
    /// </remarks>
    private async Task<Dictionary<string, ContentDoc>> ExistingAsync(
        string type, string slugField, IEnumerable<string> slugs, CancellationToken ct)
    {
        var (sql, parameters) = DeliveryQuery.FieldInIgnoreCaseSql(slugField, slugs);

        var holders = await session.Query<ContentDoc>()
            .Where(c => c.ContentType == type && c.MatchesSql(sql, parameters))
            .OrderBy(c => c.CreatedAt)
            .ThenBy(c => c.Id)
            .Take(Limits.MaxEntries * 2)
            .ToListAsync(ct);

        var bySlug = new Dictionary<string, ContentDoc>(StringComparer.OrdinalIgnoreCase);
        foreach (var holder in holders)
        {
            if (SlugOf(holder.Data, slugField) is { } slug)
            {
                bySlug.TryAdd(slug, holder);
            }
        }

        return bySlug;
    }

    private static string? SlugOf(IReadOnlyDictionary<string, object> data, string slugField)
    {
        var value = data.FirstOrDefault(kv => kv.Key.Equals(slugField, StringComparison.OrdinalIgnoreCase)).Value;
        var text = value is JsonElement element ? element.ToString() : value?.ToString();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    /// <summary>Whether the stored data and the pushed data are the same values.</summary>
    /// <remarks>
    /// Compared as JSON, because the two sides reach here in different forms: the stored values are
    /// plain CLR types read back from Postgres and the pushed ones are what the request binder made
    /// of the body. Serialized, 1200 is 1200 either way.
    /// </remarks>
    private static bool SameData(IReadOnlyDictionary<string, object> stored, Dictionary<string, object> pushed) =>
        JsonNode.DeepEquals(JsonSerializer.SerializeToNode(stored), JsonSerializer.SerializeToNode(pushed));

    /// <summary>The same derived search text the create and update endpoints write.</summary>
    private static string SearchText(Dictionary<string, object> data, HashSet<string> searchable) =>
        string.Join(' ', data
            .Where(kv => searchable.Contains(kv.Key))
            .Select(kv => kv.Value?.ToString())
            .Where(v => !string.IsNullOrWhiteSpace(v)));

    private static EntryError Error(int index, string? slug, string message) =>
        new() { Index = index, Slug = slug, Messages = [message] };
}
