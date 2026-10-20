using barakoCMS.Infrastructure.Jobs;
using barakoCMS.Features.ContentType.AddField;
using barakoCMS.Infrastructure.Auth;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using FastEndpoints;
using Marten;

namespace barakoCMS.Features.Accounts.ContentTypes.AddField;

/// <summary>POST /api/accounts/{slug}/content-types/{name}/fields: add a field to an account-owned type, for every tenant of the account.</summary>
/// <remarks>
/// Takes the request and answers the response of the tenant-side endpoint of the same name. The
/// stored entries of every tenant are not rewritten: a new field is optional or has a default, so
/// an entry written before it is still valid. A required field with no default is refused when an
/// entry of the type exists in any of the account's tenants, counted on a session opened for each
/// tenant so it holds under <c>Tenancy:DatabaseEnforcement</c>. Two requests adding fields at once
/// cannot lose one: the type is stored with optimistic concurrency and the loser gets 409. Changing or removing a field of a shared type
/// needs the versioned schema change of #934 and is not offered.
/// </remarks>
internal sealed class Endpoint(
    IDocumentSession session,
    IContentTypeValidatorService validator,
    IDocumentStore store,
    barakoCMS.Infrastructure.Multitenancy.TenantContext tenant) : Endpoint<ContentType.AddField.Request, ContentType.AddField.Response>
{
    public override void Configure()
    {
        Post("/api/accounts/{slug}/content-types/{name}/fields");
        Definition.ChecksCapability(SystemCapabilities.ManageTenants);
        Definition.RefusesWhileFieldChangeRuns();
    }

    private async Task RefuseWhatTheTenantsHoldAsync(TenantAccount account, ContentTypeDefinition definition, FieldDefinition field, CancellationToken ct)
    {
        var tenantIds = await AccountContentTypes.TenantIdsAsync(session, account.Id, ct);
        foreach (var id in tenantIds)
        {
            await using var tenantSession = store.QuerySession(id);
            if (await barakoCMS.Infrastructure.Jobs.FieldChanges.RunningAsync(tenantSession, definition.Name, ct) is { } running)
            {
                AddError(barakoCMS.Infrastructure.Jobs.FieldChanges.Busy(running));
                ThrowIfAnyErrors(StatusCodes.Status409Conflict);
            }
        }

        if (field.Sensitivity != SensitivityLevel.Public
            && await AccountContentTypes.AnyEventSourcedAsync(store, tenantIds, definition.Name, ct))
        {
            AddError($"A tenant of this account has decided \"{definition.Name}\" as event sourced, and an event sourced type cannot hold a Sensitive or Hidden field.");
            ThrowIfAnyErrors(StatusCodes.Status409Conflict);
        }

        var merged = definition.Fields.Concat(new[] { field }).ToList();
        var (isValid, errors) = validator.Validate(definition.Name, definition.DisplayName, merged, definition.Fields);
        if (!isValid)
        {
            foreach (var error in errors) AddError(error);
            ThrowIfAnyErrors();
        }

        if (field.IsRequired && field.DefaultValue is null)
        {
            var entries = (await AccountContentTypes.EntriesPerTenantAsync(store, tenantIds, [definition.Name], ct)).Values.Sum();
            if (entries > 0)
            {
                AddError($"\"{definition.Name}\" already has {entries} "
                    + (entries == 1 ? "entry" : "entries")
                    + $" across the account with no \"{field.Name}\", so a required field with no default would make "
                    + "them all invalid. Give the field a defaultValue, or add it as optional.");
                ThrowIfAnyErrors();
            }
        }
    }

    public override async Task HandleAsync(ContentType.AddField.Request req, CancellationToken ct)
    {
        var account = await AccountOwnedTypes.ManagedAsync(
            HttpContext, session, Route<string>("slug") ?? string.Empty,
            () => Send.ForbiddenAsync(ct), () => Send.NotFoundAsync(ct), ct);
        if (account is null) return;

        var name = barakoCMS.Core.ContentTypeName.Normalize(req.Name);
        var owned = await session.Query<AccountContentType>()
            .FirstOrDefaultAsync(t => t.AccountId == account.Id && t.Name == name, ct);
        if (owned is null) { await Send.NotFoundAsync(ct); return; }

        var definition = owned.Definition;
        var clash = definition.Fields
            .FirstOrDefault(f => string.Equals(f.Name, req.FieldName, StringComparison.OrdinalIgnoreCase));
        if (clash is not null)
        {
            AddError($"The content type \"{definition.Name}\" already has a field named \"{clash.Name}\". "
                + "Changing an existing field is not supported on an account-owned type.");
            ThrowIfAnyErrors(StatusCodes.Status409Conflict);
        }

        if (barakoCMS.Core.Validation.TokenFields.IsToken(req.Type))
        {
            AddError("A token field cannot be part of an account-owned content type yet.");
            ThrowIfAnyErrors();
        }

        var field = ContentType.AddField.Endpoint.NewField(req);
        foreach (var problem in await AccountTypeRoles.ToPlatformIdsAsync(session, [field], ct)) AddError(problem);
        ThrowIfAnyErrors();

        await RefuseWhatTheTenantsHoldAsync(account, definition, field, ct);

        if (req.DryRun)
        {
            await Send.OkAsync(new ContentType.AddField.Response
            {
                ContentType = definition.Name,
                Added = field.Name,
                Fields = definition.Fields.Select(f => f.Name).Append(field.Name).ToList(),
                DryRun = true,
                Changes = [new barakoCMS.Core.Validation.FieldDiff(field.Name, null, field.Type)],
            }, ct);
            return;
        }

        definition.Fields.Add(field);
        definition.UpdatedAt = DateTime.UtcNow;
        session.Store(owned);
        await AccountTypeAudit.RecordAsync(session, User, tenant.Slug, "accountcontenttype.field_added", account, definition.Name, field.Name, ct);
        try
        {
            await session.SaveChangesAsync(ct);
        }
        catch (JasperFx.ConcurrencyException)
        {
            AddError($"\"{definition.Name}\" was changed by another request while this one ran. Read it again and retry.");
            ThrowIfAnyErrors(StatusCodes.Status409Conflict);
        }

        await Send.OkAsync(new ContentType.AddField.Response
        {
            ContentType = definition.Name,
            Added = field.Name,
            Fields = definition.Fields.Select(f => f.Name).ToList(),
        }, ct);
    }
}
