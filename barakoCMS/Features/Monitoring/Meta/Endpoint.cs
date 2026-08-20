using System.Reflection;
using FastEndpoints;

namespace barakoCMS.Features.Monitoring.Meta;

// Authenticated on purpose, and deliberately not role-restricted. Handing an exact CMS version to
// anonymous callers is free CVE matching, but every signed-in backoffice user needs to be able to
// answer "what am I running" when something behaves unexpectedly.
public class Endpoint : EndpointWithoutRequest<MetaResponse>
{
    private readonly IConfiguration _configuration;

    public Endpoint(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public override void Configure()
    {
        Get("/api/meta");
        Description(b => b
            .Produces<MetaResponse>(200)
            .Produces(401)
            .WithTags("Monitoring"));
    }

    public override Task HandleAsync(CancellationToken ct)
    {
        return SendOkAsync(
            new MetaResponse
            {
                Version = ReadVersion(),
                SwaggerEnabled = _configuration.GetValue(
                    "Swagger:Enabled",
                    Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") == "Development"),
            },
            ct);
    }

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
