namespace BarakoCMS.Portability;

/// <summary>Why the bundle routes and their types are obsolete, said once.</summary>
/// <remarks>
/// The routes keep working until the removal: a bundle is accepted unchanged as a config document,
/// its <c>contentTypes</c> by the core and its <c>contents</c> by this module's section.
/// </remarks>
public static class PortabilityObsolete
{
    public const string Message =
        "Use the config engine instead: GET /api/config/export, and POST /api/config/plan and /api/config/apply, "
        + "which accept a Portability bundle as a document. Removal planned for barakoCMS 6.0.";
}
