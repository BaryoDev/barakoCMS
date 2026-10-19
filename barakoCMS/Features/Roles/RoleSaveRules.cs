using Marten;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;

namespace barakoCMS.Features.Roles;

/// <summary>The database checks a role save runs on its rules: field sets, then public delivery.</summary>
internal static class RoleSaveRules
{
    public static async Task<List<string>> CheckAsync(
        IQuerySession session, List<ContentTypePermission>? permissions, List<ContentTypePermission>? stored, CancellationToken ct)
    {
        var errors = await FieldSetRules.CheckAsync(session, permissions, stored, ct);
        errors.AddRange(await PublicDeliveryAudience.RoleErrorsAsync(session, permissions, stored, ct));
        return errors;
    }
}
