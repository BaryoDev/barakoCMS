using FastEndpoints;
using Marten;
using barakoCMS.Models;

namespace barakoCMS.Features.Roles.Delete;

public class Endpoint : Endpoint<Request, Response>
{
    private readonly IDocumentSession _session;

    public Endpoint(IDocumentSession session)
    {
        _session = session;
    }

    public override void Configure()
    {
        Delete("/api/roles/{id}");
        Roles("SuperAdmin");
    }

    public override async Task HandleAsync(Request req, CancellationToken ct)
    {
        var role = await _session.LoadAsync<Role>(req.Id, ct);

        if (role == null)
        {
            await SendNotFoundAsync(ct);
            return;
        }

        // Prevent deletion of system roles
        var systemRoleIds = new[]
        {
            barakoCMS.Data.DataSeeder.SuperAdminRoleId,
            barakoCMS.Data.DataSeeder.AdminRoleId,
            barakoCMS.Data.DataSeeder.HRRoleId,
            barakoCMS.Data.DataSeeder.UserRoleId
        };

        if (systemRoleIds.Contains(req.Id))
        {
            await SendAsync(new Response
            {
                Message = "Cannot delete system roles (SuperAdmin, Admin, HR, User)."
            }, 403, ct);
            return;
        }

        // Check referential integrity - ensure no users have this role
        var usersWithRole = await _session.Query<User>()
            .AnyAsync(u => u.RoleIds.Contains(req.Id), ct);

        if (usersWithRole)
        {
            await SendAsync(new Response
            {
                Message = "Cannot delete role: it is still assigned to users. Remove the role from all users first."
            }, 409, ct);
            return;
        }

        _session.Delete(role);
        await _session.SaveChangesAsync(ct);

        await SendOkAsync(new Response
        {
            Message = "Role deleted successfully"
        }, ct);
    }
}
