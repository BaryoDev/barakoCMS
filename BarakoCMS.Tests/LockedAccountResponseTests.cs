using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using barakoCMS.Core.Interfaces;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Marten.Patching;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OtpNet;
using Xunit;
using MfaModels = barakoCMS.Features.Auth.Mfa;

namespace BarakoCMS.Tests;

/// <summary>
/// A locked account is indistinguishable from a wrong password and from a username that does not
/// exist, and its owner still finds out it was locked (#640).
/// </summary>
/// <remarks>
/// An account that does not exist never locks, so a distinct answer for a locked one told anyone
/// which usernames were real, and a stated lock duration told them how often to re-lock it.
/// </remarks>
[Collection("Sequential")]
public class LockedAccountResponseTests
{
    private const string Password = "ValidPassword123!";

    private static int _ipCounter;

    private readonly IntegrationTestFixture _factory;
    private readonly HttpClient _client;

    public LockedAccountResponseTests(IntegrationTestFixture factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
        _client.DefaultRequestHeaders.Add(
            TestRemoteIpFilter.Header, $"2001:db8:640::{Interlocked.Increment(ref _ipCounter):x}");
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly TimeSpan NoticeWait = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The locked account is given its correct password. That is the case that has to look like a
    /// failure too: a wrong password on a locked account proves nothing if the right one is told apart.
    /// </summary>
    [Fact]
    public async Task A_locked_account_answers_exactly_as_a_wrong_password_and_an_unknown_username()
    {
        var locked = await StoreUserAsync(failedAttempts: 5, lockoutUntil: DateTime.UtcNow.AddMinutes(15));
        var unlocked = await StoreUserAsync(failedAttempts: 0, lockoutUntil: null);
        var unknown = $"nobody{Guid.NewGuid():N}"[..20];

        var responses = new[]
        {
            await LoginAsync(unknown, Password),
            await LoginAsync(unlocked.Username, "WrongPassword123!"),
            await LoginAsync(locked.Username, Password),
        };

        responses.Should().HaveCount(3);
        responses.Should().OnlyContain(r => r.StatusCode == HttpStatusCode.Unauthorized,
            "every one of the three is a refused sign-in, and a different status is an answer to 'does this account exist'");

        var bodies = new List<string>();
        foreach (var response in responses)
        {
            bodies.Add(Comparable(await response.Content.ReadAsStringAsync(Ct)));
        }

        bodies.Should().HaveCount(3);
        bodies.Distinct().Should().ContainSingle("the bodies have to match too, lockout minutes included: {0}",
            string.Join(" | ", bodies));
    }

    [Fact]
    public async Task Locking_an_account_emails_its_owner()
    {
        var user = await StoreUserAsync(failedAttempts: 4, lockoutUntil: null);

        (await LoginAsync(user.Username, "WrongPassword123!")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        await using (var session = _factory.Services.GetRequiredService<IDocumentStore>().QuerySession())
        {
            (await session.LoadAsync<User>(user.Id, Ct))!.LockoutUntil.Should().NotBeNull(
                "the fifth failure locks the account, which is the event the owner is told about");
        }

        await WaitForNoticeAsync(user.Email);
        await SettleNoticesAsync();

        var notices = LockoutNotices(user.Email).ToList();
        notices.Should().ContainSingle("the response no longer says the account is locked, so this is the only way its owner learns it")
            .Which.Subject.Should().Contain("locked");
    }

    [Fact]
    public async Task Attempts_against_an_account_already_locked_send_nothing_more()
    {
        var user = await StoreUserAsync(failedAttempts: 5, lockoutUntil: DateTime.UtcNow.AddMinutes(15));

        (await LoginAsync(user.Username, "WrongPassword123!")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await LoginAsync(user.Username, Password)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        await SettleNoticesAsync();

        LockoutNotices(user.Email).Should().BeEmpty(
            "one notice per lock; a message per attempt would let anyone flood the owner's inbox");
    }

    /// <summary>
    /// Every one of these requests sees the counter past the threshold. Only the one whose update sets
    /// the lock may send, or a burst of guesses becomes a burst of mail.
    /// </summary>
    [Fact]
    public async Task Concurrent_failures_that_lock_an_account_send_one_notice()
    {
        var user = await StoreUserAsync(failedAttempts: 4, lockoutUntil: null);

        var responses = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => LoginAsync(user.Username, "WrongPassword123!", NextIp())));

        responses.Should().HaveCount(8);
        responses.Should().OnlyContain(r => r.StatusCode == HttpStatusCode.Unauthorized);

        await WaitForNoticeAsync(user.Email);
        await SettleNoticesAsync();

        LockoutNotices(user.Email).Should().ContainSingle(
            "eight failures past the threshold are one lock, and one lock is one notice");
    }

    /// <summary>
    /// Wrong TOTP codes count toward the same lock as wrong passwords, so a lock set there is reported
    /// to the owner the same way.
    /// </summary>
    [Fact]
    public async Task Locking_an_account_through_mfa_emails_its_owner_once()
    {
        var user = await StoreUserAsync(failedAttempts: 0, lockoutUntil: null);
        await EnrollMfaAsync(user.Id);

        var login = await LoginAsync(user.Username, Password, NextIp());
        login.StatusCode.Should().Be(HttpStatusCode.OK);
        var challenge = (await login.Content.ReadFromJsonAsync<barakoCMS.Features.Auth.Login.Response>(Ct))!.MfaChallengeToken;
        challenge.Should().NotBeNullOrEmpty();

        for (var i = 0; i < 6; i++)
        {
            var verify = await PostAsync("/api/auth/mfa/verify",
                new MfaModels.VerifyRequest { ChallengeToken = challenge!, Code = "000000" }, NextIp());
            ((int)verify.StatusCode).Should().BeInRange(400, 499);
        }

        await using (var session = _factory.Services.GetRequiredService<IDocumentStore>().QuerySession())
        {
            (await session.LoadAsync<User>(user.Id, Ct))!.LockoutUntil.Should().NotBeNull(
                "five wrong codes lock the account");
        }

        await WaitForNoticeAsync(user.Email);
        await SettleNoticesAsync();

        LockoutNotices(user.Email).Should().ContainSingle(
            "the fifth wrong code set the lock and the sixth found it already set");
    }

    /// <summary>
    /// The attempt that locks an account must not take longer than a refused sign-in for a username
    /// that does not exist, however slow the mail provider is. A difference of a provider round trip
    /// tells a caller which usernames are real.
    /// </summary>
    [Fact]
    public async Task The_attempt_that_locks_an_account_does_not_wait_for_the_notice()
    {
        var slow = new SlowEmailService(TimeSpan.FromSeconds(3));
        await using var host = _factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IEmailService>();
            services.AddSingleton<IEmailService>(slow);
        }));
        var client = host.CreateClient();

        var hash = barakoCMS.Infrastructure.Auth.PasswordHashing.Hash(Password);
        var warm = await StoreUserAsync(failedAttempts: 0, lockoutUntil: null, hash);
        var user = await StoreUserAsync(failedAttempts: 4, lockoutUntil: null, hash);

        (await LoginAsync(client, $"nobody{Guid.NewGuid():N}"[..20], Password, NextIp())).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await LoginAsync(client, warm.Username, "WrongPassword123!", NextIp())).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var clock = Stopwatch.StartNew();
        (await LoginAsync(client, $"nobody{Guid.NewGuid():N}"[..20], Password, NextIp())).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var unknown = clock.Elapsed;

        clock.Restart();
        (await LoginAsync(client, user.Username, "WrongPassword123!", NextIp())).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var locking = clock.Elapsed;

        locking.Should().BeLessThan(unknown + TimeSpan.FromSeconds(1),
            "locking took {0:F0} ms and an unknown username {1:F0} ms, with a mail provider taking {2:F0} ms",
            locking.TotalMilliseconds, unknown.TotalMilliseconds, slow.Delay.TotalMilliseconds);

        (await WaitAsync(() => slow.SentTo.Contains(user.Email))).Should().BeTrue(
            "the notice is still sent, after the response");
    }

    /// <summary>
    /// A lock resets the counter. Otherwise one failure after the lock expires relocks the account and
    /// mails the owner again, and one guess per lock period keeps that going indefinitely.
    /// </summary>
    [Fact]
    public async Task One_failure_after_a_lock_expires_neither_relocks_nor_emails_again()
    {
        var user = await StoreUserAsync(failedAttempts: 4, lockoutUntil: null);

        (await LoginAsync(user.Username, "WrongPassword123!", NextIp())).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        await WaitForNoticeAsync(user.Email);

        var store = _factory.Services.GetRequiredService<IDocumentStore>();
        await using (var session = store.LightweightSession())
        {
            session.Patch<User>(user.Id).Set(x => x.LockoutUntil, DateTime.UtcNow.AddMinutes(-1));
            await session.SaveChangesAsync(Ct);
        }

        (await LoginAsync(user.Username, "WrongPassword123!", NextIp())).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        await SettleNoticesAsync();

        await using (var session = store.QuerySession())
        {
            var reloaded = await session.LoadAsync<User>(user.Id, Ct);
            reloaded!.LockoutUntil.Should().BeBefore(DateTime.UtcNow, "one failure is not five");
            reloaded.FailedLoginAttempts.Should().Be(1);
        }

        LockoutNotices(user.Email).Should().ContainSingle();
    }

    /// <summary>
    /// A failure reads the counter, and a successful sign-in can clear it before that failure goes on
    /// to lock. The lock has to look at the counter as it is then, not as the failure read it.
    /// </summary>
    [Fact]
    public async Task An_account_whose_counter_was_cleared_is_not_locked()
    {
        var user = await StoreUserAsync(failedAttempts: 0, lockoutUntil: null);
        var lockout = _factory.Services.GetRequiredService<barakoCMS.Infrastructure.Auth.AccountLockout>();

        await using (var session = _factory.Services.GetRequiredService<IDocumentStore>().QuerySession())
        {
            var loaded = (await session.LoadAsync<User>(user.Id, Ct))!;
            (await lockout.TryLockAsync(loaded, Ct)).Should().BeFalse();
        }

        await using (var session = _factory.Services.GetRequiredService<IDocumentStore>().QuerySession())
        {
            (await session.LoadAsync<User>(user.Id, Ct))!.LockoutUntil.Should().BeNull();
        }
    }

    /// <summary>
    /// Waits until every notice queued before this call has been through the sender.
    /// </summary>
    /// <remarks>
    /// The sender works through its queue in order, one at a time. Locking one more account and
    /// waiting for that notice therefore means any notice queued earlier has already been sent, so an
    /// assertion that no further notice arrived is not just an assertion made too early.
    /// </remarks>
    private async Task SettleNoticesAsync()
    {
        var marker = await StoreUserAsync(failedAttempts: 4, lockoutUntil: null);
        (await LoginAsync(marker.Username, "WrongPassword123!", NextIp())).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        await WaitForNoticeAsync(marker.Email);
    }

    private async Task WaitForNoticeAsync(string email) =>
        (await WaitAsync(() => LockoutNotices(email).Any())).Should().BeTrue(
            "a lockout notice for {0} should arrive within {1}", email, NoticeWait);

    /// <summary>
    /// Lockout notices only. The same address also gets other mail in these tests, such as the notice
    /// that MFA was turned on.
    /// </summary>
    private List<RecordingEmailService.Sent> LockoutNotices(string email) =>
        _factory.Email.Messages.Where(m => m.To == email && m.Subject.Contains("was locked")).ToList();

    private static async Task<bool> WaitAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + NoticeWait;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                return false;
            }

            await Task.Delay(50, Ct);
        }

        return true;
    }

    private async Task EnrollMfaAsync(Guid userId)
    {
        using var authed = _factory.CreateClient();
        authed.DefaultRequestHeaders.Add(TestRemoteIpFilter.Header, NextIp());
        authed.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _factory.CreateToken(new[] { "SuperAdmin" }, userId.ToString()));

        var setup = await (await authed.PostAsync("/api/auth/mfa/setup", null, Ct))
            .Content.ReadFromJsonAsync<MfaModels.SetupResponse>(Ct);
        setup!.Secret.Should().NotBeNullOrEmpty();

        var code = new Totp(Base32Encoding.ToBytes(setup.Secret)).ComputeTotp();
        (await authed.PostAsJsonAsync("/api/auth/mfa/enable", new MfaModels.CodeRequest { Code = code }, Ct))
            .StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private static int _requestIpCounter;

    /// <summary>A fresh address per request, so the per-IP auth limit is never what answers.</summary>
    private static string NextIp() => $"2001:db8:640:1::{Interlocked.Increment(ref _requestIpCounter):x}";

    private sealed class SlowEmailService(TimeSpan delay) : IEmailService
    {
        private readonly ConcurrentQueue<string> _sentTo = new();

        public TimeSpan Delay { get; } = delay;

        public IReadOnlyCollection<string> SentTo => _sentTo.ToArray();

        public async Task SendEmailAsync(string to, string subject, string body, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Delay, cancellationToken);
            _sentTo.Enqueue(to.Trim().ToLowerInvariant());
        }
    }

    /// <summary>
    /// The body as the three cases can be compared: parsed, with a per-request trace id removed if the
    /// error format carries one, and serialised back in a stable form.
    /// </summary>
    private static string Comparable(string body)
    {
        var node = JsonNode.Parse(body);
        if (node is JsonObject obj)
        {
            obj.Remove("traceId");
        }

        return node?.ToJsonString() ?? string.Empty;
    }

    private async Task<(Guid Id, string Username, string Email)> StoreUserAsync(
        int failedAttempts, DateTime? lockoutUntil, string? passwordHash = null)
    {
        var id = Guid.NewGuid();
        var username = $"lk{id:N}"[..20];
        var email = $"{username}@example.com";
        await using var session = _factory.Services.GetRequiredService<IDocumentStore>().LightweightSession();
        session.Store(new User
        {
            Id = id,
            Username = username,
            Email = email,
            PasswordHash = passwordHash ?? BCrypt.Net.BCrypt.HashPassword(Password, 4),
            FailedLoginAttempts = failedAttempts,
            LockoutUntil = lockoutUntil,
        });
        await session.SaveChangesAsync(Ct);
        return (id, username, email);
    }

    private Task<HttpResponseMessage> LoginAsync(string username, string password) =>
        _client.PostAsJsonAsync("/api/auth/login", new { Username = username, Password = password }, Ct);

    private Task<HttpResponseMessage> LoginAsync(string username, string password, string ip) =>
        LoginAsync(_factory.CreateClient(), username, password, ip);

    private static Task<HttpResponseMessage> LoginAsync(HttpClient client, string username, string password, string ip)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login")
        {
            Content = JsonContent.Create(new { Username = username, Password = password }),
        };
        request.Headers.Add(TestRemoteIpFilter.Header, ip);
        return client.SendAsync(request, Ct);
    }

    private Task<HttpResponseMessage> PostAsync<T>(string path, T body, string ip)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        request.Headers.Add(TestRemoteIpFilter.Header, ip);
        return _factory.CreateClient().SendAsync(request, Ct);
    }
}
