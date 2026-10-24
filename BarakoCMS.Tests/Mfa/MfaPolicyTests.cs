using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using barakoCMS.Infrastructure.Realtime;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using OtpNet;
using Xunit;
using LoginResponse = barakoCMS.Features.Auth.Login.Response;
using MfaModels = barakoCMS.Features.Auth.Mfa;

namespace BarakoCMS.Tests.Mfa;

/// <summary>
/// Roles that require MFA, end to end over real HTTP. Same setup as MfaTests: each instance gets its
/// own client IP, so it has its own auth rate-limit bucket.
/// </summary>
[Collection("Integration2")]
public class MfaPolicyTests
{
    internal const string Password = "P@ssword123!Ab";
    private static int _ipCounter;

    private readonly IntegrationTestFixture _factory;
    private readonly string _ip;

    public MfaPolicyTests(IntegrationTestFixture factory)
    {
        _factory = factory;
        var n = Interlocked.Increment(ref _ipCounter);
        _ip = $"198.51.100.{(n + 120) % 250 + 1}"; // offset from MfaTests so the buckets do not collide
    }

    internal HttpClient Client(string? bearer = null, string? tenant = null)
    {
        var c = _factory
            .WithWebHostBuilder(b => b.ConfigureServices(s =>
                s.AddSingleton<Microsoft.AspNetCore.Hosting.IStartupFilter, TestRemoteIpFilter>()))
            .CreateClient();
        c.DefaultRequestHeaders.Add(TestRemoteIpFilter.Header, _ip);
        if (tenant is not null) c.DefaultRequestHeaders.Add("X-Tenant", tenant);
        if (bearer is not null) c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        return c;
    }

    private async Task<Guid> StoreRoleAsync(bool requireMfa)
    {
        using var scope = _factory.Services.CreateScope();
        var s = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var id = Guid.NewGuid();
        s.Store(new Role
        {
            Id = id,
            Name = $"mfa-role-{Guid.NewGuid():N}"[..24],
            SystemCapabilities = ["manage_roles"],
            RequireMfa = requireMfa,
        });
        await s.SaveChangesAsync();
        return id;
    }

    private async Task<string> SeedUserAsync(Guid roleId)
    {
        using var scope = _factory.Services.CreateScope();
        var s = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var username = $"mfap-{Guid.NewGuid():N}";
        s.Store(new User
        {
            Id = Guid.NewGuid(),
            Username = username,
            Email = $"{username}@test.com",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(Password),
            RoleIds = [roleId],
        });
        await s.SaveChangesAsync();
        return username;
    }

    private async Task<LoginResponse> LoginAsync(string username)
    {
        var res = await Client().PostAsJsonAsync("/api/auth/login",
            new barakoCMS.Features.Auth.Login.Request { Username = username, Password = Password });
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        return (await res.Content.ReadFromJsonAsync<LoginResponse>())!;
    }

