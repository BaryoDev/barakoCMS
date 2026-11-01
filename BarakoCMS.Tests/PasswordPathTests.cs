using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Marten.Patching;
using Marten.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// The password change, admin reset and sign-in paths each write a user they loaded earlier. They
/// must change only their own fields, refuse an account with no password cleanly, and an admin
/// reset must lift a lockout.
/// </summary>
[Collection("Integration1")]
public class PasswordPathTests
{
    private const string OldPassword = "OldPassw0rd!x";
    private const string NewPassword = "BrandNewP@ss99";

    private static int _ipCounter;

    private readonly IntegrationTestFixture _factory;
    private readonly HttpClient _client;

    public PasswordPathTests(IntegrationTestFixture factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
        _client.DefaultRequestHeaders.Add(
            TestRemoteIpFilter.Header, $"2001:db8:a1:{Interlocked.Increment(ref _ipCounter):x}::1");
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Changing_the_password_of_an_account_with_no_password_is_refused_not_a_server_error()
    {
        var user = await StoreUserAsync(passwordHash: string.Empty);
        As(_factory.CreateToken(["User"], user.Id.ToString()));

        var res = await _client.PostAsJsonAsync(
            "/api/me/password", new { CurrentPassword = OldPassword, NewPassword }, Ct);

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest, await res.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task A_change_made_while_a_password_change_is_in_flight_is_kept()
    {
        var user = await StoreUserAsync(passwordHash: BCrypt.Net.BCrypt.HashPassword(OldPassword, 12));
        As(_factory.CreateToken(["User"], user.Id.ToString()));
        var changedEmail = $"changed-{user.Id:N}@example.com";

        var res = await SendWithEmailChangedBeforeTheUserWriteAsync(
            client => client.PostAsJsonAsync("/api/me/password", new { CurrentPassword = OldPassword, NewPassword }, Ct),
            user.Id, changedEmail);

        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync(Ct));
        var stored = await LoadAsync(user.Id);
        stored.Email.Should().Be(changedEmail, "the password change writes the hash and nothing else");
        BCrypt.Net.BCrypt.Verify(NewPassword, stored.PasswordHash).Should().BeTrue();
    }

    [Fact]
    public async Task A_change_made_while_an_admin_reset_is_in_flight_is_kept()
    {
        var user = await StoreUserAsync(passwordHash: BCrypt.Net.BCrypt.HashPassword(OldPassword, 4));
        As(await _factory.StoredUserTokenAsync("SuperAdmin"));
        var changedEmail = $"changed-{user.Id:N}@example.com";

        var res = await SendWithEmailChangedBeforeTheUserWriteAsync(
            client => client.PostAsJsonAsync($"/api/users/{user.Id}/password", new { NewPassword }, Ct),
            user.Id, changedEmail);

        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync(Ct));
        var stored = await LoadAsync(user.Id);
        BCrypt.Net.BCrypt.Verify(NewPassword, stored.PasswordHash).Should().BeTrue();
        stored.Email.Should().Be(changedEmail, "the reset writes the hash and nothing else");
    }

