using System.Net;
using System.Net.Http.Headers;
using BarakoCMS.DeviceTrust;
using FluentAssertions;
using Marten;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BarakoCMS.Tests.Features.DeviceTrust;

/// <summary>
/// With <c>DeviceTrust:Enforce=true</c>, a token is only good from the device it was issued to.
/// </summary>
/// <remarks>
/// The failure mode this covers is silence. If the binding stops being checked nothing errors and
/// no sign-in breaks; the system simply starts accepting a stolen token from anywhere, and the
/// feature looks fine from the outside because logging in still works. There is no symptom to
/// notice, which is why it needs a test rather than an eye.
///
/// Enforcement deliberately applies only to tokens that carry a <c>did</c> claim. A token without
/// one passes through untouched, so turning enforcement on cannot lock out sessions issued before
/// it. That is a design decision rather than a gap, and it is asserted here so that changing it is
/// a deliberate act rather than a side effect.
///
/// The clients are built once in the constructor, not per request, and each instance gets its own
/// client IP: <c>/api/auth/refresh</c> is under the auth rate limit (5 per 15 minutes per IP) and
/// the rest of the suite shares one loopback address.
/// </remarks>
[Collection("Sequential")]
public class DeviceEnforcementTests
{
    private static int _ipCounter;

    private readonly IntegrationTestFixture _fixture;
    private readonly WebApplicationFactory<Program> _enforcing;
    private readonly string _ip;

    public DeviceEnforcementTests(IntegrationTestFixture fixture)
    {
        _fixture = fixture;
        _ip = $"203.0.113.{Interlocked.Increment(ref _ipCounter) % 200 + 20}";
        _enforcing = fixture.WithSettings(new Dictionary<string, string?>
        {
            { "DeviceTrust:Enforce", "true" },
        });
    }

    private HttpClient Client(WebApplicationFactory<Program> factory, string token, string? deviceIdHeader)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        client.DefaultRequestHeaders.Add(TestRemoteIpFilter.Header, _ip);
        if (deviceIdHeader is not null)
            client.DefaultRequestHeaders.Add(barakoCMS.Infrastructure.DeviceContext.DeviceIdHeader, deviceIdHeader);
        return client;
    }

    private string TokenFor(Guid userId, string? deviceId) =>
        _fixture.CreateToken(
            roles: ["SuperAdmin"],
            userId: userId.ToString(),
            additionalClaims: deviceId is null
                ? null
                : new Dictionary<string, string> { [DeviceGate.DeviceClaim] = deviceId });

    /// <summary>Seeds a user with one trusted device and returns both ids.</summary>
    private async Task<(Guid UserId, string DeviceId)> TrustedDeviceAsync()
    {
        var userId = await SeedUserAsync();
        var deviceId = $"device-{Guid.NewGuid():n}";

        using var scope = _fixture.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IDeviceTrustService>().TrustAsync(
            userId,
            new barakoCMS.Infrastructure.DeviceContext("test-agent", "203.0.113.9", deviceId, "Test device"),
            TestContext.Current.CancellationToken);

        return (userId, deviceId);
    }

    private async Task<Guid> SeedUserAsync()
    {
        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var id = Guid.NewGuid();
        session.Store(new barakoCMS.Models.User
        {
            Id = id,
            Username = $"device-{id:n}",
            Email = $"device-{id:n}@example.com",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword("a-real-password"),
        });
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        return id;
    }

    /// <summary>
    /// The positive control, first, because every refusal below is only meaningful next to it.
    /// </summary>
    [Fact]
    public async Task A_bound_token_presented_from_its_own_device_is_allowed()
    {
        var (userId, deviceId) = await TrustedDeviceAsync();

        var response = await Client(_enforcing, TokenFor(userId, deviceId), deviceId)
            .GetAsync("/api/devices", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "enforcement that refused the right device would just be an outage");
    }

    /// <summary>
    /// The whole point: a token lifted from one device is not usable on another.
    /// </summary>
    [Fact]
    public async Task A_bound_token_presented_from_another_device_is_refused()
    {
        var (userId, deviceId) = await TrustedDeviceAsync();
        var somewhereElse = $"device-{Guid.NewGuid():n}";

        var response = await Client(_enforcing, TokenFor(userId, deviceId), somewhereElse)
            .GetAsync("/api/devices", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "the token names the device it was issued to, and this is not that device");
    }

    /// <summary>
    /// Claiming no device at all is not a way out of the check.
    /// </summary>
    [Fact]
    public async Task A_bound_token_presented_with_no_device_header_is_refused()
    {
        var (userId, deviceId) = await TrustedDeviceAsync();

        var response = await Client(_enforcing, TokenFor(userId, deviceId), deviceIdHeader: null)
            .GetAsync("/api/devices", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "omitting the header must not be easier than forging it");
    }

    /// <summary>
    /// A device that has been revoked cannot go on presenting its own token.
    /// </summary>
    [Fact]
    public async Task A_revoked_device_is_refused_even_presenting_its_own_token()
    {
        var (userId, deviceId) = await TrustedDeviceAsync();
        await RevokeAsync(userId, deviceId);

        var response = await Client(_enforcing, TokenFor(userId, deviceId), deviceId)
            .GetAsync("/api/devices", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "matching the claim is not enough; the device also has to still be trusted");
    }

    /// <summary>
    /// A token with no <c>did</c> claim passes through untouched. Documented, deliberate, and here
    /// so that changing it has to be a decision.
    /// </summary>
    /// <remarks>
    /// Sessions issued before enforcement was switched on carry no device claim. Refusing them would
    /// sign out every existing user the moment an operator flipped the flag, so the module lets them
    /// through and lets them expire instead. See docs/device-trust.md.
    /// </remarks>
    [Fact]
    public async Task A_token_with_no_device_claim_is_left_alone()
    {
        var userId = await SeedUserAsync();

        var response = await Client(_enforcing, TokenFor(userId, deviceId: null), deviceIdHeader: null)
            .GetAsync("/api/devices", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "turning enforcement on must not sign out every session that predates it");
    }

    /// <summary>
    /// And with enforcement off, which is the default, a mismatched device is not blocked. The check
    /// is opt-in, and a test that passed either way would not be telling us the flag does anything.
    /// </summary>
    [Fact]
    public async Task Without_the_flag_a_mismatched_device_is_not_blocked()
    {
        var (userId, deviceId) = await TrustedDeviceAsync();

        var response = await Client(_fixture, TokenFor(userId, deviceId), $"device-{Guid.NewGuid():n}")
            .GetAsync("/api/devices", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "DeviceTrust:Enforce is the switch, so with it unset nothing may be refused");
    }

    private async Task RevokeAsync(Guid userId, string deviceId)
    {
        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var device = await session.Query<Device>().FirstAsync(
            d => d.UserId == userId && d.DeviceId == deviceId, TestContext.Current.CancellationToken);

        var revoked = await scope.ServiceProvider.GetRequiredService<IDeviceTrustService>()
            .RevokeAsync(userId, device.Id, TestContext.Current.CancellationToken);
        revoked.Should().BeTrue();
    }
}
