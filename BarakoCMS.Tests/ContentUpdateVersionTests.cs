using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Marten;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using barakoCMS.Core.Interfaces;
using barakoCMS.Models;

namespace BarakoCMS.Tests;

/// <summary>Holds the one stream a test wants advanced behind the endpoint's back.</summary>
internal sealed class StreamAdvancer
{
    private Guid? _target;

    public void Arm(Guid streamId)
    {
        lock (this) { _target = streamId; }
    }

    /// <summary>True once, for the armed stream. Arming is spent on the first claim.</summary>
    public bool Claim(Guid streamId)
    {
        lock (this)
        {
            if (_target != streamId) return false;
            _target = null;
            return true;
        }
    }
}

/// <summary>
/// Commits an unrelated event to the stream in the window the update endpoint cannot see.
/// </summary>
/// <remarks>
/// The endpoint reads the stream state, then appends. Another writer landing between those two
/// points is the race the version report has to survive, and nothing in a plain HTTP test can be
/// timed into that window. Decorating the writer puts a test exactly there, deterministically.
/// </remarks>
internal sealed class StreamAdvancingContentWriter : IContentWriter
{
    private readonly IContentWriter _inner;
    private readonly IDocumentStore _store;
    private readonly StreamAdvancer _advancer;

    public StreamAdvancingContentWriter(IContentWriter inner, IDocumentStore store, StreamAdvancer advancer)
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

    public Task AppendAsync(Content content, object @event, CancellationToken ct)
        => _inner.AppendAsync(content, @event, ct);

    // AppendAsync(content, events, expectedVersion, ct) is deliberately NOT forwarded. Its default
    // implementation calls AppendOptimisticAsync on this decorator, which is the seam the advancer
    // below needs; forwarding it to the inner writer would take the interception out of the path and
    // the test would go green without ever racing anything.

    public async Task AppendOptimisticAsync(Content content, IReadOnlyList<object> events, CancellationToken ct)
    {
        if (_advancer.Claim(content.Id))
        {
            await using var other = _store.LightweightSession();
            other.Events.Append(content.Id, new barakoCMS.Events.ContentUpdated(
                content.Id, content.Data, Guid.Empty, string.Empty));
            await other.SaveChangesAsync(ct);
        }

        await _inner.AppendOptimisticAsync(content, events, ct);
    }
}

/// <summary>
/// The version an update reports has to be the version the stream is actually at.
/// </summary>
/// <remarks>
/// It was computed as "the state read before the append, plus the number of events appended". When
/// <c>req.Version</c> is 0 the staleness check is deliberately bypassed, so another writer can
/// advance the stream between that read and the append and the sum then under-reports. The client
/// echoes the reported version into its next update, which is checked against the real one, so an
/// under-report turns into a 412 telling the user someone else edited the content when nobody did.
/// </remarks>
[Collection("Sequential")]
public class ContentUpdateVersionTests
{
    private readonly IntegrationTestFixture _factory;
    private readonly HttpClient _client;
    private readonly IServiceProvider _services;

    public ContentUpdateVersionTests(IntegrationTestFixture factory)
    {
        _factory = factory;
        var derived = factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.AddSingleton<StreamAdvancer>();
                services.AddScoped<IContentWriter>(sp => new StreamAdvancingContentWriter(
                    new barakoCMS.Infrastructure.Services.ContentWriter(
                        sp.GetRequiredService<IDocumentSession>(),
                        sp.GetRequiredService<barakoCMS.Core.Interfaces.IContentSourcingPolicy>()),
                    sp.GetRequiredService<IDocumentStore>(),
                    sp.GetRequiredService<StreamAdvancer>()));
            }));

        _services = derived.Services;
        _client = derived.CreateClient();
    }

    [Fact]
    public async Task The_reported_version_matches_the_stream_after_a_concurrent_append()
    {
        await AuthenticateAsync();
        var id = await CreateContentAsync();

        _services.GetRequiredService<StreamAdvancer>().Arm(id);

        // Version 0 is the documented bypass of the staleness check, which is the only way to reach
        // the window the arming opens.
        var response = await _client.PutAsJsonAsync($"/api/contents/{id}", new
        {
            id,
            data = new Dictionary<string, object> { ["Title"] = "edited under a concurrent append" },
            version = 0,
        }, TestContext.Current.CancellationToken);

        response.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}",
            response.StatusCode, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var reported = document.RootElement.GetProperty("version").GetInt64();

        long actual;
        using (var scope = _services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            var state = await session.Events.FetchStreamStateAsync(id, TestContext.Current.CancellationToken);
            actual = state!.Version;
        }

        reported.Should().Be(actual,
            "the client echoes this back as its expected version, so under-reporting it turns the next "
            + "ordinary edit into a 412 blaming a conflict that never happened");
    }

    /// <summary>An update with nothing racing it still reports the version it always did.</summary>
    [Fact]
    public async Task The_reported_version_is_unchanged_when_nothing_races_the_append()
    {
        await AuthenticateAsync();
        var id = await CreateContentAsync();

        var response = await _client.PutAsJsonAsync($"/api/contents/{id}", new
        {
            id,
            data = new Dictionary<string, object> { ["Title"] = "an ordinary edit" },
            version = 1,
        }, TestContext.Current.CancellationToken);

        response.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}",
            response.StatusCode, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        document.RootElement.GetProperty("version").GetInt64().Should().Be(2,
            "one event on a stream that was at 1");
    }

    private async Task<Guid> CreateContentAsync()
    {
        var typeName = "version-report-" + Guid.NewGuid().ToString("n")[..8];
        var typeResponse = await _client.PostAsJsonAsync("/api/content-types", new
        {
            name = typeName,
            displayName = "Version Report",
            fields = new[] { new { name = "Title", type = "Text" } },
        }, TestContext.Current.CancellationToken);
        typeResponse.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}",
            typeResponse.StatusCode, await typeResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        var created = await _client.PostAsJsonAsync("/api/contents", new
        {
            contentType = typeName,
            data = new Dictionary<string, object> { ["Title"] = "before the edit" },
            status = "Published",
        }, TestContext.Current.CancellationToken);
        created.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}",
            created.StatusCode, await created.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        using var document = JsonDocument.Parse(await created.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return document.RootElement.GetProperty("id").GetGuid();
    }

    private async Task AuthenticateAsync()
    {
        string[] roles = ["SuperAdmin", "Admin"];

        using var scope = _services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();

        var roleIds = new List<Guid>();
        foreach (var roleName in roles)
        {
            var role = await session.Query<Role>().FirstOrDefaultAsync(r => r.Name == roleName, TestContext.Current.CancellationToken);
            if (role is null)
            {
                role = new Role { Id = Guid.NewGuid(), Name = roleName };
                session.Store(role);
            }

            roleIds.Add(role.Id);
        }

        var userId = Guid.NewGuid();
        session.Store(new User
        {
            Id = userId,
            Username = $"version-report-{userId:n}",
            Email = $"version-report-{userId:n}@example.com",
            RoleIds = roleIds,
        });
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);

        _client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer", _factory.CreateToken(roles: roles, userId: userId.ToString()));
    }
}
