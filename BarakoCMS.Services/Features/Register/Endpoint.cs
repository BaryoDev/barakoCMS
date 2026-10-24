using System.Text.RegularExpressions;
using barakoCMS.Infrastructure.Audit;
using barakoCMS.Infrastructure.Auth;
using barakoCMS.Infrastructure.Multitenancy;
using FastEndpoints;
using FluentValidation;
using Marten;

namespace BarakoCMS.Services.Features.Register;

internal sealed class RegisterServiceRequest
{
    public string Name { get; set; } = string.Empty;
    public List<string>? MessageTypes { get; set; }
    public string? DeliveryUrl { get; set; }
    public string? Topic { get; set; }
    public string? HealthUrl { get; set; }
    public Guid? MachineKeyId { get; set; }
    public int? HeartbeatIntervalSeconds { get; set; }
}

internal sealed partial class RegisterServiceValidator : Validator<RegisterServiceRequest>
{
    public const int MaxMessageTypes = 50;

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,63}$")]
    private static partial Regex NamePattern();

    public RegisterServiceValidator()
    {
        RuleFor(r => r.Name)
            .Must(n => NamePattern().IsMatch(ServiceAdmin.Normalize(n)))
            .WithMessage("Name is 1 to 64 letters, digits, dots, dashes or underscores, starting with a letter or digit.");

        RuleFor(r => r.MessageTypes)
            .Must(t => t is { Count: > 0 and <= MaxMessageTypes } && t.All(x => !string.IsNullOrWhiteSpace(x) && x.Trim().Length <= 100))
            .WithMessage($"Give between 1 and {MaxMessageTypes} message types, each at most 100 characters.");

        RuleFor(r => r.DeliveryUrl).Must(IsHttpUrl!).When(r => !string.IsNullOrWhiteSpace(r.DeliveryUrl))
            .WithMessage("DeliveryUrl must be an absolute http or https URL without credentials.");
        RuleFor(r => r.HealthUrl).Must(IsHttpUrl!).When(r => !string.IsNullOrWhiteSpace(r.HealthUrl))
            .WithMessage("HealthUrl must be an absolute http or https URL without credentials.");
        RuleFor(r => r.Topic).MaximumLength(200);

        RuleFor(r => r)
            .Must(r => !string.IsNullOrWhiteSpace(r.DeliveryUrl) || !string.IsNullOrWhiteSpace(r.Topic))
            .WithName("DeliveryUrl")
            .WithMessage("Give a DeliveryUrl or a Topic, so there is somewhere to send messages.");

        RuleFor(r => r.HeartbeatIntervalSeconds)
            .Must(v => v is null || (v >= ServiceHealth.MinIntervalSeconds && v <= ServiceHealth.MaxIntervalSeconds))
            .WithMessage($"HeartbeatIntervalSeconds is between {ServiceHealth.MinIntervalSeconds} and {ServiceHealth.MaxIntervalSeconds}.");
    }

    internal static bool IsHttpUrl(string value) =>
        value.Length <= 2048
        && Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)
        && uri.Scheme is "http" or "https"
        && string.IsNullOrEmpty(uri.UserInfo);
}

/// <summary>
/// POST /api/services: registers an outside service. A name already registered is a 409. A machine
/// key is checked as an API key from any tenant, because the registry is deployment-wide and the
/// caller is a global administrator.
/// </summary>
internal sealed class RegisterServiceEndpoint(IDocumentSession session, TenantContext tenant, IConfiguration configuration)
    : Endpoint<RegisterServiceRequest, ServiceDto>
{
    public override void Configure()
    {
        Post("/api/services");
        Definition.RequireCapability(
            ServicesCapabilities.ManageServices, ServicesCapabilities.Defaults.LegacyRoles);
    }

    public override async Task HandleAsync(RegisterServiceRequest req, CancellationToken ct)
    {
        if (!await ServiceAdmin.AllowedAsync(session, User, configuration, ct))
        {
            ThrowError(PlatformScope.DeploymentWideMessage, 403);
        }

        if (req.MachineKeyId is { } keyId
            && !IsUsableMachineKey(await session.LoadAsync<barakoCMS.Models.ApiKey>(keyId, ct)))
        {
            AddError(r => r.MachineKeyId!,
                "MachineKeyId must be an existing API key that is not revoked or expired, has the services:heartbeat scope and is not limited to content types.");
            await Send.ErrorsAsync(400, ct);
            return;
        }

        var name = ServiceAdmin.Normalize(req.Name);
        if (await session.Query<ServiceRegistration>().AnyAsync(s => s.Name == name, ct))
        {
            AddError(r => r.Name, $"A service called '{name}' is already registered.");
            await Send.ErrorsAsync(409, ct);
            return;
        }

        var now = DateTime.UtcNow;
        var service = new ServiceRegistration
        {
            Name = name,
            MessageTypes = req.MessageTypes!.Select(t => t.Trim()).Distinct(StringComparer.Ordinal).ToList(),
            DeliveryUrl = Clean(req.DeliveryUrl),
            Topic = Clean(req.Topic),
            HealthUrl = Clean(req.HealthUrl),
            MachineKeyId = req.MachineKeyId,
            HeartbeatIntervalSeconds = req.HeartbeatIntervalSeconds ?? ServiceHealth.DefaultIntervalSeconds,
            CreatedAt = now,
            UpdatedAt = now,
        };
        session.Store(service);

        Guid.TryParse(User.FindFirst("UserId")?.Value, out var actorId);
        await AuditLog.RecordAsync(session, tenant.Slug, "service.registered",
            actorId == Guid.Empty ? null : actorId, User.FindFirst("Username")?.Value,
            targetType: "ServiceRegistration", targetId: service.Name,
            metadata: new Dictionary<string, object> { ["messageTypes"] = service.MessageTypes },
            ct: ct);
        try
        {
            await session.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (IsUniqueViolation(ex))
        {
            AddError(r => r.Name, $"A service called '{name}' is already registered.");
            await Send.ErrorsAsync(409, ct);
            return;
        }

        await Send.ResponseAsync(ServiceDto.From(service, now), 201, ct);
    }

    private static bool IsUsableMachineKey(barakoCMS.Models.ApiKey? key) =>
        key is { Revoked: false }
        && (key.ExpiresAt is null || key.ExpiresAt > DateTime.UtcNow)
        && key.Scopes.Contains(barakoCMS.Models.ApiKeyScopes.ServicesHeartbeat, StringComparer.OrdinalIgnoreCase)
        && key.ContentTypes.Count == 0;

    private static bool IsUniqueViolation(Exception? ex)
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

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
