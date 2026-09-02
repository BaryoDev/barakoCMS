using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BarakoCMS.DeviceTrust;
using FluentAssertions;
using Marten;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BarakoCMS.Tests.Features.DeviceTrust;

/// <summary>
/// Revoking a device, and the boundary of what revoking one actually does.
/// </summary>
/// <remarks>
/// Device revocation is the button somebody presses after losing a laptop, so the two questions
/// worth pinning are whether it stops that laptop and whether it can be aimed at somebody else's.
///
/// The third is the documented limitation: revoking ends renewal, not the access token already in
/// flight, which survives until it expires. Stated as a test rather than left as an assumption, so
/// anyone reading the suite finds the boundary where the behaviour is rather than in a doc that can
/// drift away from it. See docs/device-trust.md.
///
/// Each instance takes its own client IP because <c>/api/auth/refresh</c> is under the auth rate
/// limit of five per fifteen minutes per address, and the rest of the suite shares one loopback.
/// </remarks>
[Collection("Sequential")]
public class DeviceRevocationTests
{
    private static int _ipCounter;

    private readonly IntegrationTestFixture _fixture;
    private readonly string _ip;
    private readonly HttpClient _anonymous;

    public DeviceRevocationTests(IntegrationTestFixture fixture)
    {
        _fixture = fixture;
        _ip = $"192.0.2.{Interlocked.Increment(ref _ipCounter) % 200 + 20}";
        _anonymous = fixture.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        _anonymous.DefaultRequestHeaders.Add(TestRemoteIpFilter.Header, _ip);
    }

    /// <summary>
    /// A client for this user. <paramref name="deviceId"/> binds the token to a device.
    /// </summary>
    /// <remarks>
    /// A token with no <c>did</c> claim is not device-bound, and enforcement ignores those by
    /// design. So a test about what revocation does to a device's token has to ask for one, or it
    /// is asserting about a token the feature was never going to touch.
    /// </remarks>
    private HttpClient ClientFor(Guid userId, string? deviceId = null)
    {
        var client = _fixture.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var claims = deviceId is null
            ? null
            : new Dictionary<string, string> { [DeviceGate.DeviceClaim] = deviceId };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", _fixture.CreateToken(["SuperAdmin"], userId.ToString(), claims));
        client.DefaultRequestHeaders.Add(TestRemoteIpFilter.Header, _ip);
        return client;
    }

    private async Task<Guid> SeedUserAsync()
    {
        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var id = Guid.NewGuid();
        session.Store(new barakoCMS.Models.User
        {
            Id = id,
            Username = $"revoke-{id:n}",
            Email = $"revoke-{id:n}@example.com",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword("a-real-password"),
        });
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        return id;
    }

    /// <summary>Trusts a device for the user and issues it a refresh token bound to that device.</summary>
    private async Task<(Guid RecordId, string DeviceId, string Refresh)> DeviceWithSessionAsync(Guid userId)
    {
        var deviceId = $"device-{Guid.NewGuid():n}";
        var refresh = Convert.ToBase64String(Guid.NewGuid().ToByteArray()) + Guid.NewGuid().ToString("N");

        using var scope = _fixture.Services.CreateScope();
        var device = await scope.ServiceProvider.GetRequiredService<IDeviceTrustService>().TrustAsync(
            userId,
            new barakoCMS.Infrastructure.DeviceContext("test-agent", "203.0.113.9", deviceId, "Test device"),
            TestContext.Current.CancellationToken);

        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Store(new barakoCMS.Models.RefreshToken
        {
            Id = Guid.NewGuid(),
            Token = refresh,
            UserId = userId,
            DeviceId = deviceId,
            ExpiresAt = DateTime.UtcNow.AddDays(7),
            CreatedAt = DateTime.UtcNow,
            IsRevoked = false,
        });
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);

