namespace BarakoCMS.ExternalAuth;

/// <summary>
/// What a tenant gives people for what their identity provider says about them: one claim in the
/// id token, and for each value of it a role in that tenant.
/// </summary>
/// <remarks>
/// Global, like <see cref="ExternalIdentity"/>, and keyed by the tenant's slug, so a tenant has at
/// most one. The tenant slug is the whole isolation rule: it is read from the club the sign-in
/// asked for and written from the caller's own tenant, never from a request body.
/// </remarks>
public class ExternalRoleMap
{
    /// <summary>The tenant slug, lowercased.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>The id token claim to read, for example <c>groups</c> or <c>roles</c>.</summary>
    public string Claim { get; set; } = string.Empty;

    public List<ExternalRoleRule> Rules { get; set; } = [];

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>One claim value, from one provider, and the role in the tenant a person carrying it is given.</summary>
public class ExternalRoleRule
{
    /// <summary>The <c>Oidc:Providers</c> name whose sign-in this rule reads. Another provider's token never matches it.</summary>
    public string Provider { get; set; } = string.Empty;

    public string Value { get; set; } = string.Empty;

    public Guid RoleId { get; set; }

    /// <summary>
    /// The exact issuer the token must carry. Required when the provider's issuer is a template,
    /// where the provider name alone matches every directory.
    /// </summary>
    public string? Issuer { get; set; }
}
