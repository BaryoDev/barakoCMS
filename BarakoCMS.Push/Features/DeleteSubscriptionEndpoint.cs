using FastEndpoints;
using Marten;

namespace BarakoCMS.Push.Features;

public sealed class DeleteSubscriptionEndpoint(IDocumentSession session) : EndpointWithoutRequest
{
    public override void Configure() => Delete("/api/push/subscriptions/{id}");

    public override async Task HandleAsync(CancellationToken ct)
    {
        if (!Guid.TryParse(User.FindFirst("UserId")?.Value, out var userId))
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        var row = Route<Guid>("id") is var id && id != Guid.Empty
            ? await session.LoadAsync<StoredSubscription>(id, ct)
            : null;
        if (row is null || row.UserId != userId)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        session.Delete(row);
        await session.SaveChangesAsync(ct);
        await Send.NoContentAsync(ct);
    }
}
