using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using barakoCMS.Core.Interfaces;
using barakoCMS.Events;
using barakoCMS.Models;

namespace BarakoCMS.Tests.Features.ContentApi;

/// <summary>
/// The history answers with the whole stream (issue #336).
/// </summary>
/// <remarks>
/// The mapper built entries from ContentCreated and ContentUpdated and returned null for the other
/// three event types, and the caller filtered those nulls out. Publishing a document left no trace
/// in the document's own history, and nothing in the response said the list had been shortened.
///
/// The assertion is the entry count against the stream length, not the set of types present. A test
/// naming the five known types would still pass on the day a sixth is added and silently dropped,
/// which is the failure worth preventing.
/// </remarks>
[Collection("Sequential")]
public class ContentHistoryCoverageTests
{
    private readonly IntegrationTestFixture _factory;
    private readonly HttpClient _client;

    public ContentHistoryCoverageTests(IntegrationTestFixture factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Every_event_on_the_stream_appears_in_the_history()
    {
        var (token, _) = await TestHelpers.CreateAdminUserAsync(_factory);
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var createResp = await _client.PostAsJsonAsync("/api/contents", new
        {
            ContentType = "Article",
            Data = new Dictionary<string, object> { { "Title", "v1" } },
        });
        createResp.EnsureSuccessStatusCode();
        var id = (await createResp.Content.ReadFromJsonAsync<barakoCMS.Features.Content.Create.Response>(ApiJson.Options))!.Id;

        var updateResp = await _client.PutAsJsonAsync($"/api/contents/{id}", new
        {
            Id = id,
            Data = new Dictionary<string, object> { { "Title", "v2" } },
            Version = 1,
        });
        updateResp.EnsureSuccessStatusCode();

        var statusResp = await _client.PutAsJsonAsync(
            $"/api/contents/{id}/status",
            new barakoCMS.Features.Content.ChangeStatus.Request { Id = id, NewStatus = ContentStatus.Published });
        statusResp.EnsureSuccessStatusCode();

        var scheduleResp = await _client.PutAsJsonAsync($"/api/contents/{id}/schedule", new
        {
            Id = id,
            ScheduledUnpublishAt = DateTime.UtcNow.AddDays(7),
        });
        scheduleResp.EnsureSuccessStatusCode();

        // Nothing behind the HTTP API emits ContentSensitivityChanged yet, so it is appended the way
        // the scheduler appends its own events, through the writer that also updates the document.
        using (var scope = _factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            var writer = scope.ServiceProvider.GetRequiredService<IContentWriter>();
            var content = (await session.LoadAsync<barakoCMS.Models.Content>(id))!;
            writer.Append(content, new ContentSensitivityChanged(id, SensitivityLevel.Sensitive, Guid.NewGuid()));
            await session.SaveChangesAsync();
        }

        int streamLength;
        using (var scope = _factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
            streamLength = (await session.Events.FetchStreamAsync(id)).Count;
        }

        var historyResp = await _client.GetAsync($"/api/contents/{id}/history");
        historyResp.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await historyResp.Content.ReadAsStringAsync());
        var entries = document.RootElement.GetProperty("items").EnumerateArray().ToList();

        entries.Should().HaveCount(
            streamLength,
            "an event the mapper does not recognise is dropped from a list called history, and the "
          + "caller has no way to tell the list was filtered");

        entries.Should().OnlyContain(
            e => !string.IsNullOrWhiteSpace(e.GetProperty("changeType").GetString()),
            "without a discriminator a client cannot tell a status change from a document version");

        // The values themselves, not just their presence. changeType is what a client branches on,
        // so it is wire contract, and it is decided by a switch rather than reflected from the CLR
        // type name for exactly that reason. Pinning it here is what stops a rename of an event
        // record from quietly changing the API, which is the failure the switch exists to prevent
        // and which nothing else would catch.
        entries.Select(e => e.GetProperty("changeType").GetString())
            .Should().BeEquivalentTo(
                ["Created", "Updated", "StatusChanged", "Scheduled", "SensitivityChanged"],
                "these five strings are the contract, independent of what the event records are called");
    }

    /// <summary>
    /// An event the mapper does not know is reported as "Unknown", not as its class name.
    /// </summary>
    /// <remarks>
    /// The hole `EventSurfaceTests` cannot see. That guard reflects over types, and a CLR type name
    /// turned into a string is invisible to it: by the time the value is on the wire it is a
    /// `string` property, which the guard reads as safe.
    ///
    /// So the leak #229 forbids was reachable through the one line meant to be helpful. The mapper
    /// fell back to `@event.GetType().Name` for an unrecognised event, on the reasoning that
    /// appearing under an ugly label beats disappearing. Both halves of that are right except the
    /// label: adding an event and forgetting the switch would have published its class name, and
    /// nothing would have failed.
    ///
    /// Appending a raw event through Marten rather than through `IContentWriter`, because the writer
    /// refuses an event with no `Content.Apply` overload, which is the correct behaviour and is
    /// exactly what makes this case hard to reach on purpose.
    /// </remarks>
    [Fact]
    public async Task An_unmapped_event_is_reported_as_Unknown_rather_than_its_class_name()
    {
        var (token, _) = await TestHelpers.CreateAdminUserAsync(_factory);
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var createResp = await _client.PostAsJsonAsync("/api/contents", new
        {
            ContentType = "Article",
            Data = new Dictionary<string, object> { { "Title", "unmapped" } },
        });
        createResp.EnsureSuccessStatusCode();
        var id = (await createResp.Content.ReadFromJsonAsync<barakoCMS.Features.Content.Create.Response>(ApiJson.Options))!.Id;

        using (var scope = _factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Events.Append(id, new UnmappedProbeEvent(id, "should not reach the wire"));
            await session.SaveChangesAsync();
        }

        var historyResp = await _client.GetAsync($"/api/contents/{id}/history");
        historyResp.EnsureSuccessStatusCode();
        var raw = await historyResp.Content.ReadAsStringAsync();

        raw.Should().NotContain("UnmappedProbeEvent",
            "an event this mapper does not know must not put its CLR type name on the wire, which is "
          + "the leak #229 forbids and the one EventSurfaceTests cannot see");

        using var document = JsonDocument.Parse(raw);
        var types = document.RootElement.GetProperty("items").EnumerateArray()
            .Select(e => e.GetProperty("changeType").GetString())
            .ToList();

        types.Should().Contain("Unknown",
            "the entry still appears, because the count of entries has to keep matching the count of "
          + "events in the stream");
        types.Should().Contain("Created", "and the events the mapper does know are unaffected");
    }

    /// <summary>An event type the history mapper has never heard of. Test-only.</summary>
    private sealed record UnmappedProbeEvent(Guid Id, string Note);
}
