namespace barakoCMS.Models;

/*
 * A named navigation menu for a site frontend (e.g. "main", "footer"). Tenant-scoped, so each site
 * has its own. Items are an ordered list with one optional level of children, which covers a normal
 * site nav without turning into an arbitrary tree. Managed by an admin; delivered publicly (anonymous)
 * for the frontend to render its chrome.
 */
public class Menu
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /* Stable handle used to fetch the menu, e.g. "main" or "footer". Unique per tenant. */
    public string Slug { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public List<MenuItem> Items { get; set; } = new();

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public class MenuItem
{
    public string Label { get; set; } = string.Empty;

    /* A path or absolute URL, e.g. "/blog", "/projects/verdict", or "https://github.com/BaryoDev". */
    public string Url { get; set; } = string.Empty;

    public bool OpenInNewTab { get; set; }

    /* One level of nesting (a dropdown). Children's own children are ignored on write. */
    public List<MenuItem> Children { get; set; } = new();
}
