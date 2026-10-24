using System.Net.Http.Headers;
using System.Text.Json;
using FluentAssertions;
using Marten;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using barakoCMS.Models;

namespace BarakoCMS.Tests;

/// <summary>
/// The content read and list return the lifecycle state: the stored one, or the type's initial
/// state for an entry that has not moved, and nothing for a type without a lifecycle or a hidden row.
/// </summary>
[Collection("Integration2")]
public class ContentLifecycleStateResponseTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly IntegrationTestFixture _factory;
    private readonly CapturingMartenLogger _logger = new();

    public ContentLifecycleStateResponseTests(IntegrationTestFixture factory)
    {
        _factory = factory;
    }

    private async Task<HttpClient> ClientAsync(HttpClient client, string token)
    {
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        await Task.CompletedTask;
        return client;
    }

    private async Task<HttpClient> AdminAsync() =>
        await ClientAsync(_factory.CreateClient(), await _factory.StoredUserTokenAsync("Admin", "SuperAdmin"));

    private async Task<string> TypeAsync(bool lifecycle)
    {
        var name = "lcs" + Guid.NewGuid().ToString("n")[..10];
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Store(new ContentTypeDefinition
        {
            Id = Guid.NewGuid(),
            Name = name,
            DisplayName = name,
            Fields =
            [
                new FieldDefinition { Name = "Title", DisplayName = "Title", Type = "string", Sensitivity = SensitivityLevel.Public },
                new FieldDefinition { Name = "Slug", DisplayName = "Slug", Type = "slug", Sensitivity = SensitivityLevel.Public },
            ],
            Lifecycle = lifecycle
                ? new LifecycleDefinition
                {
                    States = ["Waiting", "Called"],
                    InitialState = "Waiting",
                    Transitions = [new StateTransition { Name = "Call", From = "Waiting", To = "Called" }],
                }
                : null,
        });
        await session.SaveChangesAsync(Ct);
        return name;
    }

    private async Task<(Guid Id, string Slug)> EntryAsync(
        string type, string? state, SensitivityLevel sensitivity = SensitivityLevel.Public)
    {
        var id = Guid.NewGuid();
        var slug = $"e-{id:n}";
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Store(new Content
        {
            Id = id,
            ContentType = type,
            LifecycleState = state,
            Sensitivity = sensitivity,
            Data = new Dictionary<string, object> { ["Title"] = "T", ["Slug"] = slug },
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await session.SaveChangesAsync(Ct);
        return (id, slug);
    }

    private static string? State(JsonElement root)
    {
        foreach (var name in new[] { "lifecycleState", "LifecycleState" })
        {
            if (root.TryGetProperty(name, out var v))
                return v.ValueKind == JsonValueKind.Null ? null : v.GetString();
        }

        throw new Xunit.Sdk.XunitException($"no lifecycleState in {root}");
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync(Ct);
        response.IsSuccessStatusCode.Should().BeTrue(body);
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private static async Task<List<JsonElement>> ListAsync(HttpClient client, string type)
    {
        var root = await ReadJsonAsync(await client.GetAsync($"/api/contents?contentType={type}&pageSize=50", Ct));
        var items = root.TryGetProperty("items", out var i) ? i : root.GetProperty("Items");
        return items.EnumerateArray().Select(e => e.Clone()).ToList();
    }

    [Fact]
    public async Task Get_returns_the_stored_state()
    {
        var type = await TypeAsync(lifecycle: true);
        var (id, _) = await EntryAsync(type, "Called");

        var root = await ReadJsonAsync(await (await AdminAsync()).GetAsync($"/api/contents/{id}", Ct));

        State(root).Should().Be("Called");
    }

    [Fact]
    public async Task Get_by_slug_returns_it_too()
    {
        var type = await TypeAsync(lifecycle: true);
        var (_, slug) = await EntryAsync(type, "Called");

        var root = await ReadJsonAsync(await (await AdminAsync()).GetAsync($"/api/contents/by-slug/{type}/{slug}", Ct));

        State(root).Should().Be("Called");
    }

    [Fact]
    public async Task A_new_entry_shows_the_initial_state()
    {
        var type = await TypeAsync(lifecycle: true);
        var (id, _) = await EntryAsync(type, null);
        var client = await AdminAsync();

        State(await ReadJsonAsync(await client.GetAsync($"/api/contents/{id}", Ct))).Should().Be("Waiting");
        (await ListAsync(client, type)).Select(State).Should().Equal("Waiting");
    }

    [Fact]
    public async Task The_list_returns_the_state_of_every_row()
    {
        var type = await TypeAsync(lifecycle: true);
        var moved = (await EntryAsync(type, "Called")).Id;
        var fresh = (await EntryAsync(type, null)).Id;

        var rows = await ListAsync(await AdminAsync(), type);

        var byId = rows.ToDictionary(r => (r.TryGetProperty("id", out var v) ? v : r.GetProperty("Id")).GetGuid(), State);
        byId[moved].Should().Be("Called");
        byId[fresh].Should().Be("Waiting");
    }

    [Fact]
    public async Task A_type_without_a_lifecycle_has_a_null_state()
    {
        var type = await TypeAsync(lifecycle: false);
        var (id, _) = await EntryAsync(type, null);
        var client = await AdminAsync();

        State(await ReadJsonAsync(await client.GetAsync($"/api/contents/{id}", Ct))).Should().BeNull();
        (await ListAsync(client, type)).Select(State).Should().Equal((string?)null);
    }

    [Fact]
    public async Task A_hidden_entry_shows_no_state()
    {
        var type = await TypeAsync(lifecycle: true);
        var (id, _) = await EntryAsync(type, "Called", SensitivityLevel.Hidden);
        var client = await ReaderAsync(type);

        var root = await ReadJsonAsync(await client.GetAsync($"/api/contents/{id}", Ct));
        (root.TryGetProperty("contentType", out var t) ? t : root.GetProperty("ContentType")).GetString().Should().Be("HIDDEN");
        State(root).Should().BeNull();

        var rows = await ListAsync(client, type);
        rows.Should().ContainSingle();
        State(rows[0]).Should().BeNull();
    }

    /// <summary>A stored user who reads the type through a role with no capability, as in SensitivityIntegrationTests.</summary>
    private async Task<HttpClient> ReaderAsync(string type)
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var role = new Role
        {
            Id = Guid.NewGuid(),
            Name = $"dbrole_{Guid.NewGuid():N}",
            Permissions =
            [
                new ContentTypePermission { ContentTypeSlug = type, Read = new PermissionRule { Enabled = true } },
            ],
        };
        session.Store(role);
        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = $"user_{Guid.NewGuid()}",
            Email = $"{Guid.NewGuid()}@example.com",
            RoleIds = [role.Id],
        };
        session.Store(user);
        await session.SaveChangesAsync(Ct);

        return await ClientAsync(_factory.CreateClient(), _factory.CreateToken([role.Name], user.Id.ToString()));
    }

    [Fact]
    public async Task The_list_does_one_extra_query_not_one_per_row()
    {
        // The logger sees every session of the derived host, not only the list request's. Two startup
        // notices read the content type definitions once per tenant partition, and on a loaded machine
        // they were still running while the lists were counted, so the first count was 85 and the
        // second 39. They are left out of this host, so what is counted is the request's own reads.
        var derived = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                var notices = services.Where(d =>
                    d.ImplementationType == typeof(barakoCMS.Infrastructure.Services.StoredValidationRulesNotice)
                    || d.ImplementationType == typeof(barakoCMS.Infrastructure.Services.StoredReferenceConditionsNotice)).ToList();
                if (notices.Count != 2)
                    throw new InvalidOperationException("The startup notices are no longer registered the way this test expects.");
                foreach (var notice in notices) services.Remove(notice);
            });
            builder.ConfigureServices(services => services.ConfigureMarten(options => options.Logger(_logger)));
        });
        var client = await ClientAsync(derived.CreateClient(), await _factory.StoredUserTokenAsync("Admin", "SuperAdmin"));

        var few = await TypeAsync(lifecycle: true);
        for (var i = 0; i < 2; i++) await EntryAsync(few, null);
        var many = await TypeAsync(lifecycle: true);
        for (var i = 0; i < 20; i++) await EntryAsync(many, null);

        async Task<int> DefinitionReadsAsync(string type)
        {
            _logger.Commands.Clear();
            await ListAsync(client, type);
            return _logger.Commands.Count(c => c.Sql.Contains("mt_doc_contenttypedefinition", StringComparison.OrdinalIgnoreCase));
        }

        var small = await DefinitionReadsAsync(few);
        var large = await DefinitionReadsAsync(many);

        large.Should().Be(small, "the definitions are read once for the page, whatever its length");
    }
}
