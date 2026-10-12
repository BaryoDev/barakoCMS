using System.Security.Cryptography;
using System.Text;
using barakoCMS.Infrastructure.Auth;
using barakoCMS.Infrastructure.Security;
using barakoCMS.Models;
using FastEndpoints;
using Marten;

namespace barakoCMS.Features.Blocks.Schema;

/// <summary>GET /api/blocks/schema: the tenant's block schema, for a console editing its pages.</summary>
/// <remarks>
/// Any signed-in caller of the tenant: it says which blocks a page may hold and nothing about any
/// entry. 404 while the tenant's renderer has registered none, which is a console's cue to fall
/// back to asking the renderer.
/// </remarks>
internal sealed class GetEndpoint(IQuerySession session) : EndpointWithoutRequest<Response>
{
    public override void Configure()
    {
        Get("/api/blocks/schema");
        Description(b => b.Produces<Response>(200).Produces(401).Produces(404).WithTags("Blocks"));
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        if (await session.LoadAsync<BlockSchema>(BlockSchema.RowId, ct) is not { } schema)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        await Send.OkAsync(Response.From(schema), ct);
    }
}

/// <summary>PUT /api/blocks/schema: replaces the tenant's block schema, as someone who manages its types.</summary>
internal sealed class PutEndpoint(IDocumentSession session) : Endpoint<Request, Response>
{
    public override void Configure()
    {
        Put("/api/blocks/schema");
        Definition.RequireCapability(SystemCapabilities.ManageContentTypes, "SuperAdmin", "Admin");
        Description(b => b.Produces<Response>(200).Produces(400).Produces(401).Produces(403).WithTags("Blocks"));
    }

    public override async Task HandleAsync(Request req, CancellationToken ct) =>
        await Send.OkAsync(await BlockSchemas.StoreAsync(session, req, User.FindFirst("UserId")?.Value ?? "unknown", ct), ct);
}

/// <summary>
/// PUT /api/renderer/blocks/schema: replaces the tenant's block schema, as the renderer, which sends
/// the deployment's renderer key in <c>X-Barako-Renderer-Key</c>.
/// </summary>
/// <remarks>
/// The renderer is how a tenant's blocks are known: it registers the blocks of the plugins enabled
/// for the tenant it is serving, named by the request the same way delivery names one. With no
/// renderer key configured the route answers 404, and a missing or wrong key answers 401, compared
/// in fixed time. The key is a deployment secret that already lets the renderer skip the per-IP
/// limit, so whoever holds it can rewrite any tenant's block schema; it opens nothing else here.
/// </remarks>
internal sealed class RendererPutEndpoint(IDocumentSession session, IConfiguration configuration)
    : Endpoint<Request, Response>
{
    public override void Configure()
    {
        Put("/api/renderer/blocks/schema");
        AllowAnonymous();

        // The key is checked before the body is judged, so a caller without it learns nothing from
        // the validator.
        DontThrowIfValidationFails();
        Description(b => b.Produces<Response>(200).Produces(400).Produces(401).Produces(404).WithTags("Blocks"));
    }

    public override async Task HandleAsync(Request req, CancellationToken ct)
    {
        var key = RateLimitSetup.Read(configuration).RendererKey;
        if (key is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var sent = HttpContext.Request.Headers[RateLimitSetup.RendererHeader].ToString();
        if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(sent), Encoding.UTF8.GetBytes(key)))
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        ThrowIfAnyErrors();
        await Send.OkAsync(await BlockSchemas.StoreAsync(session, req, "renderer", ct), ct);
    }
}

internal static class BlockSchemas
{
    public static async Task<Response> StoreAsync(IDocumentSession session, Request req, string by, CancellationToken ct)
    {
        var schema = new BlockSchema
        {
            Blocks = req.Blocks ?? new(),
            RegisteredBy = by,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        session.Store(schema);
        await session.SaveChangesAsync(ct);
        return Response.From(schema);
    }
}
