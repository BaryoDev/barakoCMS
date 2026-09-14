using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

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

        var notices = _factory.Email.Messages.Where(m => m.To == user.Email).ToList();
        notices.Should().ContainSingle("the response no longer says the account is locked, so this is the only way its owner learns it")
            .Which.Subject.Should().Contain("locked");
    }

    [Fact]
    public async Task Attempts_against_an_account_already_locked_send_nothing_more()
    {
        var user = await StoreUserAsync(failedAttempts: 5, lockoutUntil: DateTime.UtcNow.AddMinutes(15));

        (await LoginAsync(user.Username, "WrongPassword123!")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await LoginAsync(user.Username, Password)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        _factory.Email.Messages.Where(m => m.To == user.Email).Should().BeEmpty(
            "one notice per lock; a message per attempt would let anyone flood the owner's inbox");
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

    private async Task<(Guid Id, string Username, string Email)> StoreUserAsync(int failedAttempts, DateTime? lockoutUntil)
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
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(Password, 4),
            FailedLoginAttempts = failedAttempts,
            LockoutUntil = lockoutUntil,
        });
        await session.SaveChangesAsync(Ct);
        return (id, username, email);
    }

    private Task<HttpResponseMessage> LoginAsync(string username, string password) =>
        _client.PostAsJsonAsync("/api/auth/login", new { Username = username, Password = password }, Ct);
}
