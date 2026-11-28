using System.Net;
using System.Text.Json;
using BarakoCMS.Tests.Features.Config;
using FluentAssertions;
using barakoCMS.Models;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace BarakoCMS.Tests.Features.Portability;

/// <summary>
/// An export of the <c>contents</c> section holds no more entries than an apply accepts, and says so
/// when it left some out.
/// </summary>
/// <remarks>A tenant per test, since the section exports every entry the tenant holds.</remarks>
[Collection("Integration1")]
public class ExportLimitTests
{
    private readonly ConfigCallers _callers;
    private WebApplicationFactory<Program>? _host;
    private readonly IntegrationTestFixture _fixture;

    public ExportLimitTests(IntegrationTestFixture fixture)
    {
        _fixture = fixture;
        _callers = new ConfigCallers(fixture);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private WebApplicationFactory<Program> Host() =>
        _host ??= _fixture.WithSetting("Portability:MaxImportRecords", "2");

    private async Task<HttpClient> ExporterWithEntriesAsync(int count)
    {
        var tenant = await _callers.TenantAsync();
        var type = ("explimit" + Guid.NewGuid().ToString("n"))[..14];
        var documents = new List<object>
        {
            new ContentTypeDefinition
            {
                Id = Guid.NewGuid(),
                Name = type,
                DisplayName = type,
                Fields = [new FieldDefinition { Name = "Title", DisplayName = "Title", Type = "string" }],
            },
        };
        for (var i = 0; i < count; i++)
        {
            documents.Add(new Content
            {
                Id = Guid.NewGuid(),
                ContentType = type,
                Status = ContentStatus.Published,
                Data = new() { ["Title"] = $"t{i}" },
            });
        }

        await _callers.StoreInTenantAsync(tenant, [.. documents]);
        return await _callers.SuperAdminAsync(tenant, Host());
    }

    [Fact]
    public async Task An_export_over_the_record_limit_holds_the_limit_and_names_it()
    {
        var client = await ExporterWithEntriesAsync(3);

        var (response, body) = await ConfigCallers.ExportAsync(client, "contents");

        response.StatusCode.Should().Be(HttpStatusCode.OK, body.ToString());
        body.GetProperty("contents").EnumerateObject().Should().HaveCount(2, "an apply takes no more than that");
        body.GetProperty("gaps").GetProperty("contents").EnumerateArray()
            .Select(g => g.GetString()).Should().ContainSingle(g => g!.Contains("2") && g.Contains("Portability:MaxImportRecords"));
    }

    [Fact]
    public async Task An_export_at_the_record_limit_is_served_whole()
    {
        var client = await ExporterWithEntriesAsync(2);

        var (response, body) = await ConfigCallers.ExportAsync(client, "contents");

        response.StatusCode.Should().Be(HttpStatusCode.OK, body.ToString());
        body.GetProperty("contents").EnumerateObject().Should().HaveCount(2);
        body.TryGetProperty("gaps", out var gaps).Should().BeTrue();
        gaps.TryGetProperty("contents", out _).Should().BeFalse("nothing was left out");
    }
}
