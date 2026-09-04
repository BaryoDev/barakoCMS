using FastEndpoints;
using barakoCMS.Infrastructure.Auth;
using Marten;
using barakoCMS.Models;

namespace barakoCMS.Features.Roles.Update;

internal class Endpoint : Endpoint<Request, Response>
{
    private readonly IDocumentSession _session;
    private readonly barakoCMS.Infrastructure.Services.IPermissionResolver _permissionResolver;
    private readonly CapabilityVocabulary _vocabulary;
    private readonly IConfiguration _configuration;
    private readonly ILogger<Endpoint> _logger;

    public Endpoint(
        IDocumentSession session,
        barakoCMS.Infrastructure.Services.IPermissionResolver permissionResolver,
        CapabilityVocabulary vocabulary,
        IConfiguration configuration,
        ILogger<Endpoint> logger)
    {
        _session = session;
        _permissionResolver = permissionResolver;
        _vocabulary = vocabulary;
        _configuration = configuration;
        _logger = logger;
    }

    public override void Configure()
    {
        Put("/api/roles/{id}");
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

        var role = await _session.LoadAsync<Role>(req.Id, ct);

        if (role == null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        // Update role properties
        role.Name = req.Name;
        role.Description = req.Description;
        role.Permissions = req.Permissions;
        role.SystemCapabilities = req.SystemCapabilities;

        _session.Store(role);
        await _session.SaveChangesAsync(ct);

        // Permissions changed, so evict cached decisions and the new rules take effect immediately.
        _permissionResolver.InvalidateAllPermissions();

        if (unknown.Count > 0)
        {
            _logger.LogWarning(
                "Role {RoleName} ({RoleId}) holds capabilities this instance does not know: {UnknownCapabilities}",
                role.Name, role.Id, string.Join(", ", unknown));
        }

        await Send.OkAsync(new Response
        {
            Message = "Role updated successfully",
            UnknownCapabilities = unknown.ToList(),
        }, ct);
    }
}
