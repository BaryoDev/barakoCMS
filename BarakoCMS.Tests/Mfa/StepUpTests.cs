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

[Collection("Sequential")]
public class StepUpTests
{
    private static int _ipCounter;

    private readonly IntegrationTestFixture _factory;
    private readonly string _ip;

    public StepUpTests(IntegrationTestFixture factory)
    {
        _factory = factory;
        var n = Interlocked.Increment(ref _ipCounter);
        _ip = $"198.51.100.{(n + 60) % 250 + 1}";
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

    private async Task<Guid> SeedUserAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var s = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var id = Guid.NewGuid();
        s.Store(new User
        {
            Id = id,
            Username = $"step-{id:N}",
            Email = $"step-{id:N}@test.com",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(MfaPolicyTests.Password),
            RoleIds = [Guid.Parse("00000000-0000-0000-0000-000000000001")],
        });
        await s.SaveChangesAsync();
        return id;
    }

    /// <summary>
    /// Enrolls the user and returns their recovery codes. Enabling spends the current TOTP step and
    /// bumps the session epoch, so later calls use a fresh client and recovery codes, which the
    /// replay guard does not tie to the clock.
    /// </summary>
    private async Task<(Guid Id, List<string> Recovery)> EnrolledUserAsync()
    {
        var id = await SeedUserAsync();
        var client = Client(id);
        var setup = (await (await client.PostAsync("/api/auth/mfa/setup", null))
            .Content.ReadFromJsonAsync<MfaModels.SetupResponse>())!;
        var code = new Totp(Base32Encoding.ToBytes(setup.Secret)).ComputeTotp();
        var enable = (await (await client.PostAsJsonAsync("/api/auth/mfa/enable", new MfaModels.CodeRequest { Code = code }))
            .Content.ReadFromJsonAsync<MfaModels.EnableResponse>())!;
        await Task.Delay(1100); // the epoch is in whole seconds; a token minted in the same second would be refused
        return (id, enable.RecoveryCodes);
    }

