using barakoCMS.Core.Interfaces;
using barakoCMS.Infrastructure.Auth;
using barakoCMS.Infrastructure.Multitenancy;
using barakoCMS.Models;
using FastEndpoints;
using FluentValidation;

namespace barakoCMS.Features.Overview.Resolve;

internal sealed class Request
{
    /// <summary>The <c>id</c> of an item that is resolvable. Today only <c>mail.bounces</c>.</summary>
    public string Id { get; set; } = string.Empty;
}

internal sealed class Response
{
    public string Id { get; init; } = string.Empty;

    /// <summary>How many records the resolve marked.</summary>
    public int Resolved { get; init; }
}

internal sealed class Validator : Validator<Request>
{
    public Validator()
    {
        RuleFor(r => r.Id).NotEmpty().MaximumLength(100);
    }
}

/// <summary>
/// POST /api/overview/items/{id}/resolve, clears a resolvable item. For the bounces item that marks
/// every unresolved bounce of the tenant resolved, so the next read shows Mail delivering and the
/// item gone.
/// </summary>
/// <remarks>
/// Needs <c>manage_email_settings</c>, the capability the item is shown under. A <c>monitor:read</c>
/// key cannot reach it: keys are read only here.
/// </remarks>
internal sealed class Endpoint(IServiceProvider services, TenantContext tenant) : Endpoint<Request, Response>
{
    public override void Configure()
    {
        Post("/api/overview/items/{id}/resolve");
        Definition.RequireCapability(SystemCapabilities.ManageEmailSettings, "SuperAdmin", "Admin");
        Description(b => b
            .Produces<Response>(200)
            .Produces(401)
            .Produces(403)
            .Produces(404)
            .WithTags("Monitoring"));
    }

    public override async Task HandleAsync(Request req, CancellationToken ct)
    {
        var bounces = services.GetService<IMailBounceSource>();
        if (bounces is null || req.Id != OverviewRules.BouncesItemId)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var resolved = await bounces.ResolveAllAsync(tenant.Slug, ct);
        await Send.OkAsync(new Response { Id = req.Id, Resolved = resolved }, ct);
    }
}
