using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using barakoCMS.Core.Interfaces;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// Creating a type compares its name with what is stored the way the sourcing decision is keyed:
/// trimmed, lowered, a space as a hyphen.
/// </summary>
/// <remarks>
/// Names were stored as typed before 4.0, so a database that came through 3.x can hold "Blog Post"
/// as a type name and on its entries. Both are stored here straight through a session, which is
/// what such a database looks like, because no endpoint writes a name that way any more.
///
/// Each refusal has its pair, so neither passes against a server that refuses every create.
/// </remarks>
[Collection("Sequential")]
public class ContentSourcingLegacyNameTests
{
    private readonly IntegrationTestFixture _factory;

    public ContentSourcingLegacyNameTests(IntegrationTestFixture factory) => _factory = factory;

    private async Task<HttpClient> ClientAsync()
    {
        var (token, _) = await TestHelpers.CreateAdminUserAsync(_factory);
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static Task<HttpResponseMessage> CreateTypeAsync(HttpClient client, string name, bool eventSourced) =>
        client.PostAsJsonAsync("/api/content-types", new
        {
            name,
            displayName = "Legacy name probe",
            eventSourced,
            fields = new[] { new { name = "Title", type = "string", sensitivity = "Public" } },
        });

    private async Task<bool> IsEventSourcedAsync(string name)
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IContentSourcingPolicy>()
            .IsEventSourcedAsync(name, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task A_name_stored_with_a_space_cannot_be_created_again_as_event_sourced()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await ClientAsync();
        var legacy = "Legacy Probe " + Guid.NewGuid().ToString("N")[..10];

        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();

        session.Store(new ContentTypeDefinition
        {
            Id = Guid.NewGuid(),
            Name = legacy,
            DisplayName = legacy,
            Fields = new List<FieldDefinition>
            {
                new() { Name = "Title", DisplayName = "Title", Type = "string" },
                new() { Name = "Secret", DisplayName = "Secret", Type = "string", Sensitivity = SensitivityLevel.Sensitive },
            },
        });
        await session.SaveChangesAsync(ct);

        try
        {
            var refused = await CreateTypeAsync(client, legacy, eventSourced: true);

            refused.StatusCode.Should().Be(HttpStatusCode.Conflict, await refused.Content.ReadAsStringAsync());
            (await refused.Content.ReadAsStringAsync()).Should().Contain("already exists");
            (await IsEventSourcedAsync(legacy)).Should().BeFalse("a refused create decides nothing for the name");

            var stored = await session.Query<ContentTypeDefinition>()
                .Where(d => d.Name == legacy)
                .ToListAsync(ct);
            stored.Should().HaveCount(1, "the stored type is still there");
            stored[0].Fields.Should().HaveCount(2);
            stored[0].Fields.Select(f => f.Name).Should().Contain("Secret");

            // The pair: a name with a space and nothing stored under it takes the same request.
            var fresh = "Fresh Probe " + Guid.NewGuid().ToString("N")[..10];
            var created = await CreateTypeAsync(client, fresh, eventSourced: true);
            created.StatusCode.Should().Be(HttpStatusCode.OK, await created.Content.ReadAsStringAsync());
            (await IsEventSourcedAsync(fresh)).Should().BeTrue();
        }
        finally
        {
            session.DeleteWhere<ContentTypeDefinition>(d => d.Name == legacy);
            await session.SaveChangesAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task A_name_with_entries_stored_under_a_spaced_spelling_cannot_become_event_sourced()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await ClientAsync();
        var legacy = "  Legacy Entries " + Guid.NewGuid().ToString("N")[..10];

        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();

        session.Store(new Content
        {
            Id = Guid.NewGuid(),
            ContentType = legacy,
            Data = new Dictionary<string, object> { ["Title"] = "written before names were normalised" },
        });
        await session.SaveChangesAsync(ct);

        try
        {
            var refused = await CreateTypeAsync(client, legacy, eventSourced: true);

            refused.StatusCode.Should().Be(HttpStatusCode.Conflict, await refused.Content.ReadAsStringAsync());
            (await refused.Content.ReadAsStringAsync()).Should().Contain("before the first entry");
            (await IsEventSourcedAsync(legacy)).Should().BeFalse("a refused create decides nothing for the name");

            // The pair: the refusal is about event sourcing, so the same name is still creatable
            // as a document-sourced type.
            var created = await CreateTypeAsync(client, legacy, eventSourced: false);
            created.StatusCode.Should().Be(HttpStatusCode.OK, await created.Content.ReadAsStringAsync());
        }
        finally
        {
            session.DeleteWhere<Content>(c => c.ContentType == legacy);
            await session.SaveChangesAsync(CancellationToken.None);
        }
    }
}
