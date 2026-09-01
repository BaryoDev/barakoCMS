using System.Net;
using System.Net.Http.Json;
using barakoCMS.Infrastructure.Auth;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// Self-registration has to prove the address it was handed (#268).
/// </summary>
/// <remarks>
/// <para>
/// Registration used to accept any well-formed address and store it on a user document immediately.
/// The obvious cost is spam accounts. The one that decides the design is next door: external
/// sign-in joins a provider's verified email to a local account by address and nothing else, and
/// it was hardened so that Google and LinkedIn must assert <c>email_verified</c>, GitHub reads only
/// the verified primary, and Facebook is refused unless an operator opts in. Self-registration
/// writing an unproven address into the same field undid that from the other direction: register as
/// somebody else's address and the next time they sign in with Google the provider puts them into
/// your account.
/// </para>
/// <para>
/// So the assertions below are about a user document not existing, not about a flag on one. A flag
/// would not have closed it, because the external path reads the address and never looks at a flag,
/// a password or a status.
/// </para>
/// </remarks>
[Collection("Sequential")]
public class EmailVerificationTests
{
    private const string Password = "ValidPassword123!";

    private static int _ipCounter;

    private readonly IntegrationTestFixture _factory;
    private readonly HttpClient _client;

    /// <summary>
    /// One bucket per test instance. Registration allows five per hour per IP and every test in the
    /// suite otherwise shares one, so a class that spends four of them leaves the next one to fail
    /// on a 429 that has nothing to do with it. The IPv6 documentation range is used because the two
    /// IPv4 documentation ranges are already crowded by other test classes counting from 1.
    /// </summary>
    private readonly string _ip = $"2001:db8:268::{Interlocked.Increment(ref _ipCounter):x}";

    public EmailVerificationTests(IntegrationTestFixture factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
        _client.DefaultRequestHeaders.Add(TestRemoteIpFilter.Header, _ip);
    }

    [Fact]
    public async Task A_registration_nobody_confirmed_cannot_sign_in()
    {
        var (username, email) = Identity();

        (await Register(username, email)).IsSuccessStatusCode.Should().BeTrue();

        var login = await Login(username);

        login.StatusCode.Should().NotBe(HttpStatusCode.OK,
            "the address was never proved, so there is no account yet, but login answered {0}",
            login.StatusCode);
        ((int)login.StatusCode).Should().BeLessThan(500,
            "refused is the answer, and a 500 would satisfy the line above while meaning login "
          + "crashed on a registration it should simply not have found");
    }

    /// <summary>
    /// The one that closes the external sign-in path, stated as the property that closes it: no user
    /// document holds an address nobody proved, so there is nothing for a provider to match onto.
    /// </summary>
    [Fact]
    public async Task Registration_writes_no_user_document_for_an_address_nobody_proved()
    {
        var (username, email) = Identity();

        await Register(username, email);

        (await FindUser(email)).Should().BeNull(
            "a user row carrying an unproven address is the landing pad the external providers were "
          + "hardened against: SocialSignIn matches a verified provider email to a local account by "
          + "address alone, so this row would hand its owner's Google sign-in to whoever created it");

        var pending = await FindPending(email);
        pending.Should().NotBeNull("the request has to be recorded somewhere, or the emailed token verifies nothing");
        pending!.Consumed.Should().BeFalse();
        pending.ExpiresAt.Should().BeAfter(DateTime.UtcNow);
        pending.PasswordHash.Should().NotBe(Password, "the plaintext password is not stored here either");
    }

    [Fact]
    public async Task A_confirmed_registration_becomes_an_account_that_can_sign_in()
    {
        var (username, email) = Identity();
        var token = await SeedPendingAsync(username, email, DateTime.UtcNow.AddHours(1));

        var verify = await Verify(token);

        verify.StatusCode.Should().Be(HttpStatusCode.OK, "the token is the one that was recorded and it has not expired");
        (await FindUser(email)).Should().NotBeNull("confirming the address is what creates the account");

        var login = await Login(username);
        login.StatusCode.Should().Be(HttpStatusCode.OK, "and the account it creates is a usable one");
    }

