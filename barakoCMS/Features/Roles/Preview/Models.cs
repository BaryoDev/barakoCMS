namespace barakoCMS.Features.Roles.Preview;

internal class Request
{
    /// <summary>The user whose view is previewed. Must belong to the current tenant.</summary>
    public Guid UserId { get; set; }

    public string ContentType { get; set; } = string.Empty;

    /// <summary>The draft rules, in the shape the role save takes. Nothing is stored.</summary>
    public List<barakoCMS.Models.ContentTypePermission> Permissions { get; set; } = new();

    /// <summary>
    /// The stored role this draft edits. Optional. The draft then takes the role's id, which is how a
    /// field lists a role in <c>visibleToRoles</c>, and its name when <c>name</c> is blank. A role
    /// outside the current tenant, or none, answers 404.
    /// </summary>
    public Guid? RoleId { get; set; }

    /// <summary>
    /// The capabilities the role will hold, in the shape the role save takes. Optional. They decide
    /// whether the user sees Sensitive and Hidden fields (<c>view_sensitive</c>, <c>view_hidden</c>).
    /// Nothing is stored.
    /// </summary>
    public List<string>? SystemCapabilities { get; set; }

    /// <summary>
    /// The name the role will be saved under. Optional. A field that lists roles by name in
    /// <c>visibleToRoles</c> matches it. When blank, the draft gets a name nothing can match.
    /// </summary>
    public string? Name { get; set; }

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
    /// Why the user would not see the entry: <c>outside branch scope</c>, <c>no read rule</c>,
    /// <c>condition not met</c> or <c>field rules hide it</c>. Null when visible.
    /// </summary>
    public string? Reason { get; set; }

    /// <summary>
    /// The condition the read rule failed on, as the JSON object the rule holds it in. Set with
    /// <c>condition not met</c>.
    /// </summary>
    public string? Condition { get; set; }

    /// <summary>
    /// The fields the user could read on a visible entry, after the rules' set and the sensitivity
    /// of each field. Null when the rules name no set and no field is masked or hidden, which means
    /// every field.
    /// </summary>
    public List<string>? ReadableFields { get; set; }
}
