using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using barakoCMS.Models;

namespace BarakoCMS.Tests;

/// <summary>
/// The settings endpoint refuses a key by the same credential rule workflow parameters are hidden
/// by, and a setting stored under such a key before the rule covered it is still read.
/// </summary>
[Collection("Sequential")]
public class SettingCredentialNameTests
{
    private readonly IntegrationTestFixture _factory;
    private static int _ipCounter;

    public SettingCredentialNameTests(IntegrationTestFixture factory) => _factory = factory;

    private static string NextIp() =>
        $"198.51.100.{Interlocked.Increment(ref _ipCounter) % 250 + 1}";

    // The prefix is hex, so it cannot spell a credential word and change what a key is refused for.
    private static string Key(string name) => $"T{Guid.NewGuid():N}:{name}";

    /// <summary>The five words workflow parameters were already hidden by and settings were not refused by.</summary>
    [Theory]
    [InlineData("Passwd")]
    [InlineData("Pwd")]
    [InlineData("AccessKey")]
    [InlineData("access_key")]
    [InlineData("private_key")]
    public async Task A_key_named_by_a_word_only_workflows_knew_is_refused_and_not_stored(string name)
    {
        var client = await SuperAdminAsync();
        var key = Key(name);

        var response = await client.PostAsJsonAsync(
            "/api/settings", new { key, value = "typed-in-clear" }, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "everything stored there is plaintext and GET /api/settings hands all of it back");
        (await StoredAsync(key)).Should().BeNull("refused has to mean not written");
    }

    /// <summary>The seven words the endpoint already refused.</summary>
    [Theory]
    [InlineData("ApiKey")]
    [InlineData("api_key")]
    [InlineData("Password")]
    [InlineData("Secret")]
    [InlineData("Token")]
    [InlineData("Credential")]
    [InlineData("PrivateKey")]
    public async Task A_key_the_endpoint_already_refused_is_still_refused(string name)
    {
        var client = await SuperAdminAsync();
        var key = Key(name);

        var response = await client.PostAsJsonAsync(
            "/api/settings", new { key, value = "typed-in-clear" }, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await StoredAsync(key)).Should().BeNull("refused has to mean not written");
    }

    [Fact]
    public async Task An_ordinary_key_is_still_saved()
    {
        var client = await SuperAdminAsync();
        var key = Key("Enabled");

        var response = await client.PostAsJsonAsync(
            "/api/settings", new { key, value = "true" }, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        (await StoredAsync(key))!.Value.Should().Be("true");
    }

    [Fact]
    public async Task A_setting_stored_under_such_a_key_earlier_is_still_returned()
    {
        var key = Key("AccessKey");
        await StoreAsync(new SystemSetting { Id = Guid.NewGuid(), Key = key, Value = "stored-earlier" });
        var client = await SuperAdminAsync();

        var found = await FindInListAsync(client, key);

        found.Should().NotBeNull("a setting saved before the key was refused is not hidden from the list");
        found!.Value.GetProperty("value").GetString().Should().Be("stored-earlier");
    }

    [Fact]
    public async Task A_setting_stored_under_such_a_key_earlier_is_not_changed_by_a_refused_save()
    {
        var key = Key("AccessKey");
        await StoreAsync(new SystemSetting { Id = Guid.NewGuid(), Key = key, Value = "stored-earlier" });
        var client = await SuperAdminAsync();

        var response = await client.PostAsJsonAsync(
            "/api/settings", new { key, value = "typed-in-clear" }, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await StoredAsync(key))!.Value.Should().Be("stored-earlier");
    }

    private async Task<HttpClient> SuperAdminAsync()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", await _factory.StoredUserTokenAsync("SuperAdmin"));
        client.DefaultRequestHeaders.Add(TestRemoteIpFilter.Header, NextIp());
        return client;
    }

    private async Task StoreAsync(SystemSetting setting)
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentStore>().LightweightSession();
        session.Store(setting);
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private async Task<SystemSetting?> StoredAsync(string key)
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentStore>().QuerySession();
        return await session.Query<SystemSetting>()
            .Where(s => s.Key == key)
            .FirstOrDefaultAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>Reads the list a page at a time, since other tests leave settings of their own in it.</summary>
    private static async Task<JsonElement?> FindInListAsync(HttpClient client, string key)
    {
        const int pageSize = 100;
        const int maxPages = 50;

        for (var page = 1; page <= maxPages; page++)
        {
            var response = await client.GetAsync(
                $"/api/settings?page={page}&pageSize={pageSize}", TestContext.Current.CancellationToken);
            var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            response.StatusCode.Should().Be(HttpStatusCode.OK, body);

            using var document = JsonDocument.Parse(body);
            var items = document.RootElement.GetProperty("items").EnumerateArray().Select(e => e.Clone()).ToList();

            foreach (var item in items)
            {
                if (item.GetProperty("key").GetString() == key) return item;
            }

            if (items.Count < pageSize) return null;
        }

        return null;
    }
}
