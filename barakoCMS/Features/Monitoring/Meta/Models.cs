namespace barakoCMS.Features.Monitoring.Meta;

internal class MetaResponse
{
    public string Version { get; set; } = "";

    // The HTTP contract version (see ApiContract), not the package version above. A console
    // compares this one against the range it supports; comparing Version instead breaks on every
    // patch that does not touch the HTTP surface.
    public int ApiContractVersion { get; set; }

    // Lets the admin offer an API reference link to *this* instance and hide it when there is
    // nothing to link to, rather than probing /swagger and guessing from a 404.
    public bool SwaggerEnabled { get; set; }
}
