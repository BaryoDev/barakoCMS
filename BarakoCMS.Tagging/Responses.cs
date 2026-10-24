using FluentValidation;

namespace BarakoCMS.Tagging;

internal sealed class VocabularyResponse
{
    public Guid Id { get; set; }
    public string Key { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public static VocabularyResponse From(Vocabulary v) => new()
    {
        Id = v.Id,
        Key = v.Key,
        Name = v.Name,
        Description = v.Description,
        CreatedAt = v.CreatedAt,
    };
}

internal sealed class TermResponse
{
    public Guid Id { get; set; }
    public Guid VocabularyId { get; set; }
    public Guid? ParentId { get; set; }
    public string Key { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }

    public static TermResponse From(Term t) => new()
    {
        Id = t.Id,
        VocabularyId = t.VocabularyId,
        ParentId = t.ParentId,
        Key = t.Key,
        Name = t.Name,
        CreatedAt = t.CreatedAt,
    };
}

internal sealed class AssignmentResponse
{
    public Guid TermId { get; set; }
    public Guid VocabularyId { get; set; }
    public string ContentType { get; set; } = string.Empty;
    public string RecordId { get; set; } = string.Empty;
    public DateTimeOffset AssignedAt { get; set; }
    public string? AssignedBy { get; set; }

    public static AssignmentResponse From(TermAssignment a) => new()
    {
        TermId = a.TermId,
        VocabularyId = a.VocabularyId,
        ContentType = a.ContentType,
        RecordId = a.RecordId,
        AssignedAt = a.AssignedAt,
        AssignedBy = a.AssignedBy,
    };
}

/// <summary>The limits and shapes the validators share.</summary>
internal static class TaggingRules
{
    public const int MaxKeyLength = 64;
    public const int MaxNameLength = 200;
    public const int MaxDescriptionLength = 1000;
    public const int MaxContentTypeLength = 100;
    public const int MaxRecordIdLength = 200;

    /// <summary>Levels of terms under a vocabulary, counting the top level as one.</summary>
    public const int MaxDepth = 8;

    /// <summary>Terms in one vocabulary, so walking its tree stays bounded.</summary>
    public const int MaxTermsPerVocabulary = 10_000;

    /// <summary>Terms on one record, so listing the terms of a record stays bounded.</summary>
    public const int MaxTermsPerRecord = 100;

    /// <summary>Lowercase letters, digits, hyphens and underscores, starting with a letter or digit.</summary>
    public static IRuleBuilderOptions<T, string> ValidKey<T>(this IRuleBuilder<T, string> rule) =>
        rule.NotEmpty().MaximumLength(MaxKeyLength).Matches("^[a-z0-9][a-z0-9_-]*$")
            .WithMessage("A key is lowercase letters, digits, hyphens and underscores, starting with a letter or digit.");

    public static IRuleBuilderOptions<T, string> ValidName<T>(this IRuleBuilder<T, string> rule) =>
        rule.NotEmpty().MaximumLength(MaxNameLength);

    public static IRuleBuilderOptions<T, string> ValidContentType<T>(this IRuleBuilder<T, string> rule) =>
        rule.NotEmpty().MaximumLength(MaxContentTypeLength);

    public static IRuleBuilderOptions<T, string> ValidRecordId<T>(this IRuleBuilder<T, string> rule) =>
        rule.NotEmpty().MaximumLength(MaxRecordIdLength);
}
