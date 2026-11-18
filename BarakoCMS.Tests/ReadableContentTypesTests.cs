using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using barakoCMS.Infrastructure.Auth;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// <c>GET /api/contents/types</c> and <c>GET /api/contents/types/{name}</c>: the shape of a content
/// type for a caller who may read it, so a client holding only a content key can see the fields before
/// it writes an entry. A type the caller may not read is answered as one that does not exist.
/// </summary>
/// <remarks>
/// Types are seeded directly in the default tenant under names made unique per test, because that
/// tenant is shared by the whole collection. The two literal names the key scope test needs
/// (<c>erase</c> and <c>share-links</c>) are stored once, by the one test that uses them.
/// </remarks>
[Collection("Integration1")]
public class ReadableContentTypesTests
{
    private const string ListRoute = "/api/contents/types";

    private readonly IntegrationTestFixture _fixture;

    public ReadableContentTypesTests(IntegrationTestFixture fixture) => _fixture = fixture;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string NewName(string prefix) => prefix + Guid.NewGuid().ToString("n")[..8];

    private static ContentTypeDefinition TypeOf(string name, params FieldDefinition[] extra) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        DisplayName = name.ToUpperInvariant(),
        Description = $"The {name} type",
        Fields =
        [
            new FieldDefinition { Name = "Title", DisplayName = "Title", Type = "string", IsRequired = true },
            .. extra,
        ],
    };

    private static ContentTypePermission Read(string type, Dictionary<string, object>? conditions = null) => new()
    {
        ContentTypeSlug = type,
        Read = new PermissionRule { Enabled = true, Conditions = conditions },
    };

    private async Task StoreAsync(params object[] documents)
    {
        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        foreach (var document in documents) session.Store(document);
        await session.SaveChangesAsync(Ct);
    }

    /// <summary>A user whose only role carries the given permissions.</summary>
    private async Task<(Guid UserId, string Token)> UserWithAsync(params ContentTypePermission[] permissions)
    {
        var role = new Role { Id = Guid.NewGuid(), Name = $"Readers_{Guid.NewGuid():n}", Permissions = [.. permissions] };
        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = $"rt_{Guid.NewGuid():n}"[..14],
            Email = $"rt_{Guid.NewGuid():n}@example.com",
            RoleIds = [role.Id],
        };
        await StoreAsync(role, user);
        return (user.Id, _fixture.CreateToken(roles: [role.Name], userId: user.Id.ToString()));
    }

    /// <summary>A key that carries only the given scopes and acts as the user, as a CLI agent's would.</summary>
    private async Task<string> KeyForAsync(Guid userId, params string[] scopes)
    {
        var secret = "bcms_" + Guid.NewGuid().ToString("N");
        await StoreAsync(new ApiKey
        {
            Id = Guid.NewGuid(),
            Name = "test",
            KeyHash = ApiKeyService.Hash(secret),
            Prefix = secret[..12],
            UserId = userId,
            TenantSlug = Tenant.DefaultSlug,
            Scopes = [.. scopes],
        });
        return secret;
    }

    private async Task<HttpResponseMessage> GetAsync(string path, string bearer)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        return await _fixture.CreateClient().SendAsync(request, Ct);
    }

    private static async Task<JsonElement> BodyAsync(HttpResponseMessage res)
    {
        var text = await res.Content.ReadAsStringAsync(Ct);
        res.StatusCode.Should().Be(HttpStatusCode.OK, text);
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private static List<string> NamesIn(JsonElement list) =>
        list.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("name").GetString()!).ToList();

    [Fact]
    public async Task A_reader_gets_the_shape_of_a_type_it_may_read()
    {
        var post = NewName("post");
        await StoreAsync(TypeOf(post, new FieldDefinition
        {
            Name = "Body",
            DisplayName = "Body",
            Type = "richtext",
            Sensitivity = SensitivityLevel.Sensitive,
            VisibleToRoles = ["Editor"],
        }, new FieldDefinition
        {
            Name = "Total",
            DisplayName = "Total",
            Type = "decimal",
            Computed = new ComputedDefinition { Fn = "sum", Args = ["Title"] },
        }));
        var (_, token) = await UserWithAsync(Read(post));

        var body = await BodyAsync(await GetAsync($"{ListRoute}/{post}", token));

        body.GetProperty("name").GetString().Should().Be(post);
        var fields = body.GetProperty("fields").EnumerateArray().ToList();
        var title = fields.Single(f => f.GetProperty("name").GetString() == "Title");
        title.GetProperty("isRequired").GetBoolean().Should().BeTrue();
        fields.Single(f => f.GetProperty("name").GetString() == "Total")
            .GetProperty("computed").GetProperty("fn").GetString().Should().Be("sum");
        foreach (var field in fields)
        {
            field.TryGetProperty("roles", out _).Should().BeFalse();
            field.TryGetProperty("visibleToRoles", out _).Should().BeFalse();
            field.TryGetProperty("sensitivity", out _).Should().BeFalse();
        }
    }

    [Fact]
    public async Task A_type_the_caller_may_not_read_is_404()
    {
        var post = NewName("post");
        var page = NewName("page");
        await StoreAsync(TypeOf(post), TypeOf(page));
        var (_, token) = await UserWithAsync(Read(post));

        (await GetAsync($"{ListRoute}/{page}", token)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task An_unknown_type_is_404()
    {
        var (_, token) = await UserWithAsync(Read(NewName("post")));

        (await GetAsync($"{ListRoute}/{NewName("nothere")}", token)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_conditional_read_rule_still_gets_the_shape()
    {
        var post = NewName("post");
        await StoreAsync(TypeOf(post));
        // "Own entries": the owner condition the ownership tests use for the same rule.
        var own = new Dictionary<string, object>
        {
            ["$createdBy"] = new Dictionary<string, object> { ["_eq"] = "$CURRENT_USER" },
        };
        var (_, token) = await UserWithAsync(Read(post, own));

        var res = await GetAsync($"{ListRoute}/{post}", token);

        (await BodyAsync(res)).GetProperty("name").GetString().Should().Be(post);
    }

    [Fact]
    public async Task The_list_names_only_readable_types()
    {
        var post = NewName("post");
        var page = NewName("page");
        await StoreAsync(TypeOf(post), TypeOf(page));
        var (_, token) = await UserWithAsync(Read(post));

        var names = NamesIn(await BodyAsync(await GetAsync(ListRoute, token)));

        names.Should().Contain(post);
        names.Should().NotContain(page);
    }

    [Fact]
    public async Task A_content_read_key_needs_no_admin()
    {
        var post = NewName("post");
        await StoreAsync(TypeOf(post));
        var (userId, _) = await UserWithAsync(Read(post));
        var key = await KeyForAsync(userId, "content:read");

        var list = await GetAsync(ListRoute, key);
        var one = await GetAsync($"{ListRoute}/{post}", key);

        NamesIn(await BodyAsync(list)).Should().Contain(post);
        (await BodyAsync(one)).GetProperty("name").GetString().Should().Be(post);
    }

    [Fact]
    public async Task Types_named_erase_and_share_links_are_ordinary_reads()
    {
        await StoreAsync(TypeOf("erase"), TypeOf("share-links"));
        var (userId, _) = await UserWithAsync(Read("erase"), Read("share-links"));
        var key = await KeyForAsync(userId, "content:read");

        (await GetAsync($"{ListRoute}/erase", key)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await GetAsync($"{ListRoute}/share-links", key)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_lifecycle_type_lists_its_transitions()
    {
        var invoice = NewName("invoice");
        var type = TypeOf(invoice);
        type.Lifecycle = new LifecycleDefinition
        {
            States = ["Draft", "Submitted", "Approved"],
            InitialState = "Draft",
            Transitions =
            [
                new StateTransition { Name = "Submit", From = "Draft", To = "Submitted" },
                new StateTransition { Name = "Approve", From = "Submitted", To = "Approved", RequiredFields = ["Title"] },
            ],
        };
        await StoreAsync(type);
        var (_, token) = await UserWithAsync(Read(invoice));

        var body = await BodyAsync(await GetAsync($"{ListRoute}/{invoice}", token));

        var lifecycle = body.GetProperty("lifecycle");
        lifecycle.GetProperty("initialState").GetString().Should().Be("Draft");
        lifecycle.GetProperty("states").EnumerateArray().Select(s => s.GetString()).Should().Equal("Draft", "Submitted", "Approved");
        var transitions = lifecycle.GetProperty("transitions").EnumerateArray().ToList();
        transitions.Select(t => t.GetProperty("name").GetString()).Should().Equal("Submit", "Approve");
        transitions[1].GetProperty("from").GetString().Should().Be("Submitted");
        transitions[1].GetProperty("to").GetString().Should().Be("Approved");
    }

    [Fact]
    public async Task A_blocks_field_points_at_the_block_schema()
    {
        var page = NewName("page");
        await StoreAsync(TypeOf(page, new FieldDefinition { Name = "Layout", DisplayName = "Layout", Type = "blocks" }));
        var (_, token) = await UserWithAsync(Read(page));

        var body = await BodyAsync(await GetAsync($"{ListRoute}/{page}", token));

        var fields = body.GetProperty("fields").EnumerateArray().ToList();
        fields.Single(f => f.GetProperty("name").GetString() == "Layout")
            .GetProperty("blockSchema").GetString().Should().Be("/api/blocks/schema");
        fields.Single(f => f.GetProperty("name").GetString() == "Title")
            .GetProperty("blockSchema").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task An_account_shared_type_is_found()
    {
        var room = NewName("room");
        // The default tenant belongs to the default account, so a type that account owns is shared to it.
        await StoreAsync(new AccountContentType
        {
            Id = Guid.NewGuid(),
            AccountId = TenantAccount.DefaultId,
            Name = room,
            Definition = TypeOf(room),
        });
        var (_, token) = await UserWithAsync(Read(room));

        var one = await GetAsync($"{ListRoute}/{room}", token);
        var list = await GetAsync(ListRoute, token);

        (await BodyAsync(one)).GetProperty("name").GetString().Should().Be(room);
        NamesIn(await BodyAsync(list)).Should().Contain(room);
    }

    [Fact]
    public async Task The_account_type_wins_over_a_tenant_type_of_the_same_name()
    {
        var room = NewName("room");
        var own = TypeOf(room);
        own.DisplayName = "Tenant display";
        var shared = TypeOf(room);
        shared.DisplayName = "Account display";
        await StoreAsync(own, new AccountContentType
        {
            Id = Guid.NewGuid(),
            AccountId = TenantAccount.DefaultId,
            Name = room,
            Definition = shared,
        });
        var (_, token) = await UserWithAsync(Read(room));

        var one = await BodyAsync(await GetAsync($"{ListRoute}/{room}", token));
        var list = await BodyAsync(await GetAsync(ListRoute, token));

        one.GetProperty("displayName").GetString().Should().Be("Account display");
        list.GetProperty("items").EnumerateArray()
            .Single(i => i.GetProperty("name").GetString() == room)
            .GetProperty("displayName").GetString().Should().Be("Account display");
    }

    [Fact]
    public async Task A_field_hidden_from_the_callers_roles_is_left_out_of_the_shape()
    {
        var post = NewName("post");
        var secretRole = new Role { Id = Guid.NewGuid(), Name = $"Finance_{Guid.NewGuid():n}", Permissions = [Read(post)] };
        var insider = new User
        {
            Id = Guid.NewGuid(),
            Username = $"in_{Guid.NewGuid():n}"[..14],
            Email = $"in_{Guid.NewGuid():n}@example.com",
            RoleIds = [secretRole.Id],
        };
        await StoreAsync(
            TypeOf(post, new FieldDefinition
            {
                Name = "Salary",
                DisplayName = "Salary",
                Type = "decimal",
                // A role list only means something on a restricted level; on Public the service
                // answers before it reads the list, and the API clears it.
                Sensitivity = SensitivityLevel.Sensitive,
                VisibleToRoles = [secretRole.Id.ToString()],
            }),
            secretRole,
            insider);
        var (_, outsider) = await UserWithAsync(Read(post));
        var insiderToken = _fixture.CreateToken(roles: [secretRole.Name], userId: insider.Id.ToString());

        static List<string?> FieldNames(JsonElement body) =>
            body.GetProperty("fields").EnumerateArray().Select(f => f.GetProperty("name").GetString()).ToList();

        FieldNames(await BodyAsync(await GetAsync($"{ListRoute}/{post}", outsider))).Should().NotContain("Salary");
        FieldNames(await BodyAsync(await GetAsync($"{ListRoute}/{post}", insiderToken))).Should().Contain("Salary");
    }
}
