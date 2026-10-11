using barakoCMS.Features.ContentType.Blueprints;
using barakoCMS.Features.EmailTemplates;
using barakoCMS.Models;
using Marten;
using Microsoft.Extensions.DependencyInjection;

namespace BarakoCMS.Tests.Features.EmailTemplates;

/// <summary>Stores the email blueprint's types and entries of them straight into a tenant.</summary>
internal static class EmailTemplateData
{
    /// <summary>The blueprint's two types in the session's tenant, unless they are there already.</summary>
    public static async Task EnsureTypesAsync(IntegrationTestFixture fixture, IDocumentSession session, CancellationToken ct)
    {
        using var scope = fixture.Services.CreateScope();
        var blueprint = scope.ServiceProvider.GetRequiredService<BlueprintCatalog>().Find("email")!.Definition!;

        foreach (var type in BlueprintCatalog.Materialize(blueprint))
        {
            var name = type.Name;
            if (await session.Query<ContentTypeDefinition>().AnyAsync(d => d.Name == name, ct)) continue;
            session.Store(type);
        }

        await session.SaveChangesAsync(ct);
    }

    public static Content Template(
        string subject, string body, ContentStatus status = ContentStatus.Published, Guid? layout = null, string? slug = null) => new()
    {
        Id = Guid.NewGuid(),
        ContentType = EmailTemplateRenderer.TemplateType,
        Status = status,
        Data = new Dictionary<string, object>
        {
            ["Name"] = "A template",
            ["Slug"] = slug ?? $"t-{Guid.NewGuid():N}",
            ["Subject"] = subject,
            ["Body"] = body,
            ["Layout"] = layout?.ToString() ?? string.Empty,
        },
    };

    public static Content Layout(string header, string accent, ContentStatus status = ContentStatus.Published) => new()
    {
        Id = Guid.NewGuid(),
        ContentType = EmailTemplateRenderer.LayoutType,
        Status = status,
        Data = new Dictionary<string, object>
        {
            ["Name"] = "A layout",
            ["Header"] = header,
            ["Footer"] = "Sent by Acme",
            ["Accent"] = accent,
        },
    };
}
