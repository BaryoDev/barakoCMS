using System.Security.Claims;
using barakoCMS.Infrastructure.Services;
using FluentValidation;
using Marten;

namespace BarakoCMS.Contacts;

/// <summary>What a create or an update carries. One shape, so the two cannot drift apart.</summary>
internal interface IContactInput
{
    string? Kind { get; }
    string? DisplayName { get; }
    string? GivenName { get; }
    string? FamilyName { get; }
    string? Website { get; }
    string? Email { get; }
    string? Phone { get; }
    string? Address { get; }
    Dictionary<string, object>? Fields { get; }
}

/// <summary>A contact as an authorised caller reads it. Includes the personal fields.</summary>
internal sealed class ContactResponse
{
    public Guid Id { get; set; }
    public string Kind { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? GivenName { get; set; }
    public string? FamilyName { get; set; }
    public string? Website { get; set; }
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public string? Address { get; set; }
    public Dictionary<string, object> Fields { get; set; } = new();
    public bool IsPublished { get; set; }
    public Guid? MergedInto { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

internal static class ContactSupport
{
    public const int MaxText = 200;
    public const int MaxEmail = 254;

    /// <summary>The key two contacts must not share: the address trimmed and lower cased.</summary>
    public static string? EmailKey(string? email) =>
        string.IsNullOrWhiteSpace(email) ? null : email.Trim().ToLowerInvariant();

    public static bool TryKind(string? text, out ContactKind kind) =>
        Enum.TryParse(text, ignoreCase: true, out kind) && Enum.IsDefined(kind);

    public static string? Clean(string? text) =>
        string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    public static Guid CallerId(ClaimsPrincipal user) =>
        Guid.TryParse(user.FindFirst("UserId")?.Value, out var id) ? id : Guid.Empty;

    public static void Apply(Contact contact, IContactInput input)
    {
        if (TryKind(input.Kind, out var kind))
        {
            contact.Kind = kind;
        }

        contact.DisplayName = input.DisplayName!.Trim();
        contact.GivenName = Clean(input.GivenName);
        contact.FamilyName = Clean(input.FamilyName);
        contact.Website = Clean(input.Website);
        contact.Email = Clean(input.Email);
        contact.EmailKey = EmailKey(input.Email);
        contact.Phone = Clean(input.Phone);
        contact.Address = Clean(input.Address);
        contact.Fields = input.Fields is null ? new() : new(input.Fields);
        contact.UpdatedAt = DateTimeOffset.UtcNow;
    }

    public static ContactResponse ToResponse(Contact contact) => new()
    {
        Id = contact.Id,
        Kind = contact.Kind.ToString(),
        DisplayName = contact.DisplayName,
        GivenName = contact.GivenName,
        FamilyName = contact.FamilyName,
        Website = contact.Website,
        Email = contact.Email,
        Phone = contact.Phone,
        Address = contact.Address,
        Fields = contact.Fields,
        IsPublished = contact.IsPublished,
        MergedInto = contact.MergedInto,
        CreatedAt = contact.CreatedAt,
        UpdatedAt = contact.UpdatedAt,
    };

    /// <summary>A losing race on the document id or on the email index.</summary>
    public static bool IsUniqueViolation(Exception? ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (current is JasperFx.DocumentAlreadyExistsException or Npgsql.PostgresException { SqlState: "23505" })
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The messages the contact-record schema gives these values, empty when they pass.</summary>
    public static async Task<List<string>> SchemaErrorsAsync(
        IQuerySession session, IContentValidatorService validator, Contact contact,
        ClaimsPrincipal caller, CancellationToken ct)
    {
        var schema = await ContactRecord.SchemaAsync(session, ct);
        var (_, errors) = await validator.ValidateFieldsAsync(
            schema, ContactRecord.TypeName, ContactRecord.Bag(contact), existing: null, caller);
        return errors;
    }

    /// <summary>The shared request rules, so create and update refuse the same things.</summary>
    public static void AddRules<T>(AbstractValidator<T> validator) where T : IContactInput
    {
        validator.RuleFor(x => x.DisplayName).NotEmpty().MaximumLength(MaxText);
        validator.RuleFor(x => x.Kind).Must(k => TryKind(k, out _)).WithMessage("kind must be Person or Organization.");
        validator.RuleFor(x => x.GivenName).MaximumLength(MaxText);
        validator.RuleFor(x => x.FamilyName).MaximumLength(MaxText);
        validator.RuleFor(x => x.Website).MaximumLength(2048);
        validator.RuleFor(x => x.Phone).MaximumLength(64);
        validator.RuleFor(x => x.Address).MaximumLength(1000);
        validator.RuleFor(x => x.Email).MaximumLength(MaxEmail).EmailAddress().When(x => !string.IsNullOrWhiteSpace(x.Email));
        validator.RuleFor(x => x.Fields)
            .Must(f => f is null || f.Keys.All(k => !ContactRecord.IsStandardName(k)))
            .WithMessage("fields holds custom fields only. Send displayName, email and the other standard fields at the top level.");
    }
}
