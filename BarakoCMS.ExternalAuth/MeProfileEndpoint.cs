using FastEndpoints;
using Marten;

namespace BarakoCMS.ExternalAuth;

/// <summary>GET /api/me/profile — the signed-in user's social profile (name, photo, birthday, location).</summary>
public class MeProfileEndpoint : EndpointWithoutRequest
{
    private readonly IQuerySession _session;
    public MeProfileEndpoint(IQuerySession session) => _session = session;

    public override void Configure()
    {
        Get("/api/me/profile"); // authenticated; /api/me/* is exempt from the tenant lock
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        Guid.TryParse(User.FindFirst("UserId")?.Value, out var userId);
        var p = await _session.Query<SocialProfile>().FirstOrDefaultAsync(x => x.UserId == userId, ct);
        await SendOkAsync(new
        {
            name = p?.Name,
            photoUrl = p?.PhotoUrl,
            birthday = p?.Birthday,
            location = p?.Location,
            source = p?.Source,
        }, ct);
    }
}
