using System.Text.RegularExpressions;

namespace BarakoCMS.ExternalAuth;

/// <summary>One OpenID Connect provider, read from <c>Oidc:Providers:{name}</c>.</summary>
/// <param name="Name">The configuration key, lowercased. It is the route segment and the cookie suffix.</param>
/// <param name="Authority">Where the discovery document is fetched from, as configured.</param>
/// <param name="Issuer">
/// What the discovery document's <c>issuer</c> has to equal. The authority unless <c>Issuer</c> is
/// set, which a provider whose issuer is a template (Microsoft's <c>{tenantid}</c>) needs.
/// </param>
/// <param name="EmailVerifiedClaim">
/// The id token claim that says the provider vouches for the address. It decides which local account
/// a first sign-in lands on, so it has to be a claim only the provider can set.
/// </param>
internal sealed record OidcProvider(
    string Name,
    string DisplayName,
    string Authority,
    string Issuer,
    string ClientId,
    string ClientSecret,
    string Scopes,
    string EmailVerifiedClaim)
{
    public const string TenantPlaceholder = "{tenantid}";

    public bool IssuerIsTemplate => Issuer.Contains(TenantPlaceholder, StringComparison.Ordinal);

    /// <summary>
    /// The provider answers the authorization request with a cross-site form post (Apple) rather
    /// than a redirect, so the callback is a POST and the flow cookies have to be sent on one.
    /// </summary>
    public bool FormPost { get; init; }

    /// <summary>When set, the client secret is a JWT signed with this key instead of <see cref="ClientSecret"/>.</summary>
    public OidcSignedSecret? SignedSecret { get; init; }

    /// <summary>
    /// The audiences the id token grant accepts. Empty means the grant is off for this provider. The
    /// redirect flow always takes <see cref="ClientId"/> alone.
    /// </summary>
    public IReadOnlyList<string> IdTokenAudiences { get; init; } = [];

    /// <summary>
    /// Also take the text <c>"true"</c> for the verified claim. Apple documents its claim as a string
    /// or a boolean. Off unless configured, for the reason given in <see cref="OidcIdToken"/>.
    /// </summary>
    public bool EmailVerifiedMayBeText { get; init; }

    /// <summary>
    /// <c>__Host-</c> makes the browser refuse the cookie unless it is Secure, has Path=/ and names
    /// no Domain, so a sibling subdomain cannot plant a state of its own choosing.
    /// </summary>
    public string StateCookie => $"__Host-oidc_{Name}";

    public string ClubCookie => $"__Host-oidc_{Name}_club";

    public string DiscoveryUrl => Authority.TrimEnd('/') + "/.well-known/openid-configuration";

    // The compiler-written ToString would print the client secret into any log line or exception
    // message this record ends up in.
    public override string ToString() => $"OidcProvider {{ Name = {Name} }}";
}

/// <summary>Reads the <c>Oidc:Providers</c> section. Read per request, like the other providers.</summary>
internal static partial class OidcProviders
{
    public const string Section = "Oidc:Providers";
    public const string DefaultScopes = "openid email profile";
    public const string DefaultEmailVerifiedClaim = "email_verified";

    /// <summary>Bounds the anonymous <c>/api/auth/providers</c> list and the metadata cache.</summary>
    public const int MaxProviders = 20;

    private const int MaxDisplayNameLength = 100;

    /// <summary>Bounds the id token grant's audience list.</summary>
    public const int MaxAudiences = 20;

