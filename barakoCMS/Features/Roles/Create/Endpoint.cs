using FastEndpoints;
using barakoCMS.Infrastructure.Auth;
using Marten;
using barakoCMS.Infrastructure.Audit;
using barakoCMS.Models;

namespace barakoCMS.Features.Roles.Create;

internal class Endpoint(
    IDocumentSession session,
    CapabilityVocabulary vocabulary,
    IConfiguration configuration,
    ILogger<Endpoint> logger,
    barakoCMS.Infrastructure.Multitenancy.TenantContext tenant) : Endpoint<Request, Response>
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

        foreach (var error in await ReferenceConditionRules.CheckAsync(session, req.Permissions, stored: null, ct))
            AddError(r => r.Permissions, error);

        FieldSetRules.Normalise(req.Permissions);
        foreach (var error in await FieldSetRules.CheckAsync(session, req.Permissions, stored: null, ct))
            AddError(r => r.Permissions, error);

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
        Guid.TryParse(User.FindFirst("UserId")?.Value, out var actorId);
        await AuditLog.RecordAsync(session, tenant.Slug, "role.created", actorId, User.FindFirst("Username")?.Value,
            targetType: "Role", targetId: role.Id.ToString(),
            metadata: RoleAudit.Describe(RoleAudit.Of(role)), ct: ct);
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
