using barakoCMS.Models;
using barakoCMS.Modules;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace BarakoCMS.AI;

/// <summary>
/// Adds semantic (vector) search over published content. Register with:
/// <code>services.AddBarakoCMS(config, m =&gt; m.Add(new AiModule()));</code>
/// It binds its own "Modules:AI" section, registers a typed embedding client, and stores one vector per published
/// entry (multi-tenanted). Endpoints: POST /api/ai/index/{type} (admin) to (re)build a type's index,
/// and GET /api/public/{type}/semantic?q=… (anonymous) to search it. Off until "Modules:AI:Enabled" is true.
/// </summary>
public sealed class AiModule : IBarakoModule
{
    public string Name => "AI";

    public string? PackageId => "BarakoCMS.AI";

    public string? PackageVersion => ModuleMetadata.VersionOf(GetType());

    public string? Category => ModuleCategory.Insight;

    public string? Description => "Semantic search over published content";

    public string? Glyph => "sparkles";

    public int HttpContractVersion => 3;

    /// <summary>Settings used to live at the root "Ai" section. See IBarakoModule.</summary>
    public string? LegacyConfigurationSection => AiOptions.SectionName;

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        // `configuration` is already this module's own section (Modules:AI), so bind it whole
        // rather than reaching for a root-level key.
        services.AddOptions<AiOptions>()
            .Bind(configuration)
            .Validate(o => o.SemanticSearchPermitLimit >= 1 && o.SemanticSearchWindowSeconds >= 1,
                "Modules:AI semantic search rate limit values must be at least 1.")
            .ValidateOnStart();

        services.AddOptions<RateLimiterOptions>().Configure<IConfiguration, IOptions<AiOptions>>((options, root, ai) =>
        {
            var rendererKeyHash = barakoCMS.Infrastructure.Security.RateLimitSetup.RendererKeyHashOf(root);
            var window = new barakoCMS.Infrastructure.Security.RateLimitWindow(
                ai.Value.SemanticSearchPermitLimit, ai.Value.SemanticSearchWindowSeconds, 0);
            options.AddPolicy(AiOptions.SemanticSearchRateLimitPolicy, context =>
                barakoCMS.Infrastructure.Security.RateLimitSetup.RendererAwarePartition(
                    context, AiOptions.SemanticSearchRateLimitPolicy, window, rendererKeyHash));
        });
        services.AddHttpClient<IEmbeddingClient, OllamaEmbeddingClient>();
    }

    public void ConfigureSchema(IModuleSchema schema)
    {
        schema.For<ContentEmbedding>()
            .DocumentAlias("content_embeddings")
            .Index(x => x.ContentType);
    }

    /// <summary>
    /// Gives this module's capabilities to the roles that already reached its endpoints.
    /// </summary>
    /// <remarks>
    /// Core cannot do this: <c>SystemCapabilities.DefaultsFor</c> does not know this module exists.
    /// Without it the endpoints would be reachable only through the legacy role-name fallback, and
    /// turning that off, which is the point of issue #443, would take the module away from every
    /// Admin. Additive and idempotent, and it skips a role the host never seeded.
    /// </remarks>
    public Task SeedAsync(IModuleSeedContext context, CancellationToken ct) =>
        AiCapabilities.Defaults.GrantAsync(context, ct);

    [Obsolete("Override SeedAsync(IModuleSeedContext, CancellationToken), which names no Marten type. "
              + "Removal planned for barakoCMS 6.0.0.")]
    public Task SeedAsync(Marten.IDocumentSession session, IServiceProvider services, CancellationToken ct)
    {
#pragma warning disable CS0618
        return SeedAsync(ModuleSeedContexts.Over(session, services), ct);
#pragma warning restore CS0618
    }
}

/// <summary>
/// Shared rules for turning a content entry into embeddable text and a safe title — using only fields
/// the content type marks Public, so a Sensitive field is never embedded or returned.
/// </summary>
internal static class PublicText
{
    public static HashSet<string> PublicFieldNames(ContentTypeDefinition def) =>
        def.Fields.Where(f => f.Sensitivity == SensitivityLevel.Public)
            .Select(f => f.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The core delivery rule: a field of type slug, else a Public text field named slug, and in
    /// either case only when the type marks it Public. Null otherwise, so nothing is returned as a
    /// slug that the type does not serve.
    /// </summary>
    /// <remarks>
    /// The same answer as <c>IPublicContentProjector.SlugField</c>, which
    /// <c>AiSemanticTests.The_module_slug_rule_agrees_with_the_core_delivery_rule</c> holds it to.
    /// Kept as a copy because the callers here are static and run without a scope.
    /// </remarks>
    public static string? SlugField(ContentTypeDefinition def)
    {
        var field = def.Fields.FirstOrDefault(f => string.Equals(f.Type, "slug", StringComparison.OrdinalIgnoreCase))
            ?? def.Fields.FirstOrDefault(f =>
                string.Equals(f.Name, "slug", StringComparison.OrdinalIgnoreCase)
                && (string.Equals(f.Type, "string", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(f.Type, "text", StringComparison.OrdinalIgnoreCase))
                && f.Sensitivity == SensitivityLevel.Public);
        return field is { Sensitivity: SensitivityLevel.Public } ? field.Name : null;
    }

    public static string? SlugValue(Content c, ContentTypeDefinition def)
    {
        var sf = SlugField(def);
        return sf is not null && c.Data.TryGetValue(sf, out var v) ? v?.ToString() : null;
    }

    /// <summary>Concatenate the entry's public field values (minus the slug) for embedding.</summary>
    public static string ToEmbeddableText(Content c, ContentTypeDefinition def)
    {
        var names = PublicFieldNames(def);
        var slug = SlugField(def);
        var parts = c.Data
            .Where(kv => names.Contains(kv.Key) && !string.Equals(kv.Key, slug, StringComparison.OrdinalIgnoreCase))
            .Select(kv => kv.Value?.ToString())
            .Where(s => !string.IsNullOrWhiteSpace(s));
        return string.Join("\n", parts!);
    }

    /// <summary>A display title from a public "Title"/"Name" field, else the slug, else empty.</summary>
    public static string TitleOf(Content c, ContentTypeDefinition def)
    {
        var names = PublicFieldNames(def);
        foreach (var key in new[] { "Title", "Name" })
        {
            if (names.Contains(key) && c.Data.TryGetValue(key, out var v) && v is not null)
            {
                var text = v.ToString();
                if (!string.IsNullOrWhiteSpace(text)) return text!;
            }
        }
        return SlugValue(c, def) ?? string.Empty;
    }
}
