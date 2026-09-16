using System.Net;
using System.Net.Http.Json;
using barakoCMS.Infrastructure.Auth;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// A successful sign-in upgrades a hash made below the current BCrypt work factor (#639).
/// </summary>
/// <remarks>
/// The fixtures hash at factor 4, below <see cref="PasswordHashing.WorkFactor"/>, which is the state
/// every existing account is in the day the factor is raised.
/// </remarks>
[Collection("Sequential")]
public class PasswordRehashTests
{
    private const string Password = "ValidPassword123!";
    private const int OldWorkFactor = 4;

    private static int _ipCounter;

    private readonly IntegrationTestFixture _factory;
    private readonly HttpClient _client;

    public PasswordRehashTests(IntegrationTestFixture factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
        _client.DefaultRequestHeaders.Add(
            TestRemoteIpFilter.Header, $"2001:db8:639::{Interlocked.Increment(ref _ipCounter):x}");
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_successful_login_upgrades_a_hash_below_the_work_factor()
    {
        var (id, username) = await StoreUserAsync(BCrypt.Net.BCrypt.HashPassword(Password, OldWorkFactor));

        var login = await LoginAsync(username, Password);

        login.StatusCode.Should().Be(HttpStatusCode.OK, await login.Content.ReadAsStringAsync(Ct));
        var stored = await StoredHashAsync(id);
        WorkFactorOf(stored).Should().Be(PasswordHashing.WorkFactor,
            "the plaintext was verified, so this was the moment to bring the hash up to the current factor");
        BCrypt.Net.BCrypt.Verify(Password, stored).Should().BeTrue("and it is still a hash of the same password");
    }

    [Fact]
    public async Task A_hash_already_at_the_work_factor_is_left_as_it_is()
    {
        var hash = PasswordHashing.Hash(Password);
        var (id, username) = await StoreUserAsync(hash);

        (await LoginAsync(username, Password)).StatusCode.Should().Be(HttpStatusCode.OK);

        (await StoredHashAsync(id)).Should().Be(hash, "rehashing a current hash costs a hash on every sign-in for nothing");
    }

    [Fact]
    public async Task A_failed_login_does_not_touch_the_hash()
    {
        var hash = BCrypt.Net.BCrypt.HashPassword(Password, OldWorkFactor);
        var (id, username) = await StoreUserAsync(hash);

        (await LoginAsync(username, "WrongPassword123!")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        (await StoredHashAsync(id)).Should().Be(hash, "a wrong password is not a plaintext to hash");
    }

    /// <summary>
    /// A social sign-in account has no password. Hashing whatever was typed at it would hand it one.
    /// </summary>
    [Fact]
    public async Task An_account_with_no_password_is_left_without_one()
    {
        var (id, username) = await StoreUserAsync(string.Empty);

        (await LoginAsync(username, Password)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        (await StoredHashAsync(id)).Should().BeEmpty();
    }

    /// <summary>
    /// The write is made to fail for real, by a trigger on this one account's row, rather than through a
    /// seam in the endpoint. The sign-in has nothing else to write to that row, so only the rehash meets it.
    /// </summary>
    [Fact]
    public async Task A_rehash_that_cannot_be_written_does_not_fail_the_login()
    {
        var hash = BCrypt.Net.BCrypt.HashPassword(Password, OldWorkFactor);
        var (id, username) = await StoreUserAsync(hash);
        var trigger = $"refuse_rehash_{id:N}";

        await ExecAsync($"""
            create function {trigger}() returns trigger language plpgsql as $$
            begin
                raise exception 'rehash write refused by test';
            end $$;
            create trigger {trigger} before update on public.mt_doc_users
                for each row when (old.id = '{id}') execute function {trigger}();
            """);

        try
        {
            var login = await LoginAsync(username, Password);

            login.StatusCode.Should().Be(HttpStatusCode.OK,
                "the password was right; failing to upgrade its hash is not a reason to refuse it: {0}",
                await login.Content.ReadAsStringAsync(Ct));
            (await StoredHashAsync(id)).Should().Be(hash, "the trigger did refuse the write, so the test exercised the failure");
        }
        finally
        {
            await ExecAsync($"drop trigger if exists {trigger} on public.mt_doc_users; drop function if exists {trigger}();");
        }
    }

    private async Task<(Guid Id, string Username)> StoreUserAsync(string passwordHash)
    {
        var id = Guid.NewGuid();
        var username = $"rh{id:N}"[..20];
        await using var session = _factory.Services.GetRequiredService<IDocumentStore>().LightweightSession();
        session.Store(new User { Id = id, Username = username, Email = $"{username}@example.com", PasswordHash = passwordHash });
        await session.SaveChangesAsync(Ct);
        return (id, username);
    }

    private async Task<string> StoredHashAsync(Guid id)
    {
        await using var session = _factory.Services.GetRequiredService<IDocumentStore>().QuerySession();
        var user = await session.LoadAsync<User>(id, Ct);
        user.Should().NotBeNull();
        return user!.PasswordHash;
    }

    private Task<HttpResponseMessage> LoginAsync(string username, string password) =>
        _client.PostAsJsonAsync("/api/auth/login", new { Username = username, Password = password }, Ct);

    private static int WorkFactorOf(string hash) => int.Parse(BCrypt.Net.BCrypt.InterrogateHash(hash).WorkFactor);

    private async Task ExecAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(_factory.ConnectionString);
        await connection.OpenAsync(CancellationToken.None);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }
}
