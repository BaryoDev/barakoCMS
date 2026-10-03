using System.Net;
using System.Text.Json;
using barakoCMS.Core.Interfaces;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// The Pages module's anonymous resolve route answers a page's <c>file</c> fields as the core
/// delivery routes do: a public file as the object, any other file not at all.
/// </summary>
/// <remarks>
/// A host of its own whose Pages options name a type of this class's, so the shared page type the
/// other Pages tests use is left as it is. One for the class and never disposed, per the note on
/// IntegrationTestFixture.WithSetting.
/// </remarks>
[Collection("Sequential")]
public class PagesFileFieldTests
{
    private const string TypeName = "pagefileprobe";

    private static readonly Lock HostGate = new();
    private static WebApplicationFactory<Program>? _host;

    private readonly IntegrationTestFixture _factory;

    public PagesFileFieldTests(IntegrationTestFixture factory) => _factory = factory;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private WebApplicationFactory<Program> Host()
    {
        lock (HostGate)
        {
            return _host ??= _factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
                services.Configure<BarakoCMS.Pages.PagesOptions>(o =>
                {
                    o.ContentType = TypeName;
                    o.TitleField = "Title";
                    o.ParentField = "ParentPage";
                    o.ShowInNavigationField = "ShowInNavigation";
                    o.OrderField = "NavigationOrder";
                })));
        }
    }

    private async Task DeclareTypeAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        if (await session.Query<ContentTypeDefinition>().AnyAsync(d => d.Name == TypeName, Ct))
        {
            return;
        }

        session.Store(new ContentTypeDefinition
        {
            Id = Guid.NewGuid(),
            Name = TypeName,
            DisplayName = "Page file probe",
            IsPubliclyDeliverable = true,
            Fields =
            [
                new FieldDefinition { Name = "Title", DisplayName = "Title", Type = "string" },
                new FieldDefinition { Name = "Slug", DisplayName = "Slug", Type = "slug" },
                new FieldDefinition { Name = "ParentPage", DisplayName = "Parent page", Type = "reference", ReferenceType = TypeName },
                new FieldDefinition { Name = "ShowInNavigation", DisplayName = "Show in navigation", Type = "bool" },
                new FieldDefinition { Name = "NavigationOrder", DisplayName = "Navigation order", Type = "int" },
                new FieldDefinition { Name = "Cover", DisplayName = "Cover", Type = "file" },
                new FieldDefinition { Name = "MetaTitle", DisplayName = "Meta title", Type = "string" },
                new FieldDefinition { Name = "SocialImage", DisplayName = "Social image", Type = "file" },
            ],
        });
        await session.SaveChangesAsync(Ct);
    }

    private async Task<Guid> FileAsync(bool isPublic, string name)
    {
        using var scope = _factory.Services.CreateScope();
        using var content = new MemoryStream(FileSamples.Png(Guid.NewGuid().ToByteArray()));
        var saved = await scope.ServiceProvider.GetRequiredService<IFileStore>().SaveAsync(
            new FileToStore { Content = content, FileName = name, ContentType = "image/png", IsPublic = isPublic, Owner = Guid.NewGuid() },
            Ct);
        saved.File.Should().NotBeNull(saved.Refused ?? string.Empty);
        return saved.File!.Id;
    }

    private async Task<string> PageAsync(Guid file)
    {
        await DeclareTypeAsync();
        var slug = "pf-" + Guid.NewGuid().ToString("n")[..10];

        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Store(new Content
        {
            Id = Guid.NewGuid(),
            ContentType = TypeName,
            Status = ContentStatus.Published,
            Sensitivity = SensitivityLevel.Public,
            Data = new Dictionary<string, object>
            {
                ["Title"] = "Harbour",
                ["Slug"] = slug,
                ["MetaTitle"] = "Harbour",
                ["Cover"] = file.ToString(),
                ["SocialImage"] = file.ToString(),
            },
        });
        await session.SaveChangesAsync(Ct);
        return slug;
    }

    private async Task<(string Body, JsonElement Entry)> ResolveAsync(string slug)
    {
        var res = await Host().CreateClient().GetAsync($"/api/public/pages/resolve?path=/{slug}", Ct);
        var body = await res.Content.ReadAsStringAsync(Ct);
        res.StatusCode.Should().Be(HttpStatusCode.OK, body);
        return (body, JsonDocument.Parse(body).RootElement.GetProperty("entry").Clone());
    }

    [Fact]
    public async Task A_private_file_on_a_resolved_page_is_left_out_and_is_no_social_image()
    {
        var locked = await FileAsync(isPublic: false, "payslip-march.png");
        var slug = await PageAsync(locked);

        var (body, entry) = await ResolveAsync(slug);

        entry.GetProperty("data").GetProperty("Title").GetString().Should().Be("Harbour");
        entry.GetProperty("data").TryGetProperty("Cover", out _).Should().BeFalse();
        entry.GetProperty("data").TryGetProperty("SocialImage", out _).Should().BeFalse();
        entry.GetProperty("seo").GetProperty("imageUrl").ValueKind.Should().Be(JsonValueKind.Null,
            "an id is not an image address, and this one names a file nobody anonymous may fetch");
        body.Should().NotContain(locked.ToString()).And.NotContain("payslip");
    }

    [Fact]
    public async Task A_public_file_on_a_resolved_page_is_answered_as_the_file_and_is_the_social_image()
    {
        var open = await FileAsync(isPublic: true, "harbour.png");
        var slug = await PageAsync(open);

        var (_, entry) = await ResolveAsync(slug);

        var cover = entry.GetProperty("data").GetProperty("Cover");
        cover.ValueKind.Should().Be(JsonValueKind.Object);
        cover.GetProperty("id").GetGuid().Should().Be(open);
        cover.GetProperty("url").GetString().Should().Be($"/api/public/files/{open}");
        cover.GetProperty("fileName").GetString().Should().Be("harbour.png");
        entry.GetProperty("seo").GetProperty("imageUrl").GetString().Should().Be($"/api/public/files/{open}");
    }
}
