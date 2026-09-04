using barakoCMS.Infrastructure.Auth;
using barakoCMS.Models;
using FastEndpoints;

namespace barakoCMS.Features.Capabilities.List;

/// <summary>
/// GET /api/capabilities, the names a role on this instance can be granted and where each comes from.
/// </summary>
/// <remarks>
/// Gated the way <c>GET /api/roles</c> is: whoever can read roles can read what a role can hold.
/// The list is what <see cref="CapabilityVocabulary"/> reads off the running host, so a module you
/// have not installed is absent and a name here is one some endpoint actually asks for. See issue #490.
/// </remarks>
internal sealed class Endpoint : Endpoint<ListCapabilitiesRequest, PaginatedResponse<KnownCapability>>
{
    private readonly CapabilityVocabulary _vocabulary;

    public Endpoint(CapabilityVocabulary vocabulary) => _vocabulary = vocabulary;

    public override void Configure()
    {
        Get("/api/capabilities");
        Definition.RequireCapability(SystemCapabilities.ManageRoles, "SuperAdmin");
    }

    public override async Task HandleAsync(ListCapabilitiesRequest req, CancellationToken ct)
    {
        await Send.OkAsync(_vocabulary.Entries.ToPagedResponse(req), ct);
    }
}
