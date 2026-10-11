namespace barakoCMS.Features.EmailTemplates.Preview;

internal sealed class Request
{
    /// <summary>The template's id or slug, from the route.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>The entry the placeholders resolve against.</summary>
    public Guid EntryId { get; set; }
}

internal sealed class Response
{
    /// <summary>The subject as a send would write it.</summary>
    public string Subject { get; init; } = string.Empty;

    /// <summary>The HTML body, in its layout, as a send would write it.</summary>
    public string Html { get; init; } = string.Empty;

    /// <summary>The template's status. Only a published template is sent.</summary>
    public string Status { get; init; } = string.Empty;

    /// <summary>Whether a workflow naming this template would send it now.</summary>
    public bool Sendable { get; init; }

    /// <summary>Why it would not be sent or could not be rendered, or null.</summary>
    public string? Problem { get; init; }

    /// <summary>The placeholder warnings a workflow save gives for inline text, per field of the template.</summary>
    public List<PreviewWarning> Warnings { get; init; } = [];

    /// <summary>What the resolve left out on purpose, such as a loop stopped at its cap.</summary>
    public List<string> Notes { get; init; } = [];
}

internal sealed record PreviewWarning(string Field, string Message);
