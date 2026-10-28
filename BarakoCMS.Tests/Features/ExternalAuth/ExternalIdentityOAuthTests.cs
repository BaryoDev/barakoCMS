using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BarakoCMS.ExternalAuth;
using FluentAssertions;
using Marten;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using barakoCMS.Models;

namespace BarakoCMS.Tests.Features.ExternalAuth;

/// <summary>
/// Answers the Google and GitHub endpoints with whichever account the test sets, so a test can
/// change what the provider reports about the same person between two sign-ins.
/// </summary>
internal sealed class OAuthStubHandler : HttpMessageHandler
{
    public string GoogleSub { get; set; } = "";
    public string GoogleEmail { get; set; } = "";
    public string? GoogleDomain { get; set; }
    public long GitHubId { get; set; }
    public string GitHubEmail { get; set; } = "";

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var uri = request.RequestUri?.ToString() ?? "";
        var domain = GoogleDomain is null ? "" : $$""","hd":"{{GoogleDomain}}" """;
        var json = uri switch
        {
            var u when u.Contains("oauth2.googleapis.com/token", StringComparison.Ordinal)
                => """{"access_token":"ya29.stub","token_type":"Bearer"}""",
            var u when u.Contains("openidconnect.googleapis.com/v1/userinfo", StringComparison.Ordinal)
                => $$"""{"sub":"{{GoogleSub}}","email":"{{GoogleEmail}}","email_verified":true,"name":"Gee Person" {{domain}} }""",
            var u when u.Contains("login/oauth/access_token", StringComparison.Ordinal)
                => """{"access_token":"gho_stub","token_type":"bearer"}""",
            var u when u.EndsWith("api.github.com/user/emails", StringComparison.Ordinal)
                => $$"""[{"email":"{{GitHubEmail}}","primary":true,"verified":true}]""",
            var u when u.EndsWith("api.github.com/user", StringComparison.Ordinal)
                => $$"""{"id":{{GitHubId}},"login":"octo","name":"Octo Cat"}""",
            _ => "{}",
        };

        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return Task.FromResult(response);
    }
}

/// <summary>
/// The OAuth providers sign people in by their provider id and not by the address (#915), a
/// provider claim can give a role, and a tenant chooses which providers it offers.
/// </summary>
/// <remarks>
/// Over real HTTP against the callback endpoints, with the provider answered by a stub. State and
/// club cookies are attached by hand, for the reason given on <see cref="ExternalAuthCallbackTests"/>.
/// Each grant or refusal is paired with the same request under a different input, so an endpoint
/// that always signed in, or always refused, fails one of them.
/// </remarks>
[Collection("Integration1")]
public class ExternalIdentityOAuthTests
{
    private const string BaseUrl = "https://cms.test.example";

    private static readonly Lock Gate = new();
    private static WebApplicationFactory<Program>? _host;
    private static OAuthStubHandler? _stub;
    private static int _ipCounter;

    private readonly IntegrationTestFixture _fixture;

