using System.Security.Claims;
using System.Text;
using System.Text.Json.Nodes;
using barakoCMS.Infrastructure.Auth;
using barakoCMS.Infrastructure.Auth.Mfa;
using barakoCMS.Infrastructure.Multitenancy;
using barakoCMS.Infrastructure.Preview;
using barakoCMS.Infrastructure.Viewers;
using barakoCMS.Models;
using FastEndpoints;
using FastEndpoints.Security;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// The host's token minter signs what <c>JwtBearer.CreateToken</c> signed, at each of the five
/// places the core issues a token (#1408).
/// </summary>
/// <remarks>
/// <para>
/// Each test runs the real site with a minter that records what the site passes it, then gives the
/// same audience, expiry and claims to <c>JwtBearer.CreateToken</c> with the same key and issuer and
/// the same fake clock, and compares the two tokens.
/// </para>
/// <para>
/// The two are not compared as whole strings. In FastEndpoints.Security 8.3.0 the descriptor sets
/// <c>iat</c> from the <c>TimeProvider</c> but not <c>nbf</c>, and
/// <c>JsonWebTokenHandler</c> fills <c>nbf</c> from <c>DateTime.UtcNow</c>, so the old path reads
/// the system clock for it. The minter leaves it unset for the same reason, to keep the bytes
/// production signs the same. A whole-string comparison would fail whenever the two calls straddle
/// a second. So the header and the payload, with <c>nbf</c> removed, are compared as exact text,
/// each <c>nbf</c> is checked to be within seconds of now, and the minter's signature is verified
/// against the key.
/// </para>
/// <para>
/// Serial, because the old path reads FastEndpoints' process-wide resolver and each test points it
/// at a provider with the fake clock, then puts the previous one back.
/// </para>
/// </remarks>
[Collection("Integration1")]
public class JwtMinterGoldenTests(IntegrationTestFixture factory)
{
    private const string Key = "golden-signing-key-é-that-is-at-least-32-chars-long"; // allow-secret
    private const string Issuer = "golden-issuer";

    private static readonly DateTimeOffset Start = new(2026, 11, 21, 10, 0, 0, TimeSpan.Zero);

