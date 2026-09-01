using FastEndpoints;
using Marten;
using barakoCMS.Infrastructure.Auth;
using barakoCMS.Models;

namespace barakoCMS.Features.Auth.Register;

internal class VerifyRequest
{
    public string Token { get; set; } = string.Empty;
}

internal class VerifyResponse
{
    public string Message { get; set; } = string.Empty;
}

/// <summary>
/// POST /api/auth/register/verify. Turns a pending registration into an account, once.
/// </summary>
/// <remarks>
/// <para>
/// Every rejection uses one message. A token that never existed, one that expired, one already
/// spent, one whose username was taken by somebody else in the meantime, and one that lost a race
/// all read the same, because telling them apart tells an anonymous caller which addresses and
/// usernames are in play. That is the same rule <c>/api/auth/otp/verify</c> follows next door.
/// </para>
/// <para>
/// POST rather than GET, even though the token arrives in an emailed link. A GET is fetched by mail
/// scanners, link previewers and corporate proxies before the person clicks, and this token is
/// single use, so a GET route hands them an account creation the recipient never asked for and a
/// dead link when they do click. The link points at a frontend page that posts here.
/// </para>
/// <para>
/// It does not sign the caller in. Nothing here proves possession of the device, only of the
/// mailbox, and there is already an endpoint for turning mailbox possession into a session
/// (<c>/api/auth/otp/verify</c>) that costs a second round trip and does the MFA and device checks
/// properly. Minting tokens here would be a fourth issuer path to keep in step with those.
/// </para>
/// </remarks>
internal class VerifyEndpoint : Endpoint<VerifyRequest, VerifyResponse>
{
    private const string Invalid = "That verification link is invalid or has expired.";

    private readonly IDocumentSession _session;
    private readonly ILogger<VerifyEndpoint> _logger;

    public VerifyEndpoint(IDocumentSession session, ILogger<VerifyEndpoint> logger)
    {
        _session = session;
        _logger = logger;
    }

    public override void Configure()
    {
        Post("/api/auth/register/verify");
        AllowAnonymous();
        Options(x => x.RequireRateLimiting("registration"));
    }

    public override async Task HandleAsync(VerifyRequest req, CancellationToken ct)
    {
        if (!EmailVerificationToken.TryParse(req.Token, out var pendingId, out var secret))
        {
            ThrowError(Invalid);
            return;
        }

        var pending = await _session.LoadAsync<PendingRegistration>(pendingId, ct);
        if (pending is null || pending.Consumed || pending.ExpiresAt < DateTime.UtcNow)
        {
            ThrowError(Invalid);
            return;
        }

        if (!EmailVerificationToken.Matches(secret, pending.TokenHash))
        {
            ThrowError(Invalid);
            return;
        }

        // Re-checked here and not only at registration. Between the two, somebody else may have
        // verified the same address, an operator may have created the account, or an external
        // provider may have signed the real owner in and created it that way. Any of those means
        // this token has nothing left to create.
        var taken = await _session.Query<User>()
            .FirstOrDefaultAsync(u => u.Username == pending.Username || u.Email == pending.Email, ct);
        if (taken is not null)
        {
            // The token is spent either way. Leaving it live would let the same token be retried
            // until the collision happened to clear.
            Consume(pending);
            await TrySaveAsync(ct);
            ThrowError(Invalid);
            return;
        }

        Consume(pending);
        _session.Store(Endpoint.NewUser(
            await Endpoint.UserRoleIdsAsync(_session, ct),
            pending.Username,
            pending.Email,
            pending.PasswordHash));

        if (!await TrySaveAsync(ct))
        {
            ThrowError(Invalid);
            return;
        }

        _logger.LogInformation("Registration verified for {Username}", pending.Username);
        await Send.ResponseAsync(new VerifyResponse { Message = "Email confirmed. You can sign in now." });
    }

    private void Consume(PendingRegistration pending)
    {
        pending.Consumed = true;
        _session.Update(pending);
    }

    /// <summary>
    /// Saves, reporting a lost race rather than throwing, exactly as <c>/api/auth/otp/verify</c> does.
    /// </summary>
    /// <remarks>
    /// Two requests carrying one token are stopped twice over, and both stops throw. Optimistic
    /// concurrency on <c>PendingRegistration</c> means the loser's save fails, and the unique indexes
    /// on <c>User.Username</c> and <c>User.Email</c> mean an insert that got past that still fails.
    /// Uncaught, either would answer 500 to what is really one token being used once, which is the
    /// outcome that was wanted.
    /// </remarks>
    private async Task<bool> TrySaveAsync(CancellationToken ct)
    {
        try
        {
            await _session.SaveChangesAsync(ct);
            return true;
        }
        catch (JasperFx.ConcurrencyException)
        {
            return false;
        }
        catch (Exception ex) when (IsUniqueViolation(ex))
        {
            return false;
        }
    }

    /// <summary>
    /// Is this a Postgres unique-constraint violation (SQLSTATE 23505), at any depth? Marten wraps
    /// the Npgsql exception at a depth that varies by command, so the chain is walked.
    /// </summary>
    private static bool IsUniqueViolation(Exception? ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (current is Npgsql.PostgresException { SqlState: "23505" })
            {
                return true;
            }
        }

        return false;
    }
}
