using FastEndpoints;
using barakoCMS.Infrastructure.Auth;
using Marten;
using barakoCMS.Models;

namespace barakoCMS.Features.Roles.Create;

internal class Endpoint : Endpoint<Request, Response>
{
    private readonly IDocumentSession _session;
    private readonly CapabilityVocabulary _vocabulary;
    private readonly IConfiguration _configuration;
    private readonly ILogger<Endpoint> _logger;

    public Endpoint(
        IDocumentSession session,
        CapabilityVocabulary vocabulary,
        IConfiguration configuration,
        ILogger<Endpoint> logger)
    {
        _session = session;
        _vocabulary = vocabulary;
        _configuration = configuration;
        _logger = logger;
    }

    public override void Configure()
    {
        Post("/api/roles");
        Definition.RequireCapability(SystemCapabilities.ManageRoles, "SuperAdmin");
    }

    public override async Task HandleAsync(Request req, CancellationToken ct)
    {
        var unknown = _vocabulary.Unknown(req.SystemCapabilities);
        if (_configuration.GetValue(CapabilityVocabulary.RefuseUnknownKey, false))
        {
            foreach (var name in unknown)
                AddError(r => r.SystemCapabilities, CapabilityVocabulary.UnknownMessage(name));
        }
        ThrowIfAnyErrors();

        var role = new Role
        {
            Id = Guid.NewGuid(),
            Name = req.Name,
            Description = req.Description,
            Permissions = req.Permissions,
            SystemCapabilities = req.SystemCapabilities,
            CreatedAt = DateTime.UtcNow
        };

        _session.Store(role);
        await _session.SaveChangesAsync(ct);

        if (unknown.Count > 0)
        {
            _logger.LogWarning(
                "Role {RoleName} ({RoleId}) holds capabilities this instance does not know: {UnknownCapabilities}",
                role.Name, role.Id, string.Join(", ", unknown));
        }

        await Send.OkAsync(new Response
        {
            Id = role.Id,
            Message = "Role created successfully",
            UnknownCapabilities = unknown.ToList(),
        }, ct);
    }
}
