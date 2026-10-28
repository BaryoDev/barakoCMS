using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// The places an owner-hidden field could still be found: a search, the text a search runs over, a
/// lookup, a block binding. A response that omits the field is not enough, since which rows match
/// gives the value away.
/// </summary>
[Collection("Sequential")]
public sealed class OwnerHiddenSurfaceTests
{
    private readonly IntegrationTestFixture _factory;

    public OwnerHiddenSurfaceTests(IntegrationTestFixture factory) => _factory = factory;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static List<FieldDefinition> MemberFields() =>
    [
        new FieldDefinition { Name = "Name", DisplayName = "Name", Type = "string" },
        new FieldDefinition { Name = "Slug", DisplayName = "Slug", Type = "slug" },
        new FieldDefinition { Name = "Phone", DisplayName = "Phone", Type = "string", ShownWhenListedIn = "ShownFields" },
        new FieldDefinition { Name = "ShownFields", DisplayName = "Shown fields", Type = "array" },
    ];

    private sealed record Seed(string Type, Guid Id, string Name, string Phone, string Slug);

    /// <summary>A member type with a Public Phone the owner has not listed, and one Published entry.</summary>
    private async Task<Seed> SeedAsync(bool deliverable = false)
    {
        var tag = Guid.NewGuid().ToString("N")[..10];
        var type = $"oh{tag}";
        var (name, phone, slug) = ($"zephyrname{tag}", $"zephyrphone{tag}", $"s-{tag}");
        var id = Guid.NewGuid();

        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Store(new ContentTypeDefinition
        {
            Id = Guid.NewGuid(), Name = type, DisplayName = type, IsPubliclyDeliverable = deliverable, Fields = MemberFields(),
        });
        session.Store(new Content
        {
            Id = id,
            ContentType = type,
            Status = ContentStatus.Published,
            Sensitivity = SensitivityLevel.Public,
            Data = new Dictionary<string, object>
            {
                ["Name"] = name, ["Slug"] = slug, ["Phone"] = phone, ["ShownFields"] = new List<object>(),
            },
            // Indexed the way the write paths index it: without the owner-hidden Phone.
            SearchText = $"{name} {slug}",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await session.SaveChangesAsync(Ct);
        return new Seed(type, id, name, phone, slug);
    }

    /// <summary>A stored user who may read the type, holding the capability given or none.</summary>
    private async Task<HttpClient> ReaderAsync(string type, string? capability = null)
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var role = new Role
        {
            Id = Guid.NewGuid(),
            Name = $"ohrole_{Guid.NewGuid():N}",
            SystemCapabilities = capability is null ? new List<string>() : new List<string> { capability },
            Permissions = [new ContentTypePermission { ContentTypeSlug = type, Read = new PermissionRule { Enabled = true } }],
        };
        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = $"oh_{Guid.NewGuid():N}",
            Email = $"{Guid.NewGuid():N}@example.com",
            RoleIds = [role.Id],
        };
        session.Store(role);
        session.Store(user);
        await session.SaveChangesAsync(Ct);

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", _factory.CreateToken(roles: [role.Name], userId: user.Id.ToString()));
        return client;
    }

