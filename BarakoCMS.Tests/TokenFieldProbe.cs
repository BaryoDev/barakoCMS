using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using barakoCMS.Models;

namespace BarakoCMS.Tests;

/// <summary>
/// What the token field test classes share: a stored type with a token field, callers with and
/// without the capability that reads it, and the entry as the database holds it.
/// </summary>
/// <remarks>
/// A token is read from the stored document and not from a response, so a test about who is
/// shown the value cannot pass because nobody was shown it.
/// </remarks>
internal static class TokenFieldProbe
{
    public const string Field = "ClaimToken";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static FieldDefinition Token(
        string name = Field, SensitivityLevel sensitivity = SensitivityLevel.Hidden, int? length = null) => new()
    {
        Name = name, DisplayName = name, Type = "token", Sensitivity = sensitivity, TokenLength = length,
    };

    public static FieldDefinition Text(string name) => new() { Name = name, DisplayName = name, Type = "string" };

    /// <summary>Stores a type straight into the default tenant, under a name no other test uses.</summary>
    public static async Task<string> StoreTypeAsync(
        IntegrationTestFixture fixture, string prefix, bool deliverable, params FieldDefinition[] fields)
    {
        var name = prefix + Guid.NewGuid().ToString("n")[..10];
        using var scope = fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Store(new ContentTypeDefinition
        {
            Id = Guid.NewGuid(),
            Name = name,
            DisplayName = name,
            IsPubliclyDeliverable = deliverable,
            Fields = fields.ToList(),
        });
        await session.SaveChangesAsync(Ct);
        return name;
    }

    public static async Task<ContentTypeDefinition> StoredTypeAsync(IntegrationTestFixture fixture, string type)
    {
        using var scope = fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        var definition = await session.Query<ContentTypeDefinition>().FirstOrDefaultAsync(d => d.Name == type, Ct);
        definition.Should().NotBeNull();
        return definition!;
    }

    public static async Task<Content> StoredAsync(IntegrationTestFixture fixture, Guid id)
    {
        using var scope = fixture.Services.CreateScope();
        var content = await scope.ServiceProvider.GetRequiredService<IQuerySession>().LoadAsync<Content>(id, Ct);
        content.Should().NotBeNull();
        return content!;
    }

    /// <summary>The stored token, which has to be there and hold something.</summary>
    public static string TokenOf(Content entry, string field = Field)
    {
        entry.Data.Should().ContainKey(field);
        var token = entry.Data[field]?.ToString();
        token.Should().NotBeNullOrWhiteSpace();
        return token!;
    }

    public static void ShouldBeAToken(string token, int length = 32)
    {
        token.Should().HaveLength(length);
        token.Should().MatchRegex("^[0-9abcdefghjkmnpqrstvwxyz]+$");
    }

    /// <summary>The seeded SuperAdmin, who reads every field and may write every type.</summary>
    public static Task<string> AdminAsync(IntegrationTestFixture fixture) =>
        fixture.StoredUserTokenAsync("SuperAdmin", "Admin");

    /// <summary>
    /// A stored user who may read, create and update the type and nothing else. A capability name
    /// puts that capability on their role, and anything else leaves the role with none.
    /// </summary>
    public static async Task<string> UserAsync(IntegrationTestFixture fixture, string access, string type)
    {
        using var scope = fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();

        var role = new Role
        {
            Id = Guid.NewGuid(),
            Name = $"dbrole_{Guid.NewGuid():N}",
            SystemCapabilities = SystemCapabilities.IsKnown(access) ? new List<string> { access } : new List<string>(),
            Permissions =
            [
                new ContentTypePermission
                {
                    ContentTypeSlug = type,
                    Read = new PermissionRule { Enabled = true },
                    Create = new PermissionRule { Enabled = true },
                    Update = new PermissionRule { Enabled = true },
                    Delete = new PermissionRule { Enabled = false },
                },
            ],
        };
        session.Store(role);

        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = $"user_{Guid.NewGuid()}",
            Email = $"{Guid.NewGuid()}@example.com",
            RoleIds = new List<Guid> { role.Id },
        };
        session.Store(user);
        await session.SaveChangesAsync(Ct);

        return fixture.CreateToken(new[] { role.Name }, user.Id.ToString());
    }

    public static HttpClient ClientFor(IntegrationTestFixture fixture, string token)
    {
        var client = fixture.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    public static async Task<Guid> CreateAsync(
        IntegrationTestFixture fixture, string token, string type, Dictionary<string, object> data,
        ContentStatus status = ContentStatus.Draft)
    {
        var response = await ClientFor(fixture, token).PostAsJsonAsync(
            "/api/contents",
            new barakoCMS.Features.Content.Create.Request { ContentType = type, Data = data, Status = status },
            ApiJson.Options,
            Ct);

        var body = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        return JsonDocument.Parse(body).RootElement.GetProperty("id").GetGuid();
    }

    public static Task<HttpResponseMessage> UpdateAsync(
        IntegrationTestFixture fixture, string token, Guid id, Dictionary<string, object> data) =>
        ClientFor(fixture, token).PutAsJsonAsync(
            $"/api/contents/{id}",
            new barakoCMS.Features.Content.Update.Request { Id = id, Version = 0, Data = data },
            ApiJson.Options,
            Ct);
}
