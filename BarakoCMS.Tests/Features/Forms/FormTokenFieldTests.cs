using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BarakoCMS.Tests.Features.Forms;

/// <summary>
/// A public form on a type with a token field: the form does not offer the field, a visitor who
/// sends it anyway is refused, and the submission is stored with a token the server generated.
/// </summary>
/// <remarks>
/// Every test sends from its own client IP, for the reason <see cref="FormSubmissionTests"/> does.
/// </remarks>
[Collection("Sequential")]
public class FormTokenFieldTests
{
    private readonly IntegrationTestFixture _factory;

    public FormTokenFieldTests(IntegrationTestFixture factory) => _factory = factory;

    private HttpClient Visitor()
    {
        var client = _factory.CreateClient();
        var bytes = Guid.NewGuid().ToByteArray();
        var ip = $"2001:db8::{bytes[0]:x2}{bytes[1]:x2}:{bytes[2]:x2}{bytes[3]:x2}:{bytes[4]:x2}{bytes[5]:x2}";
        client.DefaultRequestHeaders.Add(TestRemoteIpFilter.Header, ip);
        return client;
    }

    /// <summary>A registration form: a name a visitor fills in, and a claim token they never see.</summary>
    private async Task<string> CreateFormAsync()
    {
        var name = $"form-{Guid.NewGuid():N}";

        using (var scope = _factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Store(new ContentTypeDefinition
            {
                Id = Guid.NewGuid(),
                Name = name,
                DisplayName = "Registration",
                Fields =
                [
                    new FieldDefinition { Name = "name", DisplayName = "Name", Type = "string", IsRequired = true },
                    new FieldDefinition
                    {
                        Name = "claim", DisplayName = "Claim", Type = "token", Sensitivity = SensitivityLevel.Hidden,
                    },
                ],
            });
            await session.SaveChangesAsync();
        }

        var admin = _factory.CreateClient();
        admin.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", await _factory.StoredUserTokenAsync("Admin"));
        var enabled = await admin.PutAsJsonAsync($"/api/forms/{name}", new { enabled = true });
        enabled.StatusCode.Should().Be(HttpStatusCode.OK, await enabled.Content.ReadAsStringAsync());

        return name;
    }

    private async Task<IReadOnlyList<Content>> EntriesAsync(string type)
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IQuerySession>()
            .Query<Content>().Where(c => c.ContentType == type).ToListAsync();
    }

    [Fact]
    public async Task The_form_does_not_offer_the_token_field()
    {
        var type = await CreateFormAsync();

        var response = await Visitor().GetAsync($"/api/public/forms/{type}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var names = body.RootElement.GetProperty("fields").EnumerateArray()
            .Select(f => f.GetProperty("name").GetString())
            .ToList();
        names.Should().HaveCount(1);
        names.Should().Equal("name");
    }

    [Fact]
    public async Task A_submission_that_sends_a_token_is_refused_and_one_that_does_not_is_stored_with_one()
    {
        var type = await CreateFormAsync();

        var refused = await Visitor().PostAsJsonAsync($"/api/public/forms/{type}",
            new { data = new { name = "Ana", claim = "forged0token0forged0token0forged" } });
        refused.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var accepted = await Visitor().PostAsJsonAsync($"/api/public/forms/{type}",
            new { data = new { name = "Ben" } });
        accepted.StatusCode.Should().Be(HttpStatusCode.Accepted, await accepted.Content.ReadAsStringAsync());

        var entries = await EntriesAsync(type);
        entries.Should().HaveCount(1, "only the submission that sent no token was stored");
        entries[0].Data["name"].Should().Be("Ben");
        TokenFieldProbe.ShouldBeAToken(TokenFieldProbe.TokenOf(entries[0], "claim"));
    }
}
