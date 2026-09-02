using Microsoft.Extensions.Configuration;

namespace barakoCMS.Infrastructure.Auth;

/// <summary>
/// Whether self-registration has to prove the address it was given, read once at startup.
/// </summary>
/// <remarks>
/// Required by default. This is the one place in this file that departs from "a config default must
/// preserve existing behaviour", and it departs on purpose: what it preserves would be the defect.
/// An unverified address on a user document is a sign-in for whoever the external providers later
/// hand that address to, so leaving verification off by default would ship the hole to every
/// deployment that never read the release notes. The break is called out in the changelog and in
/// docs/upgrading-to-4.0.md.
///
/// Turning it off is still a legitimate choice for a deployment with no mail transport, or one
/// behind a closed network where the registration form is not reachable from outside. It is not a
/// choice anybody should arrive at by leaving a key unset, so it needs the acknowledgement, the
/// same shape <c>Erasure:Mode=None</c> uses and for the same reason. See DECISIONS.md D9 and D10.
/// </remarks>
public sealed class EmailVerificationOptions
{
    public const string RequiredKey = "Auth:RequireEmailVerification";
    public const string AcknowledgeKey = "Auth:AcknowledgeUnverifiedRegistration";

    /// <summary>
    /// How long an emailed token stays good. Far longer than the ten minutes an
    /// <c>OtpCode</c> gets, because the two are answering different questions: an OTP is typed from
    /// a screen the person is looking at, and this one has to survive a greylisting mail server and
    /// somebody getting back to their inbox after work. A day is the usual answer, and the token is
    /// single use regardless, so the window bounds replay of a token nobody used rather than of a
    /// live session.
    /// </summary>
    public static readonly TimeSpan TokenLifetime = TimeSpan.FromHours(24);

    public bool Required { get; init; } = true;

    /// <summary>Required to run with <see cref="Required"/> false, so it cannot be arrived at by accident.</summary>
    public bool AcknowledgeUnverifiedRegistration { get; init; }

    public static EmailVerificationOptions FromConfiguration(IConfiguration configuration) => new()
    {
        Required = configuration.GetValue(RequiredKey, true),
        AcknowledgeUnverifiedRegistration = configuration.GetValue<bool>(AcknowledgeKey),
    };

    /// <summary>Throws when registration would write addresses nobody proved without anybody saying so.</summary>
    public void Validate()
    {
        if (!Required && !AcknowledgeUnverifiedRegistration)
        {
            throw new InvalidOperationException(
                $"{RequiredKey} is false, which lets anyone create an account against an address they "
                + "do not own, including one an external provider will later match a real person to. "
                + $"Set {AcknowledgeKey} to true to confirm this deployment's registration form is not "
                + "reachable by the public, or leave verification on.");
        }
    }
}
