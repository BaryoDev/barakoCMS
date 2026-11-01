using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using barakoCMS.Core.Interfaces;
using barakoCMS.Models;

namespace BarakoCMS.Tests;

/// <summary>
/// Commits another writer's edit to the entry's document row in the window the rollback endpoint
/// cannot see, on every append overload including the single-event one rollback used to call.
/// </summary>
/// <remarks>
/// Same shape as <c>DocumentAdvancingContentWriter</c> in ContentConcurrencyTests.cs, which does not
/// advance on the single-event append. The stream is not touched: a real concurrent edit goes
/// through the writer and stores the document, so the document version is what moves.
/// </remarks>
internal sealed class SingleAppendAdvancingContentWriter : IContentWriter
{
    private readonly IContentWriter _inner;
    private readonly IDocumentStore _store;
    private readonly StreamAdvancer _advancer;

    public SingleAppendAdvancingContentWriter(IContentWriter inner, IDocumentStore store, StreamAdvancer advancer)
    {
        _inner = inner;
        _store = store;
        _advancer = advancer;
    }

#pragma warning disable CS0618 // the interface still declares them until 5.0, so a decorator still has to
    public Content Create(barakoCMS.Events.ContentCreated @event) => _inner.Create(@event);

    public void Append(Content content, object @event) => _inner.Append(content, @event);
#pragma warning restore CS0618

    public Task<Content> CreateAsync(barakoCMS.Events.ContentCreated @event, CancellationToken ct)
        => _inner.CreateAsync(@event, ct);

    public async Task AppendAsync(Content content, object @event, CancellationToken ct)
    {
        await AdvanceIfArmedAsync(content, ct);
        await _inner.AppendAsync(content, @event, ct);
    }

    public async Task AppendAsync(
        Content content, IReadOnlyList<object> events, long? expectedVersion, CancellationToken ct)
    {
        await AdvanceIfArmedAsync(content, ct);
        await _inner.AppendAsync(content, events, expectedVersion, ct);
    }

    public async Task AppendOptimisticAsync(Content content, IReadOnlyList<object> events, CancellationToken ct)
    {
        await AdvanceIfArmedAsync(content, ct);
        await _inner.AppendOptimisticAsync(content, events, ct);
    }

    private async Task AdvanceIfArmedAsync(Content content, CancellationToken ct)
    {
        if (!_advancer.Claim(content.Id)) return;

        await using var other = _store.LightweightSession();
        var theirs = await other.LoadAsync<Content>(content.Id, ct);
        theirs!.Data["Title"] = "changed by another writer mid-request";
        other.Store(theirs);
        await other.SaveChangesAsync(ct);
    }
}

/// <summary>
/// A manual status change takes the entry out of the schedule it was armed with, update's status
/// field follows the lifecycle rule change-status already applies, and a rollback refuses to
/// overwrite an entry that changed after it was read.
/// </summary>
[Collection("Integration1")]
public class StatusChangeGuardTests
{
    private readonly IntegrationTestFixture _factory;

    public StatusChangeGuardTests(IntegrationTestFixture factory) => _factory = factory;

    private async Task<HttpClient> AdminClientAsync(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program>? host = null)
    {
        var (token, _) = await TestHelpers.CreateAdminUserAsync(_factory);
        var client = (host ?? _factory).CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static async Task<Guid> CreateEntryAsync(HttpClient client, string type = "Article")
    {
        var res = await client.PostAsJsonAsync("/api/contents", new
        {
            contentType = type,
            data = new Dictionary<string, object> { ["Title"] = "first" },
        }, TestContext.Current.CancellationToken);
        res.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}",
            res.StatusCode, await res.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        using var doc = System.Text.Json.JsonDocument.Parse(
            await res.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return doc.RootElement.GetProperty("id").GetGuid();
    }

    private static async Task ScheduleAsync(HttpClient client, Guid id)
    {
        var res = await client.PutAsJsonAsync($"/api/contents/{id}/schedule", new
        {
            id,
            scheduledPublishAt = DateTime.UtcNow.AddDays(2),
        }, TestContext.Current.CancellationToken);
        res.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}",
            res.StatusCode, await res.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    private async Task<Content> LoadAsync(Guid id)
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        return (await session.LoadAsync<Content>(id, TestContext.Current.CancellationToken))!;
    }

    [Fact]
    public async Task Changing_status_away_from_Scheduled_clears_the_publish_time()
    {
        var client = await AdminClientAsync();
        var id = await CreateEntryAsync(client);
        await ScheduleAsync(client, id);
        (await LoadAsync(id)).Status.Should().Be(ContentStatus.Scheduled);

        var res = await client.PutAsJsonAsync($"/api/contents/{id}/status",
            new { id, newStatus = "Archived" }, TestContext.Current.CancellationToken);

        res.IsSuccessStatusCode.Should().BeTrue("got {0}", res.StatusCode);
        var after = await LoadAsync(id);
        after.Status.Should().Be(ContentStatus.Archived);
        after.ScheduledPublishAt.Should().BeNull("an entry moved by hand must not be published later by the old schedule");
    }

