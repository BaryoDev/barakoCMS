using barakoCMS.Infrastructure.Viewers;

namespace barakoCMS.Features.Viewers.Create;

internal sealed class CreateViewerTokenRequest
{
    /// <summary>The caller's own identifier for the viewer. Opaque to the CMS.</summary>
    public string Subject { get; set; } = string.Empty;

    /// <summary>How long the token lives, in minutes. Defaults to 60, at most 1440.</summary>
    public int? LifetimeMinutes { get; set; }
}

internal sealed class CreateViewerTokenResponse
{
    public string Token { get; set; } = string.Empty;
    public DateTime ExpiresAt { get; set; }

    /// <summary>The request header that carries the token to public delivery.</summary>
    public string Header { get; set; } = ViewerToken.Header;
}
