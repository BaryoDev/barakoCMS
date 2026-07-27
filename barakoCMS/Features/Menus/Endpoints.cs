using barakoCMS.Models;
using FastEndpoints;
using Marten;

namespace barakoCMS.Features.Menus;

/*
 * Site navigation menus. Admins manage them (create/update/delete/list); the frontend reads them
 * anonymously via the public endpoint. Tenant-scoped, so each site sees only its own menus. Nesting is
 * capped at one level on write so a menu stays a nav, not an arbitrary tree.
 */

public sealed class MenuWriteRequest
{
    public string Slug { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public List<MenuItem> Items { get; set; } = new();
}

internal static class MenuMapping
{
    /* Normalize and cap nesting at one level: a child's own children are dropped. */
    public static List<MenuItem> Sanitize(IEnumerable<MenuItem>? items) =>
        (items ?? Enumerable.Empty<MenuItem>())
            .Where(i => !string.IsNullOrWhiteSpace(i.Label))
            .Select(i => new MenuItem
            {
                Label = i.Label.Trim(),
                Url = (i.Url ?? string.Empty).Trim(),
                OpenInNewTab = i.OpenInNewTab,
                Children = (i.Children ?? new())
                    .Where(c => !string.IsNullOrWhiteSpace(c.Label))
                    .Select(c => new MenuItem
                    {
                        Label = c.Label.Trim(),
                        Url = (c.Url ?? string.Empty).Trim(),
                        OpenInNewTab = c.OpenInNewTab,
                        Children = new(), /* grandchildren are not supported */
                    })
                    .ToList(),
            })
            .ToList();

    public static string NormalizeSlug(string? slug) => (slug ?? string.Empty).Trim().ToLowerInvariant();
}

/// <summary>GET /api/menus — all menus for the current tenant (admin).</summary>
public class ListMenusEndpoint : EndpointWithoutRequest<List<Menu>>
{
    private readonly IQuerySession _session;
    public ListMenusEndpoint(IQuerySession session) => _session = session;

    public override void Configure()
    {
        Get("/api/menus");
        Roles("SuperAdmin", "Admin");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var menus = await _session.Query<Menu>().ToListAsync(ct);
        await SendOkAsync(menus.OrderBy(m => m.Slug).ToList(), ct);
    }
}

/// <summary>POST /api/menus — create a menu (admin).</summary>
public class CreateMenuEndpoint : Endpoint<MenuWriteRequest, Menu>
{
    private readonly IDocumentSession _session;
    public CreateMenuEndpoint(IDocumentSession session) => _session = session;

    public override void Configure()
    {
        Post("/api/menus");
        Roles("SuperAdmin", "Admin");
    }

    public override async Task HandleAsync(MenuWriteRequest req, CancellationToken ct)
    {
        var slug = MenuMapping.NormalizeSlug(req.Slug);
        if (string.IsNullOrEmpty(slug)) AddError(r => r.Slug, "A slug is required.");
        if (string.IsNullOrWhiteSpace(req.Name)) AddError(r => r.Name, "A name is required.");
        ThrowIfAnyErrors();

        if (await _session.Query<Menu>().AnyAsync(m => m.Slug == slug, ct))
        { AddError(r => r.Slug, "A menu with this slug already exists."); ThrowIfAnyErrors(); }

        var menu = new Menu
        {
            Id = Guid.NewGuid(),
            Slug = slug,
            Name = req.Name.Trim(),
            Items = MenuMapping.Sanitize(req.Items),
        };
        _session.Store(menu);
        await _session.SaveChangesAsync(ct);
        await SendOkAsync(menu, ct);
    }
}

/// <summary>PUT /api/menus/{slug} — replace a menu's name and items (admin).</summary>
public class UpdateMenuEndpoint : Endpoint<MenuWriteRequest, Menu>
{
    private readonly IDocumentSession _session;
    public UpdateMenuEndpoint(IDocumentSession session) => _session = session;

    public override void Configure()
    {
        Put("/api/menus/{slug}");
        Roles("SuperAdmin", "Admin");
    }

    public override async Task HandleAsync(MenuWriteRequest req, CancellationToken ct)
    {
        var slug = MenuMapping.NormalizeSlug(Route<string>("slug"));
        var menu = await _session.Query<Menu>().FirstOrDefaultAsync(m => m.Slug == slug, ct);
        if (menu is null) { await SendNotFoundAsync(ct); return; }

        if (!string.IsNullOrWhiteSpace(req.Name)) menu.Name = req.Name.Trim();
        menu.Items = MenuMapping.Sanitize(req.Items);
        menu.UpdatedAt = DateTime.UtcNow;
        _session.Store(menu);
        await _session.SaveChangesAsync(ct);
        await SendOkAsync(menu, ct);
    }
}

/// <summary>DELETE /api/menus/{slug} — delete a menu (admin).</summary>
public class DeleteMenuEndpoint : EndpointWithoutRequest
{
    private readonly IDocumentSession _session;
    public DeleteMenuEndpoint(IDocumentSession session) => _session = session;

    public override void Configure()
    {
        Delete("/api/menus/{slug}");
        Roles("SuperAdmin", "Admin");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var slug = MenuMapping.NormalizeSlug(Route<string>("slug"));
        var menu = await _session.Query<Menu>().FirstOrDefaultAsync(m => m.Slug == slug, ct);
        if (menu is null) { await SendNotFoundAsync(ct); return; }
        _session.Delete(menu);
        await _session.SaveChangesAsync(ct);
        await SendNoContentAsync(ct);
    }
}

public sealed record PublicMenuResponse(string Slug, string Name, List<MenuItem> Items);

/// <summary>GET /api/public/menus/{slug} — a menu for the frontend (anonymous). Literal "menus" wins
/// over the /api/public/{type}/{slug} content route.</summary>
public class PublicMenuEndpoint : EndpointWithoutRequest<PublicMenuResponse>
{
    private readonly IQuerySession _session;
    public PublicMenuEndpoint(IQuerySession session) => _session = session;

    public override void Configure()
    {
        Get("/api/public/menus/{slug}");
        AllowAnonymous();
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var slug = MenuMapping.NormalizeSlug(Route<string>("slug"));
        var menu = await _session.Query<Menu>().FirstOrDefaultAsync(m => m.Slug == slug, ct);
        if (menu is null) { await SendNotFoundAsync(ct); return; }

        HttpContext.Response.Headers.CacheControl = "public, max-age=60";
        await SendOkAsync(new PublicMenuResponse(menu.Slug, menu.Name, menu.Items), ct);
    }
}
