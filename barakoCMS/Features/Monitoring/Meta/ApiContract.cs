namespace barakoCMS.Features.Monitoring.Meta;

/// <summary>
/// The versions of the HTTP contract this build of the API implements: the routes under
/// <c>Features/*</c>, the JSON they accept and return, and the status codes they answer with.
/// CLAUDE.md section 6 says what counts as a breaking change to it. One number per surface, so a
/// change to what the console drives does not stop a renderer that reads only delivery. A module
/// versions its own endpoints through <see cref="barakoCMS.Modules.IBarakoModule.HttpContractVersion"/>.
/// </summary>
/// <remarks>
/// <para>
/// Mirrors <see cref="barakoCMS.Modules.ModuleContract"/> and for the same reason: the package can
/// move from 4.0 to 4.1 without a single HTTP field changing, and a break to the HTTP surface does
/// not need to wait for a package major to land. The two version numbers move independently.
/// </para>
/// <para>
/// A caller reads the admin number from the <c>X-Api-Contract-Version</c> header, sent on every
/// response including a 401, or from <c>ApiContractVersion</c> in <c>GET /api/meta</c> once signed
/// in. The delivery number is on <c>X-Delivery-Contract-Version</c> and in
/// <c>DeliveryContractVersion</c> the same way. The
/// header exists because <c>/api/meta</c> requires a session and a console has to be able to say
/// "this build is too new for that API" before a user signs in, and again mid-session if a rolling
/// upgrade moves it underneath them.
/// </para>
/// <para>
/// There is no <c>MinimumSupported</c> here, unlike <see cref="barakoCMS.Modules.ModuleContract"/>.
/// The console is the side that breaks when it is too old for the API, so it is the console's job
/// to carry the range of versions it works with and refuse to run outside it. The API only needs
/// to say what it is.
/// </para>
/// </remarks>
internal static class ApiContract
{
    /// <summary>
    /// The admin surface: every core route that is not a delivery route, which is what a console
    /// drives. It is the number this header and <c>ApiContractVersion</c> have always carried.
    /// What moves it: removing a response field, renaming one, changing a field's type, changing
    /// a status code, or tightening request validation so a request the API used to accept is now
    /// rejected. Adding an optional field, to a request or a response, does not.
    /// </summary>
    public const int Version = 6;

    /// <summary>
    /// The header every response carries the version on, so a caller can read it without a token.
    /// </summary>
    public const string HeaderName = "X-Api-Contract-Version";

    /// <summary>
    /// The delivery surface: the core routes a site reads without a console session, which is
    /// everything under <c>/api/public/</c> and the two anonymous tenant lookups a renderer starts
    /// from. The same kinds of change move it, and a change to an admin route alone does not.
    /// </summary>
    /// <remarks>
    /// Starts at 6 rather than 1. Until this number existed <see cref="Version"/> stood for the
    /// delivery routes too, so a consumer that begins checking delivery takes its floor from the
    /// number it already knew, and starting lower would put this API under that floor on the day
    /// the two were split. A literal of its own, not <c>= Version</c>, or the two could never part.
    /// </remarks>
    public const int DeliveryVersion = 6;

    /// <summary>The header every response carries <see cref="DeliveryVersion"/> on.</summary>
    public const string DeliveryHeaderName = "X-Delivery-Contract-Version";

    public static readonly string DeliveryHeaderValue =
        DeliveryVersion.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
