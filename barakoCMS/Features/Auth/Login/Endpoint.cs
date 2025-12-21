using FastEndpoints;
using FastEndpoints.Security;
using Marten;
using barakoCMS.Models;

namespace barakoCMS.Features.Auth.Login;

public class Endpoint : Endpoint<Request, Response>
{
    private readonly barakoCMS.Repository.IUserRepository _repo;
    private readonly IQuerySession _session;
    private readonly IConfiguration _config;

    public Endpoint(barakoCMS.Repository.IUserRepository repo, IQuerySession session, IConfiguration config)
    {
        _repo = repo;
        _session = session;
        _config = config;
    }

    public override void Configure()
    {
        Post("/api/auth/login");
        AllowAnonymous();
        Options(x => x.RequireRateLimiting("auth")); // Limit to 10 requests per minute
    }

    public override async Task HandleAsync(Request req, CancellationToken ct)
    {
        var user = await _repo.GetByUsernameAsync(req.Username, ct);

        if (user == null || !BCrypt.Net.BCrypt.Verify(req.Password, user.PasswordHash))
        {
            ThrowError("Invalid credentials");
        }

        var roles = await _session.Query<Role>()
            .Where(r => user.RoleIds.Contains(r.Id))
            .Select(r => r.Name)
            .ToListAsync(ct);

        var jwtToken = JWTBearer.CreateToken(
            signingKey: _config["JWT:Key"]!,
            expireAt: DateTime.UtcNow.AddDays(1),
            issuer: _config["JWT:Issuer"],
            audience: _config["JWT:Audience"],
            privileges: u =>
            {
                u.Claims.Add(new("UserId", user.Id.ToString()));
                u.Claims.Add(new("Username", user.Username));
                foreach (var role in roles)
                {
                    u.Claims.Add(new(System.Security.Claims.ClaimTypes.Role, role));
                }
                // Fallback for backward compatibility or default
                if (!roles.Any())
                {
                    u.Claims.Add(new(System.Security.Claims.ClaimTypes.Role, "User"));
                }
            });

        await SendAsync(new Response
        {
            Token = jwtToken,
            Expiry = DateTime.UtcNow.AddDays(1)
        });
    }
}
