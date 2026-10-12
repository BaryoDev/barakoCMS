using System.Security.Claims;
using barakoCMS.Models;
using Marten;

namespace barakoCMS.Infrastructure.Auth;

/// <summary>What a tenant's sign-up policy says about a person signing themselves up.</summary>
public enum SignUpOutcome
{
    /// <summary>
    /// No policy applies: the default tenant, a slug nobody registered, or a tenant whose policy is
    /// <see cref="TenantSignUp.NotSet"/>. The account is made the way it was before policies existed.
    /// </summary>
    AsBefore,

    /// <summary>The tenant takes no self sign-up: it is Closed, InviteOnly, or not Active.</summary>
    Refused,

    /// <summary>The tenant is Open: the account joins it with <see cref="SignUpDecision.RoleId"/>.</summary>
    Join,
}

/// <param name="Outcome">What happens.</param>
/// <param name="TenantSlug">The tenant decided for, as stored.</param>
/// <param name="RoleId">The role a <see cref="SignUpOutcome.Join"/> is given there.</param>
public sealed record SignUpDecision(SignUpOutcome Outcome, string TenantSlug, Guid? RoleId);

/// <summary>
/// The one reading of a tenant's sign-up policy, for registration, social sign-in and invitations,
/// so no entry path decides for itself who may join.
/// </summary>
/// <remarks>
/// Public because the ExternalAuth module consults it, the same way it mints through
/// <see cref="ITokenIssuer"/>.
/// </remarks>
public static class SignUpPolicy
{
    public const string ClosedMessage = "This tenant does not take sign-ups.";

    public const string ClosedToNewAccountsMessage =
        "This tenant is closed to new accounts, so an invitation cannot create one. Add a person who already has an account.";

    /// <summary>Decides for a person signing themselves up in <paramref name="tenantSlug"/>.</summary>
    public static async Task<SignUpDecision> DecideAsync(IQuerySession session, string? tenantSlug, CancellationToken ct)
    {
        var slug = string.IsNullOrWhiteSpace(tenantSlug) ? Tenant.DefaultSlug : tenantSlug.Trim().ToLowerInvariant();
        if (slug == Tenant.DefaultSlug)
        {
            return new SignUpDecision(SignUpOutcome.AsBefore, slug, null);
        }

        var tenant = await session.Query<Tenant>().FirstOrDefaultAsync(t => t.Slug == slug, ct);
        if (tenant is null)
        {
            return new SignUpDecision(SignUpOutcome.AsBefore, slug, null);
        }

        if (tenant.Status != TenantStatus.Active)
        {
            return new SignUpDecision(SignUpOutcome.Refused, slug, null);
        }

        return tenant.SignUp switch
        {
            TenantSignUp.Open when tenant.SignUpRoleId is { } role => new SignUpDecision(SignUpOutcome.Join, slug, role),
            TenantSignUp.NotSet => new SignUpDecision(SignUpOutcome.AsBefore, slug, null),

            // Open with no role is not a state the API stores. Read as closed rather than as a
            // membership with nothing in it.
            _ => new SignUpDecision(SignUpOutcome.Refused, slug, null),
        };
    }

    /// <summary>Whether an invitation in <paramref name="tenantSlug"/> may create an account.</summary>
    public static async Task<bool> RefusesNewAccountAsync(IQuerySession session, string tenantSlug, CancellationToken ct)
    {
        var tenant = await session.Query<Tenant>().FirstOrDefaultAsync(t => t.Slug == tenantSlug, ct);
        return tenant is { SignUp: TenantSignUp.Closed };
    }

    /// <summary>
    /// Stages the membership a <see cref="SignUpOutcome.Join"/> gives <paramref name="user"/>, unless
    /// the user has any membership row in the tenant already. Nothing is saved.
    /// </summary>
    /// <remarks>
    /// Any row, not only an active one. A member an administrator removed or suspended signing up
    /// again keeps the row they have, or open sign-up would undo the removal.
    /// </remarks>
    /// <returns>True when a membership was staged.</returns>
    public static async Task<bool> StageJoinAsync(
        IDocumentSession session, SignUpDecision decision, User user, CancellationToken ct)
    {
        if (decision is not { Outcome: SignUpOutcome.Join, RoleId: { } roleId })
        {
            return false;
        }

        var slug = decision.TenantSlug;
        if (await session.Query<Membership>().AnyAsync(m => m.UserId == user.Id && m.TenantSlug == slug, ct))
        {
            return false;
        }

        var actor = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("UserId", user.Id.ToString()), new Claim("Username", user.Username)], "signup"));

        await barakoCMS.Features.Tenants.Members.Members.AddAsync(
            session, actor, user.Id, slug, [roleId], profile: null,
            new Dictionary<string, object> { ["invited"] = false, ["signedUp"] = true }, ct);
        return true;
    }
}
