using FastEndpoints;
using Marten;
using barakoCMS.Models;

namespace barakoCMS.Features.Auth.Register;

/// <summary>
/// POST /api/auth/register. It asks for an account; it does not create one.
/// </summary>
/// <remarks>
/// <para>
/// The account appears at <c>/api/auth/register/verify</c>, when the address named here hands back
/// the token that was emailed to it. Until then there is no user document, so there is nothing to
/// sign in to, nothing holding the username, and nothing for an external provider to match against.
/// </para>
/// <para>
/// The third of those is why this is a security fix rather than a spam control. External sign-in
/// joins a provider's verified email to a local account by address alone (see
/// <c>SocialSignIn.IssueAsync</c>, which refuses any provider that has not asserted the address is
/// verified). Writing an arbitrary unproven address into <c>User.Email</c> undid that from the other
/// side: register as somebody else's address, wait for them to sign in with Google, and the provider
/// puts them into the account you control. Refusing to sign in until verified would not have closed
/// it, because that path never checks a password or a flag on the way in. Only the absence of the
/// row closes it. See DECISIONS.md D10.
/// </para>
/// <para>
/// Everything below answers identically whether or not the address is already registered. The one
/// difference a caller can see is the password policy, which is about their own input and discloses
/// nothing about anybody else.
/// </para>
/// </remarks>
internal class Endpoint : Endpoint<Request, Response>
{
    /// <summary>
    /// The one answer. Deliberately says nothing about whether an account was created, whether the
    /// address was already taken, or whether mail actually went out.
    /// </summary>
    private const string Accepted =
        "If that email address can be registered, we have sent it a link to confirm it.";

    private readonly barakoCMS.Repository.IUserRepository _repo;
    private readonly IQuerySession _session;
    private readonly barakoCMS.Infrastructure.Services.IPasswordPolicyValidator _passwordValidator;
    private readonly barakoCMS.Core.Interfaces.IEmailVerificationService _verification;
    private readonly barakoCMS.Infrastructure.Auth.EmailVerificationOptions _options;

    public Endpoint(
        barakoCMS.Repository.IUserRepository repo,
        IQuerySession session,
        barakoCMS.Infrastructure.Services.IPasswordPolicyValidator passwordValidator,
        barakoCMS.Core.Interfaces.IEmailVerificationService verification,
        barakoCMS.Infrastructure.Auth.EmailVerificationOptions options)
    {
        _repo = repo;
        _session = session;
        _passwordValidator = passwordValidator;
        _verification = verification;
        _options = options;
    }

    public override void Configure()
    {
        Post("/api/auth/register");
        AllowAnonymous();
        Options(x => x.RequireRateLimiting("registration")); // 5 per hour
    }

    public override async Task HandleAsync(Request req, CancellationToken ct)
    {
        // Before anything that touches the database, and before the branch below, so the hashing
        // cost is spent on every path. A policy failure is about the caller's own input.
        var (isValid, errorMessage) = _passwordValidator.Validate(req.Password);
        if (!isValid)
        {
            ThrowError(errorMessage!);
            return;
        }

        var email = req.Email.Trim().ToLowerInvariant();
        var passwordHash = BCrypt.Net.BCrypt.HashPassword(req.Password);

        if (!_options.Required)
        {
            await RegisterWithoutVerificationAsync(req.Username, email, passwordHash, ct);
            return;
        }

        var owner = await _session.Query<User>().FirstOrDefaultAsync(u => u.Email.ToLower() == email, ct);
        if (owner is not null)
        {
            // No pending registration, and the mailbox owner hears about the attempt rather than the
            // caller. Sending here rather than returning early also keeps the two paths doing
            // comparable work, so the response time is not the oracle the response body is not.
            _ = await _verification.SendAlreadyRegisteredAsync(owner.Email, ct);
            await Send.ResponseAsync(new Response { Message = Accepted });
            return;
        }

        // A username already in use is NOT checked here, on purpose. Refusing at this point would
        // tell an anonymous caller which usernames exist, on the one endpoint that takes care not
        // to. The pending row is harmless: verification re-checks, and a token whose username has
        // since been taken is refused with the same message every other rejection there uses.
        _ = await _verification.IssueAsync(req.Username, email, passwordHash, ct);

        await Send.ResponseAsync(new Response { Message = Accepted });
    }

    /// <summary>
    /// The pre-4.0 path, for a deployment that has acknowledged it wants unverified registration
    /// (<c>Auth:RequireEmailVerification=false</c>, which will not start without
    /// <c>Auth:AcknowledgeUnverifiedRegistration</c>). It still answers with <see cref="Accepted"/>
    /// in every case: the enumeration fix is not part of what that setting turns off.
    /// </summary>
    private async Task RegisterWithoutVerificationAsync(
        string username, string email, string passwordHash, CancellationToken ct)
    {
        var existing = await _repo.GetByUsernameOrEmailAsync(username, email, ct);
        if (existing is null)
        {
            _repo.Store(NewUser(await UserRoleIdsAsync(_session, ct), username, email, passwordHash));
            await _repo.SaveChangesAsync(ct);
        }

        await Send.ResponseAsync(new Response { Message = Accepted });
    }

    /// <summary>
    /// DataSeeder creates the "User" role on startup, so this normally finds it. When it does not,
    /// the account is created with no roles rather than refused. That fails closed: PermissionResolver
    /// returns false for a user with no roles, so the account exists and can sign in but can do
    /// nothing until someone assigns a role.
    /// </summary>
    internal static async Task<List<Guid>> UserRoleIdsAsync(IQuerySession session, CancellationToken ct)
    {
        var userRole = await session.Query<Role>().FirstOrDefaultAsync(r => r.Name == "User", ct);
        return userRole is null ? new List<Guid>() : new List<Guid> { userRole.Id };
    }

    internal static User NewUser(List<Guid> roleIds, string username, string email, string passwordHash) => new()
    {
        Id = Guid.NewGuid(),
        Username = username,
        Email = email,
        RoleIds = roleIds,
        PasswordHash = passwordHash,
    };
}
