using FastEndpoints;
using Marten;
using barakoCMS.Models;

namespace barakoCMS.Features.Auth.Register;

public class Endpoint : Endpoint<Request, Response>
{
    private readonly IDocumentSession _session;

    public Endpoint(IDocumentSession session)
    {
        _session = session;
    }

    public override void Configure()
    {
        Post("/api/auth/register");
        AllowAnonymous();
    }

    public override async Task HandleAsync(Request req, CancellationToken ct)
    {
        var existingUser = await _session.Query<User>()
            .FirstOrDefaultAsync(u => u.Username == req.Username || u.Email == req.Email, ct);

        if (existingUser != null)
        {
            ThrowError("Username or Email already exists");
        }

        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = req.Username,
            Email = req.Email,
            // In a real app, use a proper password hasher like BCrypt
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(req.Password) 
        };

        _session.Store(user);
        await _session.SaveChangesAsync(ct);

        await SendAsync(new Response { Message = "User registered successfully" });
    }
}
