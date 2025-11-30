using FastEndpoints;
using Marten;
using barakoCMS.Models;

namespace barakoCMS.Features.Auth.Register;

public class Endpoint : Endpoint<Request, Response>
{
    private readonly barakoCMS.Repository.IUserRepository _repo;

    public Endpoint(barakoCMS.Repository.IUserRepository repo)
    {
        _repo = repo;
    }

    public override void Configure()
    {
        Post("/api/auth/register");
        AllowAnonymous();
    }

    public override async Task HandleAsync(Request req, CancellationToken ct)
    {
        var existingUser = await _repo.GetByUsernameOrEmailAsync(req.Username, req.Email, ct);

        if (existingUser != null)
        {
            ThrowError("Username or Email already exists");
        }

        if (req.Password.Length < 8)
        {
            ThrowError("Password must be at least 8 characters long");
        }

        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = req.Username,
            Email = req.Email,
            Role = "User",
            // In a real app, use a proper password hasher like BCrypt
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(req.Password) 
        };

        _repo.Store(user);
        await _repo.SaveChangesAsync(ct);

        await SendAsync(new Response { Message = "User registered successfully" });
    }
}
