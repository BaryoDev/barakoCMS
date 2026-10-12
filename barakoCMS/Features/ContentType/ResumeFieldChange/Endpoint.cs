using barakoCMS.Features.ContentType.ChangeField;
using barakoCMS.Infrastructure.Auth;
using barakoCMS.Infrastructure.Jobs;
using barakoCMS.Models;
using FastEndpoints;
using Marten;

namespace barakoCMS.Features.ContentType.ResumeFieldChange;

internal sealed class Request
{
    public Guid Id { get; set; }
}

/// <summary>POST /api/content-types/field-changes/{id}/resume: queues a running change's job again.</summary>
/// <remarks>
/// For a change whose job stopped, for example after the queue gave up on it. The job starts from
/// the step and entry the change last recorded, so nothing done is done twice. A completed change
/// is a 409: there is nothing left to run. So is a change whose job is still queued or running, which
/// would otherwise run beside the new one.
/// </remarks>
internal sealed class Endpoint(IDocumentSession session) : Endpoint<Request, FieldChangeResponse>
{
    public override void Configure()
    {
        Post("/api/content-types/field-changes/{id}/resume");
        Definition.RequireCapability(SystemCapabilities.ManageContentTypes, "SuperAdmin", "Admin");
    }

    public override async Task HandleAsync(Request req, CancellationToken ct)
    {
        if (await session.LoadAsync<FieldChange>(req.Id, ct) is not { } change)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        if (change.State == FieldChangeState.Completed)
        {
            AddError("This change has completed. There is nothing left to run.");
            ThrowIfAnyErrors(StatusCodes.Status409Conflict);
        }

        var id = change.Id.ToString();
        if (await session.Query<JobRecord>().AnyAsync(
                j => (j.State == JobState.Pending || j.State == JobState.Running) && j.CommandJson.Contains(id), ct))
        {
            AddError("This change's job is still queued or running, and carries on by itself. Resume it once the queue has given up on it.");
            ThrowIfAnyErrors(StatusCodes.Status409Conflict);
        }

        change.UpdatedAt = DateTimeOffset.UtcNow;
        session.Store(change);
        await new FieldChangeCommand { TenantId = session.TenantId, ChangeId = change.Id }.QueueJobAsync(ct: ct);
        await session.SaveChangesAsync(ct);
        await Send.ResponseAsync(FieldChangeResponse.From(change), 202, ct);
    }
}
