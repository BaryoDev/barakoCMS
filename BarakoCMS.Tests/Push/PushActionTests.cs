using barakoCMS.Features.Workflows;
using barakoCMS.Models;
using BarakoCMS.Push;
using Xunit;

namespace BarakoCMS.Tests.Push;

public class PushActionTests
{
    private sealed class RecordingSender : IPushSender
    {
        public List<(string? Tenant, PushNotification N)> Tenant = new();
        public Task<PushSendResult> SendToUsersAsync(string? t, IReadOnlyCollection<Guid> u, PushNotification n, CancellationToken ct)
            => Task.FromResult(new PushSendResult(0, 0, 0, 0, false));
        public Task<PushSendResult> SendToTenantAsync(string? t, PushNotification n, CancellationToken ct)
        {
            Tenant.Add((t, n));
            return Task.FromResult(new PushSendResult(1, 1, 0, 0, false));
        }
    }

    private static WorkflowContext ContextWith(string tenant, params (string Key, string Value)[] parameters) => new()
    {
        Parameters = parameters.ToDictionary(p => p.Key, p => p.Value),
        Content = new Content { Id = Guid.NewGuid(), ContentType = "announcement" },
        TriggerEvent = "Published",
        TenantId = tenant,
    };

    [Fact]
    public async Task Sends_title_body_and_url_to_the_workflow_tenant()
    {
        var sender = new RecordingSender();
        var ctx = ContextWith("my-club", ("Title", "Dinner"), ("Body", "Friday at 7"), ("Url", "/my-club/home"));

        var result = await new PushAction(sender).RunAsync(ctx, default);

        Assert.True(result.Succeeded);
        var (tenant, n) = Assert.Single(sender.Tenant);
        Assert.Equal("my-club", tenant);
        Assert.Equal("Dinner", n.Title);
        Assert.Equal("/my-club/home", n.Url);
    }

    [Fact]
    public async Task Long_body_is_truncated_to_140_chars()
    {
        var sender = new RecordingSender();
        await new PushAction(sender).RunAsync(ContextWith("t", ("Title", "T"), ("Body", new string('x', 500))), default);
        Assert.True(sender.Tenant[0].N.Body.Length <= 140);
        Assert.EndsWith("...", sender.Tenant[0].N.Body);
    }

    [Fact]
    public async Task Long_title_is_truncated_to_80_chars()
    {
        var sender = new RecordingSender();
        await new PushAction(sender).RunAsync(ContextWith("t", ("Title", new string('x', 300)), ("Body", "b")), default);
        Assert.True(sender.Tenant[0].N.Title.Length <= 80);
    }

    [Fact]
    public async Task An_absolute_url_becomes_the_root_path()
    {
        var sender = new RecordingSender();
        await new PushAction(sender).RunAsync(
            ContextWith("t", ("Title", "T"), ("Body", "b"), ("Url", "https://evil.example/x")), default);
        Assert.Equal("/", sender.Tenant[0].N.Url);
    }

    [Theory]
    [InlineData("Title")]
    [InlineData("Body")]
    public async Task A_missing_field_is_a_permanent_failure_and_sends_nothing(string missing)
    {
        var sender = new RecordingSender();
        var all = new[] { ("Title", "T"), ("Body", "b") };
        var ctx = ContextWith("t", all.Where(p => p.Item1 != missing).ToArray());

        var result = await new PushAction(sender).RunAsync(ctx, default);

        Assert.False(result.Succeeded);
        Assert.Empty(sender.Tenant);
    }
}
