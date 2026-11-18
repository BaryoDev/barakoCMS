using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BarakoCMS.Tests.Features.Tenants;
using barakoCMS.Infrastructure.Auth;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// What an API key needs to author content: reading the page tree and the block schema as the user it
/// acts for, never writing the schema, and a create repeated under one <c>Idempotency-Key</c> making
/// one entry.
/// </summary>
[Collection("Integration1")]
public class ApiKeyAuthoringReadsTests
{
    // The tree lists one configured type (Modules:Pages:ContentType in the fixture).
    private const string PageType = PagesModuleTests.TypeName;

    private readonly IntegrationTestFixture _fixture;

    public ApiKeyAuthoringReadsTests(IntegrationTestFixture fixture) => _fixture = fixture;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string NewName(string prefix) => prefix + Guid.NewGuid().ToString("n")[..8];

    private async Task StoreAsync(params object[] documents)
    {
        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        foreach (var document in documents) session.Store(document);
        await session.SaveChangesAsync(Ct);
    }

    // Declared through PagesModuleTests' gate, so the two classes never both store the type.
    private Task EnsurePageTypeAsync() => new PagesModuleTests(_fixture).DeclareTypeAsync();

    private static ContentTypeDefinition PostType(string name) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        DisplayName = name,
        Fields = [new FieldDefinition { Name = "Title", DisplayName = "Title", Type = "string", IsRequired = true }],
    };

    /// <summary>A user whose one role carries the given permissions, in the given tenant.</summary>
    private async Task<(Guid UserId, string Token)> UserWithAsync(string tenant, params ContentTypePermission[] permissions)
    {
        var role = new Role { Id = Guid.NewGuid(), Name = $"Authors_{Guid.NewGuid():n}", Permissions = [.. permissions] };
        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = $"ak_{Guid.NewGuid():n}"[..14],
            Email = $"ak_{Guid.NewGuid():n}@example.com",
            RoleIds = [role.Id],
        };
        var documents = new List<object> { role, user };
        if (tenant != Tenant.DefaultSlug)
        {
            documents.Add(new Membership
            {
                Id = Guid.NewGuid(),
                UserId = user.Id,
                TenantSlug = tenant,
                Status = MembershipStatus.Active,
                RoleIds = [role.Id],
            });
        }

        await StoreAsync([.. documents]);
        return (user.Id, _fixture.CreateToken(roles: [role.Name], userId: user.Id.ToString()));
    }

    private async Task<string> KeyForAsync(Guid userId, string tenant, params string[] scopes)
    {
        var secret = "bcms_" + Guid.NewGuid().ToString("N");
        await StoreAsync(new ApiKey
        {
            Id = Guid.NewGuid(),
            Name = "test",
            KeyHash = ApiKeyService.Hash(secret),
            Prefix = secret[..12],
            UserId = userId,
            TenantSlug = tenant,
            Scopes = [.. scopes],
        });
        return secret;
    }

    private HttpClient ClientFor(string bearer, string tenant)
    {
        var client = _fixture.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        if (tenant != Tenant.DefaultSlug) client.DefaultRequestHeaders.Add("X-Tenant", tenant);
        return client;
    }

    private async Task<int> EntriesAsync(string type)
    {
        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        return await session.Query<Content>().CountAsync(c => c.ContentType == type, Ct);
    }

    private static readonly object HeroSchema = new
    {
        blocks = new Dictionary<string, object>
        {
            ["hero"] = new
            {
                type = "object",
                properties = new { heading = new { type = "string" } },
            },
        },
    };

    [Fact]
    public async Task A_content_read_key_reads_only_the_pages_its_role_may_read()
    {
        await EnsurePageTypeAsync();
        // "Own entries": the owner condition ContentOwnershipTests uses for the same rule.
        var own = new Dictionary<string, object>
        {
            ["$createdBy"] = new Dictionary<string, object> { ["_eq"] = "$CURRENT_USER" },
        };
        var (userId, _) = await UserWithAsync(Tenant.DefaultSlug, new ContentTypePermission
        {
            ContentTypeSlug = PageType,
            Read = new PermissionRule { Enabled = true, Conditions = own },
        });
        var visible = Guid.NewGuid();
        var hidden = Guid.NewGuid();
        await StoreAsync(
            new Content
            {
                Id = visible, ContentType = PageType, Status = ContentStatus.Published, CreatedBy = userId,
                Data = new Dictionary<string, object> { ["Title"] = "Mine", ["Slug"] = NewName("mine") },
            },
            new Content
            {
                Id = hidden, ContentType = PageType, Status = ContentStatus.Published, CreatedBy = Guid.NewGuid(),
                Data = new Dictionary<string, object> { ["Title"] = "Theirs", ["Slug"] = NewName("theirs") },
            });
        var key = await KeyForAsync(userId, Tenant.DefaultSlug, "content:read");

        var res = await ClientFor(key, Tenant.DefaultSlug).GetAsync("/api/pages/tree", Ct);

        var text = await res.Content.ReadAsStringAsync(Ct);
        res.StatusCode.Should().Be(HttpStatusCode.OK, text);
        var ids = JsonDocument.Parse(text).RootElement.GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("id").GetGuid()).ToList();
        ids.Should().Equal(visible);
    }

    [Fact]
    public async Task A_content_read_key_reads_the_block_schema()
    {
        var tenant = await TenantProfileSeed.TenantAsync(_fixture, Ct);
        var admin = await TenantProfileSeed.AdminInAsync(_fixture, tenant, Ct);
        (await admin.PutAsJsonAsync("/api/blocks/schema", HeroSchema, Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
        var (userId, _) = await UserWithAsync(tenant);
        var key = await KeyForAsync(userId, tenant, "content:read");

        var res = await ClientFor(key, tenant).GetAsync("/api/blocks/schema", Ct);

        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task A_content_write_key_cannot_put_the_block_schema()
    {
        var tenant = await TenantProfileSeed.TenantAsync(_fixture, Ct);
        // A SuperAdmin acting through the key: the role would allow the PUT, so only the scope refuses it.
        var userId = Guid.NewGuid();
        await StoreAsync(
            new User
            {
                Id = userId,
                Username = $"ak_{Guid.NewGuid():n}"[..14],
                Email = $"ak_{Guid.NewGuid():n}@example.com",
                RoleIds = [SystemRoles.SuperAdminRoleId],
            },
            new Membership
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                TenantSlug = tenant,
                Status = MembershipStatus.Active,
                RoleIds = [SystemRoles.SuperAdminRoleId],
            });
        var key = await KeyForAsync(userId, tenant, "content:write");

        var res = await ClientFor(key, tenant).PutAsJsonAsync("/api/blocks/schema", HeroSchema, Ct);

        res.StatusCode.Should().Be(HttpStatusCode.Forbidden, await res.Content.ReadAsStringAsync(Ct));
    }

    private async Task<(string Type, HttpClient Client)> WriterAsync()
    {
        var type = NewName("post");
        await StoreAsync(PostType(type));
        var (userId, _) = await UserWithAsync(Tenant.DefaultSlug, new ContentTypePermission
        {
            ContentTypeSlug = type,
            Create = new PermissionRule { Enabled = true },
        });
        var key = await KeyForAsync(userId, Tenant.DefaultSlug, "content:write");
        return (type, ClientFor(key, Tenant.DefaultSlug));
    }

    private static Task<HttpResponseMessage> CreateAsync(HttpClient client, string type, string idempotencyKey, bool dryRun = false)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/contents")
        {
            Content = JsonContent.Create(new { contentType = type, data = new { Title = "Hello" }, dryRun }),
        };
        request.Headers.Add("Idempotency-Key", idempotencyKey);
        return client.SendAsync(request, Ct);
    }

    private static async Task<Guid> IdOfAsync(HttpResponseMessage res)
    {
        var text = await res.Content.ReadAsStringAsync(Ct);
        res.IsSuccessStatusCode.Should().BeTrue(text);
        return JsonDocument.Parse(text).RootElement.GetProperty("id").GetGuid();
    }

    [Fact]
    public async Task A_create_repeated_with_one_idempotency_key_makes_one_entry()
    {
        var (type, client) = await WriterAsync();
        var idempotencyKey = Guid.NewGuid().ToString("n");

        var first = await CreateAsync(client, type, idempotencyKey);
        var second = await CreateAsync(client, type, idempotencyKey);

        (await IdOfAsync(second)).Should().Be(await IdOfAsync(first));
        (await EntriesAsync(type)).Should().Be(1);
    }

    [Fact]
    public async Task A_dry_run_then_a_real_create_with_one_idempotency_key_makes_one_entry()
    {
        var (type, client) = await WriterAsync();
        var idempotencyKey = Guid.NewGuid().ToString("n");

        var dry = await CreateAsync(client, type, idempotencyKey, dryRun: true);
        dry.IsSuccessStatusCode.Should().BeTrue(await dry.Content.ReadAsStringAsync(Ct));
        (await EntriesAsync(type)).Should().Be(0);

        var real = await CreateAsync(client, type, idempotencyKey);

        (await IdOfAsync(real)).Should().NotBe(Guid.Empty);
        (await EntriesAsync(type)).Should().Be(1);
    }
}
