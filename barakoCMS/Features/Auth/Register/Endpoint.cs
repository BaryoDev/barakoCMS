using FastEndpoints;
using Marten;
using barakoCMS.Models;

namespace barakoCMS.Features.Auth.Register;

public class Endpoint : Endpoint<Request, Response>
{
    private readonly barakoCMS.Repository.IUserRepository _repo;
    private readonly IQuerySession _session;
    private readonly barakoCMS.Infrastructure.Services.IPasswordPolicyValidator _passwordValidator;

    public Endpoint(
        barakoCMS.Repository.IUserRepository repo,
        IQuerySession session,
        barakoCMS.Infrastructure.Services.IPasswordPolicyValidator passwordValidator)
    {
        _repo = repo;
        _session = session;
        _passwordValidator = passwordValidator;
    }

    public override void Configure()
    {
        Post("/api/auth/register");
        AllowAnonymous();
        Options(x => x.RequireRateLimiting("registration")); // 5 per hour
    }

    public override async Task HandleAsync(Request req, CancellationToken ct)
    {
        var existingUser = await _repo.GetByUsernameOrEmailAsync(req.Username, req.Email, ct);

        if (existingUser != null)
        {
            ThrowError("Username or Email already exists");
        }

        // Validate password against security policy
        var (isValid, errorMessage) = _passwordValidator.Validate(req.Password);
        if (!isValid)
        {
            ThrowError(errorMessage!);
        }

        var userRole = await _session.Query<Role>().FirstOrDefaultAsync(r => r.Name == "User", ct);
        // Note: In a real app, we should ensure the role exists or handle null. 
        // For now, we assume DataSeeder ran or we create it? 
        // Let's assume DataSeeder ran. If null, we might default to empty or throw.
        var roleIds = new List<Guid>();
        if (userRole != null)
        {
            roleIds.Add(userRole.Id);
        }

        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = req.Username,
            Email = req.Email,
            RoleIds = roleIds,
            // In a real app, use a proper password hasher like BCrypt
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(req.Password) 
        };

        _repo.Store(user);
        await _repo.SaveChangesAsync(ct);

        await SendAsync(new Response { Message = "User registered successfully" });
    }
}
