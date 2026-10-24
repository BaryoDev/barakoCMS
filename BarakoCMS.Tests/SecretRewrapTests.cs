using barakoCMS.Infrastructure.Auth.Mfa;
using barakoCMS.Infrastructure.Connectors;
using barakoCMS.Infrastructure.Security;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// Issue #900: the startup pass moves stored secrets to the current key and counts what no key opens.
/// </summary>
/// <remarks>
/// The rings here share no material with the test host's own keys, so the pass cannot rewrite a row
/// another test wrote: those do not open under these rings and are only counted. That is also why
/// the count is asserted as a difference between two passes rather than as an absolute number.
/// </remarks>
[Collection("Integration1")]
public class SecretRewrapTests
{
    private const string KeyA = "rewrap-ring-material-a-at-least-32-chars-long";
    private const string KeyB = "rewrap-ring-material-b-at-least-32-chars-long";
    private const string KeyGone = "rewrap-ring-material-gone-at-least-32-chars";

    private readonly IntegrationTestFixture _fixture;

    public SecretRewrapTests(IntegrationTestFixture fixture) => _fixture = fixture;

    private static IConfiguration Ring(params (string Id, string Material)[] keys)
    {
        var settings = new Dictionary<string, string?>();
        foreach (var section in new[] { "Secrets", "Mfa", "Connectors" })
        {
            foreach (var (id, material) in keys) settings[$"{section}:Keys:{id}"] = material;
            settings[$"{section}:CurrentKey"] = keys[^1].Id;
        }

        return new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
    }

    private SecretRewrapService Pass(IConfiguration ring) => new(
        _fixture.Services.GetRequiredService<IDocumentStore>(),
        new SecretProtector(ring),
        new ConnectorSecretProtector(ring),
        new MfaSecretProtector(ring),
        _fixture.Services.GetRequiredService<IConfiguration>(),
        NullLogger<SecretRewrapService>.Instance);

    [Fact]
    public void The_pass_runs_at_startup()
    {
        _fixture.Services.GetServices<IHostedService>().OfType<SecretRewrapService>().Should().ContainSingle();
    }

    [Fact]
    public async Task After_the_pass_every_stored_value_names_the_current_key_and_values_no_key_opens_are_counted()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = _fixture.Services.GetRequiredService<IDocumentStore>();
        var ringA = Ring(("a", KeyA));
        var ringAB = Ring(("a", KeyA), ("b", KeyB));

        var mfaId = Guid.NewGuid();
        var lostMfaId = Guid.NewGuid();
        var connectorSecretId = Guid.NewGuid();
        EmailSettings? original;

        await using (var session = store.LightweightSession())
        {
            original = await session.LoadAsync<EmailSettings>(EmailSettings.SingletonId, ct);
        }

        try
        {
            await using (var session = store.LightweightSession())
            {
                session.Store(new MfaSecret { Id = mfaId, EncryptedSecret = new MfaSecretProtector(ringA).Protect("JBSWY3DPEHPK3PXP") });
                session.Store(new MfaSecret
                {
                    Id = lostMfaId,
                    EncryptedSecret = new MfaSecretProtector(Ring(("gone", KeyGone))).Protect("JBSWY3DPEHPK3PXQ"),
                });
                session.Store(new ConnectorSecret
                {
                    Id = connectorSecretId,
                    ConnectorId = Guid.NewGuid(),
                    Key = "ApiToken",
                    ProtectedValue = new ConnectorSecretProtector(ringA).Protect("tok_rewrap"),
                });
                var email = await session.LoadAsync<EmailSettings>(EmailSettings.SingletonId, ct) ?? new EmailSettings();
                email.ProtectedApiKey = new SecretProtector(ringA).Protect("re_rewrap");
                session.Store(email);
                await session.SaveChangesAsync(ct);
            }

            var first = await Pass(ringAB).RewrapAllAsync(ct);

            first.Mfa.Rewrapped.Should().BeGreaterThanOrEqualTo(1);
            first.Mfa.Unopenable.Should().BeGreaterThanOrEqualTo(1, "the row written under a key no longer in the ring");
            first.Connectors.Rewrapped.Should().BeGreaterThanOrEqualTo(1);
            first.Email.Rewrapped.Should().Be(1);

            await using (var check = store.QuerySession())
            {
                var mfa = await check.LoadAsync<MfaSecret>(mfaId, ct);
                mfa!.EncryptedSecret.Should().StartWith("enc:v1:b:");
                new MfaSecretProtector(ringAB).Unprotect(mfa.EncryptedSecret).Should().Be("JBSWY3DPEHPK3PXP");

                var secret = await check.LoadAsync<ConnectorSecret>(connectorSecretId, ct);
                secret!.ProtectedValue.Should().StartWith("enc:v1:b:");
                new ConnectorSecretProtector(ringAB).Unprotect(secret.ProtectedValue).Should().Be("tok_rewrap");

                var email = await check.LoadAsync<EmailSettings>(EmailSettings.SingletonId, ct);
                email!.ProtectedApiKey.Should().StartWith("enc:v1:b:");
                new SecretProtector(ringAB).Unprotect(email.ProtectedApiKey).Should().Be("re_rewrap");

                var lost = await check.LoadAsync<MfaSecret>(lostMfaId, ct);
                lost!.EncryptedSecret.Should().StartWith("enc:v1:gone:", "a value no key opens is left as it is");
            }

            await using (var session = store.LightweightSession())
            {
                session.Delete<MfaSecret>(lostMfaId);
                await session.SaveChangesAsync(ct);
            }

            var second = await Pass(ringAB).RewrapAllAsync(ct);

            second.Email.Rewrapped.Should().Be(0, "the first pass already moved it");
            second.Mfa.Unopenable.Should().Be(first.Mfa.Unopenable - 1, "the one unopenable row this test planted is gone");
        }
        finally
        {
            await using var session = store.LightweightSession();
            session.Delete<MfaSecret>(mfaId);
            session.Delete<MfaSecret>(lostMfaId);
            session.Delete<ConnectorSecret>(connectorSecretId);
            if (original is null) session.Delete<EmailSettings>(EmailSettings.SingletonId);
            else session.Store(original);
            await session.SaveChangesAsync(ct);
        }
    }
}
