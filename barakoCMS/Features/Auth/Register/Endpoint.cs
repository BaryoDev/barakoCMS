using FastEndpoints;
using Marten;
using barakoCMS.Models;

namespace barakoCMS.Features.Auth.Register;

public class Endpoint : Endpoint<Request, Response>
{
    private readonly barakoCMS.Repository.IUserRepository _repo;
    private readonly IQuerySession _session;

    public Endpoint(barakoCMS.Repository.IUserRepository repo, IQuerySession session)
    {
        _repo = repo;
        _session = session;
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

        // Password Complexity: Min 8, One Upper, One Lower, One Number, One Special
        var hasNumber = new System.Text.RegularExpressions.Regex(@"[0-9]+");
        var hasUpperChar = new System.Text.RegularExpressions.Regex(@"[A-Z]+");
        var hasLowerChar = new System.Text.RegularExpressions.Regex(@"[a-z]+");
        var hasSymbols = new System.Text.RegularExpressions.Regex(@"[!@#$%^&*()_+=\[{\]};:<>|./?,-]");

        if (req.Password.Length < 8 || !hasUpperChar.IsMatch(req.Password) || !hasLowerChar.IsMatch(req.Password) || !hasNumber.IsMatch(req.Password) || !hasSymbols.IsMatch(req.Password))
        {
            ThrowError("Password must be at least 8 characters long and contain at least one uppercase letter, one lowercase letter, one number, and one special character.");
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
