using Xunit;
using FluentAssertions;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BarakoCMS.Diagnostics;
using Marten;
using Microsoft.Extensions.DependencyInjection;

namespace BarakoCMS.Tests.Features.Diagnostics;

/// <summary>
/// POST /api/client-errors/{id}/resolve over real HTTP, read back through the list the Errors screen uses.
/// </summary>
[Collection("Sequential")]
public class ClientErrorResolveTests
{
    private readonly IntegrationTestFixture _factory;

    public ClientErrorResolveTests(IntegrationTestFixture factory) => _factory = factory;

    private async Task<HttpClient> AdminClient()
    {
        var c = _factory.CreateClient();
        c.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", await _factory.StoredUserTokenAsync("Admin"));
        return c;
    }

    private async Task<(Guid Id, string Marker)> SeedOpenErrorAsync()
    {
        var marker = $"resolve-{Guid.NewGuid():N}"[..24];
        var id = Guid.NewGuid();
        using var scope = _factory.Services.CreateScope();
        var s = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        s.Store(new ClientError
        {
            Id = id,
            Fingerprint = Guid.NewGuid().ToString("N"),
            Message = marker,
            Severity = "error",
        });
        await s.SaveChangesAsync();
        return (id, marker);
    }

    private static async Task<JsonElement> ListedAsync(HttpClient client, string marker)
    {
        var json = await (await client.GetAsync($"/api/client-errors?q={marker}")).Content.ReadFromJsonAsync<JsonElement>();
        var items = json.GetProperty("items");
        items.GetArrayLength().Should().Be(1, "the seeded error is the only one carrying this marker");
        return items[0];
    }

    [Fact]
    public async Task A_reference_and_a_note_given_on_resolve_come_back_on_the_list()
    {
        var (id, marker) = await SeedOpenErrorAsync();
        var admin = await AdminClient();

        var res = await admin.PostAsJsonAsync($"/api/client-errors/{id}/resolve",
            new { reference = "  https://dev.azure.com/org/project/_workitems/edit/1234  ", note = "Fixed by the envelope change" });
        res.StatusCode.Should().Be(HttpStatusCode.OK);

        var row = await ListedAsync(admin, marker);
        row.GetProperty("resolved").GetBoolean().Should().BeTrue();
        row.GetProperty("resolutionReference").GetString()
            .Should().Be("https://dev.azure.com/org/project/_workitems/edit/1234", "surrounding whitespace is a paste artefact");
        row.GetProperty("resolutionNote").GetString().Should().Be("Fixed by the envelope change");
        row.GetProperty("resolvedBy").GetString().Should().StartWith("stored-", "the name comes from the caller's token");
        row.GetProperty("resolvedAt").ValueKind.Should().Be(JsonValueKind.String);
    }

    [Fact]
    public async Task A_bare_ticket_number_is_accepted_as_a_reference()
    {
        var (id, marker) = await SeedOpenErrorAsync();
        var admin = await AdminClient();

        (await admin.PostAsJsonAsync($"/api/client-errors/{id}/resolve", new { reference = "PROJ-42" }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        (await ListedAsync(admin, marker)).GetProperty("resolutionReference").GetString().Should().Be("PROJ-42");
    }

    [Fact]
    public async Task Resolving_with_an_empty_body_still_resolves_and_records_no_reference()
    {
        var (id, marker) = await SeedOpenErrorAsync();
        var admin = await AdminClient();

        (await admin.PostAsJsonAsync($"/api/client-errors/{id}/resolve", new { }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var row = await ListedAsync(admin, marker);
        row.GetProperty("resolved").GetBoolean().Should().BeTrue();
        row.GetProperty("resolutionReference").ValueKind.Should().Be(JsonValueKind.Null);
        row.GetProperty("resolutionNote").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task A_reference_or_note_over_the_limit_is_refused()
    {
        var (id, marker) = await SeedOpenErrorAsync();
        var admin = await AdminClient();

        (await admin.PostAsJsonAsync($"/api/client-errors/{id}/resolve", new { reference = new string('r', 501) }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await admin.PostAsJsonAsync($"/api/client-errors/{id}/resolve", new { note = new string('n', 2001) }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);

        (await ListedAsync(admin, marker)).GetProperty("resolved").GetBoolean()
            .Should().BeFalse("a refused resolve must not half-apply");
    }

    [Fact]
    public async Task Reopening_clears_the_reference_the_note_and_who_resolved_it()
    {
        var (id, marker) = await SeedOpenErrorAsync();
        var admin = await AdminClient();

        await admin.PostAsJsonAsync($"/api/client-errors/{id}/resolve", new { reference = "#129", note = "shipped" });
        (await admin.PostAsJsonAsync($"/api/client-errors/{id}/resolve", new { resolved = false }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var row = await ListedAsync(admin, marker);
        row.GetProperty("resolved").GetBoolean().Should().BeFalse();
        row.GetProperty("resolvedAt").ValueKind.Should().Be(JsonValueKind.Null);
        row.GetProperty("resolvedBy").ValueKind.Should().Be(JsonValueKind.Null);
        row.GetProperty("resolutionReference").ValueKind.Should().Be(JsonValueKind.Null);
        row.GetProperty("resolutionNote").ValueKind.Should().Be(JsonValueKind.Null);
    }
}
