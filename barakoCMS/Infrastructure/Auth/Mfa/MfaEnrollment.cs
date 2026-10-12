using FastEndpoints;

namespace barakoCMS.Infrastructure.Auth.Mfa;

/// <summary>Endpoint metadata: reachable with a token that still has to enroll in MFA.</summary>
public sealed record AllowedDuringMfaEnrollment;

public static class MfaEnrollment
{
    public const string Claim = "mfa_enroll";
    public const string Header = "X-Mfa-Enrollment-Required";

    public static void AllowDuringMfaEnrollment(this EndpointDefinition definition) =>
        definition.Metadata(new AllowedDuringMfaEnrollment());

    /// <summary>
    /// True for a user token marked <c>mfa_enroll=required</c>. Station and API key principals never
    /// carry the claim, so this is false for them.
    /// </summary>
    public static bool MustEnroll(System.Security.Claims.ClaimsPrincipal? user) =>
        user?.HasClaim(Claim, "required") == true;
}

/// <summary>
/// Refuses a token marked <c>mfa_enroll=required</c> on every endpoint that did not opt in, with a
/// header the client can act on: show the MFA setup screen.
/// </summary>
public sealed class MfaEnrollmentProcessor : IGlobalPreProcessor
{
    public async Task PreProcessAsync(IPreProcessorContext context, CancellationToken ct)
    {
        var http = context.HttpContext;
        if (http.Response.HasStarted) return;
        if (!MfaEnrollment.MustEnroll(http.User)) return;
        if (http.GetEndpoint()?.Metadata.GetMetadata<AllowedDuringMfaEnrollment>() is not null) return;

        http.Response.StatusCode = StatusCodes.Status403Forbidden;
        http.Response.Headers[MfaEnrollment.Header] = "true";
        await http.Response.WriteAsync("Set up MFA before using this account.", ct);
    }
}
