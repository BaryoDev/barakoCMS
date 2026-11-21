namespace barakoCMS.Infrastructure.Security;

/// <summary><see cref="IPublicBaseUrl"/> over <see cref="CanonicalHost"/>.</summary>
internal sealed class CanonicalPublicBaseUrl(IConfiguration configuration) : IPublicBaseUrl
{
    public string BaseUrl(HttpRequest request) =>
        CanonicalHost.BaseUrl(configuration, request)
        ?? throw new BaseUrlNotConfiguredException(CanonicalHost.BaseUrlKey);
}
