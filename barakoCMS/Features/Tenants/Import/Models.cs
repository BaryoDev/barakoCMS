using System.Text.Json;
using barakoCMS.Features.Config;
using FastEndpoints;
using FluentValidation;

namespace barakoCMS.Features.Tenants.Import;

internal sealed class Request
{
    /// <summary>The handle of the new tenant. It must not exist.</summary>
    public string Handle { get; set; } = string.Empty;

    /// <summary>The new tenant's name. Left out, the bundle's is used.</summary>
    public string? Name { get; set; }

    /// <summary>A bundle, as <c>GET /api/tenants/{handle}/export</c> returns one.</summary>
    public JsonElement Bundle { get; set; }
}

internal sealed class Response
{
    /// <summary>The new tenant, or null when the import was refused and none was made.</summary>
    public TenantResponse? Tenant { get; init; }

    /// <summary>What was applied to the new tenant, section by section.</summary>
    public ConfigPlanResponse Config { get; init; } = null!;
}

internal sealed class Validator : Validator<Request>
{
    public Validator()
    {
        RuleFor(r => r.Handle).Custom((handle, context) =>
        {
            if (!barakoCMS.Infrastructure.Multitenancy.TenantHandles.IsValidHandle(handle?.Trim().ToLowerInvariant() ?? string.Empty))
            {
                context.AddFailure("handle", "Invalid or reserved handle (3-40 chars, a-z, 0-9, hyphens).");
            }
        });

        RuleFor(r => r.Name).MaximumLength(200);

        RuleFor(r => r.Bundle).Custom((bundle, context) =>
        {
            var max = TenantBundle.MaxImportEntries(Resolve<IConfiguration>());
            var errors = new List<string>();
            TenantBundle.ToConfigDocument(bundle, max, errors);
            foreach (var error in errors)
            {
                context.AddFailure("bundle", error);
            }
        });
    }
}
