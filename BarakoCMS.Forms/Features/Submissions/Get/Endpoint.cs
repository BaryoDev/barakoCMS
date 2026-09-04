using barakoCMS.Infrastructure.Auth;
using FastEndpoints;
using Marten;

namespace BarakoCMS.Forms.Features.Submissions.Get;

/// <summary>GET /api/forms/{name}/submissions/{id}.</summary>
public class Endpoint : EndpointWithoutRequest<SubmissionResponse>
{
    private readonly IQuerySession _session;

    public Endpoint(IQuerySession session) => _session = session;

    public override void Configure()
    {
        Get("/api/forms/{name}/submissions/{id}");
        Definition.RequireCapability(FormsCapabilities.ViewFormSubmissions, FormsCapabilities.LegacyRoles);
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var name = Route<string>("name") ?? string.Empty;
        if (!Guid.TryParse(Route<string>("id"), out var id)) { await Send.NotFoundAsync(ct); return; }

        var submission = await _session.LoadAsync<FormSubmission>(id, ct);
        if (submission is null || submission.FormName != name) { await Send.NotFoundAsync(ct); return; }

        await Send.ResponseAsync(SubmissionResponse.From(submission), cancellation: ct);
    }
}