    private static async Task<string> StepUpAsync(HttpClient client, string code)
    {
        var response = await client.PostAsJsonAsync("/api/auth/mfa/step-up", new MfaModels.CodeRequest { Code = code });
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<MfaModels.StepUpResponse>())!.StepUpToken;
    }

    [Fact]
    public async Task StepUp_NoHeader_Returns403WithRequiredHeader()
    {
        var (id, _) = await EnrolledUserAsync();

        var response = await Client(id).PostAsync("/api/_test/step-up", null);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        response.Headers.GetValues("X-Step-Up-Required").Should().ContainSingle("true");
    }

    [Fact]
    public async Task StepUp_ValidCode_TokenOpensSensitiveEndpoint()
    {
        var (id, recovery) = await EnrolledUserAsync();
        var client = Client(id);
        var token = await StepUpAsync(client, recovery[0]);

        client.DefaultRequestHeaders.Add("X-Step-Up", token);
        var response = await client.PostAsync("/api/_test/step-up", null);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task StepUp_OtherUsersToken_Returns403()
    {
        var (alice, aliceCodes) = await EnrolledUserAsync();
        var (bob, _) = await EnrolledUserAsync();
        var token = await StepUpAsync(Client(alice), aliceCodes[0]);

        var bobs = Client(bob);
        bobs.DefaultRequestHeaders.Add("X-Step-Up", token);

        (await bobs.PostAsync("/api/_test/step-up", null)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task StepUp_AccessTokenAsStepUp_Returns403()
    {
        // An access token is signed with the same key. The audience is what tells them apart.
        var (id, _) = await EnrolledUserAsync();
        var client = Client(id);
        client.DefaultRequestHeaders.Add("X-Step-Up", client.DefaultRequestHeaders.Authorization!.Parameter);

        (await client.PostAsync("/api/_test/step-up", null)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task StepUp_WrongCode_400AndCountsTowardLockout()
    {
        var (id, _) = await EnrolledUserAsync();

        var response = await Client(id).PostAsJsonAsync("/api/auth/mfa/step-up", new MfaModels.CodeRequest { Code = "000000" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var scope = _factory.Services.CreateScope();
        var user = await scope.ServiceProvider.GetRequiredService<IQuerySession>().LoadAsync<User>(id);
        user!.FailedLoginAttempts.Should().Be(1);
    }

    [Fact]
    public async Task StepUp_UsedRecoveryCode_Refused()
    {
        var (id, recovery) = await EnrolledUserAsync();
        var client = Client(id);
        await StepUpAsync(client, recovery[0]);

        var again = await client.PostAsJsonAsync("/api/auth/mfa/step-up", new MfaModels.CodeRequest { Code = recovery[0] });

        again.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task StepUp_WithoutMfa_400SaysSetUpFirst()
    {
        var id = await SeedUserAsync();

        var response = await Client(id).PostAsJsonAsync("/api/auth/mfa/step-up", new MfaModels.CodeRequest { Code = "123456" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("Set up MFA");
    }

    [Fact]
    public async Task StepUp_TokenAsBearer_Returns401OnNormalEndpoint()
    {
        var (id, recovery) = await EnrolledUserAsync();
        var token = await StepUpAsync(Client(id), recovery[0]);

        var client = Client(id);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        (await client.GetAsync("/api/auth/mfa/status")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task StepUp_ExpiredToken_Returns403WithNoClockSkew()
    {
        var (id, _) = await EnrolledUserAsync();
        var token = FastEndpoints.Security.JwtBearer.CreateToken(o =>
        {
            o.SigningKey = "test-super-secret-key-that-is-at-least-32-chars-long";
            o.ExpireAt = DateTime.UtcNow.AddSeconds(2);
            o.Issuer = "BarakoTest";
            o.Audience = "BarakoClient:stepup";
            o.User.Claims.Add(new("purpose", "stepup"));
            o.User.Claims.Add(new("uid", id.ToString()));
        });
        var client = Client(id);
        client.DefaultRequestHeaders.Add("X-Step-Up", token);
        (await client.PostAsync("/api/_test/step-up", null)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        await Task.Delay(2200);

        (await client.PostAsync("/api/_test/step-up", null)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task StepUp_LockedOutUser_RefusedEvenWithValidCode()
    {
        var (id, recovery) = await EnrolledUserAsync();
        var client = Client(id);
        using (var scope = _factory.Services.CreateScope())
        {
            // The auth rate limit stops five wrong codes in a row, so start one short of the lockout.
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Patch<User>(id).Set(x => x.FailedLoginAttempts, 4);
            await session.SaveChangesAsync();
        }
        await client.PostAsJsonAsync("/api/auth/mfa/step-up", new MfaModels.CodeRequest { Code = "000000" });

        var response = await client.PostAsJsonAsync("/api/auth/mfa/step-up", new MfaModels.CodeRequest { Code = recovery[0] });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("locked");
    }

    [Fact]
    public async Task StepUp_EnrollmentLimitedToken_RefusedWithEnrollmentHeader()
    {
        var id = await SeedUserAsync();
        var client = Client(id);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            _factory.CreateToken(new[] { "SuperAdmin" }, id.ToString(),
                new Dictionary<string, string> { ["mfa_enroll"] = "required" }));

        var response = await client.PostAsJsonAsync("/api/auth/mfa/step-up", new MfaModels.CodeRequest { Code = "123456" });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        response.Headers.GetValues("X-Mfa-Enrollment-Required").Should().ContainSingle("true");
    }

    [Fact]
    public async Task StepUp_ValidCodeAfterFailures_ResetsTheCount()
    {
        var (id, recovery) = await EnrolledUserAsync();
        using (var scope = _factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Patch<User>(id).Set(x => x.FailedLoginAttempts, 3);
            await session.SaveChangesAsync();
        }

        await StepUpAsync(Client(id), recovery[0]);

        using var check = _factory.Services.CreateScope();
        var user = await check.ServiceProvider.GetRequiredService<IQuerySession>().LoadAsync<User>(id);
        user!.FailedLoginAttempts.Should().Be(0);
    }

    [Fact]
    public async Task StepUp_DefaultLifetime_IsAboutFiveMinutes()
    {
        var (id, recovery) = await EnrolledUserAsync();

        var response = await Client(id).PostAsJsonAsync("/api/auth/mfa/step-up", new MfaModels.CodeRequest { Code = recovery[0] });

        var body = (await response.Content.ReadFromJsonAsync<MfaModels.StepUpResponse>())!;
        (body.ExpiresAt.ToUniversalTime() - DateTime.UtcNow).TotalSeconds.Should().BeInRange(280, 305);
    }
}
