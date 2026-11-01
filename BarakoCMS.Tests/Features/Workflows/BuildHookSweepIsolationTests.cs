using System.Net;
using barakoCMS.Features.Workflows.Actions;
using barakoCMS.Infrastructure.Http;
using barakoCMS.Infrastructure.Security;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BarakoCMS.Tests.Features.Workflows;

[Collection("Integration1")]
public class BuildHookSweepIsolationTests
{
    private const string HookUrl = "https://hook.example/build_hooks/isolation";

    private readonly IntegrationTestFixture _fixture;

    public BuildHookSweepIsolationTests(IntegrationTestFixture fixture) => _fixture = fixture;

    private IDocumentStore Store() => _fixture.Services.GetRequiredService<IDocumentStore>();

    private BuildHookSweepService Sweeper(FixedClock clock, CountingHandler handler) => new(
        Store(),
        new ConfigurationBuilder().Build(),
        new FixedClientFactory(handler),
        _fixture.Services.GetRequiredService<ISecretProtector>(),
        new OutboundAddressGuard(
            resolve: (_, _) => Task.FromResult(new[] { IPAddress.Parse("203.0.113.10") }),
            isBlocked: _ => false),
        NullLoggerFactory.Instance,
        clock);

    [Fact]
    public async Task A_row_that_throws_does_not_stop_the_rows_after_it()
    {
        var tenant = "buildhook-isolation-" + Guid.NewGuid().ToString("N");
        var clock = new FixedClock();
        var handler = new CountingHandler();
        var ct = TestContext.Current.CancellationToken;

        await using (var seed = Store().LightweightSession(tenant))
        {
            seed.Store(new PendingBuild
            {
                Id = "a-broken-row",
                ActionKey = "a-broken-row",
                TenantId = tenant,
                FireAt = clock.GetUtcNow().AddMinutes(-10),
                Parameters = null!,
            });
            seed.Store(new PendingBuild
            {
                Id = "b-good-row",
                ActionKey = "b-good-row",
                TenantId = tenant,
                FireAt = clock.GetUtcNow().AddMinutes(-5),
                Parameters = new Dictionary<string, string>
                {
                    ["Preset"] = "netlify",
                    ["Url"] = HookUrl,
                    ["WorkflowId"] = Guid.NewGuid().ToString(),
                },
            });
            await seed.SaveChangesAsync(ct);
        }

        int fired;
        await using (var session = Store().LightweightSession(tenant))
        {
            fired = await Sweeper(clock, handler).SweepTenantAsync(session, ct);
        }

        fired.Should().Be(1);
        handler.Calls.Should().Be(1, "the row after the broken one still fires");

        await using var check = Store().QuerySession(tenant);
        var left = await check.Query<PendingBuild>().ToListAsync(ct);
        left.Should().NotBeEmpty("the broken row stays behind until its claim times out");
        left.Select(p => p.Id).Should().Equal("a-broken-row");
        left[0].ClaimedAt.Should().NotBeNull();
    }

    private sealed class FixedClock : TimeProvider
    {
        private readonly DateTimeOffset _now = new(2100, 1, 1, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;
    }

    private sealed class CountingHandler : HttpMessageHandler
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
        }
    }

    private sealed class FixedClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
