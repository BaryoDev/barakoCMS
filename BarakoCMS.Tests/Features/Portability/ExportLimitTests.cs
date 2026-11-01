using System.Net;
using System.Net.Http.Headers;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

// The Portability routes are obsolete, and they are what these tests are about.
#pragma warning disable CS0618

namespace BarakoCMS.Tests.Features.Portability;

/// <summary>An export holds no more records than an import accepts.</summary>
[Collection("Integration1")]
public class ExportLimitTests
{
    private readonly IntegrationTestFixture _fixture;
    private WebApplicationFactory<Program>? _host;

    public ExportLimitTests(IntegrationTestFixture fixture) => _fixture = fixture;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private WebApplicationFactory<Program> Host() =>
        _host ??= _fixture.WithSetting("Portability:MaxImportRecords", "2");

    private async Task<string> StoreEntriesAsync(int count)
    {
        var type = ("explimit" + Guid.NewGuid().ToString("n"))[..14];
        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Store(new ContentTypeDefinition
        {
            Id = Guid.NewGuid(),
            Name = type,
            DisplayName = type,
            Fields = [new FieldDefinition { Name = "Title", DisplayName = "Title", Type = "string" }],
        });
        for (var i = 0; i < count; i++)
        {
            session.Store(new Content
            {
                Id = Guid.NewGuid(),
                ContentType = type,
                Status = ContentStatus.Published,
                Data = new() { ["Title"] = $"t{i}" },
            });
        }

        await session.SaveChangesAsync(Ct);
        return type;
    }

    private async Task<HttpResponseMessage> ExportAsync(string type)
    {
        var client = Host().CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", await _fixture.StoredUserTokenAsync("SuperAdmin", "Admin"));
        return await client.GetAsync($"/api/portability/export?types={type}", Ct);
    }

    [Fact]
    public async Task An_export_over_the_record_limit_is_refused_and_names_the_limit()
    {
        var type = await StoreEntriesAsync(3);

        var response = await ExportAsync(type);

        var body = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, body);
        body.Should().Contain("at most 2");
    }

    [Fact]
    public async Task An_export_at_the_record_limit_is_served()
    {
        var type = await StoreEntriesAsync(2);

        var response = await ExportAsync(type);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
    }
}
