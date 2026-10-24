using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using barakoCMS.Core.Interfaces;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BarakoCMS.Tests.Features.ContentTypes;

/// <summary>
/// A conditional create rule is asked about the header and its lines as they will be stored, after
/// the lifecycle hooks, the same as POST /api/contents without lines (#578).
/// </summary>
[Collection("Sequential")]
public class ContentLinesCreateRuleTests
{
    private const string Header = "linesrulehdr";
    private const string Line = "linesruleline";

    private readonly IntegrationTestFixture _fixture;

    public ContentLinesCreateRuleTests(IntegrationTestFixture fixture) => _fixture = fixture;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Moves the header to the gold tier when the entry asks for it with a Flip field.</summary>
    private sealed class TierHook : IContentLifecycleHook
    {
        public string ContentType => Header;

        public Task<IReadOnlyList<string>> OnBeforeSaveAsync(ContentLifecycleContext context, CancellationToken ct)
        {
            if (context.Data.ContainsKey("Flip"))
            {
                context.Data["Tier"] = "gold";
            }

            return Task.FromResult<IReadOnlyList<string>>([]);
        }
    }

    private static readonly Lock HostGate = new();
    private static WebApplicationFactory<Program>? _host;

    private WebApplicationFactory<Program> HookHost()
    {
        lock (HostGate)
        {
            return _host ??= _fixture.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
                services.AddScoped<IContentLifecycleHook, TierHook>()));
        }
    }

    private static readonly SemaphoreSlim TypeGate = new(1, 1);
    private static bool _typesDeclared;

    private async Task DeclareTypesAsync()
    {
        await TypeGate.WaitAsync(Ct);
        try
        {
            if (_typesDeclared)
            {
                return;
            }

            var admin = HookHost().CreateClient();
            admin.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", await _fixture.StoredUserTokenAsync("Admin", "SuperAdmin"));

            (await admin.PostAsJsonAsync("/api/content-types", new
            {
                name = Header, displayName = Header,
                fields = new object[]
                {
                    new { name = "Tier", type = "string" },
                    new { name = "Flip", type = "string" },
                },
            }, Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
            (await admin.PostAsJsonAsync("/api/content-types", new
            {
                name = Line, displayName = Line,
                fields = new object[]
                {
                    new { name = "Sku", type = "string", isRequired = true },
                    new { name = "Order", type = "reference", referenceType = Header },
                },
            }, Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
            (await admin.PutAsJsonAsync($"/api/content-types/{Header}/lines",
                new { lines = new { contentType = Line, parentField = "order" } }, Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
            _typesDeclared = true;
        }
        finally
        {
            TypeGate.Release();
        }
    }

    /// <summary>A caller who may create headers only while their tier is silver, and lines freely.</summary>
    private async Task<HttpClient> SilverOnlyCallerAsync()
    {
        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();

        var role = new Role
        {
            Id = Guid.NewGuid(),
            Name = $"Silver_{Guid.NewGuid():n}",
            Permissions =
            [
                new ContentTypePermission
                {
                    ContentTypeSlug = Header,
                    Create = new PermissionRule
                    {
                        Enabled = true,
                        Conditions = new Dictionary<string, object>
                        {
                            ["Tier"] = new Dictionary<string, object> { ["_eq"] = "silver" },
                        },
                    },
                },
                new ContentTypePermission { ContentTypeSlug = Line, Create = new PermissionRule { Enabled = true } },
            ],
        };
        session.Store(role);
        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = $"silver_{Guid.NewGuid():n}",
            Email = $"silver_{Guid.NewGuid():n}@example.com",
            RoleIds = [role.Id],
        };
        session.Store(user);
        await session.SaveChangesAsync(Ct);

        var client = HookHost().CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", _fixture.CreateToken(roles: [role.Name], userId: user.Id.ToString()));
        return client;
    }

    private async Task<int> StoredCountAsync(string type)
    {
        using var scope = _fixture.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IDocumentStore>();
        await using var session = store.QuerySession("*DEFAULT*");
        return (await session.Query<Content>().Where(c => c.ContentType == type).ToListAsync(Ct)).Count;
    }

    [Fact]
    public async Task A_hook_that_moves_the_header_out_of_the_create_rule_makes_the_write_a_403()
    {
        await DeclareTypesAsync();
        var client = await SilverOnlyCallerAsync();
        var headersBefore = await StoredCountAsync(Header);
        var linesBefore = await StoredCountAsync(Line);

        var res = await client.PostAsJsonAsync("/api/contents", new
        {
            contentType = Header,
            data = new { Tier = "silver", Flip = "yes" },
            lines = new object[] { new { Sku = "A-1" } },
        }, Ct);

        res.StatusCode.Should().Be(HttpStatusCode.Forbidden, await res.Content.ReadAsStringAsync(Ct));
        (await StoredCountAsync(Header)).Should().Be(headersBefore);
        (await StoredCountAsync(Line)).Should().Be(linesBefore);
    }

    [Fact]
    public async Task A_header_outside_the_create_rule_on_the_data_sent_is_a_403_not_a_400()
    {
        await DeclareTypesAsync();
        var client = await SilverOnlyCallerAsync();
        var headersBefore = await StoredCountAsync(Header);

        var res = await client.PostAsJsonAsync("/api/contents", new
        {
            contentType = Header,
            data = new { Tier = "gold" },
            lines = new object[] { new { Sku = "A-1" } },
        }, Ct);

        res.StatusCode.Should().Be(HttpStatusCode.Forbidden, await res.Content.ReadAsStringAsync(Ct));
        (await StoredCountAsync(Header)).Should().Be(headersBefore);
    }

    [Fact]
    public async Task A_header_the_hook_leaves_inside_the_create_rule_is_written_with_its_lines()
    {
        await DeclareTypesAsync();
        var client = await SilverOnlyCallerAsync();
        var headersBefore = await StoredCountAsync(Header);
        var linesBefore = await StoredCountAsync(Line);

        var res = await client.PostAsJsonAsync("/api/contents", new
        {
            contentType = Header,
            data = new { Tier = "silver" },
            lines = new object[] { new { Sku = "A-1" } },
        }, Ct);

        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync(Ct));
        (await StoredCountAsync(Header)).Should().Be(headersBefore + 1);
        (await StoredCountAsync(Line)).Should().Be(linesBefore + 1);
    }
}
