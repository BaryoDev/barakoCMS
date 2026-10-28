using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using barakoCMS.Models;

namespace BarakoCMS.Tests.Features.ContentApi;

/// <summary>
/// <c>GET /api/contents/counts</c> counts what the caller may read, by status (#677).
/// </summary>
/// <remarks>
/// Every type here holds entries the owner-scoped reader may not read, in a status the reader's
/// own entries are not all in, so a count that ignored the read rules comes out visibly larger.
/// </remarks>
[Collection("Integration1")]
public class ContentStatusCountsTests
{
    private const string Route = "/api/contents/counts";

    private readonly IntegrationTestFixture _factory;

    public ContentStatusCountsTests(IntegrationTestFixture factory) => _factory = factory;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static PermissionRule Owned() => new()
    {
        Enabled = true,
        Conditions = new Dictionary<string, object>
        {
            ["OwnerId"] = new Dictionary<string, object> { ["_eq"] = "$CURRENT_USER" },
        },
    };

    private async Task<Guid> UserAsync(params ContentTypePermission[] permissions)
    {
        var role = new Role { Id = Guid.NewGuid(), Name = $"counts_{Guid.NewGuid():N}", Permissions = [.. permissions] };
        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = $"counts_{Guid.NewGuid():N}",
            Email = $"{Guid.NewGuid():N}@example.com",
            RoleIds = [role.Id],
        };
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Store(role);
        session.Store(user);
        await session.SaveChangesAsync(Ct);
        return user.Id;
    }

    /// <summary>
    /// For <paramref name="owner"/>: two drafts, one scheduled, two published, one archived.
    /// For somebody else: four published, one scheduled and one draft.
    /// </summary>
    private async Task<string> SeedTypeAsync(Guid owner)
    {
        var type = $"counted_{Guid.NewGuid():N}"[..20];
        var other = Guid.NewGuid();
        var rows = new (Guid Owner, ContentStatus Status)[]
        {
            (owner, ContentStatus.Draft),
            (owner, ContentStatus.Draft),
            (owner, ContentStatus.Scheduled),
            (owner, ContentStatus.Published),
            (owner, ContentStatus.Published),
            (owner, ContentStatus.Archived),
            (other, ContentStatus.Published),
            (other, ContentStatus.Published),
            (other, ContentStatus.Published),
            (other, ContentStatus.Published),
            (other, ContentStatus.Scheduled),
            (other, ContentStatus.Draft),
        };

        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Store(new ContentTypeDefinition
        {
            Id = Guid.NewGuid(),
            Name = type,
            DisplayName = type,
            Fields =
            [
                new() { Name = "Title", Type = "string", Sensitivity = SensitivityLevel.Public },
                new() { Name = "OwnerId", Type = "string", Sensitivity = SensitivityLevel.Public },
            ],
        });
        foreach (var (rowOwner, status) in rows)
        {
            session.Store(new barakoCMS.Models.Content
            {
                Id = Guid.NewGuid(),
                ContentType = type,
                Status = status,
                ScheduledPublishAt = status == ContentStatus.Scheduled ? DateTime.UtcNow.AddDays(3) : null,
                Data = new Dictionary<string, object> { ["Title"] = "x", ["OwnerId"] = rowOwner.ToString() },
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
        }

        await session.SaveChangesAsync(Ct);
        return type;
    }

    private HttpClient Client(Guid userId)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", _factory.CreateToken(["Viewer"], userId.ToString()));
        return client;
    }

    private static async Task<JsonElement> CountsAsync(HttpClient client, string? type = null)
    {
        var response = await client.GetAsync(type is null ? Route : $"{Route}?contentType={type}", Ct);
        var body = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        using var parsed = JsonDocument.Parse(body);
        return parsed.RootElement.Clone();
    }

    private static (int Total, int Draft, int Published, int Archived, int Scheduled) Numbers(JsonElement counts) => (
        counts.GetProperty("total").GetInt32(),
        counts.GetProperty("draft").GetInt32(),
        counts.GetProperty("published").GetInt32(),
        counts.GetProperty("archived").GetInt32(),
        counts.GetProperty("scheduled").GetInt32());

    [Fact]
    public async Task An_anonymous_caller_is_refused()
    {
        var response = await _factory.CreateClient().GetAsync(Route, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_caller_reading_every_entry_of_a_type_gets_every_entry_counted()
    {
        var owner = Guid.NewGuid();
        var type = await SeedTypeAsync(owner);
        var reader = await UserAsync(new ContentTypePermission { ContentTypeSlug = type, Read = new PermissionRule { Enabled = true } });

        var counts = await CountsAsync(Client(reader), type);

        Numbers(counts).Should().Be((12, 3, 6, 1, 2), "every status counted, Scheduled on its own and in the total");
        counts.GetProperty("uncountedTypes").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task An_owner_scoped_reader_gets_only_their_own_entries_counted()
    {
        var reader = await UserAsync();
        var type = await SeedTypeAsync(reader);
        await SetPermissionsAsync(reader, new ContentTypePermission { ContentTypeSlug = type, Read = Owned() });

        var counts = await CountsAsync(Client(reader), type);

        Numbers(counts).Should().Be((6, 2, 2, 1, 1), "somebody else's six entries are not readable");
    }

    [Fact]
    public async Task Across_types_only_the_types_the_caller_may_read_are_counted()
    {
        var reader = await UserAsync();
        var type = await SeedTypeAsync(reader);
        var unreadable = await SeedTypeAsync(reader);
        await SetPermissionsAsync(reader, new ContentTypePermission { ContentTypeSlug = type, Read = Owned() });

        var counts = await CountsAsync(Client(reader));

        Numbers(counts).Should().Be((6, 2, 2, 1, 1), $"{unreadable} has no read rule for this caller, and no other test's type does either");
    }

    [Fact]
    public async Task A_rule_that_does_not_compile_names_the_type_instead_of_counting_it()
    {
        var reader = await UserAsync();
        var type = await SeedTypeAsync(reader);
        await SetPermissionsAsync(reader, new ContentTypePermission
        {
            ContentTypeSlug = type,
            Read = new PermissionRule
            {
                Enabled = true,
                Conditions = new Dictionary<string, object>
                {
                    // $status compiles now, so an unknown $ field stands in for a rule that cannot.
                    ["$unknown"] = new Dictionary<string, object> { ["_eq"] = "Published" },
                },
            },
        });

        var counts = await CountsAsync(Client(reader), type);

        Numbers(counts).Should().Be((0, 0, 0, 0, 0));
        counts.GetProperty("uncountedTypes").EnumerateArray().Select(t => t.GetString()).Should().Equal(type);
    }

    [Fact]
    public async Task A_caller_with_no_roles_counts_nothing()
    {
        var reader = await UserAsync();
        var type = await SeedTypeAsync(reader);
        await SetPermissionsAsync(reader);

        var counts = await CountsAsync(Client(reader), type);

        Numbers(counts).Should().Be((0, 0, 0, 0, 0));
        counts.GetProperty("uncountedTypes").GetArrayLength().Should().Be(0);
    }

    private async Task SetPermissionsAsync(Guid userId, params ContentTypePermission[] permissions)
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var user = await session.LoadAsync<User>(userId, Ct);
        var role = await session.LoadAsync<Role>(user!.RoleIds.Single(), Ct);
        role!.Permissions = [.. permissions];
        session.Store(role);
        await session.SaveChangesAsync(Ct);
    }
}