    private const int MaxAudienceLength = 255;

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,31}$")]
    private static partial Regex NamePattern();

    /// <summary>The providers that are on, by name, at most <see cref="MaxProviders"/>.</summary>
    public static IReadOnlyList<OidcProvider> Enabled(IConfiguration config)
    {
        if (ExternalAuthSupport.ExternalAuthDisabled(config))
        {
            return [];
        }

        return config.GetSection(Section).GetChildren()
            .Select(section => Read(config, section, out _))
            .Where(provider => provider is not null)
            .Select(provider => provider!)
            .OrderBy(provider => provider.Name, StringComparer.Ordinal)
            .Take(MaxProviders)
            .ToList();
    }

    /// <summary>The enabled provider with this name, compared the way the name is stored: lowercased.</summary>
    public static OidcProvider? Find(IConfiguration config, string? name)
    {
        var wanted = (name ?? string.Empty).Trim().ToLowerInvariant();
        return NamePattern().IsMatch(wanted)
            ? Enabled(config).FirstOrDefault(provider => provider.Name == wanted)
            : null;
    }

    /// <summary>
    /// One line for each section that has a client id and is still off, for the startup log. A
    /// section with no client id, or with <c>Enabled</c> false, is off on purpose and says nothing.
    /// </summary>
    public static IReadOnlyList<string> Problems(IConfiguration config)
    {
        var problems = new List<string>();
        foreach (var section in config.GetSection(Section).GetChildren().Take(MaxProviders * 2))
        {
            Read(config, section, out var problem);
            if (problem is not null)
            {
                problems.Add(problem);
            }
        }

        return problems;
    }

    private static OidcProvider? Read(IConfiguration config, IConfigurationSection section, out string? problem)
    {
        problem = null;
        if (!ExternalAuthSupport.ProviderEnabled(config, section.Path, "ClientId"))
        {
            return null;
        }

        var name = section.Key.Trim().ToLowerInvariant();
        if (!NamePattern().IsMatch(name))
        {
            problem = "a provider name must be 1 to 32 characters of a-z, 0-9 and hyphen, starting with a letter or digit";
            return null;
        }

        var authority = section["Authority"]?.Trim() ?? string.Empty;
        if (!IsIssuerUrl(authority))
        {
            problem = $"{Section}:{name}:Authority must be an absolute https URL with no query or fragment";
            return null;
        }

        var signed = OidcSignedSecret.Read(section.GetSection(OidcSignedSecret.Section), out var signedProblem);
        if (signedProblem is not null)
        {
            problem = $"{Section}:{name}:{OidcSignedSecret.Section} {signedProblem}";
            return null;
        }

        var secret = section["ClientSecret"] ?? string.Empty;
        if (signed is null && string.IsNullOrWhiteSpace(secret))
        {
            problem = $"{Section}:{name}:ClientSecret is not set";
            return null;
        }

        var responseMode = section["ResponseMode"]?.Trim() ?? string.Empty;
        if (responseMode is not ("" or "query" or "form_post"))
        {
            problem = $"{Section}:{name}:ResponseMode must be query or form_post";
            return null;
        }

        var audiences = Audiences(section.GetSection("IdTokenAudiences"));
        if (audiences is null)
        {
            problem = $"{Section}:{name}:IdTokenAudiences takes at most {MaxAudiences} values of at most {MaxAudienceLength} characters";
            return null;
        }

        var issuer = section["Issuer"]?.Trim();
        if (string.IsNullOrEmpty(issuer))
        {
            issuer = authority;
        }
        else if (!IsIssuerUrl(issuer.Replace(OidcProvider.TenantPlaceholder, "tenant", StringComparison.Ordinal)))
        {
            problem = $"{Section}:{name}:Issuer must be an absolute https URL with no query or fragment";
            return null;
        }

        var scopes = (section["Scopes"] ?? DefaultScopes)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        if (!scopes.Contains("openid", StringComparer.Ordinal))
        {
            scopes.Insert(0, "openid");
        }

        var displayName = section["DisplayName"]?.Trim();
        if (string.IsNullOrEmpty(displayName))
        {
            displayName = name;
        }
        else if (displayName.Length > MaxDisplayNameLength)
        {
            displayName = displayName[..MaxDisplayNameLength];
        }

        var verifiedClaim = section["EmailVerifiedClaim"]?.Trim();

        return new OidcProvider(
            name,
            displayName,
            authority,
            issuer,
            section["ClientId"]!.Trim(),
            secret,
            string.Join(' ', scopes),
            string.IsNullOrEmpty(verifiedClaim) ? DefaultEmailVerifiedClaim : verifiedClaim)
        {
            FormPost = responseMode == "form_post",
            SignedSecret = signed,
            IdTokenAudiences = audiences,
            EmailVerifiedMayBeText = string.Equals(section["EmailVerifiedMayBeText"], "true", StringComparison.OrdinalIgnoreCase),
        };
    }

    /// <summary>
    /// A configuration array, or one value with the audiences separated by commas or spaces, which
    /// is what an environment variable can hold. Null when there are too many or one is too long.
    /// </summary>
    private static IReadOnlyList<string>? Audiences(IConfigurationSection section)
    {
        var values = section.GetChildren().Select(child => child.Value ?? string.Empty).ToList();
        if (!string.IsNullOrWhiteSpace(section.Value))
        {
            values.Add(section.Value);
        }

        var audiences = values
            .SelectMany(value => value.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        return audiences.Count > MaxAudiences || audiences.Any(a => a.Length > MaxAudienceLength) ? null : audiences;
    }

    /// <summary>
    /// Absolute and https, with no user info or fragment. Applied to every endpoint the discovery
    /// document names, which may carry a query.
    /// </summary>
    public static bool IsHttpsUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && string.IsNullOrEmpty(uri.UserInfo)
        && string.IsNullOrEmpty(uri.Fragment)
        && !string.IsNullOrEmpty(uri.Host);

    /// <summary>An issuer identifier: an https URL with no query either, as OpenID Connect defines one.</summary>
    public static bool IsIssuerUrl(string? value) =>
        IsHttpsUrl(value) && string.IsNullOrEmpty(new Uri(value!).Query);
}
