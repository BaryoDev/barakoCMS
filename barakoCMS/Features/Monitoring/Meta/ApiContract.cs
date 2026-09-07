namespace barakoCMS.Features.Monitoring.Meta;

/// <summary>
/// The version of the HTTP contract this build of the API implements: the routes under
/// <c>Features/*</c>, the JSON they accept and return, and the status codes they answer with.
/// CLAUDE.md section 6 says what counts as a breaking change to it.
/// </summary>
/// <remarks>
/// <para>
/// Mirrors <see cref="barakoCMS.Modules.ModuleContract"/> and for the same reason: the package can
/// move from 4.0 to 4.1 without a single HTTP field changing, and a break to the HTTP surface does
/// not need to wait for a package major to land. The two version numbers move independently.
/// </para>
/// <para>
/// A caller reads this from the <c>X-Api-Contract-Version</c> header, sent on every response
/// including a 401, or from <c>ApiContractVersion</c> in <c>GET /api/meta</c> once signed in. The
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
    /// What moves it: removing a response field, renaming one, changing a field's type, changing
    /// a status code, or tightening request validation so a request the API used to accept is now
    /// rejected. Adding an optional field, to a request or a response, does not.
    /// </summary>
    public const int Version = 1;

    /// <summary>
    /// The header every response carries the version on, so a caller can read it without a token.
    /// </summary>
    public const string HeaderName = "X-Api-Contract-Version";
}
