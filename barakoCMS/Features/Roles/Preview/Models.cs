namespace barakoCMS.Features.Roles.Preview;

internal class Request
{
    /// <summary>The user whose view is previewed. Must belong to the current tenant.</summary>
    public Guid UserId { get; set; }

    public string ContentType { get; set; } = string.Empty;

    /// <summary>The draft rules, in the shape the role save takes. Nothing is stored.</summary>
    public List<barakoCMS.Models.ContentTypePermission> Permissions { get; set; } = new();

    /// <summary>How many entries to return. 20 when absent, 50 at most.</summary>
    public int? Take { get; set; }
}

internal class Response
{
    public List<Entry> Items { get; set; } = new();
}

internal class Entry
{
    public Guid Id { get; set; }

    public string Title { get; set; } = string.Empty;

    public bool Visible { get; set; }

    /// <summary>
    /// Why the user would not see the entry: <c>no read rule</c>, <c>condition not met</c> or
    /// <c>field rules hide it</c>. Null when visible.
    /// </summary>
    public string? Reason { get; set; }

    /// <summary>
    /// The condition the read rule failed on, as the JSON object the rule holds it in. Set with
    /// <c>condition not met</c>.
    /// </summary>
    public string? Condition { get; set; }

    /// <summary>
    /// The fields the user could read on a visible entry. Null when the rules name no set, which
    /// means every field.
    /// </summary>
    public List<string>? ReadableFields { get; set; }
}
