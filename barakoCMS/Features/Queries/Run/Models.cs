namespace barakoCMS.Features.Queries.Run;

internal sealed class Request
{
    public string Slug { get; set; } = string.Empty;

    /// <summary>First day of the range, yyyy-MM-dd. Replaces the saved range's start.</summary>
    public string? From { get; set; }

    /// <summary>Last day of the range, yyyy-MM-dd. Replaces the saved range's end.</summary>
    public string? To { get; set; }
}

internal sealed class Response
{
    public bool Ok { get; init; }
    public string? Refusal { get; init; }

    /// <summary>The number of groups.</summary>
    public int Count { get; init; }

    public IReadOnlyList<Dictionary<string, object?>> Rows { get; init; } = [];

    /// <summary>Every measure over the whole set, ignoring the grouping.</summary>
    public Dictionary<string, object?> Totals { get; init; } = new();
}
