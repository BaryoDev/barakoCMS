using barakoCMS.Models;
using barakoCMS.Modules;
using Marten;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BarakoCMS.AI;

/// <summary>
/// Adds semantic (vector) search over published content. Register with:
/// <code>services.AddBarakoCMS(config, m =&gt; m.Add(new AiModule()));</code>
/// It binds the "Ai" section, registers a typed embedding client, and stores one vector per published
/// entry (multi-tenanted). Endpoints: POST /api/ai/index/{type} (admin) to (re)build a type's index,
/// and GET /api/public/{type}/semantic?q=… (anonymous) to search it. Off until "Ai:Enabled" is true.
/// </summary>
public sealed class AiModule : IBarakoModule
{
    public string Name => "AI";

    /// <summary>Settings used to live at the root "Ai" section. See IBarakoModule.</summary>
    public string? LegacyConfigurationSection => AiOptions.SectionName;

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        // `configuration` is already this module's own section (Modules:AI), so bind it whole
        // rather than reaching for a root-level key.
        services.Configure<AiOptions>(configuration);
        services.AddHttpClient<IEmbeddingClient, OllamaEmbeddingClient>();
    }

    public void ConfigureSchema(IModuleSchema schema)
    {
        schema.For<ContentEmbedding>()
            .DocumentAlias("content_embeddings")
            .Index(x => x.ContentType);
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

    public static string? SlugField(ContentTypeDefinition def)
    {
        var byType = def.Fields.FirstOrDefault(f => string.Equals(f.Type, "slug", StringComparison.OrdinalIgnoreCase));
        if (byType is not null) return byType.Name;
        return def.Fields.FirstOrDefault(f => string.Equals(f.Name, "slug", StringComparison.OrdinalIgnoreCase))?.Name;
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
