using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Marten.Patching;
using Microsoft.Extensions.DependencyInjection;
using OtpNet;
using Xunit;
using MfaModels = barakoCMS.Features.Auth.Mfa;

namespace BarakoCMS.Tests.Mfa;

[Collection("Integration1")]
public class MfaDisableTests
{
    private static int _ipCounter;

    private readonly IntegrationTestFixture _factory;
    private readonly string _ip;

    public MfaDisableTests(IntegrationTestFixture factory)
    {
        _factory = factory;
        var n = Interlocked.Increment(ref _ipCounter);
        _ip = $"203.0.113.{n % 250 + 1}";
    }

    private HttpClient Client(Guid userId)
    {
        var c = _factory
            .WithWebHostBuilder(b => b.ConfigureServices(s =>
                s.AddSingleton<Microsoft.AspNetCore.Hosting.IStartupFilter, TestRemoteIpFilter>()))
            .CreateClient();
        c.DefaultRequestHeaders.Add(TestRemoteIpFilter.Header, _ip);
        c.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _factory.CreateToken(new[] { "SuperAdmin" }, userId.ToString()));
        return c;
    }

    private async Task<(Guid Id, string Email, List<string> Recovery)> EnrolledUserAsync()
    {
        var id = Guid.NewGuid();
        var email = $"disable-{id:N}@test.com";
        using (var scope = _factory.Services.CreateScope())
        {
            var s = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            s.Store(new User
            {
                Id = id,
                Username = $"disable-{id:N}",
                Email = email,
                PasswordHash = BCrypt.Net.BCrypt.HashPassword(MfaPolicyTests.Password),
                RoleIds = [Guid.Parse("00000000-0000-0000-0000-000000000001")],
            });
            await s.SaveChangesAsync();
        }

        var client = Client(id);
        var setup = (await (await client.PostAsync("/api/auth/mfa/setup", null))
            .Content.ReadFromJsonAsync<MfaModels.SetupResponse>())!;
        var code = new Totp(Base32Encoding.ToBytes(setup.Secret)).ComputeTotp();
        var enable = (await (await client.PostAsJsonAsync("/api/auth/mfa/enable", new MfaModels.CodeRequest { Code = code }))
            .Content.ReadFromJsonAsync<MfaModels.EnableResponse>())!;
        return (id, email, enable.RecoveryCodes);
    }

    // The auth rate limit stops five wrong codes in a row, so tests start one short of the lockout.
    private async Task SetFailuresAsync(Guid id, int attempts)
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Patch<User>(id).Set(x => x.FailedLoginAttempts, attempts);
        await session.SaveChangesAsync();
    }

    private async Task<User> LoadUserAsync(Guid id)
    {
        using var scope = _factory.Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<IQuerySession>().LoadAsync<User>(id))!;
    }

    private static Task<HttpResponseMessage> DisableAsync(HttpClient client, string code) =>
        client.PostAsJsonAsync("/api/auth/mfa/disable", new MfaModels.CodeRequest { Code = code });

    [Fact]
    public async Task Disable_WrongCode_CountsTowardLockout()
    {
        var (id, _, _) = await EnrolledUserAsync();

        var response = await DisableAsync(Client(id), "000000");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await LoadUserAsync(id)).FailedLoginAttempts.Should().Be(1);
    }

    [Fact]
    public async Task Disable_WrongCodesAtTheThreshold_LockTheAccount()
    {
        var (id, _, _) = await EnrolledUserAsync();
        await SetFailuresAsync(id, 4);

        await DisableAsync(Client(id), "000000");

        (await LoadUserAsync(id)).LockoutUntil.Should().NotBeNull();
    }

    [Fact]
    public async Task Disable_LockedAccount_RefusedEvenWithValidCode()
    {
        var (id, _, recovery) = await EnrolledUserAsync();
        await SetFailuresAsync(id, 4);
        var client = Client(id);
        await DisableAsync(client, "000000");

        var response = await DisableAsync(client, recovery[0]);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("locked");
        using var scope = _factory.Services.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<IQuerySession>().LoadAsync<MfaSecret>(id))!
            .Enabled.Should().BeTrue();
    }

    [Fact]
    public async Task Disable_ValidCodeAfterFailures_ResetsTheCount()
    {
        var (id, _, recovery) = await EnrolledUserAsync();
        await SetFailuresAsync(id, 3);

        var response = await DisableAsync(Client(id), recovery[0]);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await LoadUserAsync(id)).FailedLoginAttempts.Should().Be(0);
    }

    [Fact]
    public async Task Disable_Success_RevokesOtherSessions()
    {
        var (id, _, recovery) = await EnrolledUserAsync();
        var tokenId = Guid.NewGuid();
        using (var scope = _factory.Services.CreateScope())
        {
            var s = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            s.Store(new RefreshToken
            {
                Id = tokenId,
                Token = $"other-device-{Guid.NewGuid():N}",
                UserId = id,
                ExpiresAt = DateTime.UtcNow.AddDays(7),
                CreatedAt = DateTime.UtcNow,
                IsRevoked = false,
            });
            await s.SaveChangesAsync();
        }

        var response = await DisableAsync(Client(id), recovery[0]);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var check = _factory.Services.CreateScope();
        var stored = await check.ServiceProvider.GetRequiredService<IQuerySession>().LoadAsync<RefreshToken>(tokenId);
        stored!.IsRevoked.Should().BeTrue("a session opened under the second factor must not outlive it");
    }

    [Fact]
    public async Task Disable_Success_NotifiesTheOwner()
    {
        var (id, email, recovery) = await EnrolledUserAsync();
        var before = _factory.Email.Messages.Count(m => m.To == email);

        var response = await DisableAsync(Client(id), recovery[0]);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var sent = _factory.Email.Messages.Where(m => m.To == email).ToList();
        sent.Should().HaveCount(before + 1);
        sent[^1].Subject.Should().Contain("Two-factor authentication disabled");
    }
}
