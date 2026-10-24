using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BarakoCMS.Services;
using barakoCMS.Infrastructure.Auth;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BarakoCMS.Tests.Features.Services;

[Collection("Integration1")]
public class ServiceRegistryTests
{
    private readonly IntegrationTestFixture _factory;

    public ServiceRegistryTests(IntegrationTestFixture factory) => _factory = factory;

    private async Task<HttpClient> AdminAsync()
    {
        var (token, _) = await TestHelpers.CreateAdminUserAsync(_factory);
        return WithToken(token);
    }

    private async Task<HttpClient> CallerWithNoCapabilitiesAsync() =>
        WithToken(await _factory.StoredUserTokenAsync("NoSuchRole"));

    private HttpClient WithToken(string token)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static object Body(string name) => new
    {
        name,
        messageTypes = new[] { "invoice.created" },
        deliveryUrl = "https://billing.example.com/inbox",
    };

    private async Task StoreAsync(ServiceRegistration service) => await StoreDocumentAsync(service);

    private async Task StoreDocumentAsync(object service)
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Store(service);
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage res) =>
        (await res.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken));

    [Fact]
    public async Task An_admin_registers_a_service_and_reads_it_back()
    {
        var name = $"billing-{Guid.NewGuid():N}";
        var admin = await AdminAsync();

        var created = await admin.PostAsJsonAsync("/api/services", Body(name), TestContext.Current.CancellationToken);
        created.StatusCode.Should().Be(HttpStatusCode.Created);

        var read = await admin.GetAsync($"/api/services/{name}", TestContext.Current.CancellationToken);
        read.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await ReadAsync(read);
        json.GetProperty("name").GetString().Should().Be(name);
        json.GetProperty("health").GetString().Should().Be("Healthy", "a service gets its first interval before it can be late");
        json.GetProperty("heartbeatIntervalSeconds").GetInt32().Should().Be(60);
    }

    [Fact]
    public async Task The_same_name_cannot_be_registered_twice()
    {
        var name = $"billing-{Guid.NewGuid():N}";
        var admin = await AdminAsync();
        (await admin.PostAsJsonAsync("/api/services", Body(name), TestContext.Current.CancellationToken))
            .StatusCode.Should().Be(HttpStatusCode.Created);

        var again = await admin.PostAsJsonAsync("/api/services", Body(name.ToUpperInvariant()), TestContext.Current.CancellationToken);

        again.StatusCode.Should().Be(HttpStatusCode.Conflict, "names are stored lower case, so a different case is the same service");
    }

    [Fact]
    public async Task Racing_registrations_of_one_name_end_in_one_created_and_the_rest_conflict()
    {
        var name = $"race-{Guid.NewGuid():N}";
        var admin = await AdminAsync();

        var results = await Task.WhenAll(Enumerable.Range(0, 6)
            .Select(_ => admin.PostAsJsonAsync("/api/services", Body(name), TestContext.Current.CancellationToken)));

        var codes = results.Select(r => r.StatusCode).ToList();
        codes.Should().HaveCount(6);
        codes.Count(c => c == HttpStatusCode.Created).Should().Be(1);
        codes.Where(c => c != HttpStatusCode.Created).Should().OnlyContain(c => c == HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task The_list_is_paged_and_carries_health()
    {
        var name = $"listed-{Guid.NewGuid():N}";
        await StoreAsync(new ServiceRegistration
        {
            Name = name,
            MessageTypes = ["x"],
            DeliveryUrl = "https://x.example.com",
            HeartbeatIntervalSeconds = 60,
            CreatedAt = DateTime.UtcNow.AddDays(-1),
            LastHeartbeatAt = DateTime.UtcNow.AddHours(-1),
        });
        var admin = await AdminAsync();

        var res = await admin.GetAsync("/api/services?pageSize=100", TestContext.Current.CancellationToken);

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var items = (await ReadAsync(res)).GetProperty("items").EnumerateArray().ToList();
        items.Should().NotBeEmpty();
        var mine = items.Single(i => i.GetProperty("name").GetString() == name);
        mine.GetProperty("health").GetString().Should().Be("Down");
    }

    [Fact]
    public async Task A_heartbeat_brings_a_down_service_back_to_healthy()
    {
        var name = $"down-{Guid.NewGuid():N}";
        await StoreAsync(new ServiceRegistration
        {
            Name = name,
            MessageTypes = ["x"],
            DeliveryUrl = "https://x.example.com",
            HeartbeatIntervalSeconds = 60,
            CreatedAt = DateTime.UtcNow.AddDays(-1),
            LastHeartbeatAt = DateTime.UtcNow.AddHours(-1),
        });
        var admin = await AdminAsync();
        (await ReadAsync(await admin.GetAsync($"/api/services/{name}", TestContext.Current.CancellationToken)))
            .GetProperty("health").GetString().Should().Be("Down", "the starting state has to differ from the end state");

        var beat = await admin.PostAsync($"/api/services/{name}/heartbeat", null, TestContext.Current.CancellationToken);

        beat.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadAsync(await admin.GetAsync($"/api/services/{name}", TestContext.Current.CancellationToken)))
            .GetProperty("health").GetString().Should().Be("Healthy");
    }

    [Fact]
    public async Task A_heartbeat_for_an_unknown_service_is_a_404()
    {
        var admin = await AdminAsync();

        var res = await admin.PostAsync($"/api/services/nope-{Guid.NewGuid():N}/heartbeat", null, TestContext.Current.CancellationToken);

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_caller_without_the_capabilities_is_refused_everywhere()
    {
        var name = $"locked-{Guid.NewGuid():N}";
        await StoreAsync(new ServiceRegistration { Name = name, MessageTypes = ["x"], DeliveryUrl = "https://x.example.com" });
        var caller = await CallerWithNoCapabilitiesAsync();

        (await caller.PostAsJsonAsync("/api/services", Body($"new-{Guid.NewGuid():N}"), TestContext.Current.CancellationToken))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await caller.GetAsync("/api/services", TestContext.Current.CancellationToken))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await caller.GetAsync($"/api/services/{name}", TestContext.Current.CancellationToken))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await caller.PostAsync($"/api/services/{name}/heartbeat", null, TestContext.Current.CancellationToken))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_machine_key_must_be_a_live_heartbeat_key()
    {
        var admin = await AdminAsync();

        async Task<Guid> KeyAsync(Action<ApiKey>? tweak = null)
        {
            var key = new ApiKey
            {
                Id = Guid.NewGuid(), Name = "k", KeyHash = Guid.NewGuid().ToString("N"), Prefix = "bcms_k",
                Scopes = ["services:heartbeat"],
            };
            tweak?.Invoke(key);
            await StoreDocumentAsync(key);
            return key.Id;
        }

        Task<HttpResponseMessage> RegisterAsync(Guid key) => admin.PostAsJsonAsync("/api/services", new
        {
            name = $"keyed-{Guid.NewGuid():N}",
            messageTypes = new[] { "x" },
            deliveryUrl = "https://x.example.com",
            machineKeyId = key,
        }, TestContext.Current.CancellationToken);

        (await RegisterAsync(await KeyAsync())).StatusCode.Should().Be(HttpStatusCode.Created);
        (await RegisterAsync(await KeyAsync(k => k.Revoked = true))).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await RegisterAsync(await KeyAsync(k => k.ExpiresAt = DateTime.UtcNow.AddDays(-1)))).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await RegisterAsync(await KeyAsync(k => k.Scopes = ["content:read"]))).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await RegisterAsync(await KeyAsync(k => k.ContentTypes = ["post"]))).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await RegisterAsync(Guid.NewGuid())).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task An_anonymous_caller_is_refused()
    {
        var res = await _factory.CreateClient().GetAsync("/api/services", TestContext.Current.CancellationToken);

        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_registration_that_fails_validation_is_a_400()
    {
        var admin = await AdminAsync();

        var res = await admin.PostAsJsonAsync("/api/services",
            new { name = "bad name", messageTypes = Array.Empty<string>() }, TestContext.Current.CancellationToken);

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