    public ExternalIdentityOAuthTests(IntegrationTestFixture fixture)
    {
        _fixture = fixture;
        lock (Gate)
        {
            if (_host is null)
            {
                var stub = new OAuthStubHandler();
                _host = fixture.WithWebHostBuilder(b =>
                {
                    b.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                        new Dictionary<string, string?>
                        {
                            { "App:BaseUrl", BaseUrl },
                            { "Google:ClientId", "google-client" },
                            { "Google:ClientSecret", "google-secret" },
                            { "GitHub:ClientId", "github-client" },
                            { "GitHub:ClientSecret", "github-secret" },
                            { "Facebook:AppId", "" },
                            { "LinkedIn:ClientId", "" },
                        }));
                    b.ConfigureServices(s => s.AddSingleton<IHttpClientFactory>(new SingleClientFactory(stub)));
                });
                _stub = stub;
            }
        }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static OAuthStubHandler Stub => _stub!;

    private static string Unique(string prefix) => $"{prefix}-{Guid.NewGuid():N}"[..20].ToLowerInvariant();

    private HttpClient Client(string? role = null, string? slug = null)
    {
        var client = _host!.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = false,
        });
        client.DefaultRequestHeaders.Add(TestRemoteIpFilter.Header, $"198.51.100.{Interlocked.Increment(ref _ipCounter) % 250 + 1}");
        if (role is not null && slug is not null)
        {
            var user = new User { Id = Guid.NewGuid(), Email = $"{Unique("c")}@example.com", Username = Unique("c"), PasswordHash = "" };
            var roleId = role == "Admin" ? SystemRoles.AdminRoleId : SystemRoles.UserRoleId;
            StoreAsync(
                user,
                new Membership
                {
                    Id = Guid.NewGuid(), UserId = user.Id, TenantSlug = slug,
                    Status = MembershipStatus.Active, RoleIds = [roleId],
                }).GetAwaiter().GetResult();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
                _fixture.CreateToken(
                    roles: [role],
                    userId: user.Id.ToString(),
                    additionalClaims: new Dictionary<string, string> { ["tenant"] = slug, ["Username"] = user.Username }));
            client.DefaultRequestHeaders.Add("X-Tenant", slug);
        }

        return client;
    }

    private async Task StoreAsync(params object[] documents)
    {
        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        foreach (var document in documents) session.Store(document);
        await session.SaveChangesAsync(Ct);
    }

    private async Task<HttpResponseMessage> CallbackAsync(string provider, string club = "")
    {
        var prefix = provider == "google" ? "gg" : "gh";
        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/auth/{provider}/callback?code=good&state=s1");
        request.Headers.Add("Cookie", $"{prefix}_state=s1; {prefix}_club={club}");
        return await Client().SendAsync(request, Ct);
    }

    private static void ShouldHaveSignedIn(HttpResponseMessage response) =>
        response.Headers.Location!.ToString().Should().StartWith($"{BaseUrl}/auth/social#token=");

    private async Task<User?> UserByEmailAsync(string email)
    {
        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        return await session.Query<User>().FirstOrDefaultAsync(u => u.Email == email, Ct);
    }

    private async Task<ExternalIdentity?> LinkAsync(string provider, string subject)
    {
        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        return await session.LoadAsync<ExternalIdentity>(ExternalIdentity.KeyOf(provider, subject), Ct);
    }

    // ---- the link ----

    [Fact]
    public async Task A_google_user_whose_email_changes_still_signs_in_to_the_same_account()
    {
        var sub = Unique("gsub");
        var before = $"{Unique("old")}@example.com";
        var after = $"{Unique("new")}@example.com";
        Stub.GoogleSub = sub;
        Stub.GoogleDomain = null;

        Stub.GoogleEmail = before;
        ShouldHaveSignedIn(await CallbackAsync("google"));
        var original = await UserByEmailAsync(before);
        original.Should().NotBeNull();

        Stub.GoogleEmail = after;
        ShouldHaveSignedIn(await CallbackAsync("google"));

        (await UserByEmailAsync(after)).Should().BeNull("the address is not what names the account, so no second user is made");
        (await LinkAsync(ExternalProviders.Google, sub))!.UserId.Should().Be(original!.Id);
    }

    [Fact]
    public async Task A_github_user_whose_email_changes_still_signs_in_to_the_same_account()
    {
        var id = Random.Shared.NextInt64(1_000_000, long.MaxValue);
        var before = $"{Unique("old")}@example.com";
        var after = $"{Unique("new")}@example.com";
        Stub.GitHubId = id;

        Stub.GitHubEmail = before;
        ShouldHaveSignedIn(await CallbackAsync("github"));
        var original = await UserByEmailAsync(before);
        original.Should().NotBeNull();

        Stub.GitHubEmail = after;
        ShouldHaveSignedIn(await CallbackAsync("github"));

        (await UserByEmailAsync(after)).Should().BeNull("the address is not what names the account, so no second user is made");
        (await LinkAsync(ExternalProviders.GitHub, id.ToString()))!.UserId.Should().Be(original!.Id);
    }

    [Fact]
    public async Task A_linked_google_account_is_found_by_its_id_and_not_by_the_email_it_now_reports()
    {
        var sub = Unique("gsub");
        var first = $"{Unique("one")}@example.com";
        var someoneElse = new User
        {
            Id = Guid.NewGuid(), Email = $"{Unique("two")}@example.com", Username = Unique("u"), PasswordHash = "",
        };
        await StoreAsync(someoneElse);
        Stub.GoogleSub = sub;
        Stub.GoogleDomain = null;

        Stub.GoogleEmail = first;
        ShouldHaveSignedIn(await CallbackAsync("google"));
        var original = await UserByEmailAsync(first);

        Stub.GoogleEmail = someoneElse.Email;
        ShouldHaveSignedIn(await CallbackAsync("google"));

        (await LinkAsync(ExternalProviders.Google, sub))!.UserId.Should().Be(original!.Id,
            "an address another local user holds must not move the link");
    }

    [Fact]
    public async Task The_first_sign_in_of_an_existing_user_matches_the_verified_email_and_records_the_link()
    {
        var existing = new User
        {
            Id = Guid.NewGuid(), Email = $"{Unique("had")}@example.com", Username = Unique("u"), PasswordHash = "",
        };
        await StoreAsync(existing);
        var id = Random.Shared.NextInt64(1_000_000, long.MaxValue);
        Stub.GitHubId = id;
        Stub.GitHubEmail = existing.Email;

        ShouldHaveSignedIn(await CallbackAsync("github"));

        (await LinkAsync(ExternalProviders.GitHub, id.ToString()))!.UserId.Should().Be(existing.Id);
    }

    [Fact]
    public async Task A_google_account_and_a_github_account_with_the_same_id_are_two_links()
    {
        var shared = Random.Shared.NextInt64(1_000_000, long.MaxValue);
        Stub.GoogleSub = shared.ToString();
        Stub.GoogleDomain = null;
        Stub.GoogleEmail = $"{Unique("g")}@example.com";
        Stub.GitHubId = shared;
        Stub.GitHubEmail = $"{Unique("h")}@example.com";

        ShouldHaveSignedIn(await CallbackAsync("google"));
        ShouldHaveSignedIn(await CallbackAsync("github"));

        var google = await LinkAsync(ExternalProviders.Google, shared.ToString());
        var github = await LinkAsync(ExternalProviders.GitHub, shared.ToString());
        google!.UserId.Should().NotBe(github!.UserId, "the provider is part of the key");
    }

    // ---- the role map ----

    private async Task<(string Slug, Role Teacher)> TenantMappingAsync(string claim, string value)
    {
        var slug = Unique("oa");
        var teacher = new Role
        {
            Id = Guid.NewGuid(), Name = Unique("Teacher"), TenantSlug = slug,
            Permissions = [], SystemCapabilities = [],
        };
        await StoreAsync(
            new Tenant { Id = Guid.NewGuid(), Slug = slug, Name = slug },
            teacher,
            new ExternalRoleMap
            {
                Id = slug, Claim = claim,
                Rules = [new ExternalRoleRule { Provider = ExternalProviders.Google, Value = value, RoleId = teacher.Id }],
            });
        return (slug, teacher);
    }

    private async Task<List<Membership>> MembershipsAsync(Guid userId, string slug)
    {
        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        return (await session.Query<Membership>().Where(m => m.UserId == userId && m.TenantSlug == slug).ToListAsync(Ct)).ToList();
    }

    [Fact]
    public async Task A_provider_claim_the_tenant_mapped_gives_the_role_on_a_google_sign_in()
    {
        var (slug, teacher) = await TenantMappingAsync("hd", "school.edu");
        Stub.GoogleSub = Unique("gsub");
        Stub.GoogleEmail = $"{Unique("staff")}@school.edu";
        Stub.GoogleDomain = "school.edu";

        ShouldHaveSignedIn(await CallbackAsync("google", slug));

        var user = await UserByEmailAsync(Stub.GoogleEmail);
        var memberships = await MembershipsAsync(user!.Id, slug);
        memberships.Should().HaveCount(1);
        memberships[0].RoleIds.Should().Equal(teacher.Id);
    }

    [Fact]
    public async Task A_claim_value_the_map_does_not_name_gives_no_role_on_a_google_sign_in()
    {
        var (slug, _) = await TenantMappingAsync("hd", "school.edu");
        Stub.GoogleSub = Unique("gsub");
        Stub.GoogleEmail = $"{Unique("other")}@elsewhere.org";
        Stub.GoogleDomain = "elsewhere.org";

        var response = await CallbackAsync("google", slug);

        response.Headers.Location!.ToString().Should().NotContain("#token=", "the person is not a member and the map gave nothing");
        var user = await UserByEmailAsync(Stub.GoogleEmail);
        (await MembershipsAsync(user!.Id, slug)).Should().BeEmpty();
    }

    // ---- per-tenant providers ----

    private async Task<string> TenantListingAsync(params string[] providers)
    {
        var slug = Unique("pv");
        await StoreAsync(
            new Tenant { Id = Guid.NewGuid(), Slug = slug, Name = slug },
            new ExternalProviderSettings { Id = slug, Providers = providers.ToList() });
        return slug;
    }

    private async Task<HttpStatusCode> StartAsync(string provider, string club)
    {
        var response = await Client().GetAsync(
            $"/api/auth/{provider}/start" + (club.Length > 0 ? $"?club={club}" : ""), Ct);
        return response.StatusCode;
    }

    private async Task<(bool Google, bool GitHub)> ListedAsync(string club)
    {
        var response = await Client().GetAsync("/api/auth/providers" + (club.Length > 0 ? $"?club={club}" : ""), Ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        return (document.RootElement.GetProperty("google").GetBoolean(), document.RootElement.GetProperty("github").GetBoolean());
    }

    [Fact]
    public async Task Two_tenants_enable_different_providers_and_a_tenant_with_no_list_sees_the_deployment_set()
    {
        var onlyGoogle = await TenantListingAsync(ExternalProviders.Google);
        var onlyGitHub = await TenantListingAsync(ExternalProviders.GitHub);
        var noList = Unique("nolist");
        await StoreAsync(new Tenant { Id = Guid.NewGuid(), Slug = noList, Name = noList });

        (await StartAsync("google", onlyGoogle)).Should().Be(HttpStatusCode.Redirect);
        (await StartAsync("github", onlyGoogle)).Should().Be(HttpStatusCode.NotFound);
        (await StartAsync("github", onlyGitHub)).Should().Be(HttpStatusCode.Redirect);
        (await StartAsync("google", onlyGitHub)).Should().Be(HttpStatusCode.NotFound);

        (await ListedAsync(onlyGoogle)).Should().Be((true, false));
        (await ListedAsync(onlyGitHub)).Should().Be((false, true));

        foreach (var club in new[] { noList, "" })
        {
            (await StartAsync("google", club)).Should().Be(HttpStatusCode.Redirect);
            (await StartAsync("github", club)).Should().Be(HttpStatusCode.Redirect);
            (await ListedAsync(club)).Should().Be((true, true));
        }
    }

    [Fact]
    public async Task A_provider_the_tenant_has_off_cannot_be_called_back_for_that_tenant()
    {
        var onlyGitHub = await TenantListingAsync(ExternalProviders.GitHub);
        Stub.GoogleSub = Unique("gsub");
        Stub.GoogleEmail = $"{Unique("g")}@example.com";
        Stub.GoogleDomain = null;

        (await CallbackAsync("google", onlyGitHub)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await UserByEmailAsync(Stub.GoogleEmail)).Should().BeNull("a refused callback creates nothing");

        var elsewhere = await TenantListingAsync(ExternalProviders.Google);
        (await CallbackAsync("google", elsewhere)).StatusCode.Should().Be(HttpStatusCode.Redirect);
    }

    [Fact]
    public async Task A_tenant_administrator_sets_reads_and_removes_the_provider_list()
    {
        var slug = Unique("adm");
        await StoreAsync(new Tenant { Id = Guid.NewGuid(), Slug = slug, Name = slug });
        var admin = Client("Admin", slug);

        var put = await admin.PutAsJsonAsync("/api/tenants/external-providers",
            new { providers = new[] { ExternalProviders.Google } }, Ct);
        put.StatusCode.Should().Be(HttpStatusCode.OK, await put.Content.ReadAsStringAsync(Ct));
        (await ListedAsync(slug)).Should().Be((true, false));
        (await admin.GetAsync("/api/tenants/external-providers", Ct)).StatusCode.Should().Be(HttpStatusCode.OK);

        (await admin.PutAsJsonAsync("/api/tenants/external-providers",
            new { providers = new[] { "oauth:myspace" } }, Ct)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Client("User", slug).PutAsJsonAsync("/api/tenants/external-providers",
            new { providers = new[] { ExternalProviders.GitHub } }, Ct)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ListedAsync(slug)).Should().Be((true, false), "neither refused write changed the list");

        (await admin.DeleteAsync("/api/tenants/external-providers", Ct)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await admin.GetAsync("/api/tenants/external-providers", Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ListedAsync(slug)).Should().Be((true, true), "with no list the tenant is back on the deployment's providers");
    }
}
