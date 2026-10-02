using FastEndpoints;
using Marten;
using barakoCMS.Infrastructure.Audit;
using barakoCMS.Infrastructure.Auth;
using barakoCMS.Models;

namespace barakoCMS.Features.ContentType.SetRouteTemplate;

/// <summary>
/// PUT /api/content-types/{name}/route-template, which sets or clears the path the feed and the
/// sitemap build an entry's link from.
/// </summary>
/// <remarks>
/// Its own endpoint because content types have no general update, and a type stored before the
/// template existed has no other way to get one. No entry is read or written: the template is
/// applied when a feed or a sitemap is built.
///
/// Both of those answer with a one minute cache lifetime, so a change shows in them after that.
/// </remarks>
internal class Endpoint(
    IDocumentSession session,
    barakoCMS.Infrastructure.Services.IContentTypeValidatorService validator,
    barakoCMS.Infrastructure.Multitenancy.TenantContext tenant) : Endpoint<Request, Response>
{
    public override void Configure()
    {
        Put("/api/content-types/{name}/route-template");
        // Where a type's entries live on the site is modelling, the same gate as creating the type.
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

        var (valid, errors) = validator.ValidateRouteTemplate(req.RouteTemplate);
        if (!valid)
        {
            foreach (var error in errors) AddError(error);
            await Send.ErrorsAsync(400, ct);
            return;
        }

        if (!string.Equals(def.RouteTemplate, req.RouteTemplate, StringComparison.Ordinal))
        {
            var before = def.RouteTemplate ?? "none";

            def.RouteTemplate = req.RouteTemplate;
            def.UpdatedAt = DateTimeOffset.UtcNow;
            session.Store(def);

            var actorId = Guid.TryParse(User.FindFirst("UserId")?.Value, out var parsed) ? parsed : (Guid?)null;
            await AuditLog.RecordAsync(
                session,
                tenant.Slug,
                "contenttype.routetemplate.changed",
                actorId,
                User.FindFirst("Username")?.Value,
                targetType: "ContentType",
                targetId: def.Id.ToString(),
                metadata: new Dictionary<string, object>
                {
                    ["contentType"] = def.Name,
                    ["from"] = before,
                    ["to"] = def.RouteTemplate ?? "none",
                },
                ct: ct);

            await session.SaveChangesAsync(ct);
        }

        await Send.OkAsync(new Response { Name = def.Name, RouteTemplate = def.RouteTemplate }, ct);
    }
}
