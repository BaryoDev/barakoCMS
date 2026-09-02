using FastEndpoints;

namespace barakoCMS.Infrastructure.Auth;

/// <summary>
/// Endpoint metadata naming the system capability a caller needs, plus the role names that gated the
/// endpoint before capabilities existed.
/// </summary>
/// <param name="Capability">A value from <see cref="Models.SystemCapabilities"/>.</param>
/// <param name="LegacyRoles">
/// The exact role names the endpoint's old <c>Roles(...)</c> gate named. Honoured as an OR with the
/// capability so an existing deployment, whose stored roles carry no capabilities yet, keeps working
/// across the upgrade. Turned off with <c>Auth:LegacyRoleFallback=false</c>.
/// </param>
public sealed record RequiredCapability(string Capability, IReadOnlyList<string> LegacyRoles);

/// <summary>
/// Declares an endpoint's capability gate from <c>Configure()</c>, in place of <c>Roles(...)</c>.
/// </summary>
/// <remarks>
/// This replaces the role gate rather than adding to it. FastEndpoints combines role gates with AND,
/// so keeping <c>Roles("SuperAdmin")</c> alongside a capability would mean a caller needed both, and
/// a role created at runtime still could not reach anything. Enforcement is
/// <see cref="CapabilityGateProcessor"/>; authentication is still FastEndpoints' default, so an
/// anonymous caller is refused with 401 before any of this runs.
/// </remarks>
public static class CapabilityGate
{
    public static void RequireCapability(
        this EndpointDefinition definition, string capability, params string[] legacyRoles)
    {
        if (string.IsNullOrWhiteSpace(capability))
            throw new ArgumentException("A capability gate needs a capability name.", nameof(capability));

        definition.Metadata(new RequiredCapability(capability, legacyRoles));
    }
}
