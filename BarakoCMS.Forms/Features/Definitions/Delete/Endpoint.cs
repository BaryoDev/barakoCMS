using barakoCMS.Infrastructure.Auth;
using FastEndpoints;
using Marten;

namespace BarakoCMS.Forms.Features.Definitions.Delete;

/// <summary>
/// DELETE /api/forms/{name}. Removes the form and every submission it received.
/// </summary>
/// <remarks>
/// The submissions go with the form on purpose. They are personal data, and a mailbox nobody can
/// read any more is exactly the data that should not be kept. Export first if it is needed.
/// </remarks>
public class Endpoint : EndpointWithoutRequest
{
    private readonly IDocumentSession _session;

    public Endpoint(IDocumentSession session) => _session = session;

    public override void Configure()
    {
        Delete("/api/forms/{name}");
        Definition.RequireCapability(FormsCapabilities.ManageForms, FormsCapabilities.LegacyRoles);
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var name = Route<string>("name") ?? string.Empty;
        var form = await _session.Query<FormDefinition>().FirstOrDefaultAsync(f => f.Name == name, ct);
        if (form is null) { await Send.NotFoundAsync(ct); return; }

        _session.DeleteWhere<FormSubmission>(s => s.FormName == name);
        _session.Delete(form);
        await _session.SaveChangesAsync(ct);

        await Send.NoContentAsync(ct);
    }
}
