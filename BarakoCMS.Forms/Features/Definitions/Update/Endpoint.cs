using barakoCMS.Infrastructure.Auth;
using FastEndpoints;
using Marten;
using Microsoft.Extensions.Options;

namespace BarakoCMS.Forms.Features.Definitions.Update;

/// <summary>
/// PUT /api/forms/{name}. Replaces fields, notify addresses and the enabled flag. The name is the
/// route and cannot change: it is the URL a website already points at.
/// </summary>
public class Endpoint : Endpoint<FormRequest, FormResponse>
{
    private readonly IDocumentSession _session;
    private readonly IOptions<FormsOptions> _options;

    public Endpoint(IDocumentSession session, IOptions<FormsOptions> options)
    {
        _session = session;
        _options = options;
    }

    public override void Configure()
    {
        Put("/api/forms/{name}");
        Definition.RequireCapability(FormsCapabilities.ManageForms, FormsCapabilities.LegacyRoles);
    }

    public override async Task HandleAsync(FormRequest req, CancellationToken ct)
    {
        // Route binding wins over the body for Name, so this is the route value.
        var name = req.Name.Trim();
        var form = await _session.Query<FormDefinition>().FirstOrDefaultAsync(f => f.Name == name, ct);
        if (form is null) { await Send.NotFoundAsync(ct); return; }

        if (FormRequestMapping.HoneypotClash(req, _options.Value) is { } clash)
        {
            AddError(r => r.Fields, clash);
            await Send.ErrorsAsync(400, ct);
            return;
        }

        FormRequestMapping.Apply(req, form);
        _session.Store(form);
        await _session.SaveChangesAsync(ct);

        await Send.ResponseAsync(FormResponse.From(form), cancellation: ct);
    }
}
