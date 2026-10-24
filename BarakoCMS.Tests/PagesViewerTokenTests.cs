using System.Net;
using System.Text.Json;
using barakoCMS.Infrastructure.Viewers;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// The Pages module's resolve route honours X-Viewer-Token the way the core public reads do: a
/// Members page answers only for a viewer token valid for the request's tenant.
/// </summary>
[Collection("Integration2")]
public class PagesViewerTokenTests
{
    private const string TypeName = "pageviewerprobe";

    private static readonly Lock HostGate = new();
    private static WebApplicationFactory<Program>? _host;

    private readonly IntegrationTestFixture _factory;

    public PagesViewerTokenTests(IntegrationTestFixture factory) => _factory = factory;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Viewer(string tenant) =>
        ViewerToken.Create(_factory.Services.GetRequiredService<IConfiguration>(), tenant, "member-1").Token;

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
            DisplayName = "Page viewer probe",
            IsPubliclyDeliverable = true,
            Fields =
            [
                new FieldDefinition { Name = "Title", DisplayName = "Title", Type = "string" },
                new FieldDefinition { Name = "Slug", DisplayName = "Slug", Type = "slug" },
                new FieldDefinition { Name = "ParentPage", DisplayName = "Parent page", Type = "reference", ReferenceType = TypeName },
                new FieldDefinition { Name = "ShowInNavigation", DisplayName = "Show in navigation", Type = "bool" },
                new FieldDefinition { Name = "NavigationOrder", DisplayName = "Navigation order", Type = "int" },
            ],
        });
        await session.SaveChangesAsync(Ct);
    }

    private async Task<string> PageAsync(SensitivityLevel level)
    {
        await DeclareTypeAsync();
        var slug = "pv-" + Guid.NewGuid().ToString("n")[..10];

        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Store(new Content
        {
            Id = Guid.NewGuid(),
            ContentType = TypeName,
            Status = ContentStatus.Published,
            Sensitivity = level,
            Data = new Dictionary<string, object> { ["Title"] = "Lounge", ["Slug"] = slug },
        });
        await session.SaveChangesAsync(Ct);
        return slug;
    }

    private Task<HttpResponseMessage> ResolveAsync(string slug, string? token = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, $"/api/public/pages/resolve?path=/{slug}");
        if (token is not null)
        {
            req.Headers.Add(ViewerToken.Header, token);
        }

        return Host().CreateClient().SendAsync(req, Ct);
    }

    [Fact]
    public async Task A_members_page_is_not_found_without_a_viewer_token()
    {
        var slug = await PageAsync(SensitivityLevel.Members);

        var res = await ResolveAsync(slug);

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_members_page_resolves_for_a_valid_viewer_token_and_is_not_publicly_cacheable()
    {
        var slug = await PageAsync(SensitivityLevel.Members);

        var res = await ResolveAsync(slug, Viewer(Tenant.DefaultSlug));

        var body = await res.Content.ReadAsStringAsync(Ct);
        res.StatusCode.Should().Be(HttpStatusCode.OK, body);
        JsonDocument.Parse(body).RootElement.GetProperty("entry").GetProperty("data").GetProperty("Title").GetString()
            .Should().Be("Lounge");
        res.Headers.CacheControl!.Private.Should().BeTrue();
        res.Headers.CacheControl.NoStore.Should().BeTrue();
        res.Headers.Vary.Should().Contain(ViewerToken.Header);
        res.Headers.Vary.Should().Contain("X-Tenant");
    }

    [Fact]
    public async Task A_viewer_token_minted_for_another_tenant_is_refused_with_401()
    {
        var slug = await PageAsync(SensitivityLevel.Members);

        var res = await ResolveAsync(slug, Viewer("some-other-tenant"));

        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_garbage_viewer_token_is_refused_with_401_even_for_a_public_page()
    {
        var slug = await PageAsync(SensitivityLevel.Public);

        var res = await ResolveAsync(slug, "not-a-token");

        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_public_page_is_unchanged_without_a_token_and_varies_on_the_viewer_header()
    {
        var slug = await PageAsync(SensitivityLevel.Public);

        var res = await ResolveAsync(slug);

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        res.Headers.CacheControl!.Public.Should().BeTrue();
        res.Headers.Vary.Should().Contain(ViewerToken.Header);
        res.Headers.Vary.Should().Contain("X-Tenant");
    }

    [Fact]
    public async Task A_public_page_with_a_valid_viewer_token_is_served_privately()
    {
        var slug = await PageAsync(SensitivityLevel.Public);

        var res = await ResolveAsync(slug, Viewer(Tenant.DefaultSlug));

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        res.Headers.CacheControl!.Private.Should().BeTrue();
    }
}
