using barakoCMS.Core;
using barakoCMS.Core.Interfaces;
using barakoCMS.Core.Validation;
using barakoCMS.Infrastructure.Envelope;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using FastEndpoints;
using Marten;

namespace barakoCMS.Features.Numbering.Next;

/// <summary>POST /api/numbering/next: the next number of a type's client-allocated series.</summary>
/// <remarks>
/// For a client that has to put a number on a paper form before it can reach the server. The number
/// is committed here, so it is never handed out twice; the client sends it back as the numbering
/// field of the entry it creates. Only a caller who may create the type can take one, and only from
/// a series the type's rule marks <c>ClientAllocated</c>; every other series is numbered by the server.
/// </remarks>
internal sealed class NextNumberEndpoint(
    IDocumentSession session,
    IPermissionResolver permissionResolver,
    INumberStrategies numbering,
    IEventEnvelopeContext envelope) : Endpoint<NextNumberRequest, NextNumberResponse>
{
    public override void Configure()
    {
        Post("/api/numbering/next");
        Claims("UserId");
        Description(b => b.WithTags("Numbering"));
        Summary(s => s.Summary = "The next number of a client-allocated series.");
    }

    public override async Task HandleAsync(NextNumberRequest req, CancellationToken ct)
    {
        if (!Guid.TryParse(User.FindFirst("UserId")?.Value, out var userId)
            || await session.LoadAsync<User>(userId, ct) is not { } user)
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        // The canonical definition first, then the permission on its stored name, so a differently
        // cased spelling is checked against the grant of the type it stands for.
        ContentTypeDefinition? definition;
        try
        {
            definition = await session.Query<ContentTypeDefinition>().FirstNamedAsync(req.ContentType, ct);
        }
        catch (AmbiguousContentTypeException e)
        {
            ThrowError(e.Message);
            return;
        }

        if (definition is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        if (!await permissionResolver.CanPerformActionAsync(user, definition.Name, "create", null, ct))
        {
            await Send.ForbiddenAsync(ct);
            return;
        }

        if (definition.Numbering is not { ClientAllocated: true } rule)
        {
            ThrowError("This number is made by the server.", 403);
            return;
        }

        NumberSeries series;
        try
        {
            series = NumberingRules.SeriesFor(definition, rule, envelope.BranchCode);
        }
        catch (InvalidOperationException)
        {
            ThrowError("The numbering needs a branch with a code.");
            return;
        }

        var allocated = await numbering.For(rule.Strategy).NextAsync(session, series, ct);
        await session.SaveChangesAsync(ct);
        await Send.OkAsync(new NextNumberResponse(allocated.Series, allocated.Formatted, allocated.Value), ct);
    }
}
