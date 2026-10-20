using barakoCMS.Infrastructure.Multitenancy;
using FastEndpoints;
using FluentValidation;

namespace barakoCMS.Features.Accounts;

internal sealed class AccountWriteRequest
{
    /// <summary>The account's identifier. Read on create, ignored on update, where the route names it.</summary>
    public string Slug { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    /// <summary>Absolute http or https URL. Null or empty clears it.</summary>
    public string? LogoUrl { get; set; }

    /// <summary>Console colours by name. Null leaves them as they are on an update.</summary>
    public Dictionary<string, string>? Colours { get; set; }
}

internal sealed class AccountWriteRequestValidator : Validator<AccountWriteRequest>
{
    public const int MaxColours = 20;

    public AccountWriteRequestValidator()
    {
        RuleFor(r => r.Name).NotEmpty().MaximumLength(100);
        RuleFor(r => r.LogoUrl)
            .Must(TenantHandles.IsValidAbsoluteUrl)
            .When(r => !string.IsNullOrWhiteSpace(r.LogoUrl))
            .WithMessage("logoUrl is an absolute http or https URL.");
        RuleFor(r => r.Colours!.Count).LessThanOrEqualTo(MaxColours)
            .When(r => r.Colours is not null)
            .WithMessage($"An account has at most {MaxColours} colours.");
        RuleForEach(r => r.Colours!)
            .Must(c => !string.IsNullOrWhiteSpace(c.Key) && c.Key.Length <= 40
                       && !string.IsNullOrWhiteSpace(c.Value) && c.Value.Length <= 40)
            .When(r => r.Colours is not null)
            .WithMessage("A colour has a name and a value, each up to 40 characters.");
    }
}
