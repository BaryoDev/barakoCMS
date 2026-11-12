using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using barakoCMS.Infrastructure.Auth;
using barakoCMS.Models;
using barakoCMS.Modules;
using FastEndpoints;
using FluentAssertions;
using Marten;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// The two markers a module sets so a read-only key (<c>bcms_ro_</c>) may call a route: one for a
/// self-service route the key's own principal owns, one for a read route that is a POST only to carry
/// a body. Core registers neither; the routes below exist only in the test host.
/// </summary>
[Collection("Integration1")]
public class ReadOnlyKeySelfRouteTests
{
    private const string SelfRoute = "/api/_test/ro-self";
    private const string UnmarkedRoute = "/api/_test/ro-unmarked";
    private const string ReadRoute = "/api/analytics/_test/summary";
    private const string UnmarkedReadRoute = "/api/analytics/_test/unmarked";

    private static int _ip;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly IntegrationTestFixture _factory;
    private readonly HttpClient _client;

    public ReadOnlyKeySelfRouteTests(IntegrationTestFixture factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    internal sealed record OwnerResponse(Guid Id);

    private async Task<Guid> UserAsync(params string[] roles)
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var userId = Guid.NewGuid();
        var role = await session.Query<Role>().FirstOrDefaultAsync(r => r.Name == "SuperAdmin", Ct)
                   ?? new Role { Id = barakoCMS.Data.DataSeeder.SuperAdminRoleId, Name = "SuperAdmin", Permissions = new() };
        session.Store(role);
        session.Store(new User { Id = userId, Username = $"ro-{userId:N}"[..14], Email = $"{userId:N}@example.com", RoleIds = new() { role.Id } });
        await session.SaveChangesAsync(Ct);
        return userId;
    }

