namespace BarakoCMS.Pages;

/// <summary>
/// The version of the JSON this module's endpoints return, carried as <c>contract</c> in each body.
/// </summary>
/// <remarks>
/// In the body rather than a header, because a body field survives proxies and caches. A renderer
/// that does not speak this number should show no menu rather than refuse to run. It moves on a
/// breaking change to these bodies only, independently of the module version and of the core
/// <c>ApiContract</c>.
/// </remarks>
public static class PagesContract
{
    public const int Version = 1;
}
