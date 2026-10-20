namespace BarakoCMS.Booking;

/// <summary>
/// The version of the JSON this module's endpoints return, carried as <c>contract</c> in each body.
/// </summary>
/// <remarks>
/// It moves on a breaking change to these bodies only, independently of the module version and of
/// the core <c>ApiContract</c>.
/// </remarks>
public static class BookingContract
{
    public const int Version = 1;
}
