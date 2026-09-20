using FastEndpoints;
using barakoCMS.Infrastructure.Auth;
using Marten;
using barakoCMS.Models;

namespace barakoCMS.Features.Roles.Create;

internal class Endpoint(
    IDocumentSession session,
    CapabilityVocabulary vocabulary,
    IConfiguration configuration,
    ILogger<Endpoint> logger) : Endpoint<Request, Response>
{
    public override void Configure()
    {
        Post("/api/roles");
        Definition.RequireCapability(SystemCapabilities.ManageRoles, "SuperAdmin");
    }

    public override async Task HandleAsync(Request req, CancellationToken ct)
    {
        if (SystemRoles.IsReservedName(req.Name))
            AddError(r => r.Name, SystemRoles.ReservedNameMessage(req.Name));

        var unknown = vocabulary.Unknown(req.SystemCapabilities);
        if (configuration.GetValue(CapabilityVocabulary.RefuseUnknownKey, false))
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

        session.Store(role);
        await session.SaveChangesAsync(ct);

        if (unknown.Count > 0)
        {
            logger.LogWarning(
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