    [Fact]
    public async Task Login_RoleRequiresMfaAndNotEnrolled_FlagsEnrollment()
    {
        var username = await SeedUserAsync(await StoreRoleAsync(requireMfa: true));

        var login = await LoginAsync(username);

        login.MfaEnrollmentRequired.Should().BeTrue();
        login.Token.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Login_RoleWithoutRequirement_NoFlag()
    {
        var username = await SeedUserAsync(await StoreRoleAsync(requireMfa: false));

        (await LoginAsync(username)).MfaEnrollmentRequired.Should().BeFalse();
    }

    [Fact]
    public async Task EnrollmentToken_OtherEndpoint_Returns403WithHeader()
    {
        var login = await LoginAsync(await SeedUserAsync(await StoreRoleAsync(requireMfa: true)));

        // The role holds manage_roles, so only the enrollment rule can refuse this.
        var response = await Client(login.Token).GetAsync("/api/roles");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        response.Headers.GetValues("X-Mfa-Enrollment-Required").Should().ContainSingle("true");
    }

    [Fact]
    public async Task EnrollmentToken_StatusAndSetup_Allowed()
    {
        var login = await LoginAsync(await SeedUserAsync(await StoreRoleAsync(requireMfa: true)));
        var client = Client(login.Token);

        (await client.GetAsync("/api/auth/mfa/status")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.PostAsync("/api/auth/mfa/setup", null)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task EnrollmentToken_AfterEnrolling_NextLoginAsksForCode()
    {
        var username = await SeedUserAsync(await StoreRoleAsync(requireMfa: true));
        var client = Client((await LoginAsync(username)).Token);

        var setup = (await (await client.PostAsync("/api/auth/mfa/setup", null))
            .Content.ReadFromJsonAsync<MfaModels.SetupResponse>())!;
        var code = new Totp(Base32Encoding.ToBytes(setup.Secret)).ComputeTotp();
        (await client.PostAsJsonAsync("/api/auth/mfa/enable", new MfaModels.CodeRequest { Code = code }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var again = await LoginAsync(username);

        again.RequiresMfa.Should().BeTrue();
        again.MfaEnrollmentRequired.Should().BeFalse();
    }

    [Fact]
    public async Task Login_SetupStartedButNotEnabled_StillFlagsEnrollment()
    {
        var username = await SeedUserAsync(await StoreRoleAsync(requireMfa: true));
        var client = Client((await LoginAsync(username)).Token);
        (await client.PostAsync("/api/auth/mfa/setup", null)).StatusCode.Should().Be(HttpStatusCode.OK);

        var again = await LoginAsync(username);

        again.RequiresMfa.Should().BeFalse();
        again.MfaEnrollmentRequired.Should().BeTrue();
    }

    [Fact]
    public async Task Login_RequireMfaRoleHeldOnlyInOtherTenant_NotLimited()
    {
        var tenantA = $"club-{Guid.NewGuid():N}";
        var tenantB = $"club-{Guid.NewGuid():N}";
        var strictRole = await StoreRoleAsync(requireMfa: true);
        var plainRole = await StoreRoleAsync(requireMfa: false);
        var username = await SeedUserAsync(plainRole);
        using (var scope = _factory.Services.CreateScope())
        {
            var s = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            var userId = (await s.Query<User>().FirstAsync(u => u.Username == username)).Id;
            foreach (var slug in new[] { tenantA, tenantB })
                s.Store(new Tenant { Id = Guid.NewGuid(), Slug = slug, Name = slug });
            s.Store(new Membership
            {
                Id = Guid.NewGuid(), UserId = userId, TenantSlug = tenantA,
                Status = MembershipStatus.Active, RoleIds = [strictRole],
            });
            s.Store(new Membership
            {
                Id = Guid.NewGuid(), UserId = userId, TenantSlug = tenantB,
                Status = MembershipStatus.Active, RoleIds = [],
            });
            await s.SaveChangesAsync();
        }

        async Task<LoginResponse> LoginIn(string tenant)
        {
            var res = await Client(tenant: tenant).PostAsJsonAsync("/api/auth/login",
                new barakoCMS.Features.Auth.Login.Request { Username = username, Password = Password });
            res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
            return (await res.Content.ReadFromJsonAsync<LoginResponse>())!;
        }

        (await LoginIn(tenantA)).MfaEnrollmentRequired.Should().BeTrue();
        (await LoginIn(tenantB)).MfaEnrollmentRequired.Should().BeFalse();
    }

    [Fact]
    public async Task EnrollmentToken_Refreshed_StaysLimited()
    {
        var login = await LoginAsync(await SeedUserAsync(await StoreRoleAsync(requireMfa: true)));

        var refreshed = await Client().PostAsJsonAsync("/api/auth/refresh",
            new barakoCMS.Features.Auth.Refresh.Request { RefreshToken = login.RefreshToken });

        refreshed.StatusCode.Should().Be(HttpStatusCode.OK, await refreshed.Content.ReadAsStringAsync());
        var body = (await refreshed.Content.ReadFromJsonAsync<barakoCMS.Features.Auth.Refresh.Response>())!;
        var limited = await Client(body.Token).GetAsync("/api/roles");
        limited.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        limited.Headers.GetValues("X-Mfa-Enrollment-Required").Should().ContainSingle("true");
        (await Client(body.Token).GetAsync("/api/auth/mfa/status")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task EnrollmentToken_HubSubscribe_Refused()
    {
        var login = await LoginAsync(await SeedUserAsync(await StoreRoleAsync(requireMfa: true)));
        await using var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(_factory.Server.BaseAddress, RealtimeHub.Path), o =>
            {
                o.Transports = HttpTransportType.LongPolling;
                o.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
                o.AccessTokenProvider = () => Task.FromResult<string?>(login.Token);
            })
            .Build();
        await connection.StartAsync();

        var act = () => connection.InvokeAsync("Subscribe", "any.topic");

        await act.Should().ThrowAsync<HubException>().WithMessage("*MFA*");
    }

    [Fact]
    public async Task CreateRole_RequireMfa_IsStoredAndReturned()
    {
        var admin = Client(await _factory.StoredUserTokenAsync("SuperAdmin"));

        var response = await admin.PostAsJsonAsync("/api/roles", new
        {
            Name = $"req-{Guid.NewGuid():N}"[..20],
            Description = "",
            Permissions = Array.Empty<object>(),
            SystemCapabilities = Array.Empty<string>(),
            RequireMfa = true,
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        using var created = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var id = created.RootElement.GetProperty("id").GetGuid();

        // The create response carries only the id; the role itself comes back from GET.
        var fetched = await admin.GetAsync($"/api/roles/{id}");
        fetched.StatusCode.Should().Be(HttpStatusCode.OK);
        using var body = JsonDocument.Parse(await fetched.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("requireMfa").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task UpdateRole_WithoutRequireMfa_KeepsIt()
    {
        // An admin client built before this field existed sends no RequireMfa. That must not
        // switch the requirement off.
        var roleId = await StoreRoleAsync(requireMfa: true);
        var admin = Client(await _factory.StoredUserTokenAsync("SuperAdmin"));

        var response = await admin.PutAsJsonAsync($"/api/roles/{roleId}", new
        {
            Id = roleId,
            Name = $"renamed-{Guid.NewGuid():N}"[..20],
            Description = "renamed",
            Permissions = Array.Empty<object>(),
            SystemCapabilities = new[] { "manage_roles" },
        });

        response.IsSuccessStatusCode.Should().BeTrue(await response.Content.ReadAsStringAsync());
        using var scope = _factory.Services.CreateScope();
        var role = await scope.ServiceProvider.GetRequiredService<IQuerySession>().LoadAsync<Role>(roleId);
        role!.RequireMfa.Should().BeTrue();
    }

    [Fact]
    public async Task UpdateRole_WithRequireMfa_StoresIt()
    {
        var roleId = await StoreRoleAsync(requireMfa: false);
        var admin = Client(await _factory.StoredUserTokenAsync("SuperAdmin"));

        var response = await admin.PutAsJsonAsync($"/api/roles/{roleId}", new
        {
            Id = roleId,
            Name = $"strict-{Guid.NewGuid():N}"[..20],
            Description = "strict",
            Permissions = Array.Empty<object>(),
            SystemCapabilities = new[] { "manage_roles" },
            RequireMfa = true,
        });

        response.IsSuccessStatusCode.Should().BeTrue(await response.Content.ReadAsStringAsync());
        using var scope = _factory.Services.CreateScope();
        var role = await scope.ServiceProvider.GetRequiredService<IQuerySession>().LoadAsync<Role>(roleId);
        role!.RequireMfa.Should().BeTrue();
    }

    [Fact]
    public async Task EnrollmentToken_RefreshSentWithTheLimitedBearer_StillAnswers()
    {
        // A client that attaches its current token to the refresh call must not be turned away.
        var login = await LoginAsync(await SeedUserAsync(await StoreRoleAsync(requireMfa: true)));

        var refreshed = await Client(login.Token).PostAsJsonAsync("/api/auth/refresh",
            new barakoCMS.Features.Auth.Refresh.Request { RefreshToken = login.RefreshToken });

        refreshed.StatusCode.Should().Be(HttpStatusCode.OK, await refreshed.Content.ReadAsStringAsync());
    }
}
