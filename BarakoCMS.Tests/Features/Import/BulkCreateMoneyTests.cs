using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using barakoCMS.Models;

namespace BarakoCMS.Tests.Features.Import;

/// <summary>
/// A spreadsheet import sends every cell as text. A money field that declares a currency takes a
/// number, so the import reads plain decimal text as one before the row is validated.
/// </summary>
[Collection("Sequential")]
public class BulkCreateMoneyTests
{
    private readonly IntegrationTestFixture _fixture;

    public BulkCreateMoneyTests(IntegrationTestFixture fixture) => _fixture = fixture;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<string> ContentTypeAsync(string? currency)
    {
        var name = $"priced{Guid.NewGuid():n}"[..12];
        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Store(new ContentTypeDefinition
        {
            Id = Guid.NewGuid(),
            Name = name,
            DisplayName = name,
            Fields =
            [
                new FieldDefinition { Name = "Title", DisplayName = "Title", Type = "string", IsRequired = true },
                new FieldDefinition { Name = "Price", DisplayName = "Price", Type = "money", Currency = currency },
            ],
        });
        await session.SaveChangesAsync(Ct);
        return name;
    }

    private async Task<HttpClient> AdminAsync()
    {
        var userId = Guid.NewGuid();
        using (var scope = _fixture.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Store(new User
            {
                Id = userId,
                Username = $"imp-{Guid.NewGuid():n}"[..14],
                Email = $"imp-{Guid.NewGuid():n}@example.com",
                RoleIds = [SystemRoles.SuperAdminRoleId],
            });
            await session.SaveChangesAsync(Ct);
        }

        var client = _fixture.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", _fixture.CreateToken(["SuperAdmin"], userId.ToString()));
        return client;
    }

    private async Task<List<Content>> EntriesAsync(string contentType)
    {
        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        var entries = await session.Query<Content>().Where(c => c.ContentType == contentType).ToListAsync(Ct);
        return entries.ToList();
    }

    private static Dictionary<string, object> Row(string title, string price) =>
        new() { ["Title"] = title, ["Price"] = price };

    [Fact]
    public async Task A_cell_of_plain_decimal_text_is_stored_as_a_number_in_a_field_with_a_currency()
    {
        var type = await ContentTypeAsync("USD");
        var client = await AdminAsync();

        var response = await client.PostAsJsonAsync("/api/import/content", new
        {
            contentType = type,
            records = new[] { Row("One", "12.50") },
        }, Ct);

        response.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}", response.StatusCode,
            await response.Content.ReadAsStringAsync(Ct));

        var entries = await EntriesAsync(type);
        entries.Should().HaveCount(1);
        entries[0].Data["Price"].Should().BeOfType<decimal>("the cell was text and the field takes a number");
        entries[0].Data["Price"].Should().Be(12.50m);
    }

    [Fact]
    public async Task Text_that_is_not_a_plain_amount_or_does_not_fit_is_refused_naming_the_row_and_the_field()
    {
        var type = await ContentTypeAsync("USD");
        var client = await AdminAsync();

        var response = await client.PostAsJsonAsync("/api/import/content", new
        {
            contentType = type,
            continueOnError = true,
            records = new[]
            {
                Row("Too fine", "12.345"),
                Row("Separator", "1,250.00"),
                Row("Words", "abc"),
                Row("Fits", "12.50"),
            },
        }, Ct);

        response.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}", response.StatusCode,
            await response.Content.ReadAsStringAsync(Ct));

        using var report = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        report.RootElement.GetProperty("created").GetInt32().Should().Be(1);

        var errors = report.RootElement.GetProperty("errors").EnumerateArray().ToList();
        errors.Should().HaveCount(3);
        errors.Select(e => e.GetProperty("row").GetInt32()).Should().Equal(0, 1, 2);
        foreach (var error in errors)
        {
            var messages = error.GetProperty("messages").EnumerateArray().Select(m => m.GetString()).ToList();
            messages.Should().HaveCount(1);
            messages[0].Should().Contain("Price");
        }

        var entries = await EntriesAsync(type);
        entries.Should().HaveCount(1);
        entries[0].Data["Title"].Should().Be("Fits");
    }

    [Fact]
    public async Task A_money_field_with_no_currency_stores_the_text_it_was_sent_as_it_did()
    {
        var type = await ContentTypeAsync(null);
        var client = await AdminAsync();

        var response = await client.PostAsJsonAsync("/api/import/content", new
        {
            contentType = type,
            records = new[] { Row("One", "12.50") },
        }, Ct);

        response.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}", response.StatusCode,
            await response.Content.ReadAsStringAsync(Ct));

        var entries = await EntriesAsync(type);
        entries.Should().HaveCount(1);
        entries[0].Data["Price"].Should().Be("12.50");
    }
}
