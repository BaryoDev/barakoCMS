using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using barakoCMS.Infrastructure.Sync;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BarakoCMS.Tests.Features.Collections;

/// <summary>
/// Collections filled from an outside source on a schedule (#794).
/// </summary>
/// <remarks>
/// The two done-when cases from the issue are here by name: a collection mapped to the NuGet search
/// API shows each package and its downloads, and a failed fetch leaves the previous entries alone
/// while recording the error where an operator reads it. The rest are the properties that make those
/// two true more than once: the stable key, the floor, and not rewriting what has not changed.
///
/// The outbound half is stubbed by replacing the primary handler of the named ExternalApi client, so
/// everything above the socket is the shipped code: the request definition, the composer, the
/// connector's credentials and the fetcher.
/// </remarks>
[Collection("Sequential")]
public partial class CollectionSyncTests
{
    private readonly IntegrationTestFixture _factory;

    public CollectionSyncTests(IntegrationTestFixture factory) => _factory = factory;

    /// <summary>What the stub answers, keyed by the path the request asks for.</summary>
    private static readonly ConcurrentDictionary<string, Func<(HttpStatusCode Status, string Body)>> Routes = new();

    /// <summary>A Link header the stub adds for a path, as a paged API sends one.</summary>
    private static readonly ConcurrentDictionary<string, string> LinkHeaders = new();

    private sealed class SourceStub : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath.Trim('/');

