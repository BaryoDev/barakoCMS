using Microsoft.AspNetCore.Http;

namespace barakoCMS.Infrastructure.Security;

/// <summary>
/// Answers the deployment's public base URL for a request: the origin to put in anything a third
/// party reads back, such as an OAuth <c>redirect_uri</c> or a link in an email.
/// </summary>
/// <remarks>
/// The answer is the configured <c>App:BaseUrl</c>, else the request's own scheme and host, but
/// only where the host filter makes that host trustworthy. The <c>Host</c> header alone is the
/// caller's choice, so a module does not build a URL from it. Core registers the implementation.
/// </remarks>
public interface IPublicBaseUrl
{
    /// <summary>The base URL with no trailing slash.</summary>
    /// <exception cref="InvalidOperationException">
    /// Nothing configured a base URL and the request host cannot be believed, or a configured value
    /// is not an absolute http or https URL. The message names the setting. Core's implementation
    /// throws <c>BaseUrlNotConfiguredException</c> for the first case, which the host answers with
    /// a 503, so a caller catches nothing.
    /// </exception>
    string BaseUrl(HttpRequest request);
}