    private static async Task<string> GetAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(url, Ct);
        var body = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        return body;
    }

    [Fact]
    public async Task A_search_across_types_does_not_find_an_entry_by_its_owner_hidden_value()
    {
        var seed = await SeedAsync();
        var plain = await ReaderAsync(seed.Type);

        (await GetAsync(plain, $"/api/contents?search={seed.Name}")).Should().Contain(seed.Id.ToString(),
            "the control: a value the caller is shown still finds the entry across types");

        var hidden = JsonDocument.Parse(await GetAsync(plain, $"/api/contents?search={seed.Phone}")).RootElement;
        hidden.GetProperty("items").GetArrayLength().Should().Be(0,
            "which entries match would otherwise tell the caller what the hidden phone holds");
        hidden.GetProperty("totalItems").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task A_search_across_types_finds_it_for_a_caller_with_view_sensitive()
    {
        var seed = await SeedAsync();
        var officer = await ReaderAsync(seed.Type, SystemCapabilities.ViewSensitive);

        (await GetAsync(officer, $"/api/contents?search={seed.Phone}")).Should().Contain(seed.Id.ToString());
    }

    [Fact]
    public async Task A_search_in_one_type_leaves_the_owner_hidden_field_out()
    {
        var seed = await SeedAsync();
        var plain = await ReaderAsync(seed.Type);
        var officer = await ReaderAsync(seed.Type, SystemCapabilities.ViewSensitive);

        (await GetAsync(plain, $"/api/contents?contentType={seed.Type}&search={seed.Name}")).Should().Contain(seed.Id.ToString(),
            "the control: the other fields of the type are still searched");
        (await GetAsync(plain, $"/api/contents?contentType={seed.Type}&search={seed.Phone}")).Should().NotContain(seed.Id.ToString());
        (await GetAsync(officer, $"/api/contents?contentType={seed.Type}&search={seed.Phone}")).Should().Contain(seed.Id.ToString());
    }

    private async Task<HttpClient> AdminAsync()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", await _factory.StoredUserTokenAsync("Admin"));
        return client;
    }

    [Fact]
    public async Task A_sensitivity_change_on_another_field_does_not_put_the_hidden_value_into_search()
    {
        var tag = Guid.NewGuid().ToString("N")[..8];
        var (type, secret, phone) = ($"ohs{tag}", $"zephyrsecret{tag}", $"zephyrphone{tag}");
        var id = Guid.NewGuid();

        using (var scope = _factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            var fields = MemberFields();
            fields.Add(new FieldDefinition { Name = "Secret", DisplayName = "Secret", Type = "string" });
            session.Store(new ContentTypeDefinition
            {
                Id = Guid.NewGuid(), Name = type, DisplayName = type, IsPubliclyDeliverable = true, Fields = fields,
            });
            var data = new Dictionary<string, object>
            {
                ["Name"] = "Bea", ["Slug"] = $"s-{tag}", ["Secret"] = secret, ["Phone"] = phone, ["ShownFields"] = new List<object>(),
            };
            var indexed = $"Bea s-{tag} {secret}";
            session.Events.StartStream<Content>(
                id, new barakoCMS.Events.ContentCreated(id, type, data, ContentStatus.Published, Guid.NewGuid(), indexed, SensitivityLevel.Public));
            session.Store(new Content
            {
                Id = id, ContentType = type, Status = ContentStatus.Published, Sensitivity = SensitivityLevel.Public,
                Data = data, SearchText = indexed, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            });
            await session.SaveChangesAsync(Ct);
        }

        var anon = _factory.CreateClient();
        async Task<int> CountAsync(string term) => JsonDocument
            .Parse(await anon.GetStringAsync($"/api/public/{type}/search?q={term}", Ct))
            .RootElement.GetProperty("count").GetInt32();

        (await CountAsync(secret)).Should().Be(1, "the control: a Public field is searchable to begin with");
        (await CountAsync(phone)).Should().Be(0);

        var res = await (await AdminAsync()).PutAsJsonAsync(
            $"/api/content-types/{type}/fields/Name/sensitivity", new { sensitivity = "Sensitive" }, Ct);
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync(Ct));

        (await CountAsync(secret)).Should().Be(1, "the control: the rebuild ran and kept the Public field");
        (await CountAsync(phone)).Should().Be(0, "the rebuild leaves the owner-hidden field out like every other index writer");
    }

    [Fact]
    public async Task A_lookup_does_not_return_an_owner_hidden_field()
    {
        var tag = Guid.NewGuid().ToString("N")[..10];
        var type = $"ohl{tag}";
        var id = Guid.NewGuid();

        using (var scope = _factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            var fields = MemberFields();
            fields.Add(new FieldDefinition { Name = "Reference", DisplayName = "Reference", Type = "string" });
            fields.Add(new FieldDefinition { Name = "LastName", DisplayName = "Last name", Type = "string", Sensitivity = SensitivityLevel.Sensitive });
            session.Store(new ContentTypeDefinition
            {
                Id = Guid.NewGuid(), Name = type, DisplayName = type, Fields = fields,
                Lookup = new LookupDefinition { MatchField = "Reference", VerifyFields = ["LastName"], ReturnFields = ["Name", "Phone"] },
            });
            session.Store(new Content
            {
                Id = id, ContentType = type, Status = ContentStatus.Draft,
                Data = new Dictionary<string, object>
                {
                    ["Reference"] = $"REF-{tag}", ["LastName"] = "Cruz", ["Name"] = "Bea", ["Phone"] = "09170001111",
                    ["ShownFields"] = new List<object>(),
                },
            });
            await session.SaveChangesAsync(Ct);
        }

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestRemoteIpFilter.Header, "198.51.100.77");
        var response = await client.PostAsJsonAsync($"/api/public/{type}/lookup", new { reference = $"REF-{tag}", verify = "Cruz" }, Ct);
        var body = await response.Content.ReadAsStringAsync(Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        body.Should().Contain("Bea", "the control: a Public return field is answered");
        body.Should().NotContain("09170001111");
    }

    [Fact]
    public async Task A_definition_change_that_hides_a_field_rebuilds_the_stored_search_text()
    {
        var tag = Guid.NewGuid().ToString("N")[..10];
        var type = $"ohr{tag}";
        var id = Guid.NewGuid();
        var (phone, slug) = ($"zephyrphone{tag}", $"s-{tag}");

        using (var scope = _factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            var fields = MemberFields();
            fields.Single(f => f.Name == "Phone").ShownWhenListedIn = null;
            session.Store(new ContentTypeDefinition
            {
                Id = Guid.NewGuid(), Name = type, DisplayName = type, IsPubliclyDeliverable = true, Fields = fields,
            });
            var data = new Dictionary<string, object>
            {
                ["Name"] = "Bea", ["Slug"] = slug, ["Phone"] = phone, ["ShownFields"] = new List<object>(),
            };
            var indexed = $"Bea {slug} {phone}";
            session.Events.StartStream<Content>(
                id, new barakoCMS.Events.ContentCreated(id, type, data, ContentStatus.Published, Guid.NewGuid(), indexed, SensitivityLevel.Public));
            session.Store(new Content
            {
                Id = id, ContentType = type, Status = ContentStatus.Published, Sensitivity = SensitivityLevel.Public,
                Data = data, SearchText = indexed, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            });
            await session.SaveChangesAsync(Ct);
        }

        var anon = _factory.CreateClient();
        async Task<int> CountAsync(string term) => JsonDocument
            .Parse(await anon.GetStringAsync($"/api/public/{type}/search?q={term}", Ct))
            .RootElement.GetProperty("count").GetInt32();

        (await CountAsync(phone)).Should().Be(1, "the control: the value was indexed before the owner could hide it");

        using (var scope = _factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            var definition = await session.Query<ContentTypeDefinition>().FirstAsync(d => d.Name == type, Ct);
            var before = barakoCMS.Infrastructure.Services.OwnerHiddenReindex.HiddenNames(definition);
            definition.Fields.Single(f => f.Name == "Phone").ShownWhenListedIn = "ShownFields";
            barakoCMS.Infrastructure.Services.OwnerHiddenReindex.Gained(before, definition).Should().BeTrue();
            session.Store(definition);
            await barakoCMS.Infrastructure.Services.OwnerHiddenReindex.RebuildAsync(session, definition, _factory.Services.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>(), Ct);
            await session.SaveChangesAsync(Ct);
        }

        (await CountAsync(phone)).Should().Be(0, "the stored text is rebuilt without the field");
        (await CountAsync("Bea")).Should().Be(1, "the control: the rest of the text stays");
    }

    [Fact]
    public void The_shared_predicate_keeps_owner_hidden_fields_out_of_public_selections()
    {
        var hidden = new FieldDefinition { Name = "Phone", Type = "string", ShownWhenListedIn = "ShownFields" };
        var plain = new FieldDefinition { Name = "Name", Type = "string" };

        barakoCMS.Core.Validation.PublicFields.IsOwnerHidden(hidden).Should().BeTrue();
        barakoCMS.Core.Validation.PublicFields.IsOwnerHidden(plain).Should().BeFalse();
        barakoCMS.Core.Validation.PublicFields.InSearchText(hidden).Should().BeFalse();
        barakoCMS.Core.Validation.PublicFields.InSearchText(plain).Should().BeTrue();
        barakoCMS.Core.Validation.PublicFields.SearchTextNames([hidden, plain]).Should().BeEquivalentTo("Name");
        barakoCMS.Core.Validation.LookupRules.IsReturnable(hidden).Should().BeFalse();
    }

    [Fact]
    public void A_bound_block_cannot_filter_or_sort_on_an_owner_hidden_field()
    {
        var definition = new ContentTypeDefinition { Name = "Member", Fields = MemberFields() };

        BarakoCMS.Pages.Features.Resolve.BoundBlocks.PublicField(definition, "Phone").Should().BeNull();
        BarakoCMS.Pages.Features.Resolve.BoundBlocks.PublicField(definition, "Name").Should().Be("Name");
    }

    [Fact]
    public async Task An_anonymous_slug_lookup_cannot_find_an_entry_by_an_owner_hidden_slug()
    {
        var tag = Guid.NewGuid().ToString("N")[..10];
        var (hiddenType, plainType) = ($"ohs{tag}", $"ohp{tag}");
        var slug = $"s-{tag}";

        using (var scope = _factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            foreach (var (type, hide) in new[] { (hiddenType, true), (plainType, false) })
            {
                var fields = MemberFields();
                fields.Single(f => f.Name == "Slug").ShownWhenListedIn = hide ? "ShownFields" : null;
                session.Store(new ContentTypeDefinition
                {
                    Id = Guid.NewGuid(), Name = type, DisplayName = type, IsPubliclyDeliverable = true, Fields = fields,
                });
                var id = Guid.NewGuid();
                var data = new Dictionary<string, object>
                {
                    ["Name"] = "Bea", ["Slug"] = slug, ["Phone"] = "1", ["ShownFields"] = new List<object>(),
                };
                session.Events.StartStream<Content>(
                    id, new barakoCMS.Events.ContentCreated(id, type, data, ContentStatus.Published, Guid.NewGuid(), "Bea", SensitivityLevel.Public));
                session.Store(new Content
                {
                    Id = id, ContentType = type, Status = ContentStatus.Published, Sensitivity = SensitivityLevel.Public,
                    Data = data, SearchText = "Bea", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
                });
            }

            await session.SaveChangesAsync(Ct);
        }

        var anon = _factory.CreateClient();
        (await anon.GetAsync($"/api/public/{plainType}/{slug}", Ct)).StatusCode.Should().Be(HttpStatusCode.OK, "the control: a plain slug finds the entry");
        (await anon.GetAsync($"/api/public/{hiddenType}/{slug}", Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public void A_permission_condition_cannot_compare_an_owner_hidden_field()
    {
        var definition = new ContentTypeDefinition { Name = "Member", Fields = MemberFields() };

        barakoCMS.Infrastructure.Services.ReferenceConditions.IsComparable(definition, "Name").Should().BeTrue();
        barakoCMS.Infrastructure.Services.ReferenceConditions.IsComparable(definition, "Phone").Should().BeFalse();
    }

    [Fact]
    public void The_public_openapi_document_does_not_name_an_owner_hidden_field()
    {
        var definition = new ContentTypeDefinition { Name = "Member", IsPubliclyDeliverable = true, Fields = MemberFields() };

        var json = barakoCMS.Infrastructure.OpenApi.DeliveryDocument.Build([definition]).ToJsonString();

        json.Should().Contain("\"Name\"", "the control: a Public field is documented");
        json.Should().NotContain("\"Phone\"");
    }

    [Fact]
    public async Task A_config_apply_that_hides_a_field_removes_it_from_anonymous_search()
    {
        var tag = Guid.NewGuid().ToString("N")[..10];
        var type = $"ohc{tag}";
        var id = Guid.NewGuid();
        var phone = $"zephyrphone{tag}";

        using (var scope = _factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            var fields = MemberFields();
            fields.Single(f => f.Name == "Phone").ShownWhenListedIn = null;
            session.Store(new ContentTypeDefinition
            {
                Id = Guid.NewGuid(), Name = type, DisplayName = type, IsPubliclyDeliverable = true, Fields = fields,
            });
            var data = new Dictionary<string, object>
            {
                ["Name"] = "Bea", ["Slug"] = $"s-{tag}", ["Phone"] = phone, ["ShownFields"] = new List<object>(),
            };
            var indexed = $"Bea s-{tag} {phone}";
            session.Events.StartStream<Content>(
                id, new barakoCMS.Events.ContentCreated(id, type, data, ContentStatus.Published, Guid.NewGuid(), indexed, SensitivityLevel.Public));
            session.Store(new Content
            {
                Id = id, ContentType = type, Status = ContentStatus.Published, Sensitivity = SensitivityLevel.Public,
                Data = data, SearchText = indexed, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            });
            await session.SaveChangesAsync(Ct);
        }

        var anon = _factory.CreateClient();
        async Task<int> CountAsync(string term) => JsonDocument
            .Parse(await anon.GetStringAsync($"/api/public/{type}/search?q={term}", Ct))
            .RootElement.GetProperty("count").GetInt32();

        (await CountAsync(phone)).Should().Be(1, "the control: indexed before the owner could hide it");

        var hidden = BarakoCMS.Tests.Features.Config.ConfigCallers.Type(type);
        hidden.IsPubliclyDeliverable = true;
        hidden.Fields = MemberFields();
        var admin = await new BarakoCMS.Tests.Features.Config.ConfigCallers(_factory).SuperAdminAsync(null);
        var (applied, body) = await BarakoCMS.Tests.Features.Config.ConfigCallers.ApplyAsync(admin, new
        {
            version = 1,
            contentTypes = new Dictionary<string, object> { [type] = hidden },
        });
        applied.StatusCode.Should().Be(HttpStatusCode.OK, body.ToString());

        (await CountAsync(phone)).Should().Be(0, "the apply rebuilt the stored search text without the field");
        (await CountAsync("Bea")).Should().Be(1, "the control: the rest of the text stays");
    }

    [Fact]
    public async Task The_rebuild_stores_a_document_type_without_an_event_when_appending_is_off()
    {
        var tag = Guid.NewGuid().ToString("N")[..10];
        var type = $"ohd{tag}";
        var id = Guid.NewGuid();
        var configuration = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [barakoCMS.Infrastructure.Services.ContentWriter.DocumentTypesAppendKey] = "false" })
            .Build();

        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var definition = new ContentTypeDefinition { Id = Guid.NewGuid(), Name = type, DisplayName = type, Fields = MemberFields() };
        session.Store(definition);
        session.Store(new Content
        {
            Id = id, ContentType = type, Status = ContentStatus.Published, Sensitivity = SensitivityLevel.Public,
            Data = new Dictionary<string, object> { ["Name"] = "Bea", ["Phone"] = "0917", ["ShownFields"] = new List<object>() },
            SearchText = "Bea 0917", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        await session.SaveChangesAsync(Ct);

        await barakoCMS.Infrastructure.Services.OwnerHiddenReindex.RebuildAsync(session, definition, configuration, Ct);
        await session.SaveChangesAsync(Ct);

        await using var read = _factory.Services.GetRequiredService<IDocumentStore>().QuerySession();
        var stored = (await read.LoadAsync<Content>(id, Ct))!;
        stored.SearchText.Should().Contain("Bea");
        stored.SearchText.Should().NotContain("0917");
        (await read.Events.FetchStreamStateAsync(id, Ct)).Should().BeNull("a document type with appending off has no event for the rebuild");
    }
}