    [Fact]
    public async Task An_admin_reset_lifts_the_lockout()
    {
        var user = await StoreUserAsync(
            passwordHash: BCrypt.Net.BCrypt.HashPassword(OldPassword, 4),
            failedAttempts: 5, lockoutUntil: DateTime.UtcNow.AddMinutes(15));
        As(await _factory.StoredUserTokenAsync("SuperAdmin"));

        var res = await _client.PostAsJsonAsync($"/api/users/{user.Id}/password", new { NewPassword }, Ct);

        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync(Ct));
        var stored = await LoadAsync(user.Id);
        stored.LockoutUntil.Should().BeNull();
        stored.FailedLoginAttempts.Should().Be(0);
    }

    [Fact]
    public async Task A_change_made_while_a_sign_in_is_in_flight_is_kept()
    {
        var user = await StoreUserAsync(
            passwordHash: BCrypt.Net.BCrypt.HashPassword(OldPassword, 12), failedAttempts: 2);
        var changedEmail = $"changed-{user.Id:N}@example.com";

        var res = await SendWithEmailChangedBeforeTheUserWriteAsync(
            client => client.PostAsJsonAsync("/api/auth/login", new { user.Username, Password = OldPassword }, Ct),
            user.Id, changedEmail);

        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync(Ct));
        var stored = await LoadAsync(user.Id);
        stored.FailedLoginAttempts.Should().Be(0);
        stored.Email.Should().Be(changedEmail, "clearing the failure count writes that field and nothing else");
    }

    /// <summary>
    /// Sends the request and changes the user's email from the request's own session, just before the
    /// statement that writes the user runs. The endpoint has loaded the user by then, so a write that
    /// stores the whole document puts the old email back.
    /// </summary>
    private async Task<HttpResponseMessage> SendWithEmailChangedBeforeTheUserWriteAsync(
        Func<HttpClient, Task<HttpResponseMessage>> send, Guid userId, string email)
    {
        var hook = new BeforeUserWriteLogger(() => PatchEmailAsync(userId, email).GetAwaiter().GetResult());
        var derived = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services => services.ConfigureMarten(options => options.Logger(hook))));
        var client = derived.CreateClient();
        foreach (var header in _client.DefaultRequestHeaders)
        {
            client.DefaultRequestHeaders.Add(header.Key, header.Value);
        }

        var res = await send(client);
        hook.Fired.Should().BeTrue("the request must reach its write for the race to happen");
        return res;
    }

    private sealed class BeforeUserWriteLogger(Action onFirstUserWrite) : IMartenLogger, IMartenSessionLogger
    {
        private int _fired;

        public bool Fired => Volatile.Read(ref _fired) == 1;

        public IMartenSessionLogger StartSession(IQuerySession session) => this;

        public void SchemaChange(string sql) { }

        public void LogSuccess(NpgsqlCommand command) { }

        public void LogFailure(NpgsqlCommand command, Exception ex) { }

        public void LogSuccess(NpgsqlBatch batch) { }

        public void LogFailure(NpgsqlBatch batch, Exception ex) { }

        public void LogFailure(Exception ex, string message) { }

        public void RecordSavedChanges(IDocumentSession session, IChangeSet commit) { }

        public void OnBeforeExecute(NpgsqlCommand command) => Check(command.CommandText);

        public void OnBeforeExecute(NpgsqlBatch batch)
        {
            foreach (var command in batch.BatchCommands)
            {
                Check(command.CommandText);
            }
        }

        private void Check(string sql)
        {
            if (!WritesUser(sql) || Interlocked.Exchange(ref _fired, 1) == 1) return;
            onFirstUserWrite();
        }

        private static bool WritesUser(string sql)
        {
            var text = sql.TrimStart().ToLowerInvariant();
            return text.Contains("mt_upsert_user")
                || ((text.StartsWith("insert into") || text.StartsWith("update")) && text.Contains("mt_doc_user"));
        }
    }

    private void As(string token) =>
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

    private async Task<User> StoreUserAsync(
        string passwordHash, int failedAttempts = 0, DateTime? lockoutUntil = null)
    {
        var id = Guid.NewGuid();
        var user = new User
        {
            Id = id,
            Username = $"pp{id:N}"[..20],
            Email = $"pp{id:N}@example.com",
            PasswordHash = passwordHash,
            FailedLoginAttempts = failedAttempts,
            LockoutUntil = lockoutUntil,
        };
        await using var session = _factory.Services.GetRequiredService<IDocumentStore>().LightweightSession();
        session.Store(user);
        await session.SaveChangesAsync(Ct);
        return user;
    }

    private async Task PatchEmailAsync(Guid id, string email)
    {
        await using var session = _factory.Services.GetRequiredService<IDocumentStore>().LightweightSession();
        session.Patch<User>(id).Set(x => x.Email, email);
        await session.SaveChangesAsync(Ct);
    }

    private async Task<User> LoadAsync(Guid id)
    {
        await using var session = _factory.Services.GetRequiredService<IDocumentStore>().QuerySession();
        return (await session.LoadAsync<User>(id, Ct))!;
    }
}
