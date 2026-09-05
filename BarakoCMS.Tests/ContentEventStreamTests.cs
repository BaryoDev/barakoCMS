using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using barakoCMS.Core.Interfaces;
using barakoCMS.Events;
using barakoCMS.Infrastructure.Multitenancy;
using barakoCMS.Models;
using BarakoCMS.Tests.Builders;
using FluentAssertions;
using Marten;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// GET /api/public/events over an anonymous client. The two properties that matter are the ones
/// the REST reads already have: a subscriber on one tenant never receives another tenant's change,
/// and a field the REST read masks is masked here too.
/// </summary>
/// <remarks>
/// "Receives nothing" is asserted with a positive control rather than a timeout: the change that
/// must be silent is followed by one that must not be, and the next frame off the wire has to be
/// the control. The listener runs inside the writer's SaveChangesAsync and the channel keeps order,
/// so anything the silent change emitted would arrive first and fail the assertion.
///
/// Every write goes through the host under test. A host built by WithSettings has its own
/// container, so its own broadcaster; a write through the shared fixture would broadcast to a
/// broadcaster nobody here is subscribed to.
/// </remarks>
[Collection("Sequential")]
public class ContentEventStreamTests
{
    private readonly IntegrationTestFixture _factory;

    public ContentEventStreamTests(IntegrationTestFixture factory) => _factory = factory;

    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(20);

    private WebApplicationFactory<Program> EnabledHost(IDictionary<string, string?>? extra = null)
    {
        var settings = new Dictionary<string, string?> { ["Delivery:Events:Enabled"] = "true" };
        if (extra is not null)
        {
            foreach (var (key, value) in extra)
            {
                settings[key] = value;
            }
        }

        return _factory.WithSettings(settings);
    }

    /// <summary>A frame off the wire. A comment line is a frame whose Event starts with a colon.</summary>
    private sealed record Frame(string Event, string Data);

    private sealed class OpenStream : IDisposable
    {
        private readonly HttpResponseMessage _response;
        private readonly StreamReader _reader;

        private OpenStream(HttpResponseMessage response, StreamReader reader)
        {
            _response = response;
            _reader = reader;
        }

        public HttpStatusCode StatusCode => _response.StatusCode;

        public static async Task<OpenStream> OpenAsync(HttpClient client, string url, string? tenant = null, string? remoteIp = null)
        {
            var response = await client.SendAsync(Request(url, tenant, remoteIp), HttpCompletionOption.ResponseHeadersRead);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                // Read the body only on the failure path: on a 200 it is an open stream, and
                // reading it to the end waits for the connection to close.
                response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
            }

            response.Content.Headers.ContentType!.MediaType.Should().Be("text/event-stream");
            var reader = new StreamReader(await response.Content.ReadAsStreamAsync());
            return new OpenStream(response, reader);
        }

        /// <summary>The next frame, keepalive comments included.</summary>
        public async Task<Frame> NextFrameAsync()
        {
            string? eventName = null;
            string? data = null;

            while (true)
            {
                var line = await _reader.ReadLineAsync().WaitAsync(ReadTimeout);
                if (line is null)
                {
                    throw new InvalidOperationException("The stream ended before a frame arrived.");
                }

                if (line.Length == 0)
                {
                    if (eventName is not null)
                    {
                        return new Frame(eventName, data ?? string.Empty);
                    }

                    continue;
                }

                if (line.StartsWith(':'))
                {
                    return new Frame(line, string.Empty);
                }

                if (line.StartsWith("event: ", StringComparison.Ordinal))
                {
                    eventName = line["event: ".Length..];
                }
                else if (line.StartsWith("data: ", StringComparison.Ordinal))
                {
                    data = line["data: ".Length..];
                }
            }
        }

        /// <summary>The next content change, skipping keepalive comments.</summary>
        public async Task<Frame> NextChangeAsync()
        {
            while (true)
            {
                var frame = await NextFrameAsync();
                if (!frame.Event.StartsWith(':'))
                {
                    return frame;
                }
            }
        }

