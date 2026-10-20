using barakoCMS.Models;
using barakoCMS.Modules;

namespace BarakoCMS.Payments;

/// <summary>What this module's authenticated endpoints ask for instead of a role name.</summary>
public static class PaymentsCapabilities
{
    /// <summary>Declare a content type payable, or stop it being payable.</summary>
    public const string ManagePayments = "manage_payments";

    internal static readonly string[] All = [ManagePayments];

    /// <summary>Who starts with these: the seeded Admin role, by its id.</summary>
    internal static readonly CapabilityDefaults Defaults = CapabilityDefaults.For(All).GrantedTo(SystemRoles.Admin);
}
