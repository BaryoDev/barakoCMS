using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using barakoCMS.Models;

namespace BarakoCMS.Tests.Features.Import;

/// <summary>
/// A spreadsheet import into a type with a token field. A column mapped onto the token is not
/// taken: every row gets a token of its own.
/// </summary>
[Collection("Sequential")]
public class BulkCreateTokenTests
{
    private const string Forged = "forged0token0forged0token0forged";

    private readonly IntegrationTestFixture _fixture;

    public BulkCreateTokenTests(IntegrationTestFixture fixture) => _fixture = fixture;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Every_row_gets_its_own_generated_token_and_a_token_column_is_not_taken()
    {
        var type = await TokenFieldProbe.StoreTypeAsync(
            _fixture, "tokimp", false, TokenFieldProbe.Text("Name"), TokenFieldProbe.Token());
        var client = TokenFieldProbe.ClientFor(_fixture, await TokenFieldProbe.AdminAsync(_fixture));

        var response = await client.PostAsJsonAsync("/api/import/content", new
        {
            contentType = type,
            records = new[]
            {
                new Dictionary<string, object> { ["Name"] = "Ana", ["ClaimToken"] = Forged },
                new Dictionary<string, object> { ["Name"] = "Ben", ["ClaimToken"] = Forged },
                new Dictionary<string, object> { ["Name"] = "Cy" },
            },
        }, Ct);

        var body = await response.Content.ReadAsStringAsync(Ct);
        response.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}", response.StatusCode, body);
        JsonDocument.Parse(body).RootElement.GetProperty("created").GetInt32().Should().Be(3);

        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        var entries = await session.Query<Content>().Where(c => c.ContentType == type).ToListAsync(Ct);

        entries.Should().HaveCount(3);
        var tokens = entries.Select(e => TokenFieldProbe.TokenOf(e)).ToList();
        tokens.Should().HaveCount(3);
        tokens.Should().OnlyHaveUniqueItems();
        tokens.Should().NotContain(Forged);
        tokens.ForEach(t => TokenFieldProbe.ShouldBeAToken(t));
    }
}
