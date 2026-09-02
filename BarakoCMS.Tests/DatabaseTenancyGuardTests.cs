using FluentAssertions;
using Marten;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using barakoCMS.Infrastructure.Multitenancy;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// The startup guard that stops database tenancy being on and inert.
/// </summary>
/// <remarks>
/// A Postgres superuser bypasses row level security completely, whatever is on the table. Every
/// deployment this repository ships connects as `postgres`. So turning the setting on without also
/// changing who the application connects as would apply policies to every conjoined table, have them
/// appear in `pg_policies`, satisfy any check that asks whether row level security is enabled, and
/// enforce nothing.
///
/// That is the worst shape a security control can take, and it is the shape this project keeps
/// finding, so the guard refuses to start rather than log about it. These tests are what say the
/// refusal happens, since the whole point is a case that otherwise looks fine.
///
/// The test host connects as the container's superuser, which makes it the exact configuration the
/// guard exists to reject, and therefore the right place to test it.
/// </remarks>
[Collection("Sequential")]
public class DatabaseTenancyGuardTests
{
    private readonly IntegrationTestFixture _fixture;

    public DatabaseTenancyGuardTests(IntegrationTestFixture fixture) => _fixture = fixture;

    private static IConfiguration Config(string? value) => new ConfigurationBuilder()
        .AddInMemoryCollection(value is null
            ? new Dictionary<string, string?>()
            : new Dictionary<string, string?> { [DatabaseTenancy.EnabledKey] = value })
        .Build();

    private IDocumentStore Store => _fixture.Services.GetRequiredService<IDocumentStore>();

    [Fact]
    public async Task Enforcement_on_a_superuser_connection_refuses_to_start()
    {
        var refusing = async () => await DatabaseTenancy.AssertUsableAsync(
            Config("true"), Store, NullLogger.Instance, TestContext.Current.CancellationToken);

        var thrown = await refusing.Should().ThrowAsync<InvalidOperationException>(
            "policies on a superuser connection are applied and inert, which is worse than absent");

        // The message has to be actionable. An operator hitting this at deploy time needs to know
        // which role, why it is refused, and what to do, without reading the source.
        thrown.WithMessage("*superuser*")
              .WithMessage("*NOSUPERUSER*")
              .WithMessage("*tenancy-at-the-database*");
    }

    [Fact]
    public async Task Enforcement_off_checks_nothing_and_starts()
    {
        // The pairing, and the compatibility guarantee. Off is the default and by omission, so an
        // existing deployment that upgrades must not start failing on a connection it has always
        // used. Both spellings, because a test that only covered the explicit false would pass
        // against a guard that ran whenever the key was absent.
        var withFalse = async () => await DatabaseTenancy.AssertUsableAsync(
            Config("false"), Store, NullLogger.Instance, TestContext.Current.CancellationToken);

        var withNothing = async () => await DatabaseTenancy.AssertUsableAsync(
            Config(null), Store, NullLogger.Instance, TestContext.Current.CancellationToken);

        await withFalse.Should().NotThrowAsync();
        await withNothing.Should().NotThrowAsync();
    }
}
