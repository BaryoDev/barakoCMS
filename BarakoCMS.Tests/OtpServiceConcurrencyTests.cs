using FluentAssertions;
using Marten;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;
using barakoCMS.Core.Interfaces;
using barakoCMS.Infrastructure;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;

namespace BarakoCMS.Tests;

/// <summary>
/// Issuing a code marks every outstanding code consumed in the same save as the new one. With two
/// or more outstanding, a concurrent request makes that save lose on several documents, which
/// Marten reports as an AggregateException, and the request has to answer that no code was issued
/// rather than fail.
/// </summary>
[Collection("Integration1")]
public class OtpServiceConcurrencyTests
{
    private readonly IntegrationTestFixture _fixture;

    public OtpServiceConcurrencyTests(IntegrationTestFixture fixture) => _fixture = fixture;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// Consumes every outstanding code for the address from another session just before the
    /// service's save, so the save loses on each of them with no timing involved.
    /// </summary>
    private sealed class RivalRequest(IDocumentStore store, string email) : DocumentSessionListenerBase
    {
        public override async Task BeforeSaveChangesAsync(IDocumentSession session, CancellationToken token)
        {
            await using var rival = store.LightweightSession();
            var codes = await rival.Query<OtpCode>().Where(o => o.Email == email && !o.Consumed).ToListAsync(token);
            foreach (var code in codes)
            {
                code.Consumed = true;
                rival.Update(code);
            }

            await rival.SaveChangesAsync(token);
        }
    }

    [Fact]
    public async Task Two_outstanding_codes_lost_to_a_concurrent_request_issue_nothing()
    {
        using var scope = _fixture.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IDocumentStore>();
        var email = $"otp-race-{Guid.NewGuid():N}@example.com";
        await using (var seed = store.LightweightSession())
        {
            seed.Store(
                new OtpCode { Email = email, CodeHash = "a", ExpiresAt = DateTime.UtcNow.AddMinutes(10) },
                new OtpCode { Email = email, CodeHash = "b", ExpiresAt = DateTime.UtcNow.AddMinutes(10) });
            await seed.SaveChangesAsync(Ct);
        }

        var mail = Substitute.For<IEmailService>();
        var options = new Marten.Services.SessionOptions();
        options.Listeners.Add(new RivalRequest(store, email));
        await using var session = store.LightweightSession(options);
        var service = new OtpService(session, mail, new ConfigurationBuilder().Build(), NullLogger<OtpService>.Instance);

        var sent = await service.SendCodeAsync(email, new DeviceContext("test", "127.0.0.1", null, "test"), Ct);

        sent.Should().BeFalse();
        await mail.DidNotReceiveWithAnyArgs().SendEmailAsync(default!, default!, default!, Ct);
    }
}