        public void Dispose()
        {
            _reader.Dispose();
            _response.Dispose();
        }
    }

    private static HttpRequestMessage Request(string url, string? tenant = null, string? remoteIp = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (tenant is not null)
        {
            request.Headers.Add("X-Tenant", tenant);
        }

        if (remoteIp is not null)
        {
            request.Headers.Add(TestRemoteIpFilter.Header, remoteIp);
        }

        return request;
    }

    private static string Slug() => "evt-" + Guid.NewGuid().ToString("n")[..8];

    private static string TypeName() => "evt_" + Guid.NewGuid().ToString("n")[..8];

    private static async Task SeedTypeAsync(WebApplicationFactory<Program> host, string type, string? tenant = null)
    {
        using var scope = tenant is null
            ? host.Services.CreateScope()
            : host.Services.CreateScopeForTenant(tenant);
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Store(new ContentTypeBuilder()
            .Named(type)
            .PubliclyDeliverable()
            .WithTitleAndSlug()
            .WithSensitiveField()
            .Build());
        await session.SaveChangesAsync();
    }

    /// <summary>One commit through the same writer the endpoints use, in the given tenant.</summary>
    private static async Task<Content> WriteAsync(
        WebApplicationFactory<Program> host,
        string? tenant,
        Func<IContentWriter, Task<Content>> act)
    {
        using var scope = tenant is null
            ? host.Services.CreateScope()
            : host.Services.CreateScopeForTenant(tenant);
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var writer = scope.ServiceProvider.GetRequiredService<IContentWriter>();
        var content = await act(writer);
        await session.SaveChangesAsync();
        return content;
    }

    private static ContentCreated Created(string type, string title, string slug, ContentStatus status) =>
        new(Guid.NewGuid(), type,
            new Dictionary<string, object> { ["Title"] = title, ["Slug"] = slug, ["Secret"] = "topsecret" },
            status, Guid.NewGuid(), title, SensitivityLevel.Public, DateTime.UtcNow);

    private static Task<Content> PublishNewAsync(WebApplicationFactory<Program> host, string? tenant, string type, string slug) =>
        WriteAsync(host, tenant, w => w.CreateAsync(Created(type, "Control " + slug, slug, ContentStatus.Published), default));

    private static Task<Content> ChangeStatusAsync(WebApplicationFactory<Program> host, string? tenant, Content content, ContentStatus status) =>
        WriteAsync(host, tenant, async w =>
        {
            await w.AppendAsync(content, new ContentStatusChanged(content.Id, status, Guid.NewGuid(), DateTime.UtcNow), default);
            return content;
        });

    [Fact]
    public async Task A_publish_through_the_api_streams_the_public_fields_and_not_a_sensitive_one()
    {
        var host = EnabledHost();
        var type = TypeName();
        var slug = Slug();
        await SeedTypeAsync(host, type);

        var admin = host.CreateClient();
        admin.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", await _factory.StoredUserTokenAsync("SuperAdmin"));

        using var stream = await OpenStream.OpenAsync(host.CreateClient(), "/api/public/events");

        var created = await admin.PostAsJsonAsync("/api/contents", new
        {
            contentType = type,
            data = new Dictionary<string, object> { ["Title"] = "Hello stream", ["Slug"] = slug, ["Secret"] = "topsecret" },
        });
        created.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}", created.StatusCode, await created.Content.ReadAsStringAsync());
        using var doc = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var id = doc.RootElement.GetProperty("id").GetGuid();

        var published = await admin.PutAsJsonAsync($"/api/contents/{id}/status", new { newStatus = "Published" });
        published.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}", published.StatusCode, await published.Content.ReadAsStringAsync());

        var frame = await stream.NextChangeAsync();

        frame.Event.Should().Be("content.published");
        frame.Data.Should().Contain(id.ToString()).And.Contain(type).And.Contain(slug).And.Contain("Hello stream");
        frame.Data.Should().NotContain("topsecret", "a Sensitive field value is never streamed");
        frame.Data.Should().NotContain("Secret", "the Sensitive field is absent, not blanked");

        // The same shape the REST read returns, produced by the same projection.
        var rest = await host.CreateClient().GetFromJsonAsync<JsonElement>($"/api/public/{type}/{slug}", ApiJson.Options);
        using var streamed = JsonDocument.Parse(frame.Data);
        streamed.RootElement.EnumerateObject().Select(p => p.Name)
            .Should().BeEquivalentTo(rest.EnumerateObject().Select(p => p.Name));
    }

    [Fact]
    public async Task A_draft_save_streams_nothing()
    {
        var host = EnabledHost();
        var type = TypeName();
        await SeedTypeAsync(host, type);

        using var stream = await OpenStream.OpenAsync(host.CreateClient(), "/api/public/events");

        var draft = await WriteAsync(host, null, w => w.CreateAsync(Created(type, "Draft", Slug(), ContentStatus.Draft), default));
        await WriteAsync(host, null, async w =>
        {
            await w.AppendAsync(draft, new ContentUpdated(draft.Id,
                new Dictionary<string, object> { ["Title"] = "Draft edited", ["Slug"] = draft.Data["Slug"], ["Secret"] = "topsecret" },
                Guid.NewGuid(), "Draft edited", DateTime.UtcNow), default);
            return draft;
        });

        var control = Slug();
        await PublishNewAsync(host, null, type, control);

        var frame = await stream.NextChangeAsync();
        frame.Event.Should().Be("content.published");
        frame.Data.Should().Contain(control, "the first frame is the control, so the draft save and edit emitted nothing");
        frame.Data.Should().NotContain("Draft edited");
    }

    [Fact]
    public async Task A_published_update_streams_the_entry_and_an_unpublish_streams_only_its_identity()
    {
        var host = EnabledHost();
        var type = TypeName();
        var slug = Slug();
        await SeedTypeAsync(host, type);

        using var stream = await OpenStream.OpenAsync(host.CreateClient(), "/api/public/events");

        var content = await PublishNewAsync(host, null, type, slug);
        (await stream.NextChangeAsync()).Event.Should().Be("content.published");

        await WriteAsync(host, null, async w =>
        {
            await w.AppendAsync(content, new ContentUpdated(content.Id,
                new Dictionary<string, object> { ["Title"] = "Second title", ["Slug"] = slug, ["Secret"] = "topsecret" },
                Guid.NewGuid(), "Second title", DateTime.UtcNow), default);
            return content;
        });

        var updated = await stream.NextChangeAsync();
        updated.Event.Should().Be("content.updated");
        updated.Data.Should().Contain("Second title").And.Contain(slug);
        updated.Data.Should().NotContain("topsecret");

        await ChangeStatusAsync(host, null, content, ContentStatus.Draft);

        var unpublished = await stream.NextChangeAsync();
        unpublished.Event.Should().Be("content.unpublished");
        unpublished.Data.Should().Contain(content.Id.ToString()).And.Contain(type).And.Contain(slug);
        unpublished.Data.Should().NotContain("Second title", "an unpublish carries no fields");
        unpublished.Data.Should().NotContain("topsecret");
    }

    [Fact]
    public async Task An_archive_streams_an_unpublish_only_for_an_entry_that_was_public()
    {
        var host = EnabledHost();
        var type = TypeName();
        await SeedTypeAsync(host, type);

        using var stream = await OpenStream.OpenAsync(host.CreateClient(), "/api/public/events");

        // Draft to Archived: never public, so nothing.
        var draft = await WriteAsync(host, null, w => w.CreateAsync(Created(type, "Never public", Slug(), ContentStatus.Draft), default));
        await ChangeStatusAsync(host, null, draft, ContentStatus.Archived);

        // Published to Archived: was public, so an unpublish.
        var live = Slug();
        var published = await PublishNewAsync(host, null, type, live);
        (await stream.NextChangeAsync()).Event.Should().Be("content.published");
        await ChangeStatusAsync(host, null, published, ContentStatus.Archived);

        var frame = await stream.NextChangeAsync();
        frame.Event.Should().Be("content.unpublished");
        frame.Data.Should().Contain(live);
        frame.Data.Should().NotContain(draft.Id.ToString(), "the archived draft was never public");
    }

    [Fact]
    public async Task A_change_on_another_tenant_never_reaches_a_subscriber()
    {
        var host = EnabledHost();
        var type = TypeName();
        var tenantA = "evta" + Guid.NewGuid().ToString("n")[..8];
        var tenantB = "evtb" + Guid.NewGuid().ToString("n")[..8];
        await SeedTypeAsync(host, type, tenantA);
        await SeedTypeAsync(host, type, tenantB);

        using var stream = await OpenStream.OpenAsync(host.CreateClient(), "/api/public/events", tenant: tenantA);

        var slugB = Slug();
        var slugA = Slug();
        var inB = await PublishNewAsync(host, tenantB, type, slugB);
        await PublishNewAsync(host, tenantA, type, slugA);

        var frame = await stream.NextChangeAsync();
        frame.Event.Should().Be("content.published");
        frame.Data.Should().Contain(slugA, "the first frame is tenant A's own change");
        frame.Data.Should().NotContain(slugB).And.NotContain(inB.Id.ToString(), "tenant B's change must not reach tenant A");
    }

    [Fact]
    public async Task The_type_filter_excludes_other_types()
    {
        var host = EnabledHost();
        var wanted = TypeName();
        var other = TypeName();
        await SeedTypeAsync(host, wanted);
        await SeedTypeAsync(host, other);

        using var stream = await OpenStream.OpenAsync(host.CreateClient(), $"/api/public/events?type={wanted}&type=nothing_like_this");

        var otherSlug = Slug();
        var wantedSlug = Slug();
        await PublishNewAsync(host, null, other, otherSlug);
        await PublishNewAsync(host, null, wanted, wantedSlug);

        var frame = await stream.NextChangeAsync();
        frame.Data.Should().Contain(wantedSlug).And.Contain(wanted);
        frame.Data.Should().NotContain(otherSlug);
    }

    [Fact]
    public async Task A_type_that_has_not_opted_in_is_never_streamed()
    {
        var host = EnabledHost();
        var closed = TypeName();
        var open = TypeName();
        await SeedTypeAsync(host, open);
        using (var scope = host.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Store(new ContentTypeBuilder().Named(closed).WithTitleAndSlug().Build());
            await session.SaveChangesAsync();
        }

        using var stream = await OpenStream.OpenAsync(host.CreateClient(), "/api/public/events");

        var closedSlug = Slug();
        var openSlug = Slug();
        await PublishNewAsync(host, null, closed, closedSlug);
        await PublishNewAsync(host, null, open, openSlug);

        var frame = await stream.NextChangeAsync();
        frame.Data.Should().Contain(openSlug);
        frame.Data.Should().NotContain(closedSlug, "a type that is not publicly deliverable is not streamed");
    }

    [Fact]
    public async Task The_stream_is_404_until_enabled()
    {
        // Headers only: if the gate were missing this would be an open stream, and reading it
        // to the end would wait for the connection to close instead of failing.
        using var response = await _factory.CreateClient().GetAsync("/api/public/events", HttpCompletionOption.ResponseHeadersRead);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound, "Delivery:Events:Enabled defaults to false");
    }

    [Fact]
    public async Task Beyond_the_connection_cap_the_stream_answers_503()
    {
        var host = EnabledHost(new Dictionary<string, string?> { ["Delivery:Events:MaxConnections"] = "1" });
        var client = host.CreateClient();

        using var first = await OpenStream.OpenAsync(client, "/api/public/events");

        using var second = await client.GetAsync("/api/public/events", HttpCompletionOption.ResponseHeadersRead);
        second.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        second.Headers.RetryAfter.Should().NotBeNull();
    }

    [Fact]
    public async Task Beyond_the_per_client_cap_one_client_is_refused_while_another_still_connects()
    {
        var host = EnabledHost(new Dictionary<string, string?> { ["Delivery:Events:MaxConnectionsPerClient"] = "2" });
        var client = host.CreateClient();
        const string first = "203.0.113.10";
        const string second = "203.0.113.20";

        using var firstA = await OpenStream.OpenAsync(client, "/api/public/events", remoteIp: first);
        using var firstB = await OpenStream.OpenAsync(client, "/api/public/events", remoteIp: first);

        using var refused = await client.SendAsync(Request("/api/public/events", remoteIp: first), HttpCompletionOption.ResponseHeadersRead);
        refused.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable, "the third stream from one address is over its cap of 2");
        refused.Headers.RetryAfter.Should().NotBeNull();
        (await refused.Content.ReadAsStringAsync()).Should().Contain("This client", "the reason names the per-client cap, not the instance cap");

        using var other = await OpenStream.OpenAsync(client, "/api/public/events", remoteIp: second);
        other.StatusCode.Should().Be(HttpStatusCode.OK, "another address is under its own cap");
    }

    [Fact]
    public async Task Closing_a_stream_gives_the_client_its_slot_back()
    {
        var host = EnabledHost(new Dictionary<string, string?> { ["Delivery:Events:MaxConnectionsPerClient"] = "1" });
        var client = host.CreateClient();
        const string address = "203.0.113.30";

        var held = await OpenStream.OpenAsync(client, "/api/public/events", remoteIp: address);

        using var refused = await client.SendAsync(Request("/api/public/events", remoteIp: address), HttpCompletionOption.ResponseHeadersRead);
        refused.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);

        held.Dispose();

        // The server learns of the disconnect asynchronously, so the slot comes back a moment
        // after Dispose returns. Poll rather than sleep, and fail if it never does.
        var broadcaster = host.Services.GetRequiredService<barakoCMS.Features.Public.Events.ContentChangeBroadcaster>();
        var deadline = DateTime.UtcNow + ReadTimeout;
        while (broadcaster.ConnectionsFor(address) > 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }

        broadcaster.ConnectionsFor(address).Should().Be(0, "the count releases when the connection ends");

        using var again = await OpenStream.OpenAsync(client, "/api/public/events", remoteIp: address);
        again.StatusCode.Should().Be(HttpStatusCode.OK, "the address is back under its cap");
    }

    [Fact]
    public async Task An_instance_cap_refusal_gives_the_per_client_slot_back()
    {
        var host = EnabledHost(new Dictionary<string, string?>
        {
            ["Delivery:Events:MaxConnections"] = "1",
            ["Delivery:Events:MaxConnectionsPerClient"] = "5",
        });
        var client = host.CreateClient();
        const string address = "203.0.113.40";

        using var held = await OpenStream.OpenAsync(client, "/api/public/events", remoteIp: address);

        using var refused = await client.SendAsync(Request("/api/public/events", remoteIp: address), HttpCompletionOption.ResponseHeadersRead);
        refused.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await refused.Content.ReadAsStringAsync()).Should().Contain("The event stream", "the instance cap refused it, not the per-client cap");

        var broadcaster = host.Services.GetRequiredService<barakoCMS.Features.Public.Events.ContentChangeBroadcaster>();
        broadcaster.ConnectionsFor(address).Should().Be(1, "the refused stream took a per-client slot on the way in and must give it back");
    }

    [Fact]
    public async Task A_keepalive_arrives_when_nothing_changes()
    {
        var host = EnabledHost(new Dictionary<string, string?> { ["Delivery:Events:KeepAliveSeconds"] = "1" });

        using var stream = await OpenStream.OpenAsync(host.CreateClient(), "/api/public/events");

        var frame = await stream.NextFrameAsync();
        frame.Event.Should().Be(": keepalive", "a keepalive is an SSE comment, which an EventSource never dispatches");
    }

    [Fact]
    public async Task Raising_a_field_to_sensitive_streams_the_entry_without_that_field()
    {
        var host = EnabledHost();
        var type = TypeName();
        var slug = Slug();
        await SeedTypeAsync(host, type);

        var admin = host.CreateClient();
        admin.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", await _factory.StoredUserTokenAsync("SuperAdmin"));

        using var stream = await OpenStream.OpenAsync(host.CreateClient(), "/api/public/events");

        await PublishNewAsync(host, null, type, slug);
        (await stream.NextChangeAsync()).Data.Should().Contain("Title");

        var raised = await admin.PutAsJsonAsync($"/api/content-types/{type}/fields/Title/sensitivity", new { sensitivity = "Sensitive" });
        raised.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}", raised.StatusCode, await raised.Content.ReadAsStringAsync());

        var frame = await stream.NextChangeAsync();
        frame.Event.Should().Be("content.updated");
        frame.Data.Should().Contain(slug);
        frame.Data.Should().NotContain("Title", "the field the type just marked Sensitive is absent from the update that announces it");
    }
}
