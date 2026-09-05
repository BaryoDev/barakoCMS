using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using barakoCMS.Core.Interfaces;
using barakoCMS.Models;

namespace BarakoCMS.Tests.Features.ContentApi;

/// <summary>Holds the one content id a test wants written out from under the endpoint.</summary>
/// <remarks>Same shape as <c>StreamAdvancer</c> in ContentUpdateVersionTests.cs.</remarks>
internal sealed class DocumentVersionAdvancer
{
    private Guid? _target;

    public void Arm(Guid contentId) { lock (this) { _target = contentId; } }

    public bool Claim(Guid contentId)
    {
        lock (this)
        {
            if (_target != contentId) return false;
            _target = null;
            return true;
        }
    }
}

/// <summary>
/// Commits a full document write to the armed content id inside the window the Update endpoint
/// cannot see: after it has read the document's current Marten version, before its own commit.
/// </summary>
/// <remarks>
/// Mirrors <c>StreamAdvancingContentWriter</c> (ContentUpdateVersionTests.cs), which forces the same
/// interleaving for the event-stream version. This one has to change the document's own Marten
/// version (its <c>mt_version</c> column) rather than the stream, because that is what #565's new
/// check compares against, and it bypasses IContentWriter entirely (a plain Store) so the write does
/// not also need a content type, a sourcing policy or a validator to go through.
/// </remarks>
internal sealed class DocumentAdvancingContentWriter : IContentWriter
{
    private readonly IContentWriter _inner;
    private readonly IDocumentStore _store;
    private readonly DocumentVersionAdvancer _advancer;

    public DocumentAdvancingContentWriter(IContentWriter inner, IDocumentStore store, DocumentVersionAdvancer advancer)
    {
        _inner = inner;
        _store = store;
        _advancer = advancer;
    }

#pragma warning disable CS0618 // the interface still declares these until 5.0, so a decorator still has to
    public Content Create(barakoCMS.Events.ContentCreated @event) => _inner.Create(@event);

    public void Append(Content content, object @event) => _inner.Append(content, @event);
#pragma warning restore CS0618

    public Task<Content> CreateAsync(barakoCMS.Events.ContentCreated @event, CancellationToken ct)
        => _inner.CreateAsync(@event, ct);

    public Task AppendAsync(Content content, object @event, CancellationToken ct)
        => _inner.AppendAsync(content, @event, ct);

    public async Task AppendAsync(Content content, IReadOnlyList<object> events, long? expectedVersion, CancellationToken ct)
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
/// #565 / DECISIONS.md D16: <c>Content</c> gets Marten's own optimistic concurrency (document
/// version), exposed as a standard <c>ETag</c>/<c>If-Match</c> pair alongside the expected-version
/// check event-sourced types already have on their stream (D3). <c>Content:Concurrency:Require</c>
/// decides only what happens to a write that sends neither.
/// </summary>
[Collection("Sequential")]
public class ContentConcurrencyTests
{
    private readonly IntegrationTestFixture _factory;

    public ContentConcurrencyTests(IntegrationTestFixture factory) => _factory = factory;

    private static string NewTypeName() => "concurrency-" + Guid.NewGuid().ToString("n")[..10];

