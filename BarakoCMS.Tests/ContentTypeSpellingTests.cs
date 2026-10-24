using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;

namespace BarakoCMS.Tests;

/// <summary>
/// An entry keeps the type name as the caller spelled it, and listing by the type finds every
/// spelling (#1159).
/// </summary>
[Collection("Integration1")]
public class ContentTypeSpellingTests
{
    private readonly IntegrationTestFixture _factory;

    public ContentTypeSpellingTests(IntegrationTestFixture factory) => _factory = factory;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<HttpClient> ClientAsync()
    {
        var (token, _) = await TestHelpers.CreateAdminUserAsync(_factory);
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static async Task<string> CreateEntryAsync(HttpClient client, string spelling, string title)
    {
        var created = await client.PostAsJsonAsync("/api/contents", new
        {
            contentType = spelling,
            data = new Dictionary<string, object> { ["Title"] = title },
            status = "Published",
        }, Ct);
        created.StatusCode.Should().Be(HttpStatusCode.OK, await created.Content.ReadAsStringAsync(Ct));
        using var body = JsonDocument.Parse(await created.Content.ReadAsStringAsync(Ct));
        return body.RootElement.GetProperty("id").GetGuid().ToString();
    }

    [Fact]
    public async Task Two_spellings_of_one_type_name_both_list_under_the_type()
    {
        var client = await ClientAsync();
        var lower = "spell" + Guid.NewGuid().ToString("N")[..10];
        var mixed = char.ToUpperInvariant(lower[0]) + lower[1..];

        var type = await client.PostAsJsonAsync("/api/content-types", new
        {
            name = lower,
            displayName = "Spelling probe",
            fields = new[] { new { name = "Title", type = "string", sensitivity = "Public" } },
        }, Ct);
        type.StatusCode.Should().Be(HttpStatusCode.OK, await type.Content.ReadAsStringAsync(Ct));

        var lowerId = await CreateEntryAsync(client, lower, "as stored");
        var mixedId = await CreateEntryAsync(client, mixed, "as typed");

        var list = await client.GetAsync($"/api/contents?contentType={lower}&pageSize=50", Ct);
        list.StatusCode.Should().Be(HttpStatusCode.OK, await list.Content.ReadAsStringAsync(Ct));
        using var page = JsonDocument.Parse(await list.Content.ReadAsStringAsync(Ct));
        var items = page.RootElement.GetProperty("items").EnumerateArray().ToList();

        items.Should().HaveCount(2);
        items.Select(i => i.GetProperty("id").GetGuid().ToString()).Should().BeEquivalentTo([lowerId, mixedId]);

        // The spelling each entry was written with is kept.
        items.Select(i => i.GetProperty("contentType").GetString()).Should().BeEquivalentTo([lower, mixed]);

        var byMixed = await client.GetAsync($"/api/contents?contentType={mixed}&pageSize=50", Ct);
        using var mixedPage = JsonDocument.Parse(await byMixed.Content.ReadAsStringAsync(Ct));
        mixedPage.RootElement.GetProperty("items").GetArrayLength().Should().Be(2);
    }

    [Fact]
    public async Task A_rule_written_for_one_spelling_grants_read_on_an_entry_stored_in_another()
    {
        var slug = "spellperm" + Guid.NewGuid().ToString("N")[..8];
        var roleId = Guid.NewGuid();
        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = "reader" + Guid.NewGuid().ToString("N")[..10],
            Email = $"{Guid.NewGuid():N}@example.com",
            PasswordHash = "x",
            RoleIds = [roleId],
        };
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Store(new Role
        {
            Id = roleId,
            Name = "spellreader" + slug,
            TenantSlug = null,
            Permissions = [new ContentTypePermission { ContentTypeSlug = slug, Read = new PermissionRule { Enabled = true } }],
        });
        session.Store(user);
        await session.SaveChangesAsync(Ct);

        var resolver = scope.ServiceProvider.GetRequiredService<IPermissionResolver>();
        resolver.InvalidateAllPermissions();
        var stored = await scope.ServiceProvider.GetRequiredService<IQuerySession>().LoadAsync<User>(user.Id, Ct);

        (await resolver.CanPerformActionAsync(stored!, slug, "read", cancellationToken: Ct)).Should().BeTrue();
        (await resolver.CanPerformActionAsync(stored!, slug.ToUpperInvariant(), "read", cancellationToken: Ct))
            .Should().BeTrue("the list matches the type name ignoring case, so the rule has to as well");
    }

    [Fact]
    public async Task A_role_holding_two_spellings_resolves_each_to_its_own_rule()
    {
        var slug = "dualperm" + Guid.NewGuid().ToString("N")[..8];
        var upper = slug.ToUpperInvariant();
        var roleId = Guid.NewGuid();
        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = "dual" + Guid.NewGuid().ToString("N")[..10],
            Email = $"{Guid.NewGuid():N}@example.com",
            PasswordHash = "x",
            RoleIds = [roleId],
        };
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Store(new Role
        {
            Id = roleId,
            Name = "dualreader" + slug,
            TenantSlug = null,
            // The upper-case spelling is listed first, so a case-insensitive first match would take it.
            Permissions =
            [
                new ContentTypePermission { ContentTypeSlug = upper, Read = new PermissionRule { Enabled = false } },
                new ContentTypePermission { ContentTypeSlug = slug, Read = new PermissionRule { Enabled = true } },
            ],
        });
        session.Store(user);
        await session.SaveChangesAsync(Ct);

        var resolver = scope.ServiceProvider.GetRequiredService<IPermissionResolver>();
        resolver.InvalidateAllPermissions();
        var stored = await scope.ServiceProvider.GetRequiredService<IQuerySession>().LoadAsync<User>(user.Id, Ct);

        (await resolver.CanPerformActionAsync(stored!, slug, "read", cancellationToken: Ct)).Should().BeTrue();
        (await resolver.CanPerformActionAsync(stored!, upper, "read", cancellationToken: Ct)).Should().BeFalse();
    }
}
