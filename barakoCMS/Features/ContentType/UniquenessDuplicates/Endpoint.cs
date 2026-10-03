using FastEndpoints;
using Marten;
using Marten.Linq.MatchesSql;
using barakoCMS.Core.Validation;
using barakoCMS.Infrastructure.Auth;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;

namespace barakoCMS.Features.ContentType.UniquenessDuplicates;

internal sealed class Request : PaginatedRequest
{
}

/// <summary>One entry that shares its values under a rule with another entry the rule counts.</summary>
/// <remarks>The id and when it was created, and none of its values.</remarks>
internal sealed class DuplicateEntry
{
    public Guid Id { get; init; }

    public DateTime CreatedAt { get; init; }
}

/// <summary>
/// GET /api/content-types/{name}/uniqueness/{rule}/duplicates, the entries that share their values
/// under a rule with another entry the rule counts, oldest first.
/// </summary>
/// <remarks>
/// How an operator finds the entries a rule declared with <c>force</c> left as they were. Read in
/// the database, a page at a time; the values are compared there exactly as a write compares them.
///
/// Ids only, and only of entries the caller may read: the capability that declares rules, read on
/// the type, and the type's row rules applied to each entry as the entries list applies them, since
/// the list says which entries exist.
/// </remarks>
internal class Endpoint(
    IDocumentSession session,
    IDocumentStore store,
    IPermissionResolver permissionResolver) : Endpoint<Request, PaginatedResponse<DuplicateEntry>>
{
    public override void Configure()
    {
        Get("/api/content-types/{name}/uniqueness/{rule}/duplicates");
        Definition.RequireCapability(SystemCapabilities.ManageContentTypes, "SuperAdmin", "Admin");
    }

    public override async Task HandleAsync(Request req, CancellationToken ct)
    {
        var name = barakoCMS.Core.ContentTypeName.Normalize(Route<string>("name") ?? string.Empty);
        var ruleName = Route<string>("rule") ?? string.Empty;

        var def = await session.Query<ContentTypeDefinition>()
            .FirstOrDefaultAsync(d => d.Name.ToLower() == name, ct);

        var rule = def is null
            ? null
            : UniquenessRules.Resolve(def).Usable.FirstOrDefault(r => UniquenessRules.Matches(r.Name, ruleName));

        if (def is null || rule is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var user = Guid.TryParse(User.FindFirst("UserId")?.Value, out var userId)
            ? await session.LoadAsync<User>(userId, ct)
            : null;

        if (user is null || !await permissionResolver.CanPerformActionAsync(user, def.Name, "read", null, ct))
        {
            await Send.ForbiddenAsync(ct);
            return;
        }

        var duplicates = ContentUniqueness
            .Duplicates(session, store.Options.DatabaseSchemaName, def.Name, rule)
            .OrderBy(c => c.CreatedAt)
            .ThenBy(c => c.Id);

        // Only the entries the caller may read, as the entries list does: a row rule that hides an
        // entry hides it here too, and the count is of what is listed.
        var predicate = await permissionResolver.ReadPredicateAsync(user, def.Name, ct);

        PaginatedResponse<barakoCMS.Models.Content> page;
        if (predicate.Compiled)
        {
            page = await duplicates
                .Where(c => c.MatchesSql(predicate.Sql!, predicate.Parameters))
                .ToPagedResponseAsync(req, ct);
        }
        else
        {
            // A rule only the entry can answer: every duplicate is read and asked, as the entries
            // list does for the same rules.
            var readable = new List<barakoCMS.Models.Content>();
            foreach (var entry in await duplicates.ToListAsync(ct))
            {
                if (await permissionResolver.CanPerformActionAsync(user, def.Name, "read", entry, ct))
                {
                    readable.Add(entry);
                }
            }

            page = readable.ToPagedResponse(req);
        }

        await Send.OkAsync(new PaginatedResponse<DuplicateEntry>
        {
            Items = page.Items.Select(c => new DuplicateEntry { Id = c.Id, CreatedAt = c.CreatedAt }).ToList(),
            Page = page.Page,
            PageSize = page.PageSize,
            TotalItems = page.TotalItems,
        }, ct);
    }
}