    private async Task<HttpClient> AuthenticatedClientAsync()
    {
        var (token, _) = await TestHelpers.CreateAdminUserAsync(_factory);
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static async Task<Guid> CreateContentAsync(HttpClient client, string title = "original")
    {
        var createResp = await client.PostAsJsonAsync("/api/contents", new
        {
            contentType = NewTypeName(),
            data = new Dictionary<string, object> { ["Title"] = title },
        }, TestContext.Current.CancellationToken);
        createResp.EnsureSuccessStatusCode();
        var created = await createResp.Content.ReadFromJsonAsync<barakoCMS.Features.Content.Create.Response>(
            cancellationToken: TestContext.Current.CancellationToken);
        return created!.Id;
    }

    private static async Task<EntityTagHeaderValue> GetETagAsync(HttpClient client, Guid id)
    {
        var response = await client.GetAsync($"/api/contents/{id}", TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        response.Headers.ETag.Should().NotBeNull("GET has to return an ETag for #565's If-Match to have anything to check");
        return response.Headers.ETag!;
    }

    [Fact]
    public async Task Get_returns_an_ETag_that_a_matching_If_Match_PUT_accepts()
    {
        var client = await AuthenticatedClientAsync();
        var id = await CreateContentAsync(client);

        var etag = await GetETagAsync(client, id);

        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/contents/{id}")
        {
            Content = JsonContent.Create(new
            {
                id,
                data = new Dictionary<string, object> { ["Title"] = "edited with a matching If-Match" },
            }),
        };
        request.Headers.IfMatch.Add(etag);

        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}",
            response.StatusCode, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_stale_If_Match_is_refused_with_412()
    {
        var client = await AuthenticatedClientAsync();
        var id = await CreateContentAsync(client);

        var staleETag = await GetETagAsync(client, id);

        // Someone else edits (no If-Match at all, the default bypass), which moves the document's
        // version on from what the first GET saw.
        var unrelatedEdit = await client.PutAsJsonAsync($"/api/contents/{id}", new
        {
            id,
            data = new Dictionary<string, object> { ["Title"] = "edited by someone else first" },
        }, TestContext.Current.CancellationToken);
        unrelatedEdit.EnsureSuccessStatusCode();

        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/contents/{id}")
        {
            Content = JsonContent.Create(new
            {
                id,
                data = new Dictionary<string, object> { ["Title"] = "edited against the now-stale read" },
            }),
        };
        request.Headers.IfMatch.Add(staleETag);

        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.PreconditionFailed);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        body.Should().Contain("modified by another user");
    }

    /// <summary>
    /// The race D16's "turn on optimistic concurrency" bullet is for: two writers with no client-sent
    /// version at all, overlapping inside a single request's own commit window. Forced with the same
    /// decorator technique ContentUpdateVersionTests.cs uses for the event-stream case, because
    /// nothing in a plain HTTP test can be timed into a window this narrow.
    /// </summary>
    [Fact]
    public async Task Two_racing_updates_with_no_version_sent_one_succeeds_one_is_refused()
    {
        var advancer = new DocumentVersionAdvancer();
        var derived = _factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.AddSingleton(advancer);
            services.AddScoped<IContentWriter>(sp => new DocumentAdvancingContentWriter(
                new barakoCMS.Infrastructure.Services.ContentWriter(
                    sp.GetRequiredService<IDocumentSession>(),
                    sp.GetRequiredService<IContentSourcingPolicy>()),
                sp.GetRequiredService<IDocumentStore>(),
                advancer));
        }));

        var (token, _) = await TestHelpers.CreateAdminUserAsync(_factory);
        var client = derived.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var id = await CreateContentAsync(client);
        advancer.Arm(id);

        // No If-Match, no version in the body: the last-write-wins path every 3.x client takes.
        var response = await client.PutAsJsonAsync($"/api/contents/{id}", new
        {
            id,
            data = new Dictionary<string, object> { ["Title"] = "this request's own edit" },
        }, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.PreconditionFailed,
            "the document moved out from under this request's own read, which is a real race and not "
            + "merely a client that forgot to send a version");
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        body.Should().Contain("modified by another user");

        // The other writer's commit is the one that actually landed.
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var stored = await session.LoadAsync<Content>(id, TestContext.Current.CancellationToken);
        stored!.Data["Title"].ToString().Should().Be("changed by another writer mid-request");
    }

    [Fact]
    public async Task With_Require_off_a_write_with_no_version_still_succeeds()
    {
        var client = await AuthenticatedClientAsync();
        var id = await CreateContentAsync(client);

        var response = await client.PutAsJsonAsync($"/api/contents/{id}", new
        {
            id,
            data = new Dictionary<string, object> { ["Title"] = "no version, default settings" },
        }, TestContext.Current.CancellationToken);

        response.IsSuccessStatusCode.Should().BeTrue("Content:Concurrency:Require defaults to false in 4.x, "
            + "which is the 3.x upgrade path this flag exists to protect");
    }

    [Fact]
    public async Task With_Require_on_a_write_with_no_version_is_refused()
    {
        var derived = _factory.WithSetting("Content:Concurrency:Require", "true");
        var (token, _) = await TestHelpers.CreateAdminUserAsync(_factory);
        var client = derived.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var id = await CreateContentAsync(client);

        var response = await client.PutAsJsonAsync($"/api/contents/{id}", new
        {
            id,
            data = new Dictionary<string, object> { ["Title"] = "no version, Require is on" },
        }, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be((HttpStatusCode)428, "Precondition Required: this caller has to "
            + "GET the entry and send its ETag back before it may write");
    }

    /// <summary>Paired with the test above: the flag governs absence, not the feature.</summary>
    [Fact]
    public async Task With_Require_on_a_matching_If_Match_still_succeeds()
    {
        var derived = _factory.WithSetting("Content:Concurrency:Require", "true");
        var (token, _) = await TestHelpers.CreateAdminUserAsync(_factory);
        var client = derived.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var id = await CreateContentAsync(client);
        var etag = await GetETagAsync(client, id);

        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/contents/{id}")
        {
            Content = JsonContent.Create(new
            {
                id,
                data = new Dictionary<string, object> { ["Title"] = "Require is on but If-Match matches" },
            }),
        };
        request.Headers.IfMatch.Add(etag);

        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}",
            response.StatusCode, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }
}
