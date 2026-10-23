using barakoCMS.Core.Interfaces;
using barakoCMS.Infrastructure.Auth;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using FastEndpoints;
using Marten;

namespace barakoCMS.Features.Queries.Run;

/// <summary>
/// Runs a saved aggregate query for the caller and returns its groups and totals.
/// </summary>
/// <remarks>
/// The figures are computed in the database under the caller's own field and row rules, so two
/// callers running the same saved query can get different numbers. A field the caller may not read
/// refuses the run by name, rather than returning a total with the field quietly left out.
/// </remarks>
internal sealed class Endpoint(
    IQuerySession session,
    IPermissionResolver permissions,
    ISensitivityService sensitivity) : Endpoint<Request, Response>
{
    public override void Configure()
    {
        Post("/api/queries/{slug}/run");
        Definition.RequireCapability(SystemCapabilities.ManageQueries, QueryGate.LegacyRoles);
    }

    public override async Task HandleAsync(Request req, CancellationToken ct)
    {
        var definition = await session.Query<QueryDefinition>()
            .FirstOrDefaultAsync(q => q.Slug == req.Slug && q.DeletedAt == null, ct);
        if (definition is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        if (!definition.IsAggregate)
        {
            AddError("This query returns rows, not figures. Preview it to see them.");
            await Send.ErrorsAsync(400, ct);
            return;
        }

        var result = await new AggregateQuery(session, permissions, sensitivity)
            .RunAsync(definition, User, req.From, req.To, execute: true, ct);

        if (!result.Ok)
        {
            AddError(result.Refusal!);
            await Send.ErrorsAsync(400, ct);
            return;
        }

        await Send.ResponseAsync(new Response
        {
            Ok = true,
            Count = result.Rows.Count,
            Rows = result.Rows,
            Totals = result.Totals,
        }, cancellation: ct);
    }
}
