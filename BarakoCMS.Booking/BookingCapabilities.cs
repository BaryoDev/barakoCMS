using barakoCMS.Models;
using barakoCMS.Modules;

namespace BarakoCMS.Booking;

/// <summary>What this module's authenticated endpoints ask for instead of a role name.</summary>
public static class BookingCapabilities
{
    /// <summary>Create, change and delete bookable resources.</summary>
    public const string ManageResources = "manage_booking_resources";

    internal static readonly string[] All = [ManageResources];

    /// <summary>Who starts with these: the seeded Admin role, by its id.</summary>
    internal static readonly CapabilityDefaults Defaults = CapabilityDefaults.For(All).GrantedTo(SystemRoles.Admin);
}
