using FastEndpoints;
using FastEndpoints.Security;
using Marten;
using barakoCMS.Models;

namespace barakoCMS.Features.Auth.Login;

public class Endpoint : Endpoint<Request, Response>
{
    private readonly barakoCMS.Repository.IUserRepository _repo;
    private readonly IConfiguration _config;

    public Endpoint(barakoCMS.Repository.IUserRepository repo, IConfiguration config)
    {
        _repo = repo;
        _config = config;
    }

    public override void Configure()
    {
        Post("/api/auth/login");
        AllowAnonymous();
    }

    public override async Task HandleAsync(Request req, CancellationToken ct)
    {
        var user = await _repo.GetByUsernameAsync(req.Username, ct);

        if (user == null || !BCrypt.Net.BCrypt.Verify(req.Password, user.PasswordHash))
        {
            ThrowError("Invalid credentials");
        }

        var jwtToken = JWTBearer.CreateToken(
            signingKey: _config["JWT:Key"]!,
            expireAt: DateTime.UtcNow.AddDays(1),
            privileges: u => 
            {
                u.Claims.Add(new("UserId", user.Id.ToString()));
                u.Claims.Add(new("Username", user.Username));
                u.Claims.Add(new(System.Security.Claims.ClaimTypes.Role, user.Role));
            });

        await SendAsync(new Response 
        { 
            Token = jwtToken,
            Expiry = DateTime.UtcNow.AddDays(1)
        });
    }
}