    private static readonly IConfiguration Config = new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["JWT:Key"] = Key,
            ["JWT:Issuer"] = Issuer,
            ["JWT:Audience"] = "golden-audience",
        })
        .Build();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Call(string? Audience, DateTime ExpiresAt, List<Claim> Claims);

    private sealed class Recorder(IJwtMinter inner) : IJwtMinter
    {
        public List<Call> Calls { get; } = [];

        public string Mint(string? audience, DateTime expiresAt, IEnumerable<Claim> claims)
        {
            var list = claims.ToList();
            Calls.Add(new Call(audience, expiresAt, list));
            return inner.Mint(audience, expiresAt, list);
        }
    }

    private static Recorder NewRecorder() => new(new JwtMinter(Config, new FakeTimeProvider(Start)));

    [Fact]
    public async Task The_access_token_is_signed_the_way_JwtBearer_signed_it()
    {
        var recorder = NewRecorder();
        using var scope = factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var user = new User { Id = Guid.NewGuid(), Username = $"golden-{Guid.NewGuid():n}", Email = "golden@example.com", RoleIds = [] };
        session.Store(user);
        await session.SaveChangesAsync(Ct);
        var issuer = new TokenIssuer(
            session, Config, NullLogger<TokenIssuer>.Instance, TenancyOptions.FromConfiguration(Config), recorder);

        var issued = await issuer.IssueAccessTokenAsync(
            user, "default", [new Claim("extra", "one")], Ct);

        issued.Allowed.Should().BeTrue();
        recorder.Calls.Should().HaveCount(1);
        recorder.Calls[0].Audience.Should().Be("golden-audience");
        recorder.Calls[0].Claims.Select(c => c.Type).Should().StartWith(["jti", "iat", "UserId", "Username", "tenant"]);
        AssertMatchesOldPath(issued.Token, recorder.Calls[0], "golden-audience");
    }

    [Fact]
    public void The_MFA_challenge_token_is_signed_the_way_JwtBearer_signed_it()
    {
        var recorder = NewRecorder();
        var grant = new PendingExternalGrant("acme", "oidc|golden", [Guid.NewGuid(), Guid.NewGuid()]);

        var (token, _) = MfaChallengeToken.Create(recorder, Config, Guid.NewGuid(), grant, "social:google");

        recorder.Calls.Should().HaveCount(1);
        recorder.Calls[0].Claims.Should().HaveCount(4, "purpose, user, first factor and grant");
        AssertMatchesOldPath(token, recorder.Calls[0], "golden-audience:mfa");
    }

    [Fact]
    public void The_step_up_token_is_signed_the_way_JwtBearer_signed_it()
    {
        var recorder = NewRecorder();

        var (token, _) = StepUpToken.Create(recorder, Config, Guid.NewGuid());

        recorder.Calls.Should().HaveCount(1);
        recorder.Calls[0].Claims.Should().HaveCount(2, "purpose and user");
        AssertMatchesOldPath(token, recorder.Calls[0], "golden-audience:stepup");
    }

    [Fact]
    public void The_preview_token_is_signed_the_way_JwtBearer_signed_it()
    {
        var recorder = NewRecorder();

        var (token, _) = PreviewToken.Create(recorder, Config, "acme", "article", "wip", Guid.NewGuid(), TimeSpan.FromMinutes(7));

        recorder.Calls.Should().HaveCount(1);
        recorder.Calls[0].Claims.Should().HaveCount(5, "purpose, tenant, type, slug and entry");
        AssertMatchesOldPath(token, recorder.Calls[0], "golden-audience:preview");
    }

    [Fact]
    public void The_viewer_token_is_signed_the_way_JwtBearer_signed_it()
    {
        var recorder = NewRecorder();

        var (token, _) = ViewerToken.Create(recorder, Config, "acme", "member-1", TimeSpan.FromMinutes(9));

        recorder.Calls.Should().HaveCount(1);
        recorder.Calls[0].Claims.Should().HaveCount(3, "purpose, tenant and subject");
        AssertMatchesOldPath(token, recorder.Calls[0], "golden-audience:viewer");
    }

    [Fact]
    public void A_minter_with_no_signing_key_refuses_as_JwtBearer_did()
    {
        var unset = new ConfigurationBuilder().AddInMemoryCollection().Build();

        var mint = () => new JwtMinter(unset, TimeProvider.System).Mint("a", DateTime.UtcNow.AddMinutes(1), []);

        mint.Should().Throw<InvalidOperationException>().WithMessage("*SigningKey*required*");
    }

    private static void AssertMatchesOldPath(string minted, Call call, string audience)
    {
        call.Audience.Should().Be(audience);
        var old = SignWithJwtBearer(call);

        var (mintedHeader, mintedPayload) = Decode(minted);
        var (oldHeader, oldPayload) = Decode(old);
        mintedHeader.Should().Be(oldHeader, "same algorithm and type");

        var mintedNbf = TakeNotBefore(mintedPayload);
        var oldNbf = TakeNotBefore(oldPayload);
        mintedPayload.ToJsonString().Should().Be(oldPayload.ToJsonString(),
            "same fields, same values and the same claim order, with nbf compared below");

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        mintedNbf.Should().BeInRange(now - 10, now + 10, "the handler reads the system clock for nbf");
        oldNbf.Should().BeInRange(now - 10, now + 10, "the old path took nbf from the system clock too");

        VerifiesWithAsciiKey(minted, audience).Should().BeTrue("the key is signed as ASCII bytes, as bearer validation reads it");
    }

    /// <summary>
    /// What the five sites called before: the same key and issuer, the audience, expiry and claims the
    /// site passed, with FastEndpoints' resolver pointed at a provider whose <c>TimeProvider</c> is the
    /// fake clock. The previous resolver is put back whatever happens.
    /// </summary>
    private static string SignWithJwtBearer(Call call)
    {
        using var provider = new ServiceCollection()
            .AddSingleton<TimeProvider>(new FakeTimeProvider(Start))
            .AddFastEndpoints(o =>
            {
                // AddFastEndpoints refuses to start when it finds no endpoint declarations, so it is
                // pointed at the host assembly alone. Only the resolver it registers is used here.
                o.Assemblies = [typeof(JwtMinter).Assembly];
                o.DisableAutoDiscovery = true;
            })
            .BuildServiceProvider();
        var pointed = FastEndpointsResolver.PointAt(provider);
        try
        {
            return JwtBearer.CreateToken(o =>
            {
                o.SigningKey = Key;
                o.ExpireAt = call.ExpiresAt;
                o.Issuer = Issuer;
                o.Audience = call.Audience;
                o.User.Claims.AddRange(call.Claims);
            });
        }
        finally
        {
            pointed.Dispose();
        }
    }

    private static (string Header, JsonObject Payload) Decode(string token)
    {
        var parts = token.Split('.');
        parts.Should().HaveCount(3, "a signed compact JWT");
        return (Encoding.UTF8.GetString(Base64UrlEncoder.DecodeBytes(parts[0])),
            JsonNode.Parse(Encoding.UTF8.GetString(Base64UrlEncoder.DecodeBytes(parts[1])))!.AsObject());
    }

    private static long TakeNotBefore(JsonObject payload)
    {
        payload.ContainsKey("nbf").Should().BeTrue("the handler adds nbf when an expiry and an issue time are set");
        var nbf = payload["nbf"]!.GetValue<long>();
        payload.Remove("nbf");
        return nbf;
    }

    private static bool VerifiesWithAsciiKey(string token, string audience)
    {
        var result = new JsonWebTokenHandler().ValidateTokenAsync(token, new TokenValidationParameters
        {
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.ASCII.GetBytes(Key)),
            ValidIssuer = Issuer,
            ValidAudience = audience,
            ValidateLifetime = false,
        }).GetAwaiter().GetResult();
        return result.IsValid;
    }
}
