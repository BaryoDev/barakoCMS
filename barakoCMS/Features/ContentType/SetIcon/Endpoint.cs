using FastEndpoints;
using Marten;
using barakoCMS.Infrastructure.Jobs;
using barakoCMS.Infrastructure.Audit;
using barakoCMS.Infrastructure.Auth;
using barakoCMS.Models;

namespace barakoCMS.Features.ContentType.SetIcon;

/// <summary>
/// PUT /api/content-types/{name}/icon, which sets or clears the icon a type is drawn with.
/// </summary>
/// <remarks>
/// Its own endpoint for the reason route-template is: content types have no general update. A custom
/// SVG is sanitised on the way in (see <see cref="barakoCMS.Core.Validation.IconRules"/>), so what
/// is stored and returned is the cleaned markup, never the request's.
/// </remarks>
internal class Endpoint(
    IDocumentSession session,
    barakoCMS.Infrastructure.Multitenancy.TenantContext tenant) : Endpoint<Request, Response>
{
    public override void Configure()
    {
        Put("/api/content-types/{name}/icon");
        Definition.RequireCapability(SystemCapabilities.ManageContentTypes, "SuperAdmin", "Admin");
        Definition.RefusesWhileFieldChangeRuns();
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

        var icon = barakoCMS.Core.Validation.IconRules.Check(req.Icon).Icon;

        var before = Describe(def.Icon);
        var after = Describe(icon);
        if (before != after)
        {
            var fromLabel = Label(def.Icon);
            def.Icon = icon;
            def.UpdatedAt = DateTimeOffset.UtcNow;
            session.Store(def);

            var actorId = Guid.TryParse(User.FindFirst("UserId")?.Value, out var parsed) ? parsed : (Guid?)null;
            await AuditLog.RecordAsync(
                session,
                tenant.Slug,
                "contenttype.icon.changed",
                actorId,
                User.FindFirst("Username")?.Value,
                targetType: "ContentType",
                targetId: def.Id.ToString(),
                metadata: new Dictionary<string, object>
                {
                    ["contentType"] = def.Name,
                    ["from"] = fromLabel,
                    ["to"] = Label(icon),
                },
                ct: ct);

            await session.SaveChangesAsync(ct);
        }

        await Send.OkAsync(new Response { Name = def.Name, Icon = def.Icon }, ct);
    }

    private static string Label(ContentTypeIcon? icon) =>
        icon is null ? "none" : icon.Name is not null ? $"name:{icon.Name}" : "svg";

    private static string Describe(ContentTypeIcon? icon) =>
        icon is null ? "none" : icon.Name is not null ? $"name:{icon.Name}" : "svg:" + icon.Svg;
}
