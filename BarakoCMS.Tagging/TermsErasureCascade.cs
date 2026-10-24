using barakoCMS.Core.Interfaces;
using Marten;

namespace BarakoCMS.Tagging;

/// <summary>Removes the terms on a content entry when the entry is erased.</summary>
internal sealed class TermsErasureCascade : IContentErasureCascade
{
    public Task QueueAsync(IDocumentSession session, Guid contentId, string contentType, CancellationToken ct)
    {
        var type = contentType.ToLowerInvariant();
        var record = contentId.ToString();
        session.DeleteWhere<TermAssignment>(a => a.ContentType == type && a.RecordId == record);
        return Task.CompletedTask;
    }
}
