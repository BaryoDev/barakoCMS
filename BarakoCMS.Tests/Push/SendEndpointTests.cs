using System.Net;
using System.Net.Http.Json;
using BarakoCMS.Push;
using Xunit;

namespace BarakoCMS.Tests.Push;

[Collection("Sequential")]
public class SendEndpointTests(IntegrationTestFixture fx)
{
    [Fact]
    public async Task Test_endpoint_reaches_only_the_callers_devices()
    {
        var h = await PushSenderHost.Create(fx, respond: _ => 201);
        var (a, aId) = await h.SignedInClient("User");
        var (_, bId) = await h.SignedInClient("User");
        await h.Subscribe("https://push.example/a", aId);
        await h.Subscribe("https://push.example/b", bId);

        var res = await a.PostAsync("/api/push/test", null);

        Assert.True(res.IsSuccessStatusCode);
        var sent = Assert.Single(h.Fake.Sent);
        Assert.Equal(await h.SubscriptionIdOf(aId), sent.SubscriptionId);
    }

    [Fact]
    public async Task Send_requires_the_send_push_capability()
    {
        await PushTestHost.GrantSendPushAsync(fx);
        var h = await PushSenderHost.Create(fx, respond: _ => 201);
        var (user, _) = await h.SignedInClient("User");
        var res = await user.PostAsJsonAsync("/api/push/send", new { title = "t", body = "b" });
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task Admin_send_reaches_the_tenant()
    {
        await PushTestHost.GrantSendPushAsync(fx);
        var h = await PushSenderHost.Create(fx, respond: _ => 201);
        var (admin, _) = await h.SignedInClient("Admin");
        var (_, u) = await h.SignedInClient("User");
        await h.Subscribe("https://push.example/u", u);

        var res = await admin.PostAsJsonAsync("/api/push/send", new { title = "t", body = "b" });

        Assert.True(res.IsSuccessStatusCode);
        Assert.Single(h.Fake.Sent);
    }

    [Fact]
    public async Task Send_goes_to_the_callers_tenant_and_not_another()
    {
        await PushTestHost.GrantSendPushAsync(fx);
        var h = await PushSenderHost.Create(fx, respond: _ => 201);
        var other = await PushSenderHost.Create(fx, respond: _ => 201);
        await other.Subscribe("https://push.example/elsewhere");
        var (admin, _) = await h.SignedInClient("Admin");
        h.Fake.Clear();

        var res = await admin.PostAsJsonAsync("/api/push/send", new { title = "t", body = "b" });

        Assert.True(res.IsSuccessStatusCode);
        Assert.Empty(h.Fake.Sent);
    }

    [Fact]
    public async Task An_absolute_url_is_sent_as_the_root_path()
    {
        await PushTestHost.GrantSendPushAsync(fx);
        var h = await PushSenderHost.Create(fx, respond: _ => 201);
        var (admin, _) = await h.SignedInClient("Admin");
        var (_, u) = await h.SignedInClient("User");
        await h.Subscribe("https://push.example/url", u);

        await admin.PostAsJsonAsync("/api/push/send", new { title = "t", body = "b", url = "https://evil.example/x" });

        var payload = System.Text.Json.JsonDocument.Parse(Assert.Single(h.Fake.Sent).Payload).RootElement;
        Assert.Equal("/", payload.GetProperty("url").GetString());
    }

    [Fact]
    public async Task Oversize_body_is_a_400_not_a_500()
    {
        await PushTestHost.GrantSendPushAsync(fx);
        var h = await PushSenderHost.Create(fx, respond: _ => 201);
        var (admin, _) = await h.SignedInClient("Admin");
        var res = await admin.PostAsJsonAsync("/api/push/send", new { title = "t", body = new string('x', 5000) });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Openapi_document_never_mentions_the_private_key()
    {
        var h = await PushSenderHost.Create(fx, respond: _ => 201);
        var doc = await h.Anonymous().GetStringAsync(OpenApiTagTests.DocumentPath);
        Assert.DoesNotContain(PushTestHost.PrivateKey, doc);
        Assert.Contains("/api/push/subscriptions", doc);
    }
}
