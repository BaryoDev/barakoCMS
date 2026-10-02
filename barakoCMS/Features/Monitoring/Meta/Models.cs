namespace barakoCMS.Features.Monitoring.Meta;

internal class MetaResponse
{
    public string Version { get; set; } = "";

    // The HTTP contract version of the admin surface (see ApiContract), not the package version
    // above. A console compares this one against the range it supports; comparing Version instead
    // breaks on every patch that does not touch the HTTP surface.
    public int ApiContractVersion { get; set; }

    // The delivery surface's own number, which a renderer compares instead of the one above.
    public int DeliveryContractVersion { get; set; }

    // One entry per module that runs here. Left out of the body for a caller without view_modules:
    // the names are the enabled module list, and GET /api/modules gates that.
    [System.Text.Json.Serialization.JsonIgnore(
        Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<ModuleContractVersion>? ModuleContractVersions { get; set; }

    // Lets the admin offer an API reference link to *this* instance and hide it when there is
    // nothing to link to, rather than probing /swagger and guessing from a 404.
    public bool SwaggerEnabled { get; set; }
}

/// <param name="Name"><see cref="barakoCMS.Modules.IBarakoModule.Name"/>, verbatim.</param>
/// <param name="Version">
/// <see cref="barakoCMS.Modules.IBarakoModule.HttpContractVersion"/>. Zero means the module states
/// none: it serves no endpoints, or its author has not versioned them.
/// </param>
internal sealed record ModuleContractVersion(string Name, int Version);
