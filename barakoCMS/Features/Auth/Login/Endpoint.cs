using FastEndpoints;
using FastEndpoints.Security;
using Marten;
using barakoCMS.Models;

namespace barakoCMS.Features.Auth.Login;

public class Endpoint : Endpoint<Request, Response>
{
    private readonly IDocumentSession _session;

    public Endpoint(IDocumentSession session)
    {
        _session = session;
    }

    public override void Configure()
    {
        Post("/api/auth/login");
        AllowAnonymous();
    }

    public override async Task HandleAsync(Request req, CancellationToken ct)
    {
        var user = await _session.Query<User>()
            .FirstOrDefaultAsync(u => u.Username == req.Username, ct);

        if (user == null || !BCrypt.Net.BCrypt.Verify(req.Password, user.PasswordHash))
        {
            ThrowError("Invalid credentials");
        }

        var jwtToken = JWTBearer.CreateToken(
            signingKey: Config["JWT:Key"]!,
            expireAt: DateTime.UtcNow.AddDays(1),
            privileges: u => 
            {
                u.Claims.Add(new("UserId", user.Id.ToString()));
                u.Claims.Add(new("Username", user.Username));
            });

        await SendAsync(new Response 
        { 
            Token = jwtToken,
            Expiry = DateTime.UtcNow.AddDays(1)
        });
    }
}
