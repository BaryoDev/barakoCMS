using System.Reflection;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using barakoCMS.Modules;
using FastEndpoints;

namespace barakoCMS.Features.Monitoring.Meta;

// Authenticated on purpose, and deliberately not role-restricted. Handing an exact CMS version to
// anonymous callers is free CVE matching, but every signed-in backoffice user needs to be able to
// answer "what am I running" when something behaves unexpectedly.
internal class Endpoint(
    IConfiguration configuration,
    ModuleCatalogue catalogue,
    IPermissionResolver permissionResolver) : EndpointWithoutRequest<MetaResponse>
{
    public override void Configure()
    {
        Get("/api/meta");
        Description(b => b
            .Produces<MetaResponse>(200)
            .Produces(401)
            .WithTags("Monitoring"));
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var mayListModules = Guid.TryParse(User.FindFirst("UserId")?.Value, out var userId)
            && await permissionResolver.HasCapabilityAsync(userId, SystemCapabilities.ViewModules, ct);

        await Send.OkAsync(
            new MetaResponse
            {
                Version = ReadVersion(),
                ApiContractVersion = ApiContract.Version,
                DeliveryContractVersion = ApiContract.DeliveryVersion,
                ModuleContractVersions = mayListModules ? EnabledModules() : null,
                SwaggerEnabled = configuration.GetValue(
                    "Swagger:Enabled",
                    Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") == "Development"),
            },
            ct);
    }

    // Enabled only. A module the enabled list left off serves no endpoints, so it has no surface
    // to version, and GET /api/modules is where an operator reads that it is installed.
    private ModuleContractVersion[] EnabledModules() =>
        catalogue.Entries
            .Where(m => m.Enabled)
            .OrderBy(m => m.Name, StringComparer.Ordinal)
            .Select(m => new ModuleContractVersion(m.Name, m.HttpContractVersion))
            .ToArray();

    // InformationalVersion carries the full <Version> string; AssemblyVersion would flatten
    // 3.21.0 to 3.21.0.0 and drop any prerelease suffix. The build appends "+<commit sha>" when
    // SourceLink is active, which is not useful here.
    private static string ReadVersion()
    {
        var informational = typeof(Endpoint).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        if (string.IsNullOrWhiteSpace(informational))
        {
            return typeof(Endpoint).Assembly.GetName().Version?.ToString() ?? "unknown";
        }

        var plus = informational.IndexOf('+');
        return plus < 0 ? informational : informational[..plus];
    }
}
