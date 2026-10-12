using barakoCMS.Infrastructure.Auth;
using barakoCMS.Models;
using FastEndpoints;
using FluentValidation;
using Marten;
using Microsoft.Extensions.Options;

namespace BarakoCMS.Pages.Features.Settings;

/// <summary>What a tenant sets. A value left null takes the deployment's.</summary>
internal sealed class SettingsRequest
{
    public string? ContentType { get; set; }
    public string? ParentField { get; set; }
    public string? ShowInNavigationField { get; set; }
    public string? OrderField { get; set; }
    public string? TitleField { get; set; }
    public int? MaxDepth { get; set; }
    public List<string>? ReservedSlugs { get; set; }
    public string? HomeSlug { get; set; }
}

/// <param name="Contract">The module's HTTP contract, as on every Pages body.</param>
/// <param name="Tenant">What the tenant set, each null where it takes the deployment's.</param>
/// <param name="Effective">What the tree endpoints and the write rules read for this tenant.</param>
internal sealed record SettingsResponse(int Contract, SettingsRequest Tenant, SettingsRequest Effective);

internal sealed class SettingsValidator : Validator<SettingsRequest>
{
    public const int MaxNameLength = 100;
    public const int MaxReservedSlugs = 100;

    private const string NameMessage = "A type or field name may not be empty and is at most 100 characters.";

    private static bool IsName(string? value) => value is null || (value.Trim().Length > 0 && value.Length <= MaxNameLength);

    public SettingsValidator()
    {
        RuleFor(r => r.ContentType).Must(IsName).WithMessage(NameMessage);
        RuleFor(r => r.ParentField).Must(IsName).WithMessage(NameMessage);
        RuleFor(r => r.ShowInNavigationField).Must(IsName).WithMessage(NameMessage);
        RuleFor(r => r.OrderField).Must(IsName).WithMessage(NameMessage);
        RuleFor(r => r.TitleField).Must(IsName).WithMessage(NameMessage);
        RuleFor(r => r.MaxDepth).InclusiveBetween(1, ParentDeclaration.MostDepth).When(r => r.MaxDepth is not null);
        RuleFor(r => r.ReservedSlugs).Must(s => s is null || (s.Count <= MaxReservedSlugs
                && s.All(x => !string.IsNullOrWhiteSpace(x) && x.Length <= MaxNameLength)))
            .WithMessage($"At most {MaxReservedSlugs} reserved slugs, each 1 to {MaxNameLength} characters.");
        RuleFor(r => r.HomeSlug).MaximumLength(MaxNameLength);
    }
}

/// <summary>GET /api/pages/settings: the tenant's page tree settings, and what they come to.</summary>
internal sealed class GetEndpoint(IQuerySession session, IOptions<PagesOptions> deployment)
    : EndpointWithoutRequest<SettingsResponse>
{
    public override void Configure()
    {
        Get("/api/pages/settings");
        Definition.RequireCapability(SystemCapabilities.ManageContentTypes, "SuperAdmin", "Admin");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var row = await session.LoadAsync<PagesSettings>(PagesSettings.RowId, ct) ?? new PagesSettings();
        await Send.OkAsync(SettingsMapping.Response(row, deployment.Value), ct);
    }
}

/// <summary>PUT /api/pages/settings: replaces the tenant's page tree settings.</summary>
/// <remarks>
/// A replace, not a merge: a value left out goes back to the deployment's. Sending every value null
/// leaves the tenant reading <c>Modules:Pages</c> alone, as a tenant that never set any does.
/// </remarks>
internal sealed class PutEndpoint(IDocumentSession session, IOptions<PagesOptions> deployment)
    : Endpoint<SettingsRequest, SettingsResponse>
{
    public override void Configure()
    {
        Put("/api/pages/settings");
        Definition.RequireCapability(SystemCapabilities.ManageContentTypes, "SuperAdmin", "Admin");
    }

    public override async Task HandleAsync(SettingsRequest req, CancellationToken ct)
    {
        await RefuseNamesTheTypeLacksAsync(req, ct);

        var row = new PagesSettings
        {
            ContentType = req.ContentType?.Trim(),
            ParentField = req.ParentField?.Trim(),
            ShowInNavigationField = req.ShowInNavigationField?.Trim(),
            OrderField = req.OrderField?.Trim(),
            TitleField = req.TitleField?.Trim(),
            MaxDepth = req.MaxDepth,
            ReservedSlugs = req.ReservedSlugs?.Select(s => s.Trim()).ToList(),
            HomeSlug = req.HomeSlug?.Trim(),
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        session.Store(row);
        await session.SaveChangesAsync(ct);
        await Send.OkAsync(SettingsMapping.Response(row, deployment.Value), ct);
    }

    /// <summary>
    /// Refuses a type that does not exist, and a field the type lacks, for the names this request
    /// sets. A misspelt type would otherwise leave the real one with no tree rules at all.
    /// </summary>
    /// <remarks>
    /// Only what the request names is checked. A value left to the deployment is the operator's,
    /// and a tenant that never made the deployment's type must still be able to set a type of its
    /// own.
    /// </remarks>
    private async Task RefuseNamesTheTypeLacksAsync(SettingsRequest req, CancellationToken ct)
    {
        var fields = new[] { req.ParentField, req.ShowInNavigationField, req.OrderField, req.TitleField }
            .Where(f => f is not null).Select(f => f!.Trim()).ToList();
        if (req.ContentType is null && fields.Count == 0)
        {
            return;
        }

        var typeName = (req.ContentType ?? deployment.Value.ContentType).Trim();
        var lowered = typeName.ToLower();
        var definition = await session.Query<ContentTypeDefinition>().FirstOrDefaultAsync(d => d.Name.ToLower() == lowered, ct);
        if (definition is null)
        {
            AddError(r => r.ContentType!, $"There is no content type '{typeName}'.");
            ThrowIfAnyErrors();
        }

        foreach (var field in fields.Where(f => !definition!.Fields.Any(d => string.Equals(d.Name, f, StringComparison.OrdinalIgnoreCase))))
        {
            AddError($"'{definition!.Name}' has no field '{field}'.");
        }

        ThrowIfAnyErrors();
    }
}

internal static class SettingsMapping
{
    public static SettingsResponse Response(PagesSettings row, PagesOptions deployment)
    {
        var effective = row.Over(deployment);
        return new SettingsResponse(PagesContract.Version, Of(row), new SettingsRequest
        {
            ContentType = effective.ContentType,
            ParentField = effective.ParentField,
            ShowInNavigationField = effective.ShowInNavigationField,
            OrderField = effective.OrderField,
            TitleField = effective.TitleField,
            MaxDepth = effective.MaxDepth,
            ReservedSlugs = effective.ReservedSlugs,
            HomeSlug = effective.HomeSlug,
        });
    }

    private static SettingsRequest Of(PagesSettings row) => new()
    {
        ContentType = row.ContentType,
        ParentField = row.ParentField,
        ShowInNavigationField = row.ShowInNavigationField,
        OrderField = row.OrderField,
        TitleField = row.TitleField,
        MaxDepth = row.MaxDepth,
        ReservedSlugs = row.ReservedSlugs,
        HomeSlug = row.HomeSlug,
    };
}
