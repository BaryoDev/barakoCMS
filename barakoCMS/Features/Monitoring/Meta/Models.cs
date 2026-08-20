namespace barakoCMS.Features.Monitoring.Meta;

public class MetaResponse
{
    public string Version { get; set; } = "";

    // Lets the admin offer an API reference link to *this* instance and hide it when there is
    // nothing to link to, rather than probing /swagger and guessing from a 404.
    public bool SwaggerEnabled { get; set; }
}
