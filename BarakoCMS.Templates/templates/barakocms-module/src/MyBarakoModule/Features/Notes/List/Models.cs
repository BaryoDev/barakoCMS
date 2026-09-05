using barakoCMS.Models;

namespace MyBarakoModule.Features.Notes.List;

/// <summary>Page and page size, capped by core so no caller can ask for the whole table.</summary>
internal sealed class Request : PaginatedRequest;

internal sealed record Response(
    string Greeting,
    IReadOnlyList<NoteSummary> Items,
    int Page,
    int PageSize,
    int TotalItems);

internal sealed record NoteSummary(Guid Id, string Title, DateTime CreatedAt);
