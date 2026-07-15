using barakoCMS.Modules;
using Marten;

namespace BarakoCMS.Files;

/// <summary>
/// Optional file-attachment module for barakoCMS. Enable it with:
/// <code>services.AddBarakoCMS(config, m =&gt; m.Add(new FilesModule()));</code>
/// Adds <c>POST /api/files</c> (upload) and <c>GET /api/files/{id}</c> (download), storing bytes in
/// Postgres via Marten. No seed data.
/// </summary>
public sealed class FilesModule : IBarakoModule
{
    public string Name => "Files";

    public void ConfigureMarten(StoreOptions options)
    {
        options.Schema.For<StoredFile>()
            .DocumentAlias("stored_files")
            .Index(x => x.CreatedAt)
            .Index(x => x.UploadedBy);
    }
}
