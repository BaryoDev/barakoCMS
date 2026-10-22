using barakoCMS.Modules;

namespace BarakoCMS.Comments;

/// <summary>
/// A thread on any record. Enable it with:
/// <code>services.AddBarakoCMS(config, m =&gt; m.Add(new CommentsModule()));</code>
/// </summary>
/// <remarks>
/// A comment names its target by type and id. Content entries are a target out of the box; another
/// module adds its own kind of record by registering an <see cref="ICommentTarget"/>, which is also
/// where that module says who may read the record. Reading or posting on a record needs the comment
/// capability and the right to read the record itself.
/// </remarks>
public sealed class CommentsModule : IBarakoModule
{
    public string Name => "Comments";

    public int ContractVersion => ModuleContract.Version;

    public int HttpContractVersion => 1;

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<ICommentTarget, ContentCommentTarget>();
        services.AddScoped<CommentTargets>();
        services.AddHostedService<CommentTargetsCheck>();
    }

    public void ConfigureSchema(IModuleSchema schema)
    {
        schema.For<Comment>()
            .DocumentAlias("comments")
            .Index(x => x.TargetId);
    }

    public Task SeedAsync(IModuleSeedContext context, CancellationToken ct) =>
        CommentsCapabilities.Defaults.GrantAsync(context, ct);
}
