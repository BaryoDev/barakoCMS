using System.Data.Common;
using barakoCMS.Infrastructure.Security;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace BarakoCMS.Tests.Security;

/// <summary>
/// The startup guard that refuses a database role with superuser, BYPASSRLS or a managed admin
/// membership outside Development, run against a real Postgres.
/// </summary>
/// <remarks>
/// The container is this class's own, because the tests create roles and roles belong to the
/// cluster. Every test makes its own role names. Passwords are generated here and the assertions
/// that look for one say what they looked for, not the text they searched.
/// </remarks>
[Collection("Integration1")]
public sealed class DatabaseRoleGuardTests(DatabaseRoleGuardTests.Postgres postgres)
    : IClassFixture<DatabaseRoleGuardTests.Postgres>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public sealed class Postgres : IAsyncLifetime
    {
        private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("pgvector/pgvector:pg16")
            .WithUsername("postgres")
            .WithPassword("Su1-" + Guid.NewGuid().ToString("N"))
            .Build();

        public ValueTask InitializeAsync() => new(_container.StartAsync());

        public async ValueTask DisposeAsync() => await _container.DisposeAsync();

        public string ConnectionString(string? user = null, string? password = null)
        {
            var builder = new NpgsqlConnectionStringBuilder(_container.GetConnectionString())
            {
                Host = "127.0.0.1",
                Pooling = false,
            };
            if (user is not null)
            {
                builder.Username = user;
                builder.Password = password;
            }

            return builder.ConnectionString;
        }

        public async Task ExecuteAsync(string sql)
        {
            await using var connection = new NpgsqlConnection(ConnectionString());
            await connection.OpenAsync(Ct);
            await using var command = new NpgsqlCommand(sql, connection);
            await command.ExecuteNonQueryAsync(Ct);
        }
    }

    private static IConfiguration Config(string? allow = null) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(allow is null
                ? new Dictionary<string, string?>()
                : new Dictionary<string, string?> { [DatabaseRoleGuard.AllowKey] = allow })
            .Build();

    private static string NewRole() => "guard_" + Guid.NewGuid().ToString("N")[..12];

    private static string NewPassword() => "Pw1-" + Guid.NewGuid().ToString("N");

    private Func<CancellationToken, Task<DbConnection>> OpenAs(string? user = null, string? password = null) =>
        async token =>
        {
            var connection = new NpgsqlConnection(postgres.ConnectionString(user, password));
            await connection.OpenAsync(token);
            return connection;
        };

    private Task AssertAsAsync(
        string? allow, string? user = null, string? password = null, string query = DatabaseRoleGuard.RoleQuery, object? start = null) =>
        DatabaseRoleGuard.AssertAsync(
            Config(allow), isDevelopment: false, OpenAs(user, password), NullLogger.Instance, start ?? new object(), query, Ct);

    private async Task<(string Role, string Password)> NewLoginAsync(string attributes)
    {
        var (role, password) = (NewRole(), NewPassword());
        await postgres.ExecuteAsync($"create role {role} login password '{password}' {attributes}");
        return (role, password);
    }

    private static bool Leaks(string message, string secret) => message.Contains(secret, StringComparison.Ordinal);

    [Fact]
    public async Task A_superuser_is_refused_with_the_role_the_fix_and_the_key()
    {
        var refusing = () => AssertAsAsync(allow: null);

        var thrown = (await refusing.Should().ThrowAsync<InvalidOperationException>()).Which;

        thrown.Message.Should().Contain("'postgres'").And.Contain("is a superuser")
            .And.Contain("scripts/db-app-role.sql").And.Contain("docs/deploy-on-a-managed-platform.md").And.Contain("Database:AllowSuperuser")
            .And.Contain("Database__AllowSuperuser");
        thrown.Message.Should().NotContain("not recognised");
    }

    [Fact]
    public async Task A_role_with_only_BYPASSRLS_is_refused()
    {
        var (role, password) = await NewLoginAsync("nosuperuser bypassrls");

        var refusing = () => AssertAsAsync(allow: null, role, password);

        var thrown = (await refusing.Should().ThrowAsync<InvalidOperationException>()).Which;

        thrown.Message.Should().Contain($"'{role}'").And.Contain("has BYPASSRLS");
        thrown.Message.Should().NotContain("is a superuser");
        Leaks(thrown.Message, password).Should().BeFalse("the refusal never repeats a password");
    }

    [Fact]
    public async Task A_member_of_a_managed_admin_role_is_refused()
    {
        await postgres.ExecuteAsync(
            "do $$ begin if not exists (select 1 from pg_roles where rolname = 'rds_superuser') then create role rds_superuser; end if; end $$");
        var (role, password) = await NewLoginAsync("nosuperuser nobypassrls");
        await postgres.ExecuteAsync($"grant rds_superuser to {role}");

        var refusing = () => AssertAsAsync(allow: null, role, password);

        (await refusing.Should().ThrowAsync<InvalidOperationException>()).Which.Message
            .Should().Contain($"'{role}'").And.Contain("'rds_superuser'");
    }

    [Fact]
    public async Task A_plain_role_in_a_BYPASSRLS_role_is_refused_and_the_role_is_named()
    {
        var group = NewRole();
        await postgres.ExecuteAsync($"create role {group} nologin nosuperuser bypassrls");
        var (role, password) = await NewLoginAsync("nosuperuser nobypassrls inherit");
        await postgres.ExecuteAsync($"grant {group} to {role}");

        var refusing = () => AssertAsAsync(allow: null, role, password);

        var thrown = (await refusing.Should().ThrowAsync<InvalidOperationException>()).Which;

        thrown.Message.Should().Contain($"'{role}'").And.Contain($"member of '{group}'")
            .And.Contain("superuser or has BYPASSRLS");
        thrown.Message.Should().NotContain("is a superuser");
    }

    [Fact]
    public async Task A_member_of_a_role_with_no_privileges_starts()
    {
        var group = NewRole();
        await postgres.ExecuteAsync($"create role {group} nologin nosuperuser nobypassrls");
        var (role, password) = await NewLoginAsync("nosuperuser nobypassrls");
        await postgres.ExecuteAsync($"grant {group} to {role}");

        var starting = () => AssertAsAsync(allow: null, role, password);

        await starting.Should().NotThrowAsync();
    }

    [Fact]
    public async Task An_unrecognised_value_warns_once_even_when_the_role_passes()
    {
        var (role, password) = await NewLoginAsync("nosuperuser nobypassrls");
        var logger = new ListLogger();
        var start = new object();
        Func<CancellationToken, Task<DbConnection>> open = OpenAs(role, password);

        await DatabaseRoleGuard.AssertAsync(Config("yes"), false, open, logger, start, DatabaseRoleGuard.RoleQuery, Ct);
        await DatabaseRoleGuard.AssertAsync(Config("yes"), false, open, logger, start, DatabaseRoleGuard.RoleQuery, Ct);

        logger.Warnings.Should().ContainSingle().Which.Should().Contain("not recognised")
            .And.Contain("Database:AllowSuperuser").And.NotContain("yes");
    }

    [Fact]
    public async Task A_recognised_value_logs_no_unrecognised_warning()
    {
        var (role, password) = await NewLoginAsync("nosuperuser nobypassrls");
        var logger = new ListLogger();

        await DatabaseRoleGuard.AssertAsync(Config("false"), false, OpenAs(role, password), logger, new object(), DatabaseRoleGuard.RoleQuery, Ct);

        logger.Warnings.Should().BeEmpty();
    }

    private IHost ProductionHostAs(string? user, string? password, string? allow)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = Environments.Production });
        if (allow is not null)
        {
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { [DatabaseRoleGuard.AllowKey] = allow });
        }

        builder.Services.AddMarten(postgres.ConnectionString(user, password));
        builder.Services.AddSingleton<DatabaseRoleGuardKey>();
        builder.Services.AddSingleton<IHostedService, DatabaseRoleBackstop>();
        return builder.Build();
    }

    [Fact]
    public async Task A_production_superuser_host_that_never_applies_the_schema_fails_to_start()
    {
        using var host = ProductionHostAs(user: null, password: null, allow: null);

        var starting = () => host.StartAsync(Ct);

        (await starting.Should().ThrowAsync<InvalidOperationException>()).Which.Message
            .Should().Contain("'postgres'").And.Contain("is a superuser");
    }

    [Fact]
    public async Task The_allow_key_lets_the_backstop_host_start()
    {
        using var host = ProductionHostAs(user: null, password: null, allow: "true");

        await host.StartAsync(Ct);
        await host.StopAsync(Ct);
    }

    [Fact]
    public async Task The_backstop_skips_when_the_check_already_ran_for_the_host()
    {
        var (role, password) = await NewLoginAsync("nosuperuser nobypassrls");
        using var host = ProductionHostAs(role, password, allow: null);
        var services = host.Services;

        await DatabaseRoleGuard.AssertAsync(
            services.GetRequiredService<IConfiguration>(),
            isDevelopment: false,
            services.GetRequiredService<IDocumentStore>(),
            NullLogger.Instance,
            services.GetRequiredService<DatabaseRoleGuardKey>(),
            Ct);

        // A second check would now refuse. Only a skipped one lets the host start.
        await postgres.ExecuteAsync($"alter role {role} bypassrls");

        await host.StartAsync(Ct);
        await host.StopAsync(Ct);
    }

    [Fact]
    public async Task A_plain_role_made_in_the_test_starts()
    {
        var (role, password) = await NewLoginAsync("nosuperuser nobypassrls nocreaterole nocreatedb");

        var starting = () => AssertAsAsync(allow: null, role, password);

        await starting.Should().NotThrowAsync();
    }

    [Fact]
    public async Task No_row_for_the_role_is_refused()
    {
        var refusing = () => AssertAsAsync(
            allow: null, query: "select r.rolname::text, r.rolsuper, r.rolbypassrls, null::text from pg_roles r where false");

        (await refusing.Should().ThrowAsync<InvalidOperationException>()).Which.Message
            .Should().Contain("could not read").And.Contain("Database:AllowSuperuser");
    }

    [Fact]
    public async Task A_null_attribute_is_refused()
    {
        var refusing = () => AssertAsAsync(allow: null, query: "select current_user::text, null::bool, false, null::text");

        (await refusing.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("could not read");
    }

    [Fact]
    public async Task A_query_error_is_refused_without_the_server_text()
    {
        var refusing = () => AssertAsAsync(allow: null, query: "select no_such_column from pg_roles");

        var thrown = (await refusing.Should().ThrowAsync<InvalidOperationException>()).Which;

        thrown.Message.Should().Contain("could not read").And.Contain("PostgresException");
        thrown.Message.Should().NotContain("no_such_column");
    }

    [Theory]
    [InlineData("true")]
    [InlineData("True")]
    [InlineData("TRUE")]
    public async Task Only_true_allows_a_superuser(string value)
    {
        var starting = () => AssertAsAsync(value);

        await starting.Should().NotThrowAsync();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("false")]
    [InlineData("False")]
    [InlineData("FALSE")]
    public async Task A_blank_or_false_value_keeps_the_refusal(string? value)
    {
        var refusing = () => AssertAsAsync(value);

        (await refusing.Should().ThrowAsync<InvalidOperationException>()).Which.Message
            .Should().Contain("is a superuser").And.NotContain("not recognised");
    }

    [Theory]
    [InlineData("yes")]
    [InlineData("1")]
    [InlineData(" true ")]
    [InlineData("on")]
    [InlineData("truee")]
    public async Task Any_other_value_keeps_the_refusal_and_says_it_is_not_recognised(string value)
    {
        var refusing = () => AssertAsAsync(value);

        var thrown = (await refusing.Should().ThrowAsync<InvalidOperationException>()).Which;

        thrown.Message.Should().Contain("is a superuser").And.Contain("not recognised");
        thrown.Message.Contains($"'{value}'", StringComparison.Ordinal).Should().BeFalse("the value is not echoed");
    }

    [Fact]
    public async Task Development_does_not_ask_the_database()
    {
        var starting = () => DatabaseRoleGuard.AssertAsync(
            Config(), isDevelopment: true, _ => throw new InvalidOperationException("opened"),
            NullLogger.Instance, new object(), DatabaseRoleGuard.RoleQuery, Ct);

        await starting.Should().NotThrowAsync();
    }

    [Fact]
    public async Task An_allowed_superuser_logs_one_warning_per_start()
    {
        var logger = new ListLogger();
        var start = new object();
        Func<CancellationToken, Task<DbConnection>> neverOpened = _ => throw new InvalidOperationException("opened");

        await DatabaseRoleGuard.AssertAsync(Config("true"), false, neverOpened, logger, start, DatabaseRoleGuard.RoleQuery, Ct);
        await DatabaseRoleGuard.AssertAsync(Config("True"), false, neverOpened, logger, start, DatabaseRoleGuard.RoleQuery, Ct);

        logger.Warnings.Should().ContainSingle().Which.Should().Contain("Database:AllowSuperuser");

        await DatabaseRoleGuard.AssertAsync(Config("true"), false, neverOpened, logger, new object(), DatabaseRoleGuard.RoleQuery, Ct);

        logger.Warnings.Should().HaveCount(2, "another start warns again");
    }

    private sealed class ListLogger : ILogger
    {
        public List<string> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
            {
                Warnings.Add(formatter(state, exception));
            }
        }
    }
}
