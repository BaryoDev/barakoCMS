using barakoCMS.Models;
using Marten;
using Microsoft.Extensions.Configuration;

namespace barakoCMS.Infrastructure.Multitenancy;

/// <summary>What a deployment says about its tenants.</summary>
public enum TenancyMode
{
    /// <summary>
    /// The default, and what every deployment did before the setting existed. A request that names
    /// no tenant is served from the default partition, and a slug with no <see cref="Tenant"/>
    /// document is served as a partition of its own. Registered tenants work here too.
    /// </summary>
    Single,

    /// <summary>
    /// Every request and every token belongs to a registered, active tenant. The default partition
    /// and a slug with no active <see cref="Tenant"/> document are refused.
    /// </summary>
    Multi,
}

/// <summary>
/// The tenancy mode this deployment runs, read from <see cref="ModeKey"/>.
/// </summary>
/// <remarks>
/// An unknown value stops the host. The failure this guards against is an operator who typed
/// <c>Mutli</c> believing unregistered slugs are refused while they are served.
/// </remarks>
public sealed class TenancyOptions
{
    public const string ModeKey = "Tenancy:Mode";

    public TenancyMode Mode { get; init; } = TenancyMode.Single;

    public bool IsMulti => Mode == TenancyMode.Multi;

    public static TenancyOptions FromConfiguration(IConfiguration configuration)
    {
        var raw = configuration[ModeKey];
        if (string.IsNullOrWhiteSpace(raw))
            return new TenancyOptions();

        // By name, not Enum.TryParse, which also accepts any number.
        foreach (var mode in Enum.GetValues<TenancyMode>())
        {
            if (string.Equals(raw.Trim(), mode.ToString(), StringComparison.OrdinalIgnoreCase))
                return new TenancyOptions { Mode = mode };
        }

        throw new InvalidOperationException(
            $"{ModeKey} is '{raw}', which is not a mode. Valid values: "
            + string.Join(", ", Enum.GetNames<TenancyMode>()) + ".");
    }

    /// <summary>
    /// Whether a token or an API key for <paramref name="slug"/> is refused because of the mode:
    /// in Multi, the default partition and any slug with no active <see cref="Tenant"/> document.
    /// Never in Single.
    /// </summary>
    /// <param name="session">Any session. <see cref="Tenant"/> is stored once for the deployment.</param>
    /// <param name="slug">Trimmed and lowercased by the caller, as it is stored.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<bool> RefusesAsync(IQuerySession session, string slug, CancellationToken ct)
    {
        if (!IsMulti)
            return false;

        return slug == Tenant.DefaultSlug
            || !await session.Query<Tenant>().AnyAsync(t => t.Slug == slug && t.IsActive, ct);
    }
}
