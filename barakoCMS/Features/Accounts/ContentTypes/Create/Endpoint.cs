using barakoCMS.Infrastructure.Audit;
using barakoCMS.Infrastructure.Auth;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using FastEndpoints;
using Marten;

namespace barakoCMS.Features.Accounts.ContentTypes.Create;

internal sealed class Request
{
    /// <summary>The account, from the route.</summary>
    public string Slug { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public List<FieldDefinition> Fields { get; set; } = new();
}

internal sealed class Response
{
    public Guid Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public Guid AccountId { get; init; }
}

/// <summary>POST /api/accounts/{slug}/content-types: define a content type every tenant of the account uses.</summary>
/// <remarks>
/// Refused when the account already owns the name, and when a tenant of the account already has its
/// own type of that name, since the tenant's would shadow this one. That second check reads across
/// the account's tenants, so under <c>Tenancy:DatabaseEnforcement</c> it sees only the tenant the
/// request is made on, and the tenant-side create refusal is what holds the line there.
/// A token field is refused: handing out values for a field some tenant's entries may already hold
/// is the per-tenant check the type-level create makes, and it is not made across tenants yet.
/// </remarks>
internal sealed class Endpoint(
    IDocumentSession session,
    IContentTypeValidatorService validator,
    barakoCMS.Infrastructure.Multitenancy.TenantContext tenant) : Endpoint<Request, Response>
{
    public override void Configure()
    {
        Post("/api/accounts/{slug}/content-types");
        Definition.ChecksCapability(SystemCapabilities.ManageTenants);
    }

    public override async Task HandleAsync(Request req, CancellationToken ct)
    {
        var account = await AccountOwnedTypes.ManagedAsync(
            HttpContext, session, req.Slug, () => Send.ForbiddenAsync(ct), () => Send.NotFoundAsync(ct), ct);
        if (account is null) return;

        var fields = req.Fields ?? [];
        var (isValid, errors) = validator.Validate(req.Name, req.DisplayName, fields);
        if (fields.Any(f => f is not null && barakoCMS.Core.Validation.TokenFields.IsToken(f.Type)))
        {
            isValid = false;
            errors.Add("A token field cannot be part of an account-owned content type yet.");
        }

        if (!isValid)
        {
            foreach (var error in errors) AddError(error);
            ThrowIfAnyErrors();
        }

        var name = barakoCMS.Core.ContentTypeName.Normalize(req.Name);

        if (await session.Query<AccountContentType>().AnyAsync(t => t.AccountId == account.Id && t.Name == name, ct))
            ThrowError("The account already owns a Content Type with this name.", 409);

        var tenantIds = await AccountContentTypes.TenantIdsAsync(session, account.Id, ct);
        if (await session.Query<ContentTypeDefinition>()
                .AnyAsync(d => d.TenantIsOneOf(tenantIds.ToArray()) && d.Name == name, ct))
        {
            ThrowError("A tenant of this account already has its own Content Type with this name. Rename or remove it first.", 409);
        }

        var now = DateTime.UtcNow;
        var id = Guid.NewGuid();
        var definition = new ContentTypeDefinition
        {
            Id = id,
            Name = name,
            DisplayName = req.DisplayName,
            Description = req.Description ?? string.Empty,
            Fields = fields,
            CreatedAt = now,
            UpdatedAt = now,
        };
        await barakoCMS.Core.RoleReferences.ToIdsAsync(session, definition.Fields, ct);

        session.Store(new AccountContentType
        {
            Id = id,
            AccountId = account.Id,
            Name = name,
            Definition = definition,
            CreatedAt = now,
        });

        await AccountTypeAudit.RecordAsync(session, User, tenant.Slug, "accountcontenttype.created", account, name, null, ct);

        try
        {
            await session.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex.ToString().Contains("duplicate key", StringComparison.OrdinalIgnoreCase))
        {
            ThrowError("The account already owns a Content Type with this name.", 409);
        }

        await Send.OkAsync(new Response { Id = id, Name = name, AccountId = account.Id }, ct);
    }
}