    [Fact]
    public async Task A_verification_token_works_exactly_once()
    {
        var (username, email) = Identity();
        var token = await SeedPendingAsync(username, email, DateTime.UtcNow.AddHours(1));

        var first = await Verify(token);
        first.StatusCode.Should().Be(HttpStatusCode.OK, "the control: the token is good the first time");

        var second = await Verify(token);
        second.StatusCode.Should().NotBe(HttpStatusCode.OK, "a spent token cannot be replayed");
        ((int)second.StatusCode).Should().BeLessThan(500, "and replay is a refusal, not a server error");

        (await CountUsers(email)).Should().Be(1, "one token, one account");
    }

    [Fact]
    public async Task An_expired_verification_token_is_refused()
    {
        var (username, email) = Identity();
        var token = await SeedPendingAsync(username, email, DateTime.UtcNow.AddMinutes(-1));

        var verify = await Verify(token);

        verify.StatusCode.Should().NotBe(HttpStatusCode.OK, "the window closed");
        ((int)verify.StatusCode).Should().BeLessThan(500);
        (await FindUser(email)).Should().BeNull("and no account came out of it");
    }

    [Fact]
    public async Task A_token_that_was_never_issued_is_refused_and_says_nothing()
    {
        var refused = await Verify($"{Guid.NewGuid():N}.aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        var malformed = await Verify("not-a-token");

        refused.StatusCode.Should().NotBe(HttpStatusCode.OK);
        ((int)refused.StatusCode).Should().BeLessThan(500);
        malformed.StatusCode.Should().NotBe(HttpStatusCode.OK);
        ((int)malformed.StatusCode).Should().BeLessThan(500);
    }

    /// <summary>
    /// Registering an address that already has an account answers exactly as registering a fresh one
    /// does, byte for byte. It used to answer 400 "Username or Email already exists", which is an
    /// enumeration oracle on an anonymous endpoint, next to a login endpoint that goes to the length
    /// of a dummy BCrypt verify to avoid being one.
    /// </summary>
    [Fact]
    public async Task Registering_an_address_that_already_exists_answers_exactly_as_a_new_one()
    {
        var (takenUsername, takenEmail) = Identity();
        await SeedUserAsync(takenUsername, takenEmail);

        var (_, freshEmail) = Identity();

        var known = await Register($"a{Guid.NewGuid():N}"[..20], takenEmail);
        var unknown = await Register($"b{Guid.NewGuid():N}"[..20], freshEmail);

        known.StatusCode.Should().Be(unknown.StatusCode, "the status must not say whether the address is known");
        (await known.Content.ReadAsStringAsync()).Should().Be(
            await unknown.Content.ReadAsStringAsync(),
            "and neither must the body");
    }

    /// <summary>
    /// The token that reaches the mailbox is the one whose hash was stored, and it names the row it
    /// unlocks.
    /// </summary>
    /// <remarks>
    /// Driven through the service with a recording transport rather than over HTTP, because the
    /// claim is about what was issued and recorded, not about mail being delivered. Without this the
    /// endpoint tests above would still pass against an implementation that emailed one token and
    /// stored the hash of another, and registration would be permanently broken in production while
    /// the suite stayed green.
    /// </remarks>
    [Fact]
    public async Task The_token_that_is_emailed_is_the_one_that_was_recorded()
    {
        var (username, email) = Identity();

        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var mail = new RecordingEmailService();
        var service = new barakoCMS.Infrastructure.Services.EmailVerificationService(
            session,
            mail,
            scope.ServiceProvider.GetRequiredService<IConfiguration>(),
            NullLogger<barakoCMS.Infrastructure.Services.EmailVerificationService>.Instance);

        var sent = await service.IssueAsync(username, email, BCrypt.Net.BCrypt.HashPassword(Password), TestContext.Current.CancellationToken);

        sent.Should().BeTrue();
        mail.Messages.Should().HaveCount(1, "one registration, one email");

        var token = mail.LastVerificationTokenFor(email);
        token.Should().NotBeNull("the message has to carry a token in the shape the verify endpoint parses");

        EmailVerificationToken.TryParse(token, out var pendingId, out var secret)
            .Should().BeTrue("the emailed token has to parse");

        var pending = await session.LoadAsync<PendingRegistration>(pendingId, TestContext.Current.CancellationToken);
        pending.Should().NotBeNull("the id half of the token names the row it unlocks");
        pending!.Email.Should().Be(email.ToLowerInvariant());
        EmailVerificationToken.Matches(secret, pending.TokenHash).Should().BeTrue(
            "and the secret half has to verify against the hash that was stored, which is the whole "
          + "point of storing a hash rather than the token");
    }

    [Fact]
    public async Task Registering_the_same_address_twice_leaves_only_the_newer_token_live()
    {
        var (username, email) = Identity();

        await Register(username, email);
        var first = _factory.Email.LastVerificationTokenFor(email);
        first.Should().NotBeNull();

        await Register(username, email);
        var second = _factory.Email.LastVerificationTokenFor(email);
        second.Should().NotBeNull().And.NotBe(first, "a second attempt issues a new token");

        var stale = await Verify(first!);
        stale.StatusCode.Should().NotBe(HttpStatusCode.OK,
            "the superseded token would otherwise still create an account with whatever the first "
          + "attempt named, which is not what the person who registered second asked for");

        (await Verify(second!)).StatusCode.Should().Be(HttpStatusCode.OK, "the current one still works");
    }

    /// <summary>
    /// Two live tokens for one address cannot produce two accounts, and the second cannot produce an
    /// account at all once the first has.
    /// </summary>
    /// <remarks>
    /// Supersession in <c>IssueAsync</c> is a read, then an update of what it found, then an insert.
    /// Two registrations for the same address arriving together can both read no outstanding row and
    /// both insert, so the "only the newer token is live" property is best effort and this is the
    /// state a race leaves behind. Seeding both directly is the point: it reproduces that state
    /// without racing anything, so the test is about the consequence rather than the timing.
    ///
    /// The property that actually protects the address is enforced at verify, not at issue. The
    /// second token is refused because an account for the address now exists, and it is spent on the
    /// way out so it cannot be retried until the collision clears.
    ///
    /// The two registrations name different usernames deliberately. Verify refuses on username or
    /// email, and matching usernames would let this pass while the email half did nothing.
    /// </remarks>
    [Fact]
    public async Task Two_live_tokens_for_one_address_still_produce_one_account()
    {
        var (first, email) = Identity();
        var (second, _) = Identity();

        var firstToken = await SeedPendingAsync(first, email, DateTime.UtcNow.AddHours(1));
        var secondToken = await SeedPendingAsync(second, email, DateTime.UtcNow.AddHours(1));

        (await Verify(firstToken)).StatusCode.Should().Be(HttpStatusCode.OK);

        var loser = await Verify(secondToken);
        loser.StatusCode.Should().NotBe(HttpStatusCode.OK,
            "the address already has an account, so this token has nothing left to create");

        (await CountUsers(email)).Should().Be(1, "one address, one account, whatever the issue path left behind");
        (await FindUser(email))!.Username.Should().Be(first, "the token that was verified first is the one that decided");

        // The assertion that separates the two mechanisms. A unique index on User.Email stops the
        // second account whatever the endpoint does, so every assertion above passes with the
        // collision check removed: the insert fails, the save is rolled back and the caller is
        // refused. What the check adds is spending the token inside a transaction that succeeds.
        // Without it the row stays live until it expires, and every retry re-attempts the insert.
        (await PendingFor(second))!.Consumed.Should().BeTrue(
            "the refusal has to spend the token, or it can be retried until the collision clears");
    }

    private static (string Username, string Email) Identity()
    {
        var id = $"ev{Guid.NewGuid():N}"[..20];
        return (id, $"{id}@example.com");
    }

    private Task<HttpResponseMessage> Register(string username, string email) =>
        _client.PostAsJsonAsync("/api/auth/register",
            new { Username = username, Email = email, Password }, TestContext.Current.CancellationToken);

    private Task<HttpResponseMessage> Verify(string token) =>
        _client.PostAsJsonAsync("/api/auth/register/verify", new { Token = token }, TestContext.Current.CancellationToken);

    private Task<HttpResponseMessage> Login(string username) =>
        _client.PostAsJsonAsync("/api/auth/login",
            new { Username = username, Password }, TestContext.Current.CancellationToken);

    /// <summary>
    /// A pending registration with a token this test knows, so the expiry and single-use cases can be
    /// driven without waiting a day or reaching into an inbox.
    /// </summary>
    private async Task<string> SeedPendingAsync(string username, string email, DateTime expiresAt)
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();

        var pending = new PendingRegistration
        {
            Username = username,
            Email = email.ToLowerInvariant(),
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(Password),
            ExpiresAt = expiresAt,
        };
        var (token, hash) = EmailVerificationToken.Create(pending.Id);
        pending.TokenHash = hash;

        session.Store(pending);
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        return token;
    }

