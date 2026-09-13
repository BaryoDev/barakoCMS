using System.Collections.Concurrent;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// The shared fixture stops its host while the database is still there.
/// </summary>
/// <remarks>
/// It used to dispose the Postgres container first. Stopping the host then ran the projection
/// daemon and the workflow runner down against a refused connection, and Marten's coordinator threw
/// <c>ObjectDisposedException</c> from <c>StopAsync</c>. xUnit reports that as a cleanup failure on
/// every test in the collection, so one bad teardown turned a green run into 1501 failures.
///
/// That throw is a race and does not happen every run. What the order guarantees does: a hosted
/// service stopping with the host can still reach the database. This asserts that, which fails on the
/// old order every time instead of only when the race lands.
///
/// It boots a second fixture, which sets <c>DATABASE_URL</c> for the whole process. Its own
/// collection has parallelization off so no other host is built while that points here, and the
/// variables are put back before anything else runs.
/// </remarks>
[Collection(nameof(IntegrationTestFixtureTeardownTests))]
public class IntegrationTestFixtureTeardownTests
{
    [Fact]
    public async Task The_database_is_still_up_when_the_host_stops()
    {
        var saved = SavedVariables.Capture();
        var fixture = new ProbingFixture();

        try
        {
            await fixture.InitializeAsync();
        }
        finally
        {
            saved.Restore();
        }

        await fixture.DisposeAsync();

        fixture.Probe.Attempts.Should().NotBeEmpty("the probe has to have run when the host stopped");
        fixture.Probe.Attempts.Should().AllSatisfy(error => error.Should().BeNull());
    }

    private sealed class ProbingFixture : IntegrationTestFixture
    {
        public DatabaseProbe Probe { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureServices(services =>
            {
                Probe.ConnectionString = ConnectionString;
                services.AddSingleton<IHostedService>(Probe);
            });
        }
    }

    /// <summary>Opens a connection when the host stops, and records what went wrong, if anything.</summary>
    private sealed class DatabaseProbe : IHostedService
    {
        public string ConnectionString { get; set; } = string.Empty;

        public ConcurrentQueue<string?> Attempts { get; } = new();

        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            try
            {
                await using var connection = new NpgsqlConnection(ConnectionString);
                await connection.OpenAsync(CancellationToken.None);
                Attempts.Enqueue(null);
            }
            catch (Exception exception)
            {
                Attempts.Enqueue(exception.Message);
            }
        }
    }

    private sealed record SavedVariables(IReadOnlyDictionary<string, string?> Values)
    {
        private static readonly string[] Names =
            ["DATABASE_URL", "ConnectionStrings__DefaultConnection", "SKIP_SEEDER", "ASPNETCORE_ENVIRONMENT", "JWT__Key"];

        public static SavedVariables Capture() =>
            new(Names.ToDictionary(name => name, Environment.GetEnvironmentVariable));

        public void Restore()
        {
            foreach (var (name, value) in Values)
                Environment.SetEnvironmentVariable(name, value);
        }
    }
}

[CollectionDefinition(nameof(IntegrationTestFixtureTeardownTests), DisableParallelization = true)]
public class IntegrationTestFixtureTeardownCollection;