    private async Task<Guid> PrincipalAsync(Guid createdBy, bool disabled = false)
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var principal = new ServicePrincipal
        {
            Id = Guid.NewGuid(),
            TenantSlug = Tenant.DefaultSlug,
            Name = $"ro-{Guid.NewGuid():N}"[..12],
            CreatedBy = createdBy,
            DisabledAt = disabled ? DateTime.UtcNow : null,
        };
        session.Store(principal);
        await session.SaveChangesAsync(Ct);
        return principal.Id;
    }

    private async Task<string> KeyAsync(
        Guid creator, Guid? principalId, string[]? scopes = null, string[]? contentTypes = null,
        bool revoked = false, DateTime? expiresAt = null, bool readOnly = true)
    {
        var secret = (readOnly ? "bcms_ro_" : "bcms_") + Guid.NewGuid().ToString("N");
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Store(new ApiKey
        {
            Id = Guid.NewGuid(),
            Name = "ro-test",
            KeyHash = ApiKeyService.Hash(secret),
            Prefix = secret[..12],
            UserId = creator,
            ServicePrincipalId = principalId,
            TenantSlug = Tenant.DefaultSlug,
            Scopes = (scopes ?? []).ToList(),
            ContentTypes = (contentTypes ?? []).ToList(),
            ReadOnly = readOnly,
            Revoked = revoked,
            ExpiresAt = expiresAt,
        });
        await session.SaveChangesAsync(Ct);
        return secret;
    }

    private async Task<(Guid Creator, Guid Principal, string Secret)> PrincipalKeyAsync(
        string[]? scopes = null, string[]? contentTypes = null)
    {
        var creator = await UserAsync();
        var principal = await PrincipalAsync(creator);
        return (creator, principal, await KeyAsync(creator, principal, scopes, contentTypes));
    }

    private Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string bearer, HttpContent? body = null)
    {
        var req = new HttpRequestMessage(method, path) { Content = body };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        var n = Interlocked.Increment(ref _ip);
        req.Headers.Add(TestRemoteIpFilter.Header, $"198.19.{n / 250 % 250}.{n % 250 + 1}"); // benchmark range
        return _client.SendAsync(req, Ct);
    }

    private static HttpContent Json() => JsonContent.Create(new { });

    private static HttpContent? BodyFor(HttpMethod method) =>
        method == HttpMethod.Post || method == HttpMethod.Put ? Json() : null;

    private static async Task<string> TextAsync(HttpResponseMessage res) => await res.Content.ReadAsStringAsync(Ct);

    // ---- self-service marker -------------------------------------------------

    [Fact]
    public async Task Read_only_principal_key_may_POST_PUT_DELETE_a_marked_route()
    {
        var (_, principal, secret) = await PrincipalKeyAsync();

        foreach (var method in new[] { HttpMethod.Post, HttpMethod.Put, HttpMethod.Delete, HttpMethod.Get })
        {
            var res = await SendAsync(method, SelfRoute, secret, BodyFor(method));
            res.StatusCode.Should().Be(HttpStatusCode.OK, $"{method}: {await TextAsync(res)}");
            (await res.Content.ReadFromJsonAsync<OwnerResponse>(Ct))!.Id.Should().Be(principal);
        }
    }

    [Fact]
    public async Task Read_only_principal_key_still_gets_403_on_an_unmarked_write()
    {
        var (_, _, secret) = await PrincipalKeyAsync();

        foreach (var method in new[] { HttpMethod.Post, HttpMethod.Delete })
        {
            var res = await SendAsync(method, UnmarkedRoute, secret, BodyFor(method));
            res.StatusCode.Should().Be(HttpStatusCode.Forbidden, method.ToString());
        }
    }

    [Fact]
    public async Task Read_only_key_without_a_principal_gets_403_on_a_marked_route()
    {
        var creator = await UserAsync();
        var secret = await KeyAsync(creator, principalId: null, scopes: ["content:read"]);

        foreach (var method in new[] { HttpMethod.Post, HttpMethod.Put, HttpMethod.Delete, HttpMethod.Get })
        {
            var res = await SendAsync(method, SelfRoute, secret, BodyFor(method));
            res.StatusCode.Should().Be(HttpStatusCode.Forbidden, method.ToString());
        }
    }

    [Fact]
    public async Task Read_only_key_with_content_type_limit_gets_403_on_a_marked_route()
    {
        var (_, _, secret) = await PrincipalKeyAsync(contentTypes: ["note"]);

        foreach (var method in new[] { HttpMethod.Post, HttpMethod.Put, HttpMethod.Delete, HttpMethod.Get })
        {
            var res = await SendAsync(method, SelfRoute, secret, BodyFor(method));
            res.StatusCode.Should().Be(HttpStatusCode.Forbidden, method.ToString());
        }
    }

    [Fact]
    public async Task Read_only_principal_key_still_gets_403_on_content_writes_and_on_other_module_routes()
    {
        var (_, _, secret) = await PrincipalKeyAsync(scopes: ["*", "content:write", "contenttype:write"]);

        var attempts = new (HttpMethod Method, string Path)[]
        {
            (HttpMethod.Post, "/api/contents"),
            (HttpMethod.Delete, $"/api/contents/{Guid.NewGuid()}"),
            (HttpMethod.Post, "/api/content-types"),
            (HttpMethod.Post, UnmarkedRoute),
            (HttpMethod.Post, UnmarkedReadRoute),
            // The marker does not widen reads: an unmarked route outside the allowlist stays closed.
            (HttpMethod.Get, UnmarkedRoute),
            (HttpMethod.Get, "/api/content-types"),
        };
        foreach (var (method, path) in attempts)
        {
            var res = await SendAsync(method, path, secret, BodyFor(method));
            res.StatusCode.Should().Be(HttpStatusCode.Forbidden, $"{method} {path}");
        }
    }

    [Fact]
    public async Task Revoked_expired_and_disabled_principal_keys_are_refused_on_a_marked_route()
    {
        var creator = await UserAsync();
        var principal = await PrincipalAsync(creator);
        var disabledPrincipal = await PrincipalAsync(creator, disabled: true);
        var keys = new Dictionary<string, string>
        {
            ["revoked"] = await KeyAsync(creator, principal, revoked: true),
            ["expired"] = await KeyAsync(creator, principal, expiresAt: DateTime.UtcNow.AddMinutes(-1)),
            ["disabled principal"] = await KeyAsync(creator, disabledPrincipal),
        };

        foreach (var (why, secret) in keys)
        {
            var res = await SendAsync(HttpMethod.Post, SelfRoute, secret, Json());
            res.StatusCode.Should().Be(HttpStatusCode.Unauthorized, why);
        }
    }

    [Fact]
    public async Task Marked_route_sees_the_principal_id_not_the_creators_user_id()
    {
        var (creator, principal, secret) = await PrincipalKeyAsync();
        principal.Should().NotBe(creator);

        var res = await SendAsync(HttpMethod.Post, SelfRoute, secret, Json());

        res.StatusCode.Should().Be(HttpStatusCode.OK, await TextAsync(res));
        var owner = (await res.Content.ReadFromJsonAsync<OwnerResponse>(Ct))!.Id;
        owner.Should().Be(principal);
        owner.Should().NotBe(creator);
    }

    [Fact]
    public async Task Marked_route_with_multipart_body_answers_403_not_415_for_an_unmarked_one()
    {
        // The middleware decides before the endpoint binds the body, so a caller the marker does not
        // cover hears 403 whatever the body is, and one it covers is not refused by the key rule.
        var creator = await UserAsync();
        var noPrincipal = await KeyAsync(creator, principalId: null);
        var (_, _, principalKey) = await PrincipalKeyAsync();

        HttpContent Form() => new MultipartFormDataContent { { new StringContent("x"), "note" } };

        (await SendAsync(HttpMethod.Post, SelfRoute, noPrincipal, Form())).StatusCode
            .Should().Be(HttpStatusCode.Forbidden);
        (await SendAsync(HttpMethod.Post, UnmarkedRoute, principalKey, Form())).StatusCode
            .Should().Be(HttpStatusCode.Forbidden);
        (await SendAsync(HttpMethod.Post, SelfRoute, principalKey, Form())).StatusCode
            .Should().NotBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Person_token_and_station_behave_as_before_on_a_marked_route()
    {
        var userId = await UserAsync();
        var token = _factory.CreateToken(["SuperAdmin"], userId.ToString());
        var person = await SendAsync(HttpMethod.Post, SelfRoute, token, Json());
        person.StatusCode.Should().Be(HttpStatusCode.OK, await TextAsync(person));
        (await person.Content.ReadFromJsonAsync<OwnerResponse>(Ct))!.Id.Should().Be(userId);

        // A key that is not read-only is judged as it was: the marked route is outside the content
        // API, so it is refused without a cap: scope, marker or not.
        var plain = await KeyAsync(userId, principalId: null, scopes: ["content:read"], readOnly: false);
        (await SendAsync(HttpMethod.Post, SelfRoute, plain, Json())).StatusCode
            .Should().Be(HttpStatusCode.Forbidden);

        var (secret, display, hash) = barakoCMS.Infrastructure.Auth.Stations.StationKeys.Generate();
        using (var scope = _factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Store(new Station
            {
                Id = Guid.NewGuid(), Name = "board", TenantSlug = Tenant.DefaultSlug,
                KeyHash = hash, DisplayPrefix = display, CreatedBy = userId,
            });
            await session.SaveChangesAsync(Ct);
        }
        (await SendAsync(HttpMethod.Post, SelfRoute, secret, Json())).StatusCode
            .Should().Be(HttpStatusCode.Forbidden);
    }

    // ---- read-route marker ---------------------------------------------------

    [Fact]
    public async Task Read_only_key_may_POST_a_route_marked_read_only()
    {
        var creator = await UserAsync();
        var secret = await KeyAsync(creator, principalId: null, scopes: ["monitor:read"]);

        var res = await SendAsync(HttpMethod.Post, ReadRoute, secret, Json());

        res.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Read_only_key_still_gets_403_on_an_unmarked_POST()
    {
        var creator = await UserAsync();
        var secret = await KeyAsync(creator, principalId: null, scopes: ["monitor:read"]);

        var res = await SendAsync(HttpMethod.Post, UnmarkedReadRoute, secret, Json());

        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Read_only_key_with_a_content_type_limit_is_still_limited_on_a_marked_read_route()
    {
        var creator = await UserAsync();
        var limited = await KeyAsync(creator, principalId: null, scopes: ["monitor:read"], contentTypes: ["note"]);
        var unscoped = await KeyAsync(creator, principalId: null, scopes: ["content:read"]);

        (await SendAsync(HttpMethod.Post, ReadRoute, limited, Json())).StatusCode
            .Should().Be(HttpStatusCode.Forbidden);
        // Scopes apply as for a GET: this route needs monitor:read.
        (await SendAsync(HttpMethod.Post, ReadRoute, unscoped, Json())).StatusCode
            .Should().Be(HttpStatusCode.Forbidden);
    }

    // ---- allowlist fixes -----------------------------------------------------

    [Fact]
    public async Task Read_only_key_with_monitor_read_gets_200_from_the_monitoring_health_read()
    {
        var creator = await UserAsync();
        var secret = await KeyAsync(creator, principalId: null, scopes: ["monitor:read"]);

        var res = await SendAsync(HttpMethod.Get, "/api/monitoring/health", secret);

        res.StatusCode.Should().Be(HttpStatusCode.OK, await TextAsync(res));
    }

    [Fact]
    public async Task Health_ready_with_a_read_only_bearer_answers_as_it_does_with_none()
    {
        var creator = await UserAsync();
        var secret = await KeyAsync(creator, principalId: null, scopes: ["content:read"]);

        var bare = await _client.GetAsync("/health/ready", Ct);
        var withKey = await SendAsync(HttpMethod.Get, "/health/ready", secret);

        withKey.StatusCode.Should().NotBe(HttpStatusCode.Forbidden);
        withKey.StatusCode.Should().Be(bare.StatusCode);
    }

    // ---- the middleware and the processor agree ---------------------------------

    private sealed record Run(bool Refused, int Status);

    private static DefaultHttpContext Request(
        string method, string path, string marker, bool principal, bool contentType, string[] scopes)
    {
        var claims = new List<Claim>
        {
            new("auth_method", "apikey"),
            new(ApiKeyAuthenticationHandler.ReadOnlyClaim, "true"),
            new("UserId", Guid.NewGuid().ToString()),
        };
        if (principal)
            claims.Add(new Claim(ApiKeyAuthenticationHandler.PrincipalClaim, Guid.NewGuid().ToString()));
        if (contentType)
            claims.Add(new Claim(ApiKeyAuthenticationHandler.ContentTypeClaim, "note"));
        claims.AddRange(scopes.Select(s => new Claim("scope", s)));

        var http = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().BuildServiceProvider(),
            User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")),
        };
        http.Request.Method = method;
        http.Request.Path = path;
        http.Response.Body = new MemoryStream();
        object[] metadata = marker switch
        {
            "self" => [new ReadOnlyKeySelfRoute()],
            "read" => [new ReadOnlyKeyReadRoute()],
            _ => [],
        };
        http.SetEndpoint(new Endpoint(_ => Task.CompletedTask, new EndpointMetadataCollection(metadata), "test"));
        return http;
    }

    private static async Task<Run> ViaMiddlewareAsync(DefaultHttpContext http)
    {
        var reached = false;
        await new ReadOnlyKeyMiddleware(_ => { reached = true; return Task.CompletedTask; }).InvokeAsync(http);
        return new Run(!reached, http.Response.StatusCode);
    }

    private static async Task<Run> ViaProcessorAsync(DefaultHttpContext http)
    {
        var context = Substitute.For<IPreProcessorContext>();
        context.HttpContext.Returns(http);
        await new ApiKeyScopeProcessor().PreProcessAsync(context, CancellationToken.None);
        return new Run(http.Response.StatusCode == 403, http.Response.StatusCode);
    }

    [Theory]
    // marker, method, path, principal, contentType, scope, middleware passes, processor passes
    [InlineData("self", "POST", SelfRoute, true, false, "", true, true)]
    [InlineData("self", "DELETE", SelfRoute, true, false, "", true, true)]
    [InlineData("self", "POST", SelfRoute, false, false, "", false, false)]
    [InlineData("self", "POST", SelfRoute, true, true, "", false, false)]
    [InlineData("self", "GET", SelfRoute, false, false, "content:read", false, false)]
    [InlineData("none", "POST", SelfRoute, true, false, "", false, false)]
    [InlineData("none", "GET", SelfRoute, true, false, "", false, false)]
    [InlineData("read", "POST", ReadRoute, false, false, "monitor:read", true, true)]
    [InlineData("read", "DELETE", ReadRoute, true, false, "monitor:read", true, true)]
    [InlineData("read", "POST", ReadRoute, false, false, "content:read", true, false)]
    [InlineData("read", "POST", ReadRoute, false, true, "monitor:read", true, false)]
    [InlineData("none", "POST", ReadRoute, false, false, "monitor:read", false, false)]
    [InlineData("none", "GET", "/api/monitoring/health", false, false, "monitor:read", true, true)]
    [InlineData("none", "POST", "/api/monitoring/health", false, false, "monitor:read", false, false)]
    [InlineData("none", "GET", "/api/monitoring/metrics", false, false, "monitor:read", false, false)]
    public async Task The_middleware_and_the_processor_answer_the_same_request_the_same_way(
        string marker, string method, string path, bool principal, bool contentType, string scope,
        bool middlewarePasses, bool processorPasses)
    {
        string[] scopes = scope.Length == 0 ? [] : [scope];

        var middleware = await ViaMiddlewareAsync(Request(method, path, marker, principal, contentType, scopes));
        var processor = await ViaProcessorAsync(Request(method, path, marker, principal, contentType, scopes));

        middleware.Refused.Should().Be(!middlewarePasses, "middleware");
        processor.Refused.Should().Be(!processorPasses, "processor");
        // Whatever the middleware refuses, the processor refuses too.
        if (middleware.Refused)
            processor.Refused.Should().BeTrue();
    }

    [Fact]
    public void The_owner_id_is_the_principal_id_and_a_read_only_key_without_one_has_none()
    {
        var principal = Guid.NewGuid();
        var user = Guid.NewGuid();

        Caller(("principal_id", principal.ToString()), ("apikey_readonly", "true"))
            .TryGetOwnerId(out var viaPrincipal).Should().BeTrue();
        viaPrincipal.Should().Be(principal);

        Caller(("UserId", user.ToString()), ("apikey_readonly", "true"))
            .TryGetOwnerId(out _).Should().BeFalse();

        Caller(("UserId", user.ToString())).TryGetOwnerId(out var viaUser).Should().BeTrue();
        viaUser.Should().Be(user);

        Caller().TryGetOwnerId(out _).Should().BeFalse();

        ReadOnlyKeyRoutes.PrincipalClaim.Should().Be(ApiKeyAuthenticationHandler.PrincipalClaim);
        ApiKeyAuthenticationHandler.ReadOnlyClaim.Should().Be("apikey_readonly");
    }

    private static ClaimsPrincipal Caller(params (string Type, string Value)[] claims) =>
        new(new ClaimsIdentity(claims.Select(c => new Claim(c.Type, c.Value)), "test"));
}

internal sealed class RoSelfBody { public string? Note { get; set; } }


internal sealed class ReadOnlyKeySelfTestEndpoint : Endpoint<RoSelfBody, ReadOnlyKeySelfRouteTests.OwnerResponse>
{
    public override void Configure()
    {
        Verbs(Http.GET, Http.POST, Http.PUT, Http.DELETE);
        Routes("/api/_test/ro-self");
        Definition.AllowReadOnlyKeySelf();
        Description(b => b.ExcludeFromDescription());
    }

    public override async Task HandleAsync(RoSelfBody req, CancellationToken ct)
    {
        if (!User.TryGetOwnerId(out var owner))
        {
            await Send.ForbiddenAsync(ct);
            return;
        }
        await Send.OkAsync(new ReadOnlyKeySelfRouteTests.OwnerResponse(owner), ct);
    }
}

internal sealed class ReadOnlyKeyUnmarkedTestEndpoint : EndpointWithoutRequest
{
    public override void Configure()
    {
        Verbs(Http.GET, Http.POST, Http.DELETE);
        Routes("/api/_test/ro-unmarked");
        Description(b => b.ExcludeFromDescription());
    }

    public override Task HandleAsync(CancellationToken ct) => Send.NoContentAsync(ct);
}

internal sealed class ReadOnlyKeyReadTestEndpoint : EndpointWithoutRequest
{
    public override void Configure()
    {
        Post("/api/analytics/_test/summary");
        Definition.AllowReadOnlyKeyRead();
        Description(b => b.ExcludeFromDescription());
    }

    public override Task HandleAsync(CancellationToken ct) => Send.NoContentAsync(ct);
}

internal sealed class ReadOnlyKeyUnmarkedReadTestEndpoint : EndpointWithoutRequest
{
    public override void Configure()
    {
        Post("/api/analytics/_test/unmarked");
        Description(b => b.ExcludeFromDescription());
    }

    public override Task HandleAsync(CancellationToken ct) => Send.NoContentAsync(ct);
}
