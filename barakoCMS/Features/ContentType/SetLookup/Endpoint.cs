using barakoCMS.Core.Validation;
using barakoCMS.Infrastructure.Audit;
using barakoCMS.Infrastructure.Auth;
using barakoCMS.Infrastructure.Jobs;
using barakoCMS.Models;
using FastEndpoints;
using Marten;

namespace barakoCMS.Features.ContentType.SetLookup;

/// <summary>
/// PUT /api/content-types/{name}/lookup, which sets or removes the anonymous lookup the type answers
/// at <c>POST /api/public/{type}/lookup</c>.
/// </summary>
/// <remarks>
/// The same gate as turning public delivery on, because it is the same decision: what an anonymous
/// caller can read. A field that is Sensitive, Hidden or a token cannot be in the returned set, and
/// a reveal may add a token but no other non-Public field.
/// </remarks>
internal class Endpoint(
    IDocumentSession session,
    barakoCMS.Infrastructure.Multitenancy.TenantContext tenant) : Endpoint<Request, Response>
{
    public override void Configure()
    {
        Put("/api/content-types/{name}/lookup");
        Definition.RequireCapability(SystemCapabilities.ManagePublicDelivery, "SuperAdmin", "Admin");
        Definition.RefusesWhileFieldChangeRuns();
    }

    public override async Task HandleAsync(Request req, CancellationToken ct)
    {
        var name = barakoCMS.Core.ContentTypeName.Normalize(Route<string>("name") ?? string.Empty);

        var def = await session.Query<ContentTypeDefinition>()
            .FirstOrDefaultAsync(d => d.Name.ToLower() == name, ct);

        if (def is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        if (req.Lookup is { } lookup)
        {
            ContentTypeDefinition? settingType = null;
            if (lookup.Reveal is { } reveal)
            {
                var settingName = barakoCMS.Core.ContentTypeName.Normalize(reveal.SettingType);
                settingType = await session.Query<ContentTypeDefinition>()
                    .FirstOrDefaultAsync(d => d.Name.ToLower() == settingName, ct);
            }

            var errors = LookupRules.Errors(lookup, def, settingType);
            if (errors.Count > 0)
            {
                foreach (var error in errors) AddError(error);
                await Send.ErrorsAsync(400, ct);
                return;
            }

            // Stored with the names the type spells them with, so the read side matches them exactly.
            lookup.MatchField = LookupRules.Field(def, lookup.MatchField)!.Name;
            lookup.VerifyFields = lookup.VerifyFields.Select(f => LookupRules.Field(def, f)!.Name).ToList();
            lookup.ReturnFields = lookup.ReturnFields.Select(f => LookupRules.Field(def, f)!.Name).ToList();
            if (lookup.Reveal is { } stored)
            {
                stored.Fields = stored.Fields.Select(f => LookupRules.Field(def, f)!.Name).ToList();
                stored.SettingType = settingType!.Name;
                stored.SettingField = LookupRules.Field(settingType, stored.SettingField)!.Name;
            }
        }

        var before = def.Lookup is null ? "none" : Describe(def.Lookup);
        def.Lookup = req.Lookup;
        def.UpdatedAt = DateTimeOffset.UtcNow;
        session.Store(def);

        var actorId = Guid.TryParse(User.FindFirst("UserId")?.Value, out var parsed) ? parsed : (Guid?)null;
        await AuditLog.RecordAsync(
            session,
            tenant.Slug,
            "contenttype.lookup.changed",
            actorId,
            User.FindFirst("Username")?.Value,
            targetType: "ContentType",
            targetId: def.Id.ToString(),
            metadata: new Dictionary<string, object>
            {
                ["contentType"] = def.Name,
                ["from"] = before,
                ["to"] = def.Lookup is null ? "none" : Describe(def.Lookup),
            },
            ct: ct);

        await session.SaveChangesAsync(ct);

        await Send.OkAsync(new Response { Name = def.Name, Lookup = def.Lookup }, ct);
    }

    private static string Describe(LookupDefinition lookup) =>
        $"match {lookup.MatchField}; verify {string.Join(", ", lookup.VerifyFields)}; "
        + $"return {string.Join(", ", lookup.ReturnFields)}"
        + (lookup.Reveal is { } reveal
            ? $"; reveal {string.Join(", ", reveal.Fields)} while {reveal.SettingType}.{reveal.SettingField}"
            : string.Empty);
}