        return (device.Id, deviceId, refresh);
    }

    private Task<HttpResponseMessage> RefreshAsync(string refresh) =>
        _anonymous.PostAsJsonAsync(
            "/api/auth/refresh", new { refreshToken = refresh }, TestContext.Current.CancellationToken);

    /// <summary>
    /// The positive control: a live device's session refreshes. Without it, a refresh endpoint that
    /// refused everything would satisfy the revocation test below.
    /// </summary>
    [Fact]
    public async Task A_live_devices_session_still_refreshes()
    {
        var userId = await SeedUserAsync();
        var (_, _, refresh) = await DeviceWithSessionAsync(userId);

        (await RefreshAsync(refresh)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    /// <summary>
    /// Revoking the device stops its session renewing, straight away rather than at the next expiry.
    /// </summary>
    [Fact]
    public async Task Revoking_a_device_stops_its_refresh_immediately()
    {
        var userId = await SeedUserAsync();
        var (recordId, _, refresh) = await DeviceWithSessionAsync(userId);

        var revoked = await ClientFor(userId).PostAsync(
            $"/api/devices/{recordId}/revoke", null, TestContext.Current.CancellationToken);
        revoked.StatusCode.Should().Be(HttpStatusCode.OK);

        (await RefreshAsync(refresh)).StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "a lost laptop's session must end when the user says so, not seven days later");
    }

    /// <summary>
    /// And only that device's. Revoking one must not sign the user out everywhere, or nobody will
    /// use it for the case it exists for.
    /// </summary>
    [Fact]
    public async Task Revoking_one_device_leaves_the_users_other_devices_signed_in()
    {
        var userId = await SeedUserAsync();
        var (lostId, _, _) = await DeviceWithSessionAsync(userId);
        var (_, _, keptRefresh) = await DeviceWithSessionAsync(userId);

        await ClientFor(userId).PostAsync(
            $"/api/devices/{lostId}/revoke", null, TestContext.Current.CancellationToken);

        (await RefreshAsync(keptRefresh)).StatusCode.Should().Be(HttpStatusCode.OK,
            "revocation is per device, so the phone in your pocket keeps working");
    }

    /// <summary>
    /// The documented limitation, asserted rather than assumed: revoking ends renewal, and an access
    /// token already issued runs out its own clock.
    /// </summary>
    /// <remarks>
    /// Access tokens are not checked against a device list on every request unless enforcement is
    /// on, so this is the deliberate boundary of the feature and not a defect. Writing it down as a
    /// test means a future change that closes the window has to come and edit this, which is the
    /// point.
    /// </remarks>
    [Fact]
    public async Task A_revoked_device_keeps_its_existing_access_token_until_it_expires()
    {
        var userId = await SeedUserAsync();
        var (recordId, deviceId, _) = await DeviceWithSessionAsync(userId);

        // Bound to the device being revoked. Without the did claim this request would stay
        // authorized whatever revocation did, so the assertion below would hold for the wrong reason.
        var alreadyIssued = ClientFor(userId, deviceId);

        await alreadyIssued.PostAsync(
            $"/api/devices/{recordId}/revoke", null, TestContext.Current.CancellationToken);

        (await alreadyIssued.GetAsync("/api/devices", TestContext.Current.CancellationToken))
            .StatusCode.Should().Be(HttpStatusCode.OK,
                "with enforcement off, revoking stops renewal and nothing more; the access token "
                + "runs out its own fifteen minutes");
    }

    /// <summary>
    /// A user's device list is their own. Somebody else's devices are not in it.
    /// </summary>
    [Fact]
    public async Task A_user_sees_only_their_own_devices()
    {
        var mine = await SeedUserAsync();
        var theirs = await SeedUserAsync();
        var (myRecord, _, _) = await DeviceWithSessionAsync(mine);
        var (theirRecord, _, _) = await DeviceWithSessionAsync(theirs);

        var listed = await ClientFor(mine).GetAsync("/api/devices", TestContext.Current.CancellationToken);
        listed.StatusCode.Should().Be(HttpStatusCode.OK);

        using var document = JsonDocument.Parse(
            await listed.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var ids = document.RootElement.GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("id").GetGuid()).ToArray();

        ids.Should().Contain(myRecord, "a list that showed nothing would pass the next assertion for free");
        ids.Should().NotContain(theirRecord);
    }

    /// <summary>
    /// And nobody can revoke a device they do not own, which would be a remote sign-out of a
    /// stranger.
    /// </summary>
    [Fact]
    public async Task A_user_cannot_revoke_someone_elses_device()
    {
        var attacker = await SeedUserAsync();
        var victim = await SeedUserAsync();
        var (victimRecord, _, victimRefresh) = await DeviceWithSessionAsync(victim);

        var attempt = await ClientFor(attacker).PostAsync(
            $"/api/devices/{victimRecord}/revoke", null, TestContext.Current.CancellationToken);

        attempt.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "someone else's device id is not a thing this caller has");

        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        (await session.LoadAsync<Device>(victimRecord, TestContext.Current.CancellationToken))!
            .Status.Should().Be(DeviceStatus.Trusted, "and the refusal must be a refusal, not a 404 after the write");

        (await RefreshAsync(victimRefresh)).StatusCode.Should().Be(HttpStatusCode.OK,
            "the victim's session is untouched");
    }

    /// <summary>
    /// A revoked device drops out of the list rather than sitting there looking active.
    /// </summary>
    [Fact]
    public async Task A_revoked_device_leaves_the_list()
    {
        var userId = await SeedUserAsync();
        var (recordId, _, _) = await DeviceWithSessionAsync(userId);
        var client = ClientFor(userId);

        await client.PostAsync($"/api/devices/{recordId}/revoke", null, TestContext.Current.CancellationToken);

        using var document = JsonDocument.Parse(await (await client.GetAsync(
            "/api/devices", TestContext.Current.CancellationToken))
            .Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        document.RootElement.GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("id").GetGuid())
            .Should().NotContain(recordId);
    }
}
