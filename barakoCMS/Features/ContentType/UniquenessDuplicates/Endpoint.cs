using FastEndpoints;
using Marten;
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
/// Ids only. The caller needs the capability that declares rules and read on the type, since the
/// list says which entries exist, and opens each entry through the content API, which applies its
/// own rules to what it shows.
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

        var page = await ContentUniqueness
            .Duplicates(session, store.Options.DatabaseSchemaName, def.Name, rule)
            .OrderBy(c => c.CreatedAt)
            .ThenBy(c => c.Id)
            .ToPagedResponseAsync(req, ct);

        await Send.OkAsync(new PaginatedResponse<DuplicateEntry>
        {
            Items = page.Items.Select(c => new DuplicateEntry { Id = c.Id, CreatedAt = c.CreatedAt }).ToList(),
            Page = page.Page,
            PageSize = page.PageSize,
            TotalItems = page.TotalItems,
        }, ct);
    }
}
