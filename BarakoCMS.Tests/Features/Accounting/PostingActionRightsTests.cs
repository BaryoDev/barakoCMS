using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using BarakoCMS.Accounting;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;

namespace BarakoCMS.Tests.Features.Accounting;

/// <summary>
/// The posting actions run as the system, so the person who saves the workflow must hold the
/// capability to post journal entries.
/// </summary>
[Collection("Integration1")]
public class PostingActionRightsTests(IntegrationTestFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly (string Type, (string, string)[] Parameters)[] Postings =
    [
        ("Accounting.PostJournal", [("Mapping", "any"), ("ActionKey", "receipt")]),
        ("Accounting.Reverse", [("ActionKey", "receipt"), ("Reason", "cancelled")]),
    ];

    [Fact]
    public async Task An_author_without_the_posting_capability_cannot_save_a_workflow_with_a_posting_action()
    {
        var client = await AuthorAsync();
        foreach (var (type, parameters) in Postings)
        {
            var response = await client.PostAsJsonAsync("/api/workflows", Workflow(type, parameters), Ct);
            response.StatusCode.Should().Be(HttpStatusCode.Forbidden, type);
        }
    }

    [Fact]
    public async Task An_author_with_the_posting_capability_can_save_a_workflow_with_a_posting_action()
    {
        var client = await AuthorAsync(AccountingCapabilities.PostEntries);
        foreach (var (type, parameters) in Postings)
        {
            var response = await client.PostAsJsonAsync("/api/workflows", Workflow(type, parameters), Ct);
            response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        }
    }

    [Fact]
    public async Task A_posting_action_inside_a_conditional_branch_needs_the_capability_too()
    {
        var client = await AuthorAsync();
        var branch = System.Text.Json.JsonSerializer.Serialize(new[]
        {
            new { Type = "Accounting.Reverse", Parameters = new Dictionary<string, string> { ["ActionKey"] = "receipt", ["Reason"] = "cancelled" } },
        });
        var conditional = new
        {
            type = "Conditional",
            parameters = new Dictionary<string, string> { ["Condition"] = "{{data.Name}} == \"x\"", ["ThenActions"] = branch },
        };

        var response = await client.PostAsJsonAsync("/api/workflows", Body(AccountingContentTypes.JournalEntry, conditional), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    private static object Workflow(string type, (string Key, string Value)[] parameters) =>
        Body(AccountingContentTypes.JournalEntry, new { type, parameters = parameters.ToDictionary(p => p.Key, p => p.Value) });

    private static object Body(string type, object action) => new
    {
        name = "posting-" + Guid.NewGuid().ToString("n")[..10],
        triggerContentType = type,
        triggerEvent = "Created",
        actions = new[] { action },
    };

    /// <summary>A user whose role can manage workflows and holds the extra capabilities given.</summary>
    private async Task<HttpClient> AuthorAsync(params string[] capabilities)
    {
        var roleName = $"posting {Guid.NewGuid():N}";
        var userId = Guid.NewGuid();
        using (var scope = fixture.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            var role = new Role
            {
                Id = Guid.NewGuid(),
                Name = roleName,
                SystemCapabilities = [SystemCapabilities.ManageWorkflows, .. capabilities],
            };
            session.Store(role);
            session.Store(new User { Id = userId, Username = $"posting-{userId}", Email = $"posting-{userId}@example.com", RoleIds = [role.Id] });
            await session.SaveChangesAsync(Ct);
        }

        var client = fixture.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", fixture.CreateToken(roles: [roleName], userId: userId.ToString()));
        return client;
    }
}
