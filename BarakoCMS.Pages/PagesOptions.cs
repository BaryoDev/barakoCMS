namespace BarakoCMS.Pages;

/// <summary>
/// Which content type is the page tree, and which of its fields carry the tree. Bound from
/// <c>Modules:Pages</c>.
/// </summary>
/// <remarks>
/// The defaults match the <c>page</c> type the <c>blog</c> blueprint creates. Nothing else in the
/// module names a field, so a client whose type is <c>landing</c> with <c>MenuWeight</c> sets these
/// and changes no code. The slug field is not configured here: it is the one delivery already uses.
/// </remarks>
public sealed class PagesOptions
{
    /// <summary>The content type holding the pages.</summary>
    public string ContentType { get; set; } = "page";

    /// <summary>The reference field naming a page's parent. Empty or absent means a root page.</summary>
    public string ParentField { get; set; } = "ParentPage";

    /// <summary>The boolean field that puts a page in the navigation.</summary>
    public string ShowInNavigationField { get; set; } = "ShowInNavigation";

    /// <summary>The integer field that orders siblings. A page without one sorts after those with one.</summary>
    public string OrderField { get; set; } = "NavigationOrder";

    /// <summary>The field shown as a page's title in the navigation, breadcrumbs and tree.</summary>
    public string TitleField { get; set; } = "Title";

    /// <summary>The most ancestors a page may have. A root page has none.</summary>
    public int MaxDepth { get; set; } = 8;

    /// <summary>
    /// Slugs a root page may not take, compared case-insensitively. Empty by default; a site sets the
    /// top-level routes its renderer owns, such as <c>api</c> or <c>blog</c>.
    /// </summary>
    public List<string> ReservedSlugs { get; set; } = [];

    /// <summary>
    /// The slug of the root page served at <c>/</c>. Null or empty means no page is the home page.
    /// </summary>
    public string? HomeSlug { get; set; } = "home";

    /// <summary>
    /// The most pages one request reads. The tree is built in memory, so this is what keeps an
    /// anonymous request bounded.
    /// </summary>
    public int MaxPages { get; set; } = 1000;
}
