using barakoCMS.Core;
using barakoCMS.Core.Interfaces;
using System.Security.Claims;
using barakoCMS.Features.Activity;
using barakoCMS.Infrastructure.Auth;
using barakoCMS.Infrastructure.Envelope;
using barakoCMS.Infrastructure.Multitenancy;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using FastEndpoints;
using Marten;

namespace barakoCMS.Features.Roles.Preview;

/// <summary>
/// POST /api/roles/preview, which entries a user would see under rules that are not saved yet.
/// </summary>
/// <remarks>
/// <para>
/// The draft is judged by a <see cref="PermissionResolver"/> made for the request and pinned to it,
/// so the rule logic is the one a saved role runs through. It is never the cached resolver: nothing
/// the draft decides may reach the shared cache. Nothing is stored. The user is judged as holding
/// the draft alone, so the answer is what the role would grant that user, not a merge with the
/// roles the user holds now.
/// </para>
/// <para>
/// The user's branch scope and field sensitivity are applied, by the services the real read uses,
/// judged for the previewed user and not the caller. An entry outside the user's branches is hidden
/// with <c>outside branch scope</c>. A field sensitivity masks or hides for the user, judged as
/// the draft alone, is left out of the readable fields. The draft holds the capabilities and the
/// name the request gives it, and none when it gives none. With a <c>roleId</c> the draft is an edit
/// of that stored role: it takes the role's id, which is how a field lists a role, and its name when
/// the request gives none, and the update's checks apply (a role the caller does not reach is 404,
/// a platform role needs a platform administrator, a seeded role may keep its reserved name, a
/// platform capability the role already holds is kept). A read condition is still judged on the
/// stored value, as the real read judges it: the permission check runs on the stored entry and the
/// mask is applied afterwards.
/// </para>
/// <para>
/// The caller only sees entries the caller may read, so the preview cannot be used to list what the
/// caller cannot open. Those are left out, not shown with a reason. A draft read condition on a
/// field the caller may not read is refused with 400, since which entries match would tell the
/// caller the field's value.
/// </para>
/// </remarks>
internal sealed class Endpoint(
    IDocumentSession session,
    IConditionEvaluator conditions,
    IPermissionResolver callerPermissions,
    IBranchScopeGuard branchGuard,
    IBranchMembership branchMembership,
    ISensitivityService sensitivity,
    IConfiguration configuration,
    CapabilityVocabulary vocabulary,
    TenantContext tenant,
    IEventEnvelopeContext envelope,
    ILoggerFactory loggers) : Endpoint<Request, Response>
{
    internal const int DefaultTake = 20;
    internal const int MaxTake = 50;

    // How many stored entries are looked at while filling a page, so a caller who can read little
    // cannot make one request scan a whole type.
    private const int ScanLimit = 500;
    private const int BatchSize = 100;

    public override void Configure()
    {
        Post("/api/roles/preview");
        Definition.RequireCapability(SystemCapabilities.ManageRoles, "SuperAdmin");
    }

    public override async Task HandleAsync(Request req, CancellationToken ct)
    {
        // The role being edited, found the way the save finds it: one the caller does not reach
        // answers as if it did not exist.
        var platformCaller = await RoleScope.IsPlatformCallerAsync(HttpContext, ct);
        var stored = await LoadStoredAsync(req, platformCaller, ct);

        await CheckDraftAsync(req, stored, platformCaller, ct);

        if (req.RoleId is not null && stored is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var previewed = await session.LoadAsync<User>(req.UserId, ct);
        if (previewed is null || !await InTenantAsync(previewed.Id, ct))
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var definition = await FindTypeAsync(req, ct);
        if (definition is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var caller = await ActingCaller.LoadAsync(session, User, ct);
        if (caller is null)
        {
            await Send.ForbiddenAsync(ct);
            return;
        }

        await RefuseUnreadableConditionAsync(req, definition, ct);

        // An edit of a stored role carries that role's id, which is how a field lists a role, and
        // its name when the request gives none. A new role gets a fresh id and, with no name, its
        // own id as text, which no stored role or field list can hold. The id is only read by this
        // request's own resolver and sensitivity service, which are made here and never shared.
        var draftId = stored?.Id ?? Guid.NewGuid();
        var draft = new Role
        {
            Id = draftId,
            Name = !string.IsNullOrWhiteSpace(req.Name) ? req.Name : stored?.Name ?? draftId.ToString(),
            Permissions = req.Permissions,
            SystemCapabilities = req.SystemCapabilities ?? [.. stored?.SystemCapabilities ?? []],
        };
        var resolver = new PermissionResolver(session, conditions, tenant, envelope, loggers.CreateLogger<PermissionResolver>());
        await resolver.PinRolesAsync(previewed, [draft], ct);

        // The services the real read uses, made for this request and judging the previewed user.
        // The guard is a new instance so the caller's branches, which the request's own guard
        // holds, are not swapped out. The sensitivity service reads its rule sets from the pinned
        // resolver and the roles from the draft, so nothing here touches the shared cache.
        var judged = new Judged(
            resolver,
            new BranchScopeGuard(session, branchMembership),
            new SensitivityService(session, configuration, tenant, resolver) { PinnedRoles = [draft] },
            new ClaimsPrincipal(new ClaimsIdentity([new Claim("UserId", previewed.Id.ToString())], "role-preview")),
            definition);

        var take = Math.Clamp(req.Take ?? DefaultTake, 1, MaxTake);
        var items = await CollectAsync(judged, req.Permissions, previewed, caller, take, ct);

        await Send.OkAsync(new Response { Items = items }, ct);
    }

    private async Task<Role?> LoadStoredAsync(Request req, bool platformCaller, CancellationToken ct)
    {
        if (req.RoleId is not { } id)
            return null;

        var role = await session.LoadAsync<Role>(id, ct);
        return role is not null && role.DeletedAt is null && (RoleScope.Reaches(role, tenant.Slug) || platformCaller)
            ? role
            : null;
    }

    /// <summary>
    /// The checks the role save makes, so a draft it would refuse is refused here. With a stored
    /// role they are the update's, which passes over what that role already holds. Without one they
    /// are the create's.
    /// </summary>
    private async Task CheckDraftAsync(Request req, Role? stored, bool platformCaller, CancellationToken ct)
    {
        foreach (var error in await ReferenceConditionRules.CheckAsync(session, req.Permissions, stored?.Permissions, ct))
            AddError(r => r.Permissions, error);
        foreach (var error in FieldSetRules.DuplicateTypeErrors(req.Permissions, stored?.Permissions))
            AddError(r => r.Permissions, error);
        FieldSetRules.CarryOver(req.Permissions, stored?.Permissions);
        FieldSetRules.Normalise(req.Permissions);
        foreach (var error in await RoleSaveRules.CheckAsync(session, req.Permissions, stored?.Permissions, ct))
            AddError(r => r.Permissions, error);

        // A seeded role keeps its own name, as on the update.
        if (!string.IsNullOrWhiteSpace(req.Name) && SystemRoles.IsReservedName(req.Name)
            && !(stored is not null && SystemRoles.Contains(stored.Id)))
            AddError(r => r.Name, SystemRoles.ReservedNameMessage(req.Name));

        if (req.SystemCapabilities is { Count: > 0 } capabilities
            && configuration.GetValue(CapabilityVocabulary.RefuseUnknownKey, false))
        {
            foreach (var name in vocabulary.Unknown(capabilities))
                AddError(r => r.SystemCapabilities, CapabilityVocabulary.UnknownMessage(name));
        }

        ThrowIfAnyErrors();

        // A platform role is changed by a platform administrator only.
        if (stored is { IsPlatform: true } && !platformCaller)
            ThrowError(RoleScope.PlatformRoleMessage, 403);

        // Only a platform administrator hands out a platform capability. One the stored role
        // already holds is kept, as on the update.
        if (!platformCaller && (req.SystemCapabilities ?? [])
                .Except(stored?.SystemCapabilities ?? [], StringComparer.OrdinalIgnoreCase)
                .Any(c => barakoCMS.Features.Users.PlatformRoles.PlatformCapabilities.Contains(c)))
        {
            ThrowError(RoleScope.PlatformCapabilityMessage, 403);
        }
    }

    private async Task<ContentTypeDefinition?> FindTypeAsync(Request req, CancellationToken ct)
    {
        // The resolution the read routes use, so an account-owned type is judged by its own fields.
        try
        {
            return await AccountContentTypes.ResolveAsync(session, req.ContentType, ct);
        }
        catch (AmbiguousContentTypeException ambiguous)
        {
            AddError(r => r.ContentType, ambiguous.Message);
            ThrowIfAnyErrors();
            return null;
        }
    }

    /// <summary>
    /// Refuses a draft read condition on a field the caller may not read. Whether an entry is
    /// visible under a condition says something about the field's value, so a caller who may not
    /// read the field must not be able to ask. A path through a reference field asks about that
    /// reference field on this type. A document property (<c>$createdBy</c> and the like) is not a
    /// field and is not checked.
    /// </summary>
    private async Task RefuseUnreadableConditionAsync(Request req, ContentTypeDefinition definition, CancellationToken ct)
    {
        var rule = PermissionResolver.PermissionFor(req.Permissions, definition.Name)?.Read;
        if (rule?.Conditions is not { Count: > 0 } held)
            return;

        foreach (var key in held.Keys.Where(k => !k.StartsWith('$')))
        {
            var name = ReferenceConditions.TrySplit(key, out var reference, out _) ? reference : key.Split('.')[0];
            var field = definition.Fields.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.Ordinal));
            // A key the type does not declare can still be in older entries, so it fails closed.
            if (field is null || !await sensitivity.MayReadFieldAsync(definition.Name, field, null, User, ct))
                AddError(r => r.Permissions,
                    $"The read condition on '{name}' is refused: '{name}' is not a field you may read on {definition.Name}, "
                    + "and which entries match would tell you its value.");
        }

        ThrowIfAnyErrors();
    }

    /// <summary>
    /// Pages through the type's entries, in the order they were made, until <paramref name="take"/>
    /// are collected or the scan limit is reached. Entries the caller may not read are skipped.
    /// </summary>
    private async Task<List<Entry>> CollectAsync(
        Judged judged, List<ContentTypePermission> draft, User previewed, User caller,
        int take, CancellationToken ct)
    {
        var typeName = judged.Definition.Name;
        var items = new List<Entry>();
        var scanned = 0;

        while (items.Count < take && scanned < ScanLimit)
        {
            var skip = scanned;
            var batch = await session.Query<barakoCMS.Models.Content>()
                .Where(c => c.ContentType == typeName)
                .OrderBy(c => c.CreatedAt).ThenBy(c => c.Id)
                .Skip(skip).Take(BatchSize)
                .ToListAsync(ct);
            if (batch.Count == 0) break;
            scanned += batch.Count;

            foreach (var entry in batch)
            {
                if (items.Count >= take) break;

                var scrubbed = await CallerTitleDataAsync(caller, entry, ct);
                if (scrubbed is null) continue;

                items.Add(await JudgeAsync(judged, draft, previewed, entry, scrubbed, ct));
            }
        }

        return items;
    }

    private async Task<Entry> JudgeAsync(
        Judged judged, List<ContentTypePermission> draft, User previewed, barakoCMS.Models.Content entry,
        IDictionary<string, object> scrubbed, CancellationToken ct)
    {
        var resolver = judged.Resolver;
        var typeName = judged.Definition.Name;
        var result = new Entry { Id = entry.Id, Title = ActivitySources.TitleOf(entry.Data, scrubbed) };

        // The list narrows to the user's branches before it asks any read rule, so this comes first.
        if (!await judged.Branches.MayAccessAsync(judged.Principal, entry, ct))
        {
            result.Visible = false;
            result.Reason = "outside branch scope";
            return result;
        }

        var reads = await resolver.CanPerformActionAsync(previewed, typeName, "read", entry, ct);
        if (!reads)
        {
            result.Visible = false;
            if (PermissionResolver.PermissionFor(draft, typeName)?.Read is { Enabled: true })
            {
                result.Reason = "condition not met";
                result.Condition = await resolver.FirstFailedReadConditionAsync(previewed, typeName, entry, ct);
            }
            else
            {
                result.Reason = "no read rule";
            }

            return result;
        }

        // An entry whose own level the user may not see comes back with its data cleared.
        if (!await judged.Sensitivity.MaySeeDocumentAsync(entry.Sensitivity, judged.Principal, ct))
        {
            result.Visible = false;
            result.Reason = "field rules hide it";
            return result;
        }

        var fields = await resolver.FieldSetAsync(previewed, typeName, "read", entry, ct);

        // The read the scrub applies: a field is shown when sensitivity, owner hiding and the
        // rule's set all allow it.
        // Case-insensitive, as the scrub matches keys: a stored key can differ from the field by case.
        var masked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in judged.Definition.Fields)
        {
            if (!await judged.Sensitivity.MayReadFieldAsync(typeName, field, entry, judged.Principal, ct))
                masked.Add(field.Name);
        }

        bool Readable(string name) => fields.Allows(name) && !masked.Contains(name);

        if (entry.Data.Count > 0 && !entry.Data.Keys.Any(Readable))
        {
            result.Visible = false;
            result.Reason = "field rules hide it";
            return result;
        }

        result.Visible = true;
        if (fields.IsAll && masked.Count == 0)
        {
            result.ReadableFields = null;
        }
        else
        {
            IEnumerable<string> names = fields.IsAll
                ? judged.Definition.Fields.Select(f => f.Name).Concat(entry.Data.Keys).Distinct(StringComparer.Ordinal)
                : fields.Names!;
            result.ReadableFields = names.Where(n => !masked.Contains(n)).Order(StringComparer.Ordinal).ToList();
        }

        return result;
    }

    /// <summary>What judging the previewed user takes, made once for the request.</summary>
    private sealed record Judged(
        PermissionResolver Resolver,
        IBranchScopeGuard Branches,
        ISensitivityService Sensitivity,
        ClaimsPrincipal Principal,
        ContentTypeDefinition Definition);

    /// <summary>
    /// The entry's data as the caller may see it, or null when the caller may not read the entry or
    /// sensitivity hides it whole. The caller's check is an entry decision, which the cached
    /// resolver never stores.
    /// </summary>
    private async Task<IDictionary<string, object>?> CallerTitleDataAsync(User caller, barakoCMS.Models.Content entry, CancellationToken ct)
    {
        if (!await branchGuard.MayAccessAsync(User, entry, ct)
            || !await callerPermissions.CanPerformActionAsync(caller, entry.ContentType, "read", entry, ct))
        {
            return null;
        }

        var scrubbed = new Dictionary<string, object>(entry.Data);
        return await sensitivity.ApplyAsync(entry, scrubbed, HttpContext, ct) ? null : scrubbed;
    }

    private async Task<bool> InTenantAsync(Guid userId, CancellationToken ct)
    {
        // The default tenant records no memberships, so any existing user belongs to it. Elsewhere
        // only an active member counts, as it does when the user's roles are resolved.
        if (tenant.IsDefault) return true;

        var slug = tenant.Slug;
        return await session.Query<Membership>()
            .AnyAsync(m => m.UserId == userId && m.TenantSlug == slug && m.Status == MembershipStatus.Active, ct);
    }
}