    [Fact]
    public async Task Updating_status_away_from_Scheduled_clears_the_publish_time()
    {
        var client = await AdminClientAsync();
        var id = await CreateEntryAsync(client);
        await ScheduleAsync(client, id);
        (await LoadAsync(id)).Status.Should().Be(ContentStatus.Scheduled);

        var res = await client.PutAsJsonAsync($"/api/contents/{id}", new
        {
            id,
            data = new Dictionary<string, object> { ["Title"] = "second" },
            status = "Archived",
        }, TestContext.Current.CancellationToken);

        res.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}",
            res.StatusCode, await res.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var after = await LoadAsync(id);
        after.Status.Should().Be(ContentStatus.Archived);
        after.ScheduledPublishAt.Should().BeNull("an entry moved by hand must not be published later by the old schedule");
    }

    [Fact]
    public async Task Updating_status_on_a_type_with_a_lifecycle_is_refused()
    {
        var client = await AdminClientAsync();
        var name = "sgt" + Guid.NewGuid().ToString("n")[..8];
        var typeRes = await client.PostAsJsonAsync("/api/content-types", new
        {
            name,
            displayName = "Invoice",
            fields = new[] { new { name = "Title", type = "string" } },
            lifecycle = new LifecycleDefinition
            {
                States = ["Draft", "Submitted"],
                InitialState = "Draft",
                Transitions = [new StateTransition { Name = "Submit", From = "Draft", To = "Submitted" }],
            },
        }, TestContext.Current.CancellationToken);
        typeRes.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}",
            typeRes.StatusCode, await typeRes.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var id = await CreateEntryAsync(client, name);
        var before = (await LoadAsync(id)).Status;

        var res = await client.PutAsJsonAsync($"/api/contents/{id}", new
        {
            id,
            data = new Dictionary<string, object> { ["Title"] = "second" },
            status = "Published",
        }, TestContext.Current.CancellationToken);

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await LoadAsync(id)).Status.Should().Be(before, "the status field must not step around the named transitions");
    }

    [Fact]
    public async Task Updating_with_the_stored_status_on_a_type_with_a_lifecycle_still_succeeds()
    {
        var client = await AdminClientAsync();
        var name = "sgt" + Guid.NewGuid().ToString("n")[..8];
        var typeRes = await client.PostAsJsonAsync("/api/content-types", new
        {
            name,
            displayName = "Invoice",
            fields = new[] { new { name = "Title", type = "string" } },
            lifecycle = new LifecycleDefinition
            {
                States = ["Draft", "Submitted"],
                InitialState = "Draft",
                Transitions = [new StateTransition { Name = "Submit", From = "Draft", To = "Submitted" }],
            },
        }, TestContext.Current.CancellationToken);
        typeRes.IsSuccessStatusCode.Should().BeTrue("got {0}", typeRes.StatusCode);
        var id = await CreateEntryAsync(client, name);
        var stored = (await LoadAsync(id)).Status;

        var res = await client.PutAsJsonAsync($"/api/contents/{id}", new
        {
            id,
            data = new Dictionary<string, object> { ["Title"] = "second" },
            status = stored.ToString(),
        }, TestContext.Current.CancellationToken);

        res.IsSuccessStatusCode.Should().BeTrue("a client echoing the stored status changes nothing, got {0}: {1}",
            res.StatusCode, await res.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_rollback_that_loses_a_race_with_another_writer_answers_409()
    {
        var advancer = new StreamAdvancer();
        var derived = _factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.AddSingleton(advancer);
            services.AddScoped<IContentWriter>(sp => new SingleAppendAdvancingContentWriter(
                new barakoCMS.Infrastructure.Services.ContentWriter(
                    sp.GetRequiredService<IDocumentSession>(),
                    sp.GetRequiredService<IContentSourcingPolicy>()),
                sp.GetRequiredService<IDocumentStore>(),
                advancer));
        }));
        var client = await AdminClientAsync(derived);
        var id = await CreateEntryAsync(client);

        var edit = await client.PutAsJsonAsync($"/api/contents/{id}", new
        {
            id,
            data = new Dictionary<string, object> { ["Title"] = "second" },
            version = 1,
        }, TestContext.Current.CancellationToken);
        edit.EnsureSuccessStatusCode();

        var history = await client.GetFromJsonAsync<System.Text.Json.JsonElement>(
            $"/api/contents/{id}/history", TestContext.Current.CancellationToken);
        var versions = history.GetProperty("items").EnumerateArray().ToList();
        versions.Should().NotBeEmpty();
        var first = versions.Single(v => v.GetProperty("data").GetProperty("Title").GetString() == "first")
            .GetProperty("versionId").GetGuid();

        advancer.Arm(id);
        var res = await client.PostAsJsonAsync($"/api/contents/{id}/rollback/{first}", new { },
            TestContext.Current.CancellationToken);

        res.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }
}
