using System.Security.Cryptography;
using System.Text;

namespace BarakoCMS.Tagging;

/// <summary>A named set of terms. The key is its slug and cannot change.</summary>
public sealed class Vocabulary
{
    /// <summary>Derived from the key, so a second vocabulary with the same key cannot be stored.</summary>
    public Guid Id { get; set; }

    public string Key { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>One term of a vocabulary, optionally under another term of the same vocabulary.</summary>
public sealed class Term
{
    /// <summary>Derived from the vocabulary and the key, so a key is unique within its vocabulary.</summary>
    public Guid Id { get; set; }

    public Guid VocabularyId { get; set; }

    public Guid? ParentId { get; set; }

    public string Key { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// A term on a record. The record is named by its content type and id, and the module never looks
/// the record up, so anything with an id can carry a term.
/// </summary>
public sealed class TermAssignment
{
    /// <summary>Derived from the term, the content type and the record id, so applying twice is one row.</summary>
    public Guid Id { get; set; }

    public Guid TermId { get; set; }

    public Guid VocabularyId { get; set; }

    public string ContentType { get; set; } = string.Empty;

    public string RecordId { get; set; } = string.Empty;

    public DateTimeOffset AssignedAt { get; set; }

    public string? AssignedBy { get; set; }
}

/// <summary>The ids the documents are stored under.</summary>
internal static class TaggingIds
{
    /// <summary>A record id as it is stored: trimmed, and a Guid in its canonical lowercase form.</summary>
    public static string RecordKey(string recordId)
    {
        var trimmed = recordId.Trim();
        return Guid.TryParse(trimmed, out var guid) ? guid.ToString("D") : trimmed;
    }

    /// <summary>A content type as it is stored: trimmed and lowercase.</summary>
    public static string TypeKey(string contentType) => contentType.Trim().ToLowerInvariant();

    public static Guid ForVocabulary(string key) => From($"vocabulary|{key}");

    public static Guid ForTerm(Guid vocabularyId, string key) => From($"term|{vocabularyId:N}|{key}");

    public static Guid ForAssignment(Guid termId, string contentType, string recordId) =>
        From($"assignment|{termId:N}|{contentType}|{recordId}");

    private static Guid From(string text) =>
        new(SHA256.HashData(Encoding.UTF8.GetBytes(text)).AsSpan(0, 16));
}
