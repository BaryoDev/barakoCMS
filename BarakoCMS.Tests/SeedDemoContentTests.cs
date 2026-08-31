using FluentAssertions;
using Marten;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using barakoCMS.Data;
using barakoCMS.Models;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// The demo login accounts were gated to Development. The demo content around them was not.
/// </summary>
/// <remarks>
/// Every production first run came up with an AttendanceRecord content type nobody asked for, fake
/// records, and an active "Attendance Confirmation Email" workflow. The workflow is the part that
/// bites: it is stored active, and once an operator configures Resend it sends real mail to whatever
/// address a record's Email field holds. A demo fixture becomes an outbound mail path in someone's
/// production system.
///
/// The environment alone could not decide it, because the quickstart runs as Production and a
/// developer trying the product there is exactly who wants the sample content. So the switch is
/// explicit, with the environment supplying the default.
///
/// Both directions are asserted. A gate that refuses everything would pass every "production gets
/// nothing" test on its own. See issue #283.
/// </remarks>
[Collection("Sequential")]
public class SeedDemoContentTests
{
    private const string ContentTypeName = "AttendanceRecord";
    private const string WorkflowName = "Attendance Confirmation Email";

    private readonly IntegrationTestFixture _fixture;

    public SeedDemoContentTests(IntegrationTestFixture fixture) => _fixture = fixture;

    private static IConfiguration Config(string? demoContent) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { { "Seed:DemoContent", demoContent } })
            .Build();

    private static T WithEnvironment<T>(string? environment, Func<T> body)
    {
        var previous = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
        Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", environment);
        try
        {
            return body();
        }
        finally
        {
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", previous);
        }
    }

    [Fact]
    public void Unset_the_demo_content_switch_follows_the_environment()
    {
        WithEnvironment("Production", () => DataSeeder.SeedsDemoContent(Config(null)))
            .Should().BeFalse("a production instance must not come up holding somebody else's demo data");

        WithEnvironment("Development", () => DataSeeder.SeedsDemoContent(Config(null)))
            .Should().BeTrue("a local run is unchanged, and the demo content is the worked example");
    }

    [Fact]
    public void The_switch_overrides_the_environment_in_both_directions()
    {
        WithEnvironment("Production", () => DataSeeder.SeedsDemoContent(Config("true")))
            .Should().BeTrue("the quickstart runs as Production, and someone trying the product there "
                           + "has to be able to ask for the sample content");

        WithEnvironment("Development", () => DataSeeder.SeedsDemoContent(Config("false")))
            .Should().BeFalse();
    }

    private sealed class HostShim : IHost
    {
        public HostShim(IServiceProvider services) => Services = services;

        public IServiceProvider Services { get; }

        public void Dispose() { }

        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    /// <summary>
    /// The wiring, not just the predicate. The three attendance seeders used to be called
    /// unconditionally, so a test of the predicate alone would pass against the broken code.
    /// </summary>
    /// <remarks>
    /// The fixture host sets SKIP_SEEDER, so none of this exists before the test runs. Both cases
    /// live in one method because the order matters: "off leaves nothing behind" is only meaningful
    /// before "on creates it".
    /// </remarks>
    [Fact]
    public async Task SeedAsync_creates_the_demo_content_only_when_the_switch_is_on()
    {
        var ct = TestContext.Current.CancellationToken;

        await using (var setup = _fixture.Services.GetRequiredService<IDocumentStore>().LightweightSession())
        {
            // SeedUsersAsync only runs on an empty user table, and the demo accounts it would create
            // are not what this test is about.
            setup.Store(new User
            {
                Id = Guid.NewGuid(),
                Username = $"seed_gate_probe_{Guid.NewGuid():N}",
                Email = "seed-gate-probe@example.com",
                PasswordHash = "not-a-real-hash",
                CreatedAt = DateTime.UtcNow
            });
            await setup.SaveChangesAsync(ct);
        }

        try
        {
            await DataSeeder.SeedAsync(new HostShim(_fixture.WithSetting("Seed:DemoContent", "false").Services));

            (await CountDemoContentAsync(ct)).Should().Be(
                (ContentTypes: 0, Workflows: 0, Records: 0),
                "a Production boot creates roles and the configured admin, and nothing else");

            await DataSeeder.SeedAsync(new HostShim(_fixture.WithSetting("Seed:DemoContent", "true").Services));

            var after = await CountDemoContentAsync(ct);
            after.ContentTypes.Should().Be(1);
            after.Workflows.Should().Be(1);
            after.Records.Should().BeGreaterThan(0);
        }
        finally
        {
            await using var cleanup = _fixture.Services.GetRequiredService<IDocumentStore>().LightweightSession();
            cleanup.DeleteWhere<ContentType>(x => x.Name == ContentTypeName);
            cleanup.DeleteWhere<WorkflowDefinition>(x => x.Name == WorkflowName);
            cleanup.DeleteWhere<Content>(x => x.ContentType == ContentTypeName);
            await cleanup.SaveChangesAsync(ct);
        }
    }

    private async Task<(int ContentTypes, int Workflows, int Records)> CountDemoContentAsync(CancellationToken ct)
    {
        await using var session = _fixture.Services.GetRequiredService<IDocumentStore>().LightweightSession();

        return (
            await session.Query<ContentType>().CountAsync(x => x.Name == ContentTypeName, ct),
            await session.Query<WorkflowDefinition>().CountAsync(x => x.Name == WorkflowName, ct),
            await session.Query<Content>().CountAsync(x => x.ContentType == ContentTypeName, ct));
    }
}
