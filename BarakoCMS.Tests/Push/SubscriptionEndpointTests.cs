using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BarakoCMS.Push;
using Marten;
using Microsoft.Extensions.DependencyInjection;
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
        var res = await client.PostAsJsonAsync("/api/push/subscriptions", Body("https://fcm.googleapis.com/fcm/send/a"));
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var rows = await PushTestHost.RowsFor(fx, userId);
        Assert.Single(rows);
        Assert.Equal("https://fcm.googleapis.com/fcm/send/a", rows[0].Endpoint);
    }

    [Fact]
    public async Task Same_endpoint_and_keys_from_another_user_takes_the_row_over()
    {
        var (a, aId) = await PushTestHost.SignedIn(fx, configured: true);
        var (b, bId) = await PushTestHost.SignedIn(fx, configured: true);
        Assert.NotEqual(aId, bId);
        var body = Body("https://fcm.googleapis.com/fcm/send/shared");
        await a.PostAsJsonAsync("/api/push/subscriptions", body);
        var res = await b.PostAsJsonAsync("/api/push/subscriptions", body);
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        Assert.Empty(await PushTestHost.RowsFor(fx, aId));
        Assert.Single(await PushTestHost.RowsFor(fx, bId));
    }

    [Fact]
    public async Task User_cannot_delete_another_users_subscription_and_the_row_survives()
    {
        var (a, aId) = await PushTestHost.SignedIn(fx, configured: true);
        var (b, _) = await PushTestHost.SignedIn(fx, configured: true);
        var id = await CreatedId(await a.PostAsJsonAsync("/api/push/subscriptions", Body("https://fcm.googleapis.com/fcm/send/mine")));

        var res = await b.DeleteAsync($"/api/push/subscriptions/{id}");

        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        Assert.Single(await PushTestHost.RowsFor(fx, aId));
    }

    [Fact]
    public async Task Owner_can_delete_their_own_subscription()
    {
        var (a, aId) = await PushTestHost.SignedIn(fx, configured: true);
        var id = await CreatedId(await a.PostAsJsonAsync("/api/push/subscriptions", Body("https://fcm.googleapis.com/fcm/send/own")));

        var res = await a.DeleteAsync($"/api/push/subscriptions/{id}");

        Assert.Equal(HttpStatusCode.NoContent, res.StatusCode);
        Assert.Empty(await PushTestHost.RowsFor(fx, aId));
    }

    [Fact]
    public async Task List_returns_only_the_callers_rows_and_no_keys_or_endpoint()
    {
        var (a, _) = await PushTestHost.SignedIn(fx, configured: true);
        var (b, _) = await PushTestHost.SignedIn(fx, configured: true);
        await a.PostAsJsonAsync("/api/push/subscriptions", Body("https://fcm.googleapis.com/fcm/send/la"));
        await b.PostAsJsonAsync("/api/push/subscriptions", Body("https://fcm.googleapis.com/fcm/send/lb"));

        var json = await a.GetStringAsync("/api/push/subscriptions");

        Assert.DoesNotContain("fcm.googleapis.com", json);
        Assert.DoesNotContain("p256dh", json, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, JsonDocument.Parse(json).RootElement.GetArrayLength());
    }

    // The_same_endpoint_in_two_tenants_stays_two_separate_rows is the real isolation proof.
    [Fact]
    public async Task A_user_in_another_tenant_cannot_list_or_delete_a_subscription()
    {
        var tenantA = PushTestHost.NewTenant();
        var tenantB = PushTestHost.NewTenant();
        var (a, aId) = await PushTestHost.SignedIn(fx, configured: true, tenantA);
        var (b, _) = await PushTestHost.SignedIn(fx, configured: true, tenantB);
        var id = await CreatedId(await a.PostAsJsonAsync("/api/push/subscriptions", Body("https://fcm.googleapis.com/fcm/send/xt")));

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

        await a.PostAsJsonAsync("/api/push/subscriptions", Body("https://fcm.googleapis.com/fcm/send/both"));
        await b.PostAsJsonAsync("/api/push/subscriptions", Body("https://fcm.googleapis.com/fcm/send/both"));

        Assert.Single(await PushTestHost.RowsFor(fx, aId, tenantA));
        Assert.Single(await PushTestHost.RowsFor(fx, bId, tenantB));
    }

    [Theory]
    [InlineData("https://localhost/x")]
    [InlineData("https://169.254.169.254/x")]
    [InlineData("https://10.0.0.1/x")]
    [InlineData("https://[::1]/x")]
    [InlineData("https://user:pass@fcm.googleapis.com/x")]
    [InlineData("https://fcm.googleapis.com:8443/x")]
    [InlineData("https://evil.example/x")]
    [InlineData("https://push.services.mozilla.com.evil.example/x")]
    [InlineData("https://push.services.mozilla.com/x")]
    public async Task Endpoints_off_the_allow_list_are_rejected(string endpoint)
    {
        var (client, userId) = await PushTestHost.SignedIn(fx, configured: true);
        var res = await client.PostAsJsonAsync("/api/push/subscriptions", Body(endpoint));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Empty(await PushTestHost.RowsFor(fx, userId));
    }

    [Theory]
    [InlineData("https://fcm.googleapis.com/fcm/send/abc")]
    [InlineData("https://updates.push.services.mozilla.com/wpush/v2/abc")]
    [InlineData("https://fcm.googleapis.com:443/fcm/send/abc")]
    public async Task Known_push_service_endpoints_are_accepted(string endpoint)
    {
        var (client, _) = await PushTestHost.SignedIn(fx, configured: true);
        var res = await client.PostAsJsonAsync("/api/push/subscriptions", Body(endpoint + Guid.NewGuid().ToString("N")));
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
    }

    [Fact]
    public async Task Another_user_posting_a_known_endpoint_with_different_keys_gets_409_and_the_row_is_unchanged()
    {
        var (a, aId) = await PushTestHost.SignedIn(fx, configured: true);
        var (b, bId) = await PushTestHost.SignedIn(fx, configured: true);
        var endpoint = "https://fcm.googleapis.com/fcm/send/hijack";
        var original = Body(endpoint);
        await a.PostAsJsonAsync("/api/push/subscriptions", original);

        var res = await b.PostAsJsonAsync("/api/push/subscriptions", Body(endpoint));

        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        Assert.Contains("belongs to another user", await res.Content.ReadAsStringAsync());
        var rows = await PushTestHost.RowsFor(fx, aId);
        Assert.Single(rows);
        Assert.Equal(original.GetType().GetProperty("p256dh")!.GetValue(original), rows[0].P256dh);
        Assert.Empty(await PushTestHost.RowsFor(fx, bId));
    }

    [Fact]
    public async Task The_same_user_resubscribing_with_new_keys_updates_the_row()
    {
        var (a, aId) = await PushTestHost.SignedIn(fx, configured: true);
        var endpoint = "https://fcm.googleapis.com/fcm/send/rekey";
        await a.PostAsJsonAsync("/api/push/subscriptions", Body(endpoint));
        var next = Body(endpoint);
        var res = await a.PostAsJsonAsync("/api/push/subscriptions", next);
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var rows = await PushTestHost.RowsFor(fx, aId);
        Assert.Single(rows);
        Assert.Equal(next.GetType().GetProperty("auth")!.GetValue(next), rows[0].Auth);
    }

    [Theory]
    [InlineData("endpoint")]
    [InlineData("p256dh")]
    [InlineData("auth")]
    public async Task A_null_field_returns_400(string field)
    {
        var (client, _) = await PushTestHost.SignedIn(fx, configured: true);
        var body = new Dictionary<string, string?>
        {
            ["endpoint"] = "https://fcm.googleapis.com/fcm/send/null",
            ["p256dh"] = PushTestKeys.P256dh(),
            ["auth"] = PushTestKeys.Auth(),
        };
        body[field] = null;
        var res = await client.PostAsJsonAsync("/api/push/subscriptions", body);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task The_21st_subscription_removes_the_least_recently_seen()
    {
        var (client, userId) = await PushTestHost.SignedIn(fx, configured: true);
        for (var i = 0; i < 21; i++)
        {
            var res = await client.PostAsJsonAsync("/api/push/subscriptions", Body($"https://fcm.googleapis.com/fcm/send/cap{i}"));
            Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        }
        var rows = await PushTestHost.RowsFor(fx, userId);
        Assert.Equal(20, rows.Count);
        Assert.DoesNotContain(rows, r => r.Endpoint == "https://fcm.googleapis.com/fcm/send/cap0");
        Assert.Contains(rows, r => r.Endpoint == "https://fcm.googleapis.com/fcm/send/cap20");
    }

    [Fact]
    public async Task Endpoint_index_is_unique_and_scoped_to_the_tenant()
    {
        var store = fx.Services.GetRequiredService<IDocumentStore>();
        await using var session = store.QuerySession();
        var defs = await session.QueryAsync<string>(
            "select indexdef from pg_indexes where tablename = 'mt_doc_push_subscriptions'");
        var def = Assert.Single(defs, d => d.Contains("endpoint", StringComparison.OrdinalIgnoreCase) && d.Contains("UNIQUE"));
        Assert.Contains("tenant_id", def);
    }

    [Fact]
    public async Task Bad_key_lengths_are_rejected()
    {
        var (client, _) = await PushTestHost.SignedIn(fx, configured: true);
        var res = await client.PostAsJsonAsync("/api/push/subscriptions",
            new { endpoint = "https://fcm.googleapis.com/fcm/send/x", p256dh = "AAAA", auth = "AAAA" });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Subscribe_needs_a_signed_in_user()
    {
        var res = await PushTestHost.Client(fx, configured: true)
            .PostAsJsonAsync("/api/push/subscriptions", Body("https://fcm.googleapis.com/fcm/send/anon"));
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

        var res = await client.PostAsJsonAsync("/api/push/subscriptions", Body("https://fcm.googleapis.com/fcm/send/off"));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, res.StatusCode);
        Assert.DoesNotContain(PushTestHost.PrivateKey, await res.Content.ReadAsStringAsync());
    }
}
