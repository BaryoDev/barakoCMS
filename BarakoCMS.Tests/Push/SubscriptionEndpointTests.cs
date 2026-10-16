using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace BarakoCMS.Tests.Push;

[Collection("Sequential")]
public class SubscriptionEndpointTests(IntegrationTestFixture fx)
{
    private static object Body(string endpoint) => new
    {
        endpoint,
        p256dh = PushTestKeys.P256dh(),
        auth = PushTestKeys.Auth(),
    };

    private static async Task<Guid> CreatedId(HttpResponseMessage res) =>
        (await res.Content.ReadFromJsonAsync<Dictionary<string, Guid>>())!["id"];

    [Fact]
    public async Task Subscribe_stores_a_row_for_the_caller_and_returns_201()
    {
        var (client, userId) = await PushTestHost.SignedIn(fx, configured: true);
        var res = await client.PostAsJsonAsync("/api/push/subscriptions", Body("https://push.example/a"));
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var rows = await PushTestHost.RowsFor(fx, userId);
        Assert.Single(rows);
        Assert.Equal("https://push.example/a", rows[0].Endpoint);
    }

    [Fact]
    public async Task Same_endpoint_from_another_user_takes_the_row_over()
    {
        var (a, aId) = await PushTestHost.SignedIn(fx, configured: true);
        var (b, bId) = await PushTestHost.SignedIn(fx, configured: true);
        Assert.NotEqual(aId, bId);
        await a.PostAsJsonAsync("/api/push/subscriptions", Body("https://push.example/shared"));
        await b.PostAsJsonAsync("/api/push/subscriptions", Body("https://push.example/shared"));
        Assert.Empty(await PushTestHost.RowsFor(fx, aId));
        Assert.Single(await PushTestHost.RowsFor(fx, bId));
    }

    [Fact]
    public async Task User_cannot_delete_another_users_subscription_and_the_row_survives()
    {
        var (a, aId) = await PushTestHost.SignedIn(fx, configured: true);
        var (b, _) = await PushTestHost.SignedIn(fx, configured: true);
        var id = await CreatedId(await a.PostAsJsonAsync("/api/push/subscriptions", Body("https://push.example/mine")));

        var res = await b.DeleteAsync($"/api/push/subscriptions/{id}");

        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        Assert.Single(await PushTestHost.RowsFor(fx, aId));
    }

    [Fact]
    public async Task Owner_can_delete_their_own_subscription()
    {
        var (a, aId) = await PushTestHost.SignedIn(fx, configured: true);
        var id = await CreatedId(await a.PostAsJsonAsync("/api/push/subscriptions", Body("https://push.example/own")));

        var res = await a.DeleteAsync($"/api/push/subscriptions/{id}");

        Assert.Equal(HttpStatusCode.NoContent, res.StatusCode);
        Assert.Empty(await PushTestHost.RowsFor(fx, aId));
    }

    [Fact]
    public async Task List_returns_only_the_callers_rows_and_no_keys_or_endpoint()
    {
        var (a, _) = await PushTestHost.SignedIn(fx, configured: true);
        var (b, _) = await PushTestHost.SignedIn(fx, configured: true);
        await a.PostAsJsonAsync("/api/push/subscriptions", Body("https://push.example/la"));
        await b.PostAsJsonAsync("/api/push/subscriptions", Body("https://push.example/lb"));

        var json = await a.GetStringAsync("/api/push/subscriptions");

        Assert.DoesNotContain("push.example", json);
        Assert.DoesNotContain("p256dh", json, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, JsonDocument.Parse(json).RootElement.GetArrayLength());
    }

    [Fact]
    public async Task A_user_in_another_tenant_cannot_list_or_delete_a_subscription()
    {
        var tenantA = PushTestHost.NewTenant();
        var tenantB = PushTestHost.NewTenant();
        var (a, aId) = await PushTestHost.SignedIn(fx, configured: true, tenantA);
        var (b, _) = await PushTestHost.SignedIn(fx, configured: true, tenantB);
        var id = await CreatedId(await a.PostAsJsonAsync("/api/push/subscriptions", Body("https://push.example/xt")));

        var listed = await b.GetStringAsync("/api/push/subscriptions");
        var res = await b.DeleteAsync($"/api/push/subscriptions/{id}");

        Assert.Equal(0, JsonDocument.Parse(listed).RootElement.GetArrayLength());
        Assert.DoesNotContain(id.ToString(), listed);
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        Assert.Single(await PushTestHost.RowsFor(fx, aId, tenantA));
    }

    [Fact]
    public async Task The_same_endpoint_in_two_tenants_stays_two_separate_rows()
    {
        var tenantA = PushTestHost.NewTenant();
        var tenantB = PushTestHost.NewTenant();
        var (a, aId) = await PushTestHost.SignedIn(fx, configured: true, tenantA);
        var (b, bId) = await PushTestHost.SignedIn(fx, configured: true, tenantB);

        await a.PostAsJsonAsync("/api/push/subscriptions", Body("https://push.example/both"));
        await b.PostAsJsonAsync("/api/push/subscriptions", Body("https://push.example/both"));

        Assert.Single(await PushTestHost.RowsFor(fx, aId, tenantA));
        Assert.Single(await PushTestHost.RowsFor(fx, bId, tenantB));
    }

    [Fact]
    public async Task Bad_key_lengths_are_rejected()
    {
        var (client, _) = await PushTestHost.SignedIn(fx, configured: true);
        var res = await client.PostAsJsonAsync("/api/push/subscriptions",
            new { endpoint = "https://push.example/x", p256dh = "AAAA", auth = "AAAA" });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Subscribe_needs_a_signed_in_user()
    {
        var res = await PushTestHost.Client(fx, configured: true)
            .PostAsJsonAsync("/api/push/subscriptions", Body("https://push.example/anon"));
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Config_never_contains_the_private_key()
    {
        var (client, _) = await PushTestHost.SignedIn(fx, configured: true);
        var json = await client.GetStringAsync("/api/push/config");
        Assert.Contains("\"enabled\":true", json.Replace(" ", ""));
        Assert.DoesNotContain(PushTestHost.PrivateKey, json);
        Assert.Contains(PushTestHost.PublicKey, json);
    }

    [Fact]
    public async Task Unconfigured_host_reports_disabled_and_subscribe_returns_503_without_leaking()
    {
        var (client, _) = await PushTestHost.SignedIn(fx, configured: false);
        var cfg = await client.GetStringAsync("/api/push/config");
        Assert.Contains("\"enabled\":false", cfg.Replace(" ", ""));
        Assert.DoesNotContain(PushTestHost.PrivateKey, cfg);
        Assert.DoesNotContain(PushTestHost.PublicKey, cfg);

        var res = await client.PostAsJsonAsync("/api/push/subscriptions", Body("https://push.example/off"));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, res.StatusCode);
        Assert.DoesNotContain(PushTestHost.PrivateKey, await res.Content.ReadAsStringAsync());
    }
}
