using barakoCMS.Core.Interfaces;
using barakoCMS.Infrastructure.Auth;
using barakoCMS.Infrastructure.Numbering;
using barakoCMS.Models;
using FastEndpoints;
using Marten;

namespace barakoCMS.Features.Numbering;

internal sealed class GapsRequest
{
    public string Series { get; set; } = string.Empty;

    [QueryParam]
    public long From { get; set; }

    [QueryParam]
    public long To { get; set; }
}

internal sealed class GapsEndpoint(IExternalNumberLedger ledger, IQuerySession session)
    : Endpoint<GapsRequest, NumberGapReport>
{
    public override void Configure()
    {
        Get("/api/numbering/external/{series}/gaps");
        Definition.RequireCapability(SystemCapabilities.ViewNumberGaps);
        Description(b => b.WithTags("Numbering"));
        Summary(s => s.Summary = "Numbers in a range of an external series that were never recorded.");
    }

    public override async Task HandleAsync(GapsRequest req, CancellationToken ct)
    {
        if (ExternalNumberLedger.RangeProblem(req.From, req.To) is { } problem)
            ThrowError(problem);

        try
        {
            new NumberSeries(req.Series).Validate();
        }
        catch (ArgumentException e)
        {
            ThrowError(e.Message);
        }

        await Send.OkAsync(await ledger.GapsAsync(session, req.Series, req.From, req.To, ct), ct);
    }
}
