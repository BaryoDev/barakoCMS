using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BarakoCMS.FeatureFlags;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BarakoCMS.Tests.Features.FeatureFlags;

[Collection("Sequential")]
public class FeatureFlagLevelTests(IntegrationTestFixture factory)
{
    private const string Ofrep = "/ofrep/v1/evaluate/flags";

    private static int _ipCounter;
    private readonly string _ip = $"2001:db8:1149::{Interlocked.Increment(ref _ipCounter):x}";

    private HttpClient Client(string? token = null)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestRemoteIpFilter.Header, _ip);
        if (token is not null) client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private string SignedInToken() =>
        factory.CreateToken(roles: new[] { "Member" }, userId: Guid.NewGuid().ToString());

    private async Task StoreAsync(FeatureFlag flag)
    {
        using var scope = factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Store(flag);
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private static string NewKey(string prefix) => $"{prefix}-{Guid.NewGuid():N}"[..24];

    private async Task<Dictionary<string, bool>> LegacyAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<Dictionary<string, bool>>("/api/feature-flags", TestContext.Current.CancellationToken))!;

    private async Task<HashSet<string>> BulkKeysAsync(HttpClient client)
    {
        var res = await client.PostAsJsonAsync(Ofrep, new { }, TestContext.Current.CancellationToken);
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var doc = await res.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        return doc.GetProperty("flags").EnumerateArray().Select(f => f.GetProperty("key").GetString()!).ToHashSet();
    }

    [Fact]
    public async Task A_legacy_private_flag_with_no_level_is_still_seen_by_a_signed_in_user()
    {
        var key = NewKey("legacy-private");
        await StoreAsync(new FeatureFlag { Key = key, Enabled = true, IsPublic = false, Visibility = null });
        var signedIn = Client(SignedInToken());

        (await LegacyAsync(signedIn)).Should().ContainKey(key);
        (await BulkKeysAsync(signedIn)).Should().Contain(key);
        (await signedIn.PostAsJsonAsync($"{Ofrep}/{key}", new { }, TestContext.Current.CancellationToken))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await LegacyAsync(Client())).Should().NotContainKey(key);
    }

    [Fact]
    public async Task A_legacy_public_flag_with_no_level_is_still_public()
    {
        var key = NewKey("legacy-public");
        await StoreAsync(new FeatureFlag { Key = key, Enabled = true, IsPublic = true, Visibility = null });

        (await LegacyAsync(Client())).Should().ContainKey(key);
        (await BulkKeysAsync(Client())).Should().Contain(key);
    }

    [Fact]
    public async Task An_admin_level_flag_is_hidden_from_a_signed_in_user_and_looks_like_a_missing_key()
    {
        var key = NewKey("admin-only");
        await StoreAsync(new FeatureFlag { Key = key, Enabled = true, Visibility = FlagVisibility.Admin });
        var signedIn = Client(SignedInToken());

        var hidden = await signedIn.PostAsJsonAsync($"{Ofrep}/{key}", new { }, TestContext.Current.CancellationToken);
        var missing = await signedIn.PostAsJsonAsync($"{Ofrep}/{NewKey("no-such")}", new { }, TestContext.Current.CancellationToken);

        hidden.StatusCode.Should().Be(HttpStatusCode.NotFound);
        missing.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var hiddenBody = await hidden.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        var missingBody = await missing.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        hiddenBody.GetProperty("errorCode").GetString().Should().Be(missingBody.GetProperty("errorCode").GetString());
        hiddenBody.GetProperty("errorDetails").GetString().Should().Be(missingBody.GetProperty("errorDetails").GetString());

        (await LegacyAsync(signedIn)).Should().NotContainKey(key);
        (await BulkKeysAsync(signedIn)).Should().NotContain(key);
        (await LegacyAsync(Client())).Should().NotContainKey(key);
    }

    [Fact]
    public async Task An_admin_level_flag_is_seen_by_a_caller_who_manages_flags()
    {
        var key = NewKey("admin-sees");
        await StoreAsync(new FeatureFlag { Key = key, Enabled = true, Visibility = FlagVisibility.Admin });
        var (token, _) = await TestHelpers.CreateAdminUserAsync(factory);
        var admin = Client(token);

        (await LegacyAsync(admin)).Should().ContainKey(key);
        (await BulkKeysAsync(admin)).Should().Contain(key);
        (await admin.PostAsJsonAsync($"{Ofrep}/{key}", new { }, TestContext.Current.CancellationToken))
            .StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_signed_in_level_flag_is_hidden_from_anonymous_and_seen_when_signed_in()
    {
        var key = NewKey("signed-in");
        await StoreAsync(new FeatureFlag { Key = key, Enabled = true, Visibility = FlagVisibility.SignedIn });

        (await LegacyAsync(Client())).Should().NotContainKey(key);
        (await LegacyAsync(Client(SignedInToken()))).Should().ContainKey(key);
    }

    [Fact]
    public async Task A_public_level_flag_is_seen_by_everyone()
    {
        var key = NewKey("public-lvl");
        await StoreAsync(new FeatureFlag { Key = key, Enabled = true, Visibility = FlagVisibility.Public });

        (await LegacyAsync(Client())).Should().ContainKey(key);
        (await LegacyAsync(Client(SignedInToken()))).Should().ContainKey(key);
    }

    private async Task<JsonElement> SaveAsync(HttpClient admin, object body)
    {
        var res = await admin.PostAsJsonAsync("/api/feature-flags/admin", body, TestContext.Current.CancellationToken);
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        return await res.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task A_new_flag_saved_with_no_level_is_admin_only()
    {
        var key = NewKey("unstated");
        var (token, _) = await TestHelpers.CreateAdminUserAsync(factory);
        var admin = Client(token);

        var saved = await SaveAsync(admin, new { key, enabled = true });

        saved.GetProperty("visibility").GetString().Should().Be("Admin");
        saved.GetProperty("isPublic").GetBoolean().Should().BeFalse();
        (await LegacyAsync(Client(SignedInToken()))).Should().NotContainKey(key);
        (await LegacyAsync(admin)).Should().ContainKey(key);
    }

    [Fact]
    public async Task A_save_that_sends_only_isPublic_still_works()
    {
        var publicKey = NewKey("old-public");
        var privateKey = NewKey("old-private");
        var (token, _) = await TestHelpers.CreateAdminUserAsync(factory);
        var admin = Client(token);

        var published = await SaveAsync(admin, new { key = publicKey, enabled = true, isPublic = true });
        var kept = await SaveAsync(admin, new { key = privateKey, enabled = true, isPublic = false });

        published.GetProperty("visibility").GetString().Should().Be("Public");
        published.GetProperty("isPublic").GetBoolean().Should().BeTrue();
        kept.GetProperty("visibility").GetString().Should().Be("SignedIn");
        (await LegacyAsync(Client())).Should().ContainKey(publicKey).And.NotContainKey(privateKey);
        (await LegacyAsync(Client(SignedInToken()))).Should().ContainKey(privateKey);
    }

    [Fact]
    public async Task An_isPublic_false_save_on_an_admin_flag_keeps_it_admin_only()
    {
        var key = NewKey("stay-admin");
        var (token, _) = await TestHelpers.CreateAdminUserAsync(factory);
        var admin = Client(token);
        await SaveAsync(admin, new { key, enabled = true, visibility = "Admin" });

        var saved = await SaveAsync(admin, new { key, enabled = false, isPublic = false });

        saved.GetProperty("visibility").GetString().Should().Be("Admin");
    }

    [Fact]
    public async Task The_admin_list_returns_each_flags_level()
    {
        var key = NewKey("listed");
        await StoreAsync(new FeatureFlag { Key = key, Enabled = true, IsPublic = true, Visibility = null });
        var (token, _) = await TestHelpers.CreateAdminUserAsync(factory);

        var list = await Client(token).GetFromJsonAsync<JsonElement>("/api/feature-flags/admin", TestContext.Current.CancellationToken);
        var row = list.EnumerateArray().First(f => f.GetProperty("key").GetString() == key);

        row.GetProperty("visibility").GetString().Should().Be("Public");
        row.GetProperty("isPublic").GetBoolean().Should().BeTrue();
    }
}