            if (!Routes.TryGetValue(path, out var answer))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
                {
                    Content = new StringContent("{}", Encoding.UTF8, "application/json"),
                });
            }

            var (status, body) = answer();

            var response = new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };

            if (LinkHeaders.TryGetValue(path, out var link)) response.Headers.TryAddWithoutValidation("Link", link);

            return Task.FromResult(response);
        }
    }

    private static readonly Lock HostLock = new();
    private static WebApplicationFactory<Program>? _host;

    /// <summary>
    /// One derived host for the whole class, because every host a test builds stays alive for the
    /// rest of the run. Tests keep out of each other's way through unique route paths instead.
    /// </summary>
    private WebApplicationFactory<Program> Host
    {
        get
        {
            lock (HostLock)
            {
                return _host ??= _factory.WithWebHostBuilder(b => b.ConfigureServices(services =>
                    services.AddHttpClient("ExternalApi").ConfigurePrimaryHttpMessageHandler(() => new SourceStub())));
            }
        }
    }

    private const string TwoPackages = """
    {
      "totalHits": 2,
      "data": [
        { "id": "BarakoCMS",       "totalDownloads": 1200, "description": "Headless CMS" },
        { "id": "BarakoCMS.Files", "totalDownloads": 310,  "description": "File storage" }
      ]
    }
    """;

    /// <summary>
    /// The issue's first done-when: the NuGet search shape becomes one entry per package, with its
    /// download count.
    /// </summary>
    [Fact]
    public async Task A_collection_mapped_to_a_nuget_search_shows_each_package_and_its_downloads()
    {
        var setup = await ArrangeAsync(() => (HttpStatusCode.OK, TwoPackages));

        var outcome = await RunAsync(setup);

        outcome.GetProperty("succeeded").GetBoolean().Should().BeTrue(
            "got: {0}", outcome.TryGetProperty("error", out var e) ? e.ToString() : "no error");
        outcome.GetProperty("created").GetInt32().Should().Be(2);

        var entries = await EntriesAsync(setup.Type);
        entries.Should().HaveCount(2, "the assertions below have nothing to run on otherwise");

        var cms = entries.Single(c => Value(c, "packageId") == "BarakoCMS");
        Value(cms, "downloads").Should().Be("1200");
        Value(cms, "summary").Should().Be("Headless CMS");
        cms.Status.Should().Be(ContentStatus.Published, "a collection filled from outside exists to be served");
    }

    /// <summary>
    /// The issue's second done-when: a failed fetch keeps the previous entries and shows the error.
    /// </summary>
    /// <remarks>
    /// The failure is asserted from two sides. The entries still hold the values of the last good
    /// run, and the sync itself reports the error and a <c>lastSuccessAt</c> that did not move, which
    /// is what tells an operator the page is real but stale rather than empty.
    /// </remarks>
    [Fact]
    public async Task A_failed_fetch_keeps_the_last_good_entries_and_records_the_failure()
    {
        var answer = TwoPackages;
        var status = HttpStatusCode.OK;
        var setup = await ArrangeAsync(() => (status, answer));

        (await RunAsync(setup)).GetProperty("created").GetInt32().Should().Be(2, "the control: the good run happened");
        var good = await GetSyncAsync(setup);
        var lastSuccess = good.GetProperty("lastSuccessAt").GetString();

        status = HttpStatusCode.Forbidden;
        answer = "{}";

        var outcome = await RunAsync(setup);

        outcome.GetProperty("succeeded").GetBoolean().Should().BeFalse();
        outcome.GetProperty("error").GetString().Should().Contain("403");

        var entries = await EntriesAsync(setup.Type);
        entries.Should().HaveCount(2, "a failed sync does not empty the collection");
        Value(entries.Single(c => Value(c, "packageId") == "BarakoCMS"), "downloads").Should().Be("1200");

        var after = await GetSyncAsync(setup);
        after.GetProperty("lastError").GetString().Should().Contain("403");
        after.GetProperty("consecutiveFailures").GetInt32().Should().Be(1);
        after.GetProperty("lastSuccessAt").GetString().Should().Be(lastSuccess,
            "the last good run is what says how stale the entries are");
        after.GetProperty("lastEntryCount").GetInt32().Should().Be(2);
    }

    /// <summary>
    /// The stable key: a second run updates the entries rather than adding another set.
    /// </summary>
    /// <remarks>
    /// The second response changes a value as well as repeating the keys, so an implementation that
    /// created fresh entries every time fails on the count, and one that never wrote again fails on
    /// the value. Either alone would pass against half a bug.
    /// </remarks>
    [Fact]
    public async Task A_second_run_updates_the_entries_the_key_already_names()
    {
        var answer = TwoPackages;
        var setup = await ArrangeAsync(() => (HttpStatusCode.OK, answer));

        (await RunAsync(setup)).GetProperty("created").GetInt32().Should().Be(2);

        answer = """
        {
          "data": [
            { "id": "BarakoCMS",       "totalDownloads": 1500, "description": "Headless CMS" },
            { "id": "BarakoCMS.Files", "totalDownloads": 310,  "description": "File storage" }
          ]
        }
        """;

        var second = await RunAsync(setup);

        second.GetProperty("created").GetInt32().Should().Be(0, "the key already names both entries");
        second.GetProperty("updated").GetInt32().Should().Be(1);
        second.GetProperty("unchanged").GetInt32().Should().Be(1);

        var entries = await EntriesAsync(setup.Type);
        entries.Should().HaveCount(2, "a re-sync updates rather than duplicating");
        Value(entries.Single(c => Value(c, "packageId") == "BarakoCMS"), "downloads").Should().Be("1500");
    }

    /// <summary>
    /// A floored field keeps the greater of the stored and the fetched value; an unfloored one does
    /// not.
    /// </summary>
    /// <remarks>
    /// Both halves in one test on purpose. A floor that applied to every field would look correct
    /// against the download count alone, and it would freeze every other value in the collection at
    /// its high-water mark. The description moving proves the floor is the named field's and not the
    /// whole entry's.
    /// </remarks>
    [Fact]
    public async Task A_floored_counter_does_not_walk_backwards_while_an_unfloored_field_still_moves()
    {
        var answer = TwoPackages;
        var setup = await ArrangeAsync(() => (HttpStatusCode.OK, answer), floorDownloads: true);

        (await RunAsync(setup)).GetProperty("created").GetInt32().Should().Be(2);

        // The lagging index: a smaller count than an hour ago, and a genuinely edited description.
        answer = """
        {
          "data": [
            { "id": "BarakoCMS",       "totalDownloads": 900, "description": "Headless CMS for .NET" },
            { "id": "BarakoCMS.Files", "totalDownloads": 310, "description": "File storage" }
          ]
        }
        """;

        await RunAsync(setup);

        var cms = (await EntriesAsync(setup.Type)).Single(c => Value(c, "packageId") == "BarakoCMS");

        Value(cms, "downloads").Should().Be("1200",
            "a lagging registry index must not walk a download count backwards");
        Value(cms, "summary").Should().Be("Headless CMS for .NET",
            "the floor is on the counter, not on the entry");
    }

    /// <summary>
    /// A response that says the same thing writes nothing at all.
    /// </summary>
    /// <remarks>
    /// Not a micro-optimisation. Without it every tick appends a ContentUpdated to every entry's
    /// stream, fires every Updated workflow, and moves the whole collection to the top of any
    /// recently-updated list once an hour, forever.
    /// </remarks>
    [Fact]
    public async Task A_response_that_has_not_changed_writes_nothing()
    {
        var setup = await ArrangeAsync(() => (HttpStatusCode.OK, TwoPackages));

        (await RunAsync(setup)).GetProperty("created").GetInt32().Should().Be(2);
        var before = (await EntriesAsync(setup.Type)).ToDictionary(c => c.Id, c => c.UpdatedAt);
        before.Should().HaveCount(2);

        var second = await RunAsync(setup);

        second.GetProperty("updated").GetInt32().Should().Be(0);
        second.GetProperty("unchanged").GetInt32().Should().Be(2);

        foreach (var entry in await EntriesAsync(setup.Type))
        {
            entry.UpdatedAt.Should().Be(before[entry.Id], "nothing about the entry changed, so nothing was written");
        }
    }

    /// <summary>
    /// A synced entry is ordinary content, so the public delivery API serves it like any other.
    /// </summary>
    [Fact]
    public async Task A_synced_entry_is_served_by_the_delivery_api_like_any_other()
    {
        var setup = await ArrangeAsync(() => (HttpStatusCode.OK, TwoPackages), publiclyDeliverable: true);

        (await RunAsync(setup)).GetProperty("created").GetInt32().Should().Be(2);

        var response = await _factory.CreateClient().GetAsync(
            $"/api/public/{setup.Type}", TestContext.Current.CancellationToken);

        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK, "got {0}", body);
        body.Should().Contain("BarakoCMS.Files", "blocks and delivery treat a synced entry like any other");
        body.Should().Contain("1200", "and the mapped counter is part of the entry");
    }

    /// <summary>
    /// The sweep runs a sync when it is due and leaves it alone when it is not.
    /// </summary>
    /// <remarks>
    /// Driven through the sweeper rather than the endpoint, because the schedule is the sweeper's
    /// half and the "run now" button deliberately ignores it. The first sweep has to do nothing, or
    /// the second one proves only that a sweep writes.
    /// </remarks>
    [Fact]
    public async Task The_sweep_runs_a_due_sync_and_skips_one_that_is_not_due()
    {
        var setup = await ArrangeAsync(() => (HttpStatusCode.OK, TwoPackages));

        (await RunAsync(setup)).GetProperty("created").GetInt32().Should().Be(2, "the control: one run has happened");

        // Built by the container rather than by hand, because the schedule is off in the test host
        // (see IntegrationTestFixture) and so nothing else here would notice the day one of its
        // dependencies stopped being resolvable from the root provider.
        var sweeper = ActivatorUtilities.CreateInstance<CollectionSyncService>(Host.Services);

        // Asserted on this sync's own LastRunAt rather than on how many syncs the sweep ran, since
        // other tests in this class leave their own syncs in the same partition.
        var before = (await GetSyncAsync(setup)).GetProperty("lastRunAt").GetString();
        before.Should().NotBeNull();

        await sweeper.SweepTenantAsync(null, DateTime.UtcNow, 100, TestContext.Current.CancellationToken);

        (await GetSyncAsync(setup)).GetProperty("lastRunAt").GetString()
            .Should().Be(before, "the interval has not elapsed, so this sync is not due");

        await MoveLastRunBackAsync(setup);
        await sweeper.SweepTenantAsync(null, DateTime.UtcNow, 100, TestContext.Current.CancellationToken);

        var after = await GetSyncAsync(setup);
        after.GetProperty("lastRunAt").GetString().Should().NotBe(before, "the interval has elapsed now");
        after.GetProperty("lastError").ValueKind.Should().Be(JsonValueKind.Null);
    }

    /// <summary>
    /// The schedule is on by omission and off only when a deployment says so.
    /// </summary>
    /// <remarks>
    /// Both spellings, because a test that only covered the explicit false would pass against a
    /// registration that ran whenever the key was absent, which is the opposite default.
    /// </remarks>
    [Fact]
    public void The_schedule_is_on_unless_a_deployment_turns_it_off()
    {
        static Microsoft.Extensions.Configuration.IConfiguration Config(string? value) =>
            new Microsoft.Extensions.Configuration.ConfigurationBuilder()
                .AddInMemoryCollection(value is null
                    ? new Dictionary<string, string?>()
                    : new Dictionary<string, string?> { [CollectionSyncService.EnabledKey] = value })
                .Build();

        CollectionSyncService.IsEnabled(Config(null)).Should().BeTrue();
        CollectionSyncService.IsEnabled(Config("true")).Should().BeTrue();
        CollectionSyncService.IsEnabled(Config("false")).Should().BeFalse();
    }

    /// <summary>
    /// A mapping onto a field the content type does not have is refused when it is saved.
    /// </summary>
    /// <remarks>
    /// At save time rather than at three in the morning. The whole failure mode this guards is a
    /// sync that is configured, looks fine in the list, and quietly writes nothing.
    /// </remarks>
    [Fact]
    public async Task A_mapping_onto_a_field_the_type_does_not_have_is_refused()
    {
        var setup = await ArrangeAsync(() => (HttpStatusCode.OK, TwoPackages), save: false);

        var body = SyncBody(setup);
        body["fieldMap"] = new Dictionary<string, string>
        {
            ["packageId"] = "id",
            ["nosuchfield"] = "totalDownloads",
        };

        var response = await (await AdminAsync()).PostAsJsonAsync(
            "/api/collection-syncs", body, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
            .Should().Contain("nosuchfield");
    }

    /// <summary>
    /// A feed mapping naming something a feed entry does not carry is refused when it is saved.
    /// </summary>
    /// <remarks>
    /// A feed reads into one fixed vocabulary, so "pubDate" matches nothing: RSS and Atom are both
    /// read into "published". Left unchecked the sync would save, run and fill that field with
    /// nothing, which is the silent emptiness the other save-time checks exist to stop.
    /// </remarks>
    [Fact]
    public async Task A_feed_mapping_onto_a_path_a_feed_does_not_carry_is_refused()
    {
        var setup = await ArrangeAsync(() => (HttpStatusCode.OK, TwoPackages), save: false);
        var client = await AdminAsync();

        var body = SyncBody(setup);
        body["source"] = "Feed";
        body["feedUrl"] = "https://rotary.example/feed.xml";
        body["fieldMap"] = new Dictionary<string, string>
        {
            ["packageId"] = "id",
            ["summary"] = "pubDate",
        };
        body["floorFields"] = new List<string>();

        var refused = await client.PostAsJsonAsync("/api/collection-syncs", body, TestContext.Current.CancellationToken);

        refused.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await refused.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
            .Should().Contain("pubDate").And.Contain("published");

        // The control. The same mapping with a path a feed does carry saves, so the check above is
        // refusing the path rather than refusing every feed.
        body["fieldMap"] = new Dictionary<string, string>
        {
            ["packageId"] = "id",
            ["summary"] = "published",
        };

        var accepted = await client.PostAsJsonAsync("/api/collection-syncs", body, TestContext.Current.CancellationToken);

        accepted.StatusCode.Should().Be(HttpStatusCode.OK,
            "got {0}", await accepted.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>A floor on a field that is not numeric is refused, since "greater" has to mean something.</summary>
    [Fact]
    public async Task A_floor_on_a_field_that_is_not_numeric_is_refused()
    {
        var setup = await ArrangeAsync(() => (HttpStatusCode.OK, TwoPackages), save: false);

        var body = SyncBody(setup);
        body["floorFields"] = new List<string> { "summary" };

        var response = await (await AdminAsync()).PostAsJsonAsync(
            "/api/collection-syncs", body, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
            .Should().Contain("summary");
    }

    /// <summary>
    /// Every route is closed to a caller the capability was not granted to, and to no caller at all.
    /// </summary>
    /// <remarks>
    /// Run against the sync one already exists for, so a 403 cannot be a 404 in disguise. The run
    /// route is in the list deliberately: it is the one that makes this instance call a third party.
    /// </remarks>
    [Fact]
    public async Task The_routes_are_closed_without_the_capability()
    {
        var setup = await ArrangeAsync(() => (HttpStatusCode.OK, TwoPackages));

        var anonymous = Host.CreateClient();
        var wrongRole = Host.CreateClient();
        wrongRole.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", await _factory.StoredUserTokenAsync("HR"));

        (await anonymous.GetAsync("/api/collection-syncs", TestContext.Current.CancellationToken))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        (await wrongRole.GetAsync("/api/collection-syncs", TestContext.Current.CancellationToken))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

        (await wrongRole.GetAsync($"/api/collection-syncs/{setup.Slug}", TestContext.Current.CancellationToken))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

        (await wrongRole.PostAsJsonAsync("/api/collection-syncs", SyncBody(setup), TestContext.Current.CancellationToken))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

        (await wrongRole.PostAsync($"/api/collection-syncs/{setup.Slug}/run", null, TestContext.Current.CancellationToken))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

        (await wrongRole.DeleteAsync($"/api/collection-syncs/{setup.Slug}", TestContext.Current.CancellationToken))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

        (await EntriesAsync(setup.Type)).Should().BeEmpty("none of those calls reached anything");
    }

    /// <summary>Deleting a sync leaves the entries it wrote, because they are ordinary content.</summary>
    [Fact]
    public async Task Deleting_a_sync_leaves_the_entries_it_wrote()
    {
        var setup = await ArrangeAsync(() => (HttpStatusCode.OK, TwoPackages));
        (await RunAsync(setup)).GetProperty("created").GetInt32().Should().Be(2);

        var deleted = await (await AdminAsync()).DeleteAsync(
            $"/api/collection-syncs/{setup.Slug}", TestContext.Current.CancellationToken);

        deleted.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await EntriesAsync(setup.Type)).Should().HaveCount(2,
            "somebody may be linking to them, and deleting a schedule is not a decision to delete published pages");
    }

    private sealed record Setup(string Type, string Slug, string Route);

    /// <summary>
    /// Seeds a content type, a connector, a request definition pointed at the stub, and the sync.
    /// </summary>
    private async Task<Setup> ArrangeAsync(
        Func<(HttpStatusCode, string)> answer,
        bool floorDownloads = false,
        bool publiclyDeliverable = false,
        bool save = true,
        List<FieldDefinition>? fields = null)
    {
        var suffix = Guid.NewGuid().ToString("n")[..10];
        var type = "pkg" + suffix;
        var route = "search" + suffix;

        Routes[route] = answer;

        using (var scope = _factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Store(new ContentTypeDefinition
            {
                Id = Guid.NewGuid(),
                Name = type,
                DisplayName = "Package",
                IsPubliclyDeliverable = publiclyDeliverable,
                Fields = fields ??
                [
                    new FieldDefinition { Name = "packageId", Type = "string" },
                    new FieldDefinition { Name = "downloads", Type = "int" },
                    new FieldDefinition { Name = "summary", Type = "string" },
                ],
            });
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var client = await AdminAsync();
        var connectorSlug = "conn" + suffix;
        var requestSlug = "req" + suffix;

        await PostAsync(client, "/api/connectors", new
        {
            name = "Registry",
            slug = connectorSlug,
            baseUrl = "https://registry.example",
            auth = "None",
            settings = new Dictionary<string, string>(),
            enabled = true,
            probePath = "/",
        });

        await PostAsync(client, "/api/requests", new
        {
            name = "Search",
            slug = requestSlug,
            connectorSlug,
            method = "GET",
            pathTemplate = "/" + route,
            headerTemplates = new Dictionary<string, string>(),
            bodyTemplate = (string?)null,
        });

        var setup = new Setup(type, "sync" + suffix, requestSlug);

        if (save)
        {
            var body = SyncBody(setup);
            if (floorDownloads) body["floorFields"] = new List<string> { "downloads" };
            await PostAsync(client, "/api/collection-syncs", body);
        }

        return setup;
    }

    private static Dictionary<string, object?> SyncBody(Setup setup) => new()
    {
        ["name"] = "Packages",
        ["slug"] = setup.Slug,
        ["contentType"] = setup.Type,
        ["enabled"] = true,
        ["source"] = "Request",
        ["requestSlug"] = setup.Route,
        ["itemsPath"] = "data",
        ["fieldMap"] = new Dictionary<string, string>
        {
            ["packageId"] = "id",
            ["downloads"] = "totalDownloads",
            ["summary"] = "description",
        },
        ["keyField"] = "packageId",
        ["floorFields"] = new List<string>(),
        ["intervalMinutes"] = 60,
        ["maxEntries"] = 100,
        ["entryStatus"] = "Published",
    };

    private async Task<JsonElement> RunAsync(Setup setup)
    {
        var response = await (await AdminAsync()).PostAsync(
            $"/api/collection-syncs/{setup.Slug}/run", null, TestContext.Current.CancellationToken);

        response.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}",
            response.StatusCode, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        return JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).RootElement.Clone();
    }

    private async Task<JsonElement> GetSyncAsync(Setup setup)
    {
        var response = await (await AdminAsync()).GetAsync(
            $"/api/collection-syncs/{setup.Slug}", TestContext.Current.CancellationToken);

        response.IsSuccessStatusCode.Should().BeTrue("got {0}", response.StatusCode);

        return JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).RootElement.Clone();
    }

    private async Task MoveLastRunBackAsync(Setup setup)
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();

        var sync = await session.Query<CollectionSync>()
            .FirstOrDefaultAsync(s => s.Slug == setup.Slug, TestContext.Current.CancellationToken);

        sync!.LastRunAt = DateTime.UtcNow.AddMinutes(-(sync.IntervalMinutes + 1));
        session.Store(sync);
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private async Task<List<Content>> EntriesAsync(string type)
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();

        var entries = await session.Query<Content>()
            .Where(c => c.ContentType == type)
            .ToListAsync(TestContext.Current.CancellationToken);

        return entries.ToList();
    }

    private static string? Value(Content entry, string field) =>
        entry.Data.TryGetValue(field, out var value) ? value?.ToString() : null;

    private async Task PostAsync(HttpClient client, string route, object body)
    {
        var response = await client.PostAsJsonAsync(route, body, TestContext.Current.CancellationToken);

        response.IsSuccessStatusCode.Should().BeTrue("POST {0} got {1}: {2}",
            route, response.StatusCode, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    private async Task<HttpClient> AdminAsync()
    {
        var client = Host.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", await _factory.StoredUserTokenAsync("SuperAdmin", "Admin"));
        return client;
    }
}
