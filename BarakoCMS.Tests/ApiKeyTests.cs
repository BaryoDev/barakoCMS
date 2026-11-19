using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BarakoCMS.Tests.SampleProbe;
using barakoCMS.Infrastructure.Auth;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>Where an API key was last used from (#1365).</summary>
[Collection("Integration1")]
public class ApiKeyTests
{
    private readonly IntegrationTestFixture _fixture;

    public ApiKeyTests(IntegrationTestFixture fixture) => _fixture = fixture;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Last_used_ip_is_recorded_and_listed()
    {
        var owner = Guid.NewGuid();
        var keyId = Guid.NewGuid();
        var secret = "bcms_" + Guid.NewGuid().ToString("N");
        using (var scope = _fixture.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Store(new User { Id = owner, Username = $"key-{owner:N}", Email = $"key-{owner:N}@example.com", RoleIds = [] });
            session.Store(new ApiKey
            {
                Id = keyId, Name = "ip-test", KeyHash = ApiKeyService.Hash(secret), Prefix = secret[..12],
                UserId = owner, TenantSlug = Tenant.DefaultSlug, Scopes = ["content:read"],
            });
            await session.SaveChangesAsync(Ct);
        }

        var ip = "2001:db8:1365:abcd::7";
        var use = _fixture.CreateClient();
        use.DefaultRequestHeaders.Add(TestRemoteIpFilter.Header, ip);
        use.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        await use.GetAsync("/api/me", Ct);

        // The write is best effort and does not hold the request, so read until it lands.
        ApiKey? stored = null;
        for (var attempt = 0; attempt < 50 && stored?.LastUsedIp is null; attempt++)
        {
            await Task.Delay(100, Ct);
            using var scope = _fixture.Services.CreateScope();
            stored = await scope.ServiceProvider.GetRequiredService<IQuerySession>().LoadAsync<ApiKey>(keyId, Ct);
        }

        stored!.LastUsedIp.Should().Be(ip);
        stored.LastUsedAt.Should().NotBeNull("the address is written in the patch that sets the time");

        var admin = await _fixture.CreateClientAsync("SuperAdmin");
        var list = await admin.GetFromJsonAsync<JsonElement>("/api/api-keys?pageSize=100", Ct);
        var item = list.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("id").GetGuid() == keyId);
        item.GetProperty("lastUsedIp").GetString().Should().Be(ip);
    }
}
