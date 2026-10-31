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
/// What a Facebook data deletion request does to an account (#1270): when Facebook was the only way
/// in, the person chooses to keep or delete it, and an unanswered choice erases it when the window
/// passes. Over real HTTP, with the sweep called directly.
/// </summary>
[Collection("Integration1")]
public class FacebookAccountDeletionTests
{
    private const string BaseUrl = "https://cms.test.example";
    private static readonly Lock Gate = new();
    private static WebApplicationFactory<Program>? _host;
    private static int _ipCounter;

    private readonly IntegrationTestFixture _fixture;

    public FacebookAccountDeletionTests(IntegrationTestFixture fixture)
    {
        _fixture = fixture;
        lock (Gate)
        {
            _host ??= fixture.WithWebHostBuilder(b => b.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    { "App:BaseUrl", BaseUrl },
                    { "Facebook:AppId", "fb-app" },
                    { "Facebook:AppSecret", SignedRequests.Secret },
                })));
        }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Unique(string prefix) => $"{prefix}{Guid.NewGuid():N}"[..20];

    private static HttpClient Client()
    {
        var client = _host!.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });
        client.DefaultRequestHeaders.Add(TestRemoteIpFilter.Header, $"198.51.103.{Interlocked.Increment(ref _ipCounter) % 250 + 1}");
        return client;
    }

    private async Task<T> InSessionAsync<T>(Func<IDocumentSession, Task<T>> work)
    {
        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var result = await work(session);
        await session.SaveChangesAsync(Ct);
        return result;
    }

    private async Task<User> NewUserAsync(string passwordHash = "", params Guid[] roleIds)
    {
        var name = Unique("u");
        var user = new User
        {
            Id = Guid.NewGuid(), Username = name, Email = $"{name}@example.com",
            PasswordHash = passwordHash, RoleIds = [.. roleIds], DisplayName = "Person " + name,
        };
        await InSessionAsync(async s => { s.Store(user); return 0; });
        return user;
    }

    private async Task<string> LinkAsync(string provider, Guid userId, string? subject = null)
    {
        subject ??= Random.Shared.NextInt64(1_000_000_000, long.MaxValue).ToString();
        await InSessionAsync(async s =>
        {
            s.Store(new ExternalIdentity
            {
                Id = ExternalIdentity.KeyOf(provider, subject), Issuer = provider, Subject = subject,
                UserId = userId, Provider = provider,
            });
            return 0;
        });
        return subject;
    }

    private static async Task<(string Code, JsonElement Body)> RequestDeletionAsync(string facebookId)
    {
        var response = await Client().PostAsync("/api/auth/facebook/data-deletion", new FormUrlEncodedContent(
            [new KeyValuePair<string, string>("signed_request", SignedRequests.For(facebookId))]), Ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsyncElement();
        return (body.GetProperty("confirmation_code").GetString()!, body);
    }

    private static async Task<(HttpStatusCode Status, string? State, JsonElement Body)> ChooseAsync(string code, string? choice)
    {
        var response = await Client().PostAsJsonAsync($"/api/auth/facebook/data-deletion/status/{code}", new { choice }, Ct);
        var body = response.Content.Headers.ContentLength == 0 ? default : await response.Content.ReadFromJsonAsyncElement();
        string? state = body.ValueKind == JsonValueKind.Object && body.TryGetProperty("status", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
        return (response.StatusCode, state, body);
    }

    private static async Task<string> StatusOfAsync(string code)
    {
        var response = await Client().GetAsync($"/api/auth/facebook/data-deletion/status/{code}", Ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsyncElement()).GetProperty("status").GetString()!;
    }

    private async Task<User?> UserAsync(Guid id) =>
        await InSessionAsync(s => s.LoadAsync<User>(id, Ct));

    private async Task<bool> LinkExistsAsync(string provider, string subject) =>
        await InSessionAsync(async s => await s.LoadAsync<ExternalIdentity>(ExternalIdentity.KeyOf(provider, subject), Ct) is not null);

    private static bool IsErased(User? user) =>
        user is not null && user.Username.StartsWith("erased-", StringComparison.Ordinal)
        && user.Email.EndsWith("@erased.invalid", StringComparison.Ordinal);

    /// <summary>A Facebook only account with its pending request, ready for a choice.</summary>
    private async Task<(User User, string Facebook, string Code)> AwaitingAsync(params Guid[] roleIds)
    {
        var user = await NewUserAsync(roleIds: roleIds);
        var facebook = await LinkAsync(ExternalProviders.Facebook, user.Id);
        var (code, _) = await RequestDeletionAsync(facebook);
        (await StatusOfAsync(code)).Should().Be("awaiting_choice");
        return (user, facebook, code);
    }

    // ---- opening ----

    [Theory]
    [InlineData("password")]
    [InlineData("google")]
    public async Task With_another_login_the_link_goes_and_the_account_stays_and_the_request_completes(string other)
    {
        var user = await NewUserAsync(passwordHash: other == "password" ? "hash-of-a-password" : "");
        var facebook = await LinkAsync(ExternalProviders.Facebook, user.Id);
        var google = other == "google" ? await LinkAsync(ExternalProviders.Google, user.Id) : null;

        var (code, _) = await RequestDeletionAsync(facebook);

        (await StatusOfAsync(code)).Should().Be("completed");
        (await LinkExistsAsync(ExternalProviders.Facebook, facebook)).Should().BeFalse();
        if (google is not null) (await LinkExistsAsync(ExternalProviders.Google, google)).Should().BeTrue();
        var kept = await UserAsync(user.Id);
        kept!.Username.Should().Be(user.Username);
        (await ChooseAsync(code, "delete_account")).State.Should().Be("completed", "there was nothing to choose");
        IsErased(await UserAsync(user.Id)).Should().BeFalse();
    }

    [Fact]
    public async Task With_facebook_as_the_only_login_the_link_goes_at_once_and_the_request_awaits_a_choice()
    {
        var user = await NewUserAsync();
        var facebook = await LinkAsync(ExternalProviders.Facebook, user.Id);

        var (code, _) = await RequestDeletionAsync(facebook);

        (await LinkExistsAsync(ExternalProviders.Facebook, facebook)).Should().BeFalse();
        var response = await Client().GetAsync($"/api/auth/facebook/data-deletion/status/{code}", Ct);
        var body = await response.Content.ReadFromJsonAsyncElement();
        body.GetProperty("status").GetString().Should().Be("awaiting_choice");
        body.GetProperty("choiceExpiresAt").GetDateTime().Should().BeCloseTo(DateTime.UtcNow.AddDays(30), TimeSpan.FromMinutes(5));
        (await UserAsync(user.Id))!.Username.Should().Be(user.Username, "nothing is erased before a choice");
    }

    [Fact]
    public void The_choice_window_defaults_to_thirty_days_and_reads_its_setting()
    {
        IConfiguration Config(string? days) => new ConfigurationBuilder()
            .AddInMemoryCollection(days is null ? [] : new Dictionary<string, string?> { { "Facebook:DeletionChoiceDays", days } })
            .Build();

        FacebookDeletionFlow.ChoiceWindow(Config(null)).Should().Be(TimeSpan.FromDays(30));
        FacebookDeletionFlow.ChoiceWindow(Config("7")).Should().Be(TimeSpan.FromDays(7));
        FacebookDeletionFlow.ChoiceWindow(Config("0")).Should().Be(TimeSpan.FromDays(30));
        FacebookDeletionFlow.ChoiceWindow(Config("soon")).Should().Be(TimeSpan.FromDays(30));
    }

    // ---- choices ----

    [Fact]
    public async Task Keeping_the_account_settles_the_request_as_kept_and_changes_nothing_else()
    {
        var (user, _, code) = await AwaitingAsync();

        var result = await ChooseAsync(code, "keep_account");

        result.Status.Should().Be(HttpStatusCode.OK);
        result.State.Should().Be("kept");
        (await StatusOfAsync(code)).Should().Be("kept");
        (await UserAsync(user.Id))!.Username.Should().Be(user.Username);
    }

    [Fact]
    public async Task Deleting_the_account_erases_it_and_completes_the_request()
    {
        var (user, facebook, code) = await AwaitingAsync();

        var result = await ChooseAsync(code, "delete_account");

        result.State.Should().Be("completed");
        (await StatusOfAsync(code)).Should().Be("completed");
        var erased = await UserAsync(user.Id);
        IsErased(erased).Should().BeTrue();
        erased!.PasswordHash.Should().BeEmpty();
        erased.RoleIds.Should().BeEmpty();
        erased.TokensValidFrom.Should().NotBeNull("sessions already issued end");

        var audit = await InSessionAsync(s => s.Query<AuditEvent>()
            .Where(a => a.Action == UserEraser.AuditAction && a.TargetId == user.Id.ToString()).ToListAsync(Ct));
        audit.Should().ContainSingle();
        var written = JsonSerializer.Serialize(audit);
        written.Should().NotContain(facebook, "the entry carries no Facebook user id");
        written.Should().NotContain(code);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("erase_everything")]
    public async Task A_choice_that_is_not_one_of_the_two_answers_400_and_changes_nothing(string? choice)
    {
        var (user, _, code) = await AwaitingAsync();

        (await ChooseAsync(code, choice)).Status.Should().Be(HttpStatusCode.BadRequest);

        (await StatusOfAsync(code)).Should().Be("awaiting_choice");
        IsErased(await UserAsync(user.Id)).Should().BeFalse();
        (await ChooseAsync(code, "keep_account")).State.Should().Be("kept", "the same request with a real choice works");
    }

    [Fact]
    public async Task A_choice_for_an_unknown_or_malformed_code_answers_404()
    {
        (await ChooseAsync(Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"), "keep_account")).Status.Should().Be(HttpStatusCode.NotFound);
        (await ChooseAsync("not-hex-at-all-not-hex", "keep_account")).Status.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_second_choice_after_the_request_settled_changes_nothing()
    {
        var (kept, _, keepCode) = await AwaitingAsync();
        await ChooseAsync(keepCode, "keep_account");
        var (erased, _, deleteCode) = await AwaitingAsync();
        await ChooseAsync(deleteCode, "delete_account");

        (await ChooseAsync(keepCode, "delete_account")).State.Should().Be("kept");
        (await ChooseAsync(deleteCode, "keep_account")).State.Should().Be("completed");

        IsErased(await UserAsync(kept.Id)).Should().BeFalse("a kept account is not erased by a later choice");
        IsErased(await UserAsync(erased.Id)).Should().BeTrue();
    }

    [Fact]
    public async Task Racing_choices_settle_once_and_every_caller_sees_the_same_status()
    {
        var (user, _, code) = await AwaitingAsync();

        var results = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(i => ChooseAsync(code, i % 2 == 0 ? "delete_account" : "keep_account")));

        results.Should().OnlyContain(r => r.Status == HttpStatusCode.OK);
        var settled = results.Select(r => r.State).Distinct().ToList();
        settled.Should().ContainSingle("one choice won and the others reported it");
        settled[0].Should().BeOneOf("kept", "completed");
        (await StatusOfAsync(code)).Should().Be(settled[0]);
        IsErased(await UserAsync(user.Id)).Should().Be(settled[0] == "completed", "the account is erased only if the delete choice won");
    }

    // ---- the recheck ----

    [Fact]
    public async Task A_delete_choice_keeps_an_account_that_gained_a_password_in_the_window()
    {
        var (user, _, code) = await AwaitingAsync();
        await InSessionAsync(async s =>
        {
            var u = (await s.LoadAsync<User>(user.Id, Ct))!;
            u.PasswordHash = "hash-of-a-password";
            s.Store(u);
            return 0;
        });

        (await ChooseAsync(code, "delete_account")).State.Should().Be("kept");

        IsErased(await UserAsync(user.Id)).Should().BeFalse();
    }

    [Fact]
    public async Task A_delete_choice_keeps_an_account_that_linked_facebook_again_in_the_window()
    {
        var (user, _, code) = await AwaitingAsync();
        await LinkAsync(ExternalProviders.Facebook, user.Id);

        (await ChooseAsync(code, "delete_account")).State.Should().Be("kept");

        IsErased(await UserAsync(user.Id)).Should().BeFalse();
    }

    // ---- the sweep ----

    private Task ExpireAsync(string code) => InSessionAsync(async s =>
    {
        var request = (await s.LoadAsync<FacebookDeletionRequest>(FacebookDeletionFlow.HashOf(code), Ct))!;
        request.ExpiresAt = DateTime.UtcNow.AddMinutes(-1);
        s.Store(request);
        return 0;
    });

    [Fact]
    public async Task The_sweep_erases_an_unanswered_request_after_its_window_and_leaves_a_live_one()
    {
        var (due, _, dueCode) = await AwaitingAsync();
        var (live, _, liveCode) = await AwaitingAsync();
        await ExpireAsync(dueCode);

        var settled = await FacebookDeletionFlow.SweepAsync(_fixture.Services, DateTime.UtcNow, Ct);

        settled.Should().BeGreaterThanOrEqualTo(1);
        (await StatusOfAsync(dueCode)).Should().Be("completed");
        IsErased(await UserAsync(due.Id)).Should().BeTrue();
        (await StatusOfAsync(liveCode)).Should().Be("awaiting_choice", "its window has not passed");
        IsErased(await UserAsync(live.Id)).Should().BeFalse();
    }

    [Fact]
    public async Task The_sweep_run_twice_or_at_once_settles_a_request_once()
    {
        var (user, _, code) = await AwaitingAsync();
        await ExpireAsync(code);

        var runs = await Task.WhenAll(
            FacebookDeletionFlow.SweepAsync(_fixture.Services, DateTime.UtcNow, Ct),
            FacebookDeletionFlow.SweepAsync(_fixture.Services, DateTime.UtcNow, Ct));
        var again = await FacebookDeletionFlow.SweepAsync(_fixture.Services, DateTime.UtcNow, Ct);

        runs.Sum().Should().Be(1, "of two nodes sweeping at once, one settles it");
        again.Should().Be(0);
        (await StatusOfAsync(code)).Should().Be("completed");
        IsErased(await UserAsync(user.Id)).Should().BeTrue();
    }

    [Fact]
    public async Task The_sweep_keeps_an_account_that_gained_a_login_in_the_window()
    {
        var (user, _, code) = await AwaitingAsync();
        await LinkAsync(ExternalProviders.Google, user.Id);
        await ExpireAsync(code);

        await FacebookDeletionFlow.SweepAsync(_fixture.Services, DateTime.UtcNow, Ct);

        (await StatusOfAsync(code)).Should().Be("kept");
        IsErased(await UserAsync(user.Id)).Should().BeFalse();
    }

    // ---- the admin guard ----

    [Fact]
    public async Task A_super_admin_is_never_erased_by_a_choice_or_the_sweep()
    {
        var (viaChoice, _, choiceCode) = await AwaitingAsync(SystemRoles.SuperAdminRoleId);
        var (viaSweep, _, sweepCode) = await AwaitingAsync(SystemRoles.AdminRoleId);
        await ExpireAsync(sweepCode);

        (await ChooseAsync(choiceCode, "delete_account")).State.Should().Be("needs_admin");
        await FacebookDeletionFlow.SweepAsync(_fixture.Services, DateTime.UtcNow, Ct);

        (await StatusOfAsync(sweepCode)).Should().Be("needs_admin");
        IsErased(await UserAsync(viaChoice.Id)).Should().BeFalse();
        IsErased(await UserAsync(viaSweep.Id)).Should().BeFalse();
    }

    [Fact]
    public async Task A_holder_of_a_platform_capability_role_is_never_erased()
    {
        var role = new Role { Id = Guid.NewGuid(), Name = Unique("plat"), SystemCapabilities = [SystemCapabilities.ManageTenants] };
        await InSessionAsync(async s => { s.Store(role); return 0; });
        var (user, _, code) = await AwaitingAsync(role.Id);

        (await ChooseAsync(code, "delete_account")).State.Should().Be("needs_admin");

        IsErased(await UserAsync(user.Id)).Should().BeFalse();
    }

    [Fact]
    public async Task The_last_admin_of_a_tenant_is_not_erased_but_one_of_two_is()
    {
        var slug = Unique("tenant");
        var (last, _, lastCode) = await AwaitingAsync();
        var (first, _, firstCode) = await AwaitingAsync();
        var (second, _, _) = await AwaitingAsync();
        var otherSlug = Unique("tenant");
        await InSessionAsync(async s =>
        {
            Membership Admin(Guid userId, string tenant) => new()
            {
                Id = Guid.NewGuid(), UserId = userId, TenantSlug = tenant,
                RoleIds = [SystemRoles.AdminRoleId], Status = MembershipStatus.Active,
            };
            s.Store(Admin(last.Id, slug));
            s.Store(Admin(first.Id, otherSlug));
            s.Store(Admin(second.Id, otherSlug));
            return 0;
        });

        (await ChooseAsync(lastCode, "delete_account")).State.Should().Be("needs_admin");
        (await ChooseAsync(firstCode, "delete_account")).State.Should().Be("completed");

        IsErased(await UserAsync(last.Id)).Should().BeFalse();
        IsErased(await UserAsync(first.Id)).Should().BeTrue();
        (await InSessionAsync(s => s.Query<Membership>().CountAsync(m => m.TenantSlug == otherSlug, Ct)))
            .Should().Be(1, "the erased admin's membership went and the other admin's stayed");
    }

    private async Task InTenantAsync(string tenant, params object[] documents)
    {
        using var scope = _fixture.Services.CreateScope();
        await using var session = scope.ServiceProvider.GetRequiredService<IDocumentStore>().LightweightSession(tenant);
        foreach (var document in documents) session.Store(document);
        await session.SaveChangesAsync(Ct);
    }

    [Fact]
    public async Task A_super_admin_through_a_group_in_another_tenant_is_not_erased_from_the_default_tenant()
    {
        var (user, _, code) = await AwaitingAsync();
        await InTenantAsync(Unique("tenant"), new UserGroup
        {
            Id = Guid.NewGuid(), Name = Unique("g"), UserIds = [user.Id], RoleIds = [SystemRoles.SuperAdminRoleId],
        });

        (await ChooseAsync(code, "delete_account")).State.Should().Be("needs_admin");

        IsErased(await UserAsync(user.Id)).Should().BeFalse();
    }

    [Fact]
    public async Task A_platform_capability_role_defined_in_another_tenant_protects_its_holder()
    {
        var role = new Role { Id = Guid.NewGuid(), Name = Unique("plat"), SystemCapabilities = [SystemCapabilities.ManageTenants] };
        await InTenantAsync(Unique("tenant"), role);
        var (user, _, code) = await AwaitingAsync(role.Id);

        (await ChooseAsync(code, "delete_account")).State.Should().Be("needs_admin");

        IsErased(await UserAsync(user.Id)).Should().BeFalse();
    }

    [Fact]
    public async Task Erasing_a_user_removes_their_id_from_a_group_in_another_tenant_and_leaves_one_copy()
    {
        var user = await NewUserAsync();
        var bystander = await NewUserAsync();
        var tenant = Unique("tenant");
        var group = new UserGroup { Id = Guid.NewGuid(), Name = Unique("g"), UserIds = [user.Id, bystander.Id] };
        await InTenantAsync(tenant, group);

        using (var scope = _fixture.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            var current = (await session.LoadAsync<User>(user.Id, Ct))!;
            await scope.ServiceProvider.GetRequiredService<UserEraser>().QueueEraseAsync(session, current, "test", Ct);
            await session.SaveChangesAsync(Ct);
        }

        var store = _fixture.Services.GetRequiredService<IDocumentStore>();
        await using (var inTenant = store.QuerySession(tenant))
        {
            var after = await inTenant.LoadAsync<UserGroup>(group.Id, Ct);
            after.Should().NotBeNull();
            after!.UserIds.Should().Equal(bystander.Id);
        }

        await using var inDefault = store.QuerySession();
        (await inDefault.LoadAsync<UserGroup>(group.Id, Ct)).Should().BeNull("the edit is not written into the default tenant");
    }

    // ---- the code is the capability ----

    [Fact]
    public async Task The_raw_code_is_never_stored_and_only_its_hash_finds_the_request()
    {
        var (_, _, code) = await AwaitingAsync();

        await InSessionAsync(async s =>
        {
            (await s.LoadAsync<FacebookDeletionRequest>(code, Ct)).Should().BeNull("the code is not the id");
            var row = await s.LoadAsync<FacebookDeletionRequest>(FacebookDeletionFlow.HashOf(code), Ct);
            row.Should().NotBeNull();
            JsonSerializer.Serialize(row).Should().NotContain(code);
            return 0;
        });
        var viaHash = await Client().GetAsync($"/api/auth/facebook/data-deletion/status/{FacebookDeletionFlow.HashOf(code)}", Ct);
        viaHash.StatusCode.Should().Be(HttpStatusCode.NotFound, "the stored hash is not a code");
    }

    // ---- GET changes nothing ----

    [Fact]
    public async Task A_get_renders_the_two_choices_for_html_and_json_otherwise_and_changes_nothing()
    {
        var (user, _, code) = await AwaitingAsync();
        var page = new HttpRequestMessage(HttpMethod.Get, $"/api/auth/facebook/data-deletion/status/{code}");
        page.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));

        var html = await Client().SendAsync(page, Ct);
        var json = await Client().GetAsync($"/api/auth/facebook/data-deletion/status/{code}", Ct);

        html.StatusCode.Should().Be(HttpStatusCode.OK);
        html.Content.Headers.ContentType!.MediaType.Should().Be("text/html");
        var text = await html.Content.ReadAsStringAsync(Ct);
        text.Should().Contain("delete_account").And.Contain("keep_account").And.Contain("method=\"post\"");
        html.Headers.GetValues("Content-Security-Policy").Single().Should().Contain("default-src 'none'").And.Contain("form-action 'self'");
        html.Headers.GetValues("Cache-Control").Single().Should().Contain("no-store");
        json.Content.Headers.ContentType!.MediaType.Should().Be("application/json");

        for (var i = 0; i < 3; i++)
        {
            await Client().SendAsync(new HttpRequestMessage(HttpMethod.Get, $"/api/auth/facebook/data-deletion/status/{code}")
                { Headers = { Accept = { new MediaTypeWithQualityHeaderValue("text/html") } } }, Ct);
        }

        (await StatusOfAsync(code)).Should().Be("awaiting_choice", "a preview bot fetching the page settles nothing");
        IsErased(await UserAsync(user.Id)).Should().BeFalse();
    }

    [Fact]
    public async Task A_form_post_from_the_page_settles_the_request_and_answers_html()
    {
        var (user, _, code) = await AwaitingAsync();
        var post = new HttpRequestMessage(HttpMethod.Post, $"/api/auth/facebook/data-deletion/status/{code}")
        {
            Content = new FormUrlEncodedContent([new KeyValuePair<string, string>("choice", "keep_account")]),
        };
        post.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));

        var response = await Client().SendAsync(post, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/html");
        (await StatusOfAsync(code)).Should().Be("kept");
        IsErased(await UserAsync(user.Id)).Should().BeFalse();
    }

    // ---- what erasure removes ----

    [Fact]
    public async Task Erasing_a_user_removes_or_clears_every_record_that_holds_their_id()
    {
        var user = await NewUserAsync();
        var bystander = await NewUserAsync();
        var email = user.Email;
        var tenant = Unique("t");
        var group = new UserGroup { Id = Guid.NewGuid(), Name = Unique("g"), UserIds = [user.Id, bystander.Id] };
        var refresh = new RefreshToken { Id = Guid.NewGuid(), TokenHash = Unique("h"), UserId = user.Id, ExpiresAt = DateTime.UtcNow.AddDays(1) };
        var apiKey = new ApiKey { Id = Guid.NewGuid(), KeyHash = Unique("k"), Prefix = "p", UserId = user.Id, Name = "k" };
        var otherKey = new ApiKey { Id = Guid.NewGuid(), KeyHash = Unique("k"), Prefix = "p", UserId = bystander.Id, Name = "k" };
        var googleSubject = await LinkAsync(ExternalProviders.Google, user.Id);
        var bystanderSubject = await LinkAsync(ExternalProviders.Google, bystander.Id);
        await InSessionAsync(async s =>
        {
            s.Store(group);
            s.Store(refresh);
            s.Store(apiKey);
            s.Store(otherKey);
            s.Store(new Membership { Id = Guid.NewGuid(), UserId = user.Id, TenantSlug = tenant, RoleIds = [SystemRoles.UserRoleId] });
            s.Store(new Membership { Id = Guid.NewGuid(), UserId = bystander.Id, TenantSlug = tenant, RoleIds = [SystemRoles.UserRoleId] });
            s.Store(new MfaSecret { Id = user.Id, EncryptedSecret = "x", Enabled = true });
            s.Store(new SocialProfile { Id = Guid.NewGuid(), UserId = user.Id, Name = "Person", Birthday = "01/02/1990" });
            s.Store(new TenantAccountManager { Id = Guid.NewGuid(), AccountId = Guid.NewGuid(), UserId = user.Id });
            s.Store(new OtpCode { Email = email, CodeHash = "c", ExpiresAt = DateTime.UtcNow.AddMinutes(5) });
            s.Store(new MagicLinkToken { Email = email, TokenHash = Unique("m"), TenantSlug = tenant, ExpiresAt = DateTime.UtcNow.AddMinutes(5) });
            s.Store(new PendingRegistration { Email = email, Username = Unique("p"), TokenHash = Unique("r"), ExpiresAt = DateTime.UtcNow.AddMinutes(5) });
            return 0;
        });

        using (var scope = _fixture.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            var current = (await session.LoadAsync<User>(user.Id, Ct))!;
            await scope.ServiceProvider.GetRequiredService<UserEraser>().QueueEraseAsync(session, current, "test", Ct);
            await session.SaveChangesAsync(Ct);
        }

        await InSessionAsync(async s =>
        {
            (await s.Query<Membership>().CountAsync(m => m.UserId == user.Id, Ct)).Should().Be(0);
            (await s.Query<Membership>().CountAsync(m => m.UserId == bystander.Id, Ct)).Should().Be(1);
            (await s.LoadAsync<ApiKey>(apiKey.Id, Ct)).Should().BeNull();
            (await s.LoadAsync<ApiKey>(otherKey.Id, Ct)).Should().NotBeNull();
            (await s.LoadAsync<MfaSecret>(user.Id, Ct)).Should().BeNull();
            (await s.Query<SocialProfile>().CountAsync(p => p.UserId == user.Id, Ct)).Should().Be(0);
            (await s.Query<TenantAccountManager>().CountAsync(m => m.UserId == user.Id, Ct)).Should().Be(0);
            (await s.LoadAsync<ExternalIdentity>(ExternalIdentity.KeyOf(ExternalProviders.Google, googleSubject), Ct)).Should().BeNull();
            (await s.LoadAsync<ExternalIdentity>(ExternalIdentity.KeyOf(ExternalProviders.Google, bystanderSubject), Ct)).Should().NotBeNull();
            (await s.Query<OtpCode>().CountAsync(o => o.Email == email, Ct)).Should().Be(0);
            (await s.Query<MagicLinkToken>().CountAsync(t => t.Email == email, Ct)).Should().Be(0);
            (await s.Query<PendingRegistration>().CountAsync(r => r.Email == email, Ct)).Should().Be(0);

            var groupAfter = (await s.LoadAsync<UserGroup>(group.Id, Ct))!;
            groupAfter.UserIds.Should().Equal(bystander.Id);

            var tokenAfter = (await s.LoadAsync<RefreshToken>(refresh.Id, Ct))!;
            tokenAfter.IsRevoked.Should().BeTrue("sessions end");

            var tomb = (await s.LoadAsync<User>(user.Id, Ct))!;
            IsErased(tomb).Should().BeTrue();
            tomb.DisplayName.Should().BeNull();
            tomb.PasswordHash.Should().BeEmpty();
            tomb.RoleIds.Should().BeEmpty();
            tomb.TokensValidFrom.Should().NotBeNull();
            (await s.LoadAsync<User>(bystander.Id, Ct))!.Username.Should().Be(bystander.Username);

            (await s.Query<AuditEvent>().CountAsync(a => a.Action == UserEraser.AuditAction && a.TargetId == user.Id.ToString(), Ct)).Should().Be(1);
            return 0;
        });
    }
}
