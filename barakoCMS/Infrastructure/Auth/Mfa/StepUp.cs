using FastEndpoints;

namespace barakoCMS.Infrastructure.Auth.Mfa;

/// <summary>Endpoint metadata: the caller must send a valid step-up token.</summary>
public sealed record StepUpRequired;

public static class StepUp
{
    public const string Header = "X-Step-Up";
    public const string RequiredHeader = "X-Step-Up-Required";

    /// <summary>
    /// Requires a step-up token (from <c>POST /api/auth/mfa/step-up</c>) for this endpoint, in the
    /// <c>X-Step-Up</c> header, issued to the same user as the access token.
    /// </summary>
    public static void RequireStepUp(this EndpointDefinition definition) =>
        definition.Metadata(new StepUpRequired());
}

/// <summary>
/// Refuses calls to <see cref="StepUp.RequireStepUp"/> endpoints that carry no valid step-up token for
/// the calling user. Runs after the capability gate, so a caller without the capability hears that
/// first, not a request for a code they could not use.
/// </summary>
public sealed class StepUpProcessor(IConfiguration config) : IGlobalPreProcessor
{
    public async Task PreProcessAsync(IPreProcessorContext context, CancellationToken ct)
    {
        var http = context.HttpContext;
        if (http.Response.HasStarted) return;
        if (http.GetEndpoint()?.Metadata.GetMetadata<StepUpRequired>() is null) return;

        var caller = http.User.FindFirst("UserId")?.Value;
        var proven = StepUpToken.ValidatedUserId(config, http.Request.Headers[StepUp.Header].ToString());
        if (caller is not null && proven is { } id && id.ToString() == caller) return;

        http.Response.StatusCode = StatusCodes.Status403Forbidden;
        http.Response.Headers[StepUp.RequiredHeader] = "true";
        await http.Response.WriteAsync("Enter your authenticator code to continue.", ct);
    }
}
