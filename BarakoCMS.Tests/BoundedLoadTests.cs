using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BarakoCMS.Portability;
using FluentAssertions;
using Marten;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using barakoCMS.Models;

#pragma warning disable CS0618

namespace BarakoCMS.Tests;

/// <summary>
/// The bundle export and the preview mint ask Postgres for the entries they need, not for every
/// entry of the tenant or of the type.
/// </summary>
/// <remarks>
/// A behavioural assertion cannot see this: the old and new shapes return the same rows. What
/// separates them is whether the filter reaches the database, so the tests read the statements
/// Marten sent, and check the answer alongside it.
/// </remarks>
[Collection("Integration1")]
public class BoundedLoadTests
{
    private readonly IntegrationTestFixture _factory;
    private readonly CapturingMartenLogger _logger = new();
    private readonly HttpClient _client;
    private readonly IServiceProvider _services;

    public BoundedLoadTests(IntegrationTestFixture factory)
    {
        _factory = factory;
        var derived = factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
                services.ConfigureMarten(options => options.Logger(_logger))));
        _client = derived.CreateClient();
        _services = derived.Services;
    }

    private async Task<string> SeedTypeAsync(string prefix, params string[] slugs)
    {
        var type = (prefix + Guid.NewGuid().ToString("n"))[..14];
        using var scope = _services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Store(new ContentTypeDefinition
        {
            Id = Guid.NewGuid(),
            Name = type,
            DisplayName = type,
            Fields =
            [
                new FieldDefinition { Name = "Title", DisplayName = "Title", Type = "string" },
                new FieldDefinition { Name = "Slug", DisplayName = "Slug", Type = "slug" },
            ],
        });
        foreach (var slug in slugs)
        {
            session.Store(new Content
            {
                Id = Guid.NewGuid(),
                ContentType = type,
                Status = ContentStatus.Draft,
                Data = new Dictionary<string, object> { ["Title"] = "T-" + slug, ["Slug"] = slug },
            });
        }
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        return type;
    }

    private async Task AuthenticateAsync()
    {
        var userId = Guid.NewGuid();
        using (var scope = _services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Store(new User
            {
                Id = userId,
                Username = ("bl-" + userId.ToString("n"))[..14],
                Email = "bl-" + userId.ToString("n") + "@example.com",
                RoleIds = [barakoCMS.Data.DataSeeder.SuperAdminRoleId],
            });
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", _factory.CreateToken(["SuperAdmin"], userId.ToString()));
    }

    // The content table is mt_doc_contents. A bare "mt_doc_content" prefix would also take the
    // content type definition select, which a filtered export legitimately sends without a filter.
    private IEnumerable<string> SelectsFrom(string table) =>
        _logger.Commands.Select(c => c.Sql)
            .Where(sql => sql.TrimStart().StartsWith("select", StringComparison.OrdinalIgnoreCase)
                          && System.Text.RegularExpressions.Regex.IsMatch(sql, @"\b" + table + @"\b"));

    [Fact]
    public async Task Export_of_named_types_filters_the_entries_in_postgres()
    {
        var wanted = await SeedTypeAsync("bl-exp-a", "one", "two");
        var other = await SeedTypeAsync("bl-exp-b", "three");
        await AuthenticateAsync();
        _logger.Commands.Clear();

        var response = await _client.GetAsync(
            "/api/portability/export?types=" + wanted, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var bundle = (await response.Content.ReadFromJsonAsync<PortabilityBundle>(ApiJson.Options, TestContext.Current.CancellationToken))!;
        bundle.Contents.Should().HaveCount(2);
        bundle.Contents.Should().OnlyContain(c => c.ContentType == wanted);
        bundle.Contents.Should().NotContain(c => c.ContentType == other);

        var selects = SelectsFrom("mt_doc_contents").ToList();
        selects.Should().NotBeEmpty("the export has to read the entries from the content table");
        selects.Should().OnlyContain(sql => sql.Contains("ANY(", StringComparison.Ordinal),
            "an export of named types must not read every entry of the tenant and filter in memory");
        SelectsFrom("mt_doc_contenttypedefinition").Should().NotBeEmpty(
            "the type definitions are read separately and are not what this test is about");
    }

    [Fact]
    public async Task A_preview_mint_asks_postgres_for_the_slug()
    {
        var type = await SeedTypeAsync("bl-prev", "target", "decoy-1", "decoy-2");
        await AuthenticateAsync();
        _logger.Commands.Clear();

        var response = await _client.PostAsJsonAsync(
            "/api/preview", new { Type = type, Slug = "TARGET" }, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        doc.RootElement.GetProperty("token").GetString().Should().NotBeNullOrEmpty();

        _logger.Commands.Should().Contain(
            c => c.Parameters.Contains("TARGET") && c.Sql.Contains("mt_doc_content", StringComparison.Ordinal),
            "the slug has to reach the database, or every entry of the type is loaded to match one");
    }

    [Fact]
    public async Task A_preview_mint_for_a_missing_slug_is_a_404()
    {
        var type = await SeedTypeAsync("bl-miss", "present");
        await AuthenticateAsync();
        _logger.Commands.Clear();

        var response = await _client.PostAsJsonAsync(
            "/api/preview", new { Type = type, Slug = "absent" }, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        _logger.Commands.Should().Contain(
            c => c.Parameters.Contains("absent") && c.Sql.Contains("mt_doc_contents", StringComparison.Ordinal),
            "a missing slug is decided by the database, not by loading every entry of the type and finding none");
    }
}
