using barakoCMS.Infrastructure.Auth;
using FastEndpoints;
using Marten;
using Microsoft.Extensions.Options;

namespace BarakoCMS.Forms.Features.Definitions.Create;

/// <summary>POST /api/forms. A name is unique within the tenant; a repeat is 409.</summary>
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
        Post("/api/forms");
        Definition.RequireCapability(FormsCapabilities.ManageForms, FormsCapabilities.LegacyRoles);
    }

    public override async Task HandleAsync(FormRequest req, CancellationToken ct)
    {
        if (FormRequestMapping.HoneypotClash(req, _options.Value) is { } clash)
        {
            AddError(r => r.Fields, clash);
            await Send.ErrorsAsync(400, ct);
            return;
        }

        var name = req.Name.Trim();
        var existing = await _session.Query<FormDefinition>().AnyAsync(f => f.Name == name, ct);
        if (existing)
        {
            AddError(r => r.Name, $"A form named '{name}' already exists.");
            await Send.ErrorsAsync(409, ct);
            return;
        }

        var form = new FormDefinition { Name = name };
        FormRequestMapping.Apply(req, form);
        form.CreatedAt = form.UpdatedAt;

        _session.Store(form);
        await _session.SaveChangesAsync(ct);

        await Send.ResponseAsync(FormResponse.From(form), 201, ct);
    }
}
