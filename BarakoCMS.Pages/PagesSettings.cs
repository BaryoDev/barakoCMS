using Marten;

namespace BarakoCMS.Pages;

/// <summary>
/// A tenant's own choice of the type and fields that form its page tree. Each value left null takes
/// the deployment's, from <c>Modules:Pages</c>.
/// </summary>
/// <remarks>
/// Per tenant, so two tenants on one deployment can name different page types and fields. A tenant
/// with no row, which is every tenant before this existed, reads exactly what <c>Modules:Pages</c>
/// says, as before. <see cref="PagesOptions.MaxPages"/> is not here: it bounds the work an anonymous
/// request may ask for, which is the operator's to set and not a tenant's.
/// </remarks>
public sealed class PagesSettings
{
    /// <summary>The one row a tenant holds.</summary>
    public static readonly Guid RowId = new("5a9e5000-0000-4000-8000-000000000001");

    public Guid Id { get; set; } = RowId;

    public string? ContentType { get; set; }
    public string? ParentField { get; set; }
    public string? ShowInNavigationField { get; set; }
    public string? OrderField { get; set; }
    public string? TitleField { get; set; }
    public int? MaxDepth { get; set; }
    public List<string>? ReservedSlugs { get; set; }
    public string? HomeSlug { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>The deployment's options with this tenant's values laid over them.</summary>
    public PagesOptions Over(PagesOptions deployment) => new()
    {
        ContentType = ContentType ?? deployment.ContentType,
        ParentField = ParentField ?? deployment.ParentField,
        ShowInNavigationField = ShowInNavigationField ?? deployment.ShowInNavigationField,
        OrderField = OrderField ?? deployment.OrderField,
        TitleField = TitleField ?? deployment.TitleField,
        MaxDepth = MaxDepth ?? deployment.MaxDepth,
        ReservedSlugs = ReservedSlugs ?? deployment.ReservedSlugs,
        HomeSlug = HomeSlug ?? deployment.HomeSlug,
        MaxPages = deployment.MaxPages,
    };

    /// <summary>The options the session's tenant reads: its own row over the deployment's.</summary>
    public static async Task<PagesOptions> ResolveAsync(IQuerySession session, PagesOptions deployment, CancellationToken ct) =>
        await session.LoadAsync<PagesSettings>(RowId, ct) is { } row ? row.Over(deployment) : deployment;
}