    private async Task SeedUserAsync(string username, string email)
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Store(new User
        {
            Id = Guid.NewGuid(),
            Username = username,
            Email = email.ToLowerInvariant(),
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(Password),
        });
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private async Task<User?> FindUser(string email) => (await UsersAsync(email)).FirstOrDefault();

    private async Task<int> CountUsers(string email) => (await UsersAsync(email)).Count;

    private async Task<IReadOnlyList<User>> UsersAsync(string email)
    {
        var normalized = email.ToLowerInvariant();
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        return await session.Query<User>()
            .Where(u => u.Email == normalized)
            .ToListAsync(TestContext.Current.CancellationToken);
    }

    private async Task<PendingRegistration?> PendingFor(string username)
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        return (await session.Query<PendingRegistration>()
            .Where(p => p.Username == username)
            .ToListAsync(TestContext.Current.CancellationToken)).FirstOrDefault();
    }

    private async Task<PendingRegistration?> FindPending(string email)
    {
        var normalized = email.ToLowerInvariant();
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        return (await session.Query<PendingRegistration>()
            .Where(p => p.Email == normalized)
            .ToListAsync(TestContext.Current.CancellationToken)).FirstOrDefault();
    }
}

/// <summary>
/// The startup guard. Turning verification off is a decision somebody has to have made, not one
/// arrived at by leaving a key unset, which is the shape <c>Erasure:Mode=None</c> already uses.
/// </summary>
public class EmailVerificationOptionsTests
{
    [Fact]
    public void Turning_verification_off_without_acknowledging_it_refuses_to_start()
    {
        var act = () => Build(required: "false", acknowledge: null).Validate();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*AcknowledgeUnverifiedRegistration*");
    }

    /// <summary>
    /// The positive controls. Without them a Validate that threw unconditionally would pass the case
    /// above, and a default that was silently false would too.
    /// </summary>
    [Theory]
    [InlineData(null, null, true)]
    [InlineData("true", null, true)]
    [InlineData("false", "true", false)]
    public void A_settled_configuration_is_accepted(string? required, string? acknowledge, bool expected)
    {
        var options = Build(required, acknowledge);

        options.Validate();
        options.Required.Should().Be(expected);
    }

    private static EmailVerificationOptions Build(string? required, string? acknowledge) =>
        EmailVerificationOptions.FromConfiguration(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [EmailVerificationOptions.RequiredKey] = required,
                [EmailVerificationOptions.AcknowledgeKey] = acknowledge,
            })
            .Build());
}
