using barakoCMS.Models;
using Marten;
using Microsoft.Extensions.Configuration;

namespace barakoCMS.Infrastructure.Envelope;

/// <summary>The short code of a branch, for numbering that carries the branch in the series name.</summary>
internal interface IBranchCodes
{
    /// <summary>
    /// The code field of the branch entry with this id, or null when there is none. The content type
    /// and the field come from <c>Envelope:BranchContentType</c> and <c>Envelope:BranchCodeField</c>
    /// and default to <c>Branch</c> and <c>Code</c>.
    /// </summary>
    Task<string?> CodeOfAsync(Guid branchId, CancellationToken ct);
}

internal sealed class BranchCodes(IQuerySession session, IConfiguration config) : IBranchCodes
{
    public async Task<string?> CodeOfAsync(Guid branchId, CancellationToken ct)
    {
        var type = config["Envelope:BranchContentType"] is { Length: > 0 } t ? t : "Branch";
        var field = config["Envelope:BranchCodeField"] is { Length: > 0 } f ? f : "Code";
        var lowered = type.ToLower();
        var branch = await session.Query<Content>()
            .FirstOrDefaultAsync(c => c.Id == branchId && c.ContentType.ToLower() == lowered, ct);
        return branch is not null && branch.Data.TryGetValue(field, out var code) ? code?.ToString() : null;
    }
}
