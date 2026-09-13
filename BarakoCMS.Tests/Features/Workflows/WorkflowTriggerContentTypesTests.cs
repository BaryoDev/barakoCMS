using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using barakoCMS.Models;

namespace BarakoCMS.Tests.Features.Workflows;

/// <summary>
/// What the workflow endpoints accept and return for a trigger naming more than one content type.
/// </summary>
/// <remarks>
/// Read as raw JSON rather than through the response class, because the console reads the wire shape
/// without ever seeing that class, and a field the class has but the JSON lacks is the break that
/// matters.
/// </remarks>
[Collection("Sequential")]
public class WorkflowTriggerContentTypesTests
{
    private readonly IntegrationTestFixture _factory;
    private readonly HttpClient _client;

    public WorkflowTriggerContentTypesTests(IntegrationTestFixture factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Creating_a_workflow_with_a_list_returns_the_list_and_its_first_type_as_the_single_field()
    {
        await AuthenticateAsync();
        var page = NewName("page");
        var post = NewName("post");

        var res = await PostAsync(new { name = NewName("0726-wf"), triggerContentTypes = new[] { page, post, page } });
        res.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}", res.StatusCode, await res.Content.ReadAsStringAsync());
        var created = await ReadAsync(res);

        TypesOf(created).Should().Equal([page, post], "a repeated type is stored once");
        created.GetProperty("triggerContentType").GetString().Should().Be(page,
            "a reader that only knows the single field still sees a type the workflow fires for");

        var listed = await FindInListAsync(created.GetProperty("id").GetGuid());
        TypesOf(listed).Should().Equal([page, post]);
        listed.GetProperty("triggerContentType").GetString().Should().Be(page);
    }

    [Fact]
    public async Task A_single_type_workflow_reads_back_unchanged_with_its_type_in_the_list()
    {
        await AuthenticateAsync();
        var post = NewName("post");

        var res = await PostAsync(new { name = NewName("0726-wf"), triggerContentType = post });
        res.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}", res.StatusCode, await res.Content.ReadAsStringAsync());
        var created = await ReadAsync(res);

        created.GetProperty("triggerContentType").GetString().Should().Be(post);
        TypesOf(created).Should().Equal([post]);
    }

    [Fact]
    public async Task A_workflow_stored_before_the_list_existed_lists_its_single_type()
    {
        await AuthenticateAsync();
        var post = NewName("post");
        var id = Guid.NewGuid();

        using (var scope = _factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Store(new WorkflowDefinition
            {
                Id = id,
                Name = NewName("0726-wf"),
                TriggerContentType = post,
                TriggerEvent = "Published",
                Actions = [EmailAction()],
            });
            await session.SaveChangesAsync();
        }

        await StripListFromStoredDocumentAsync(_factory, id);

        var listed = await FindInListAsync(id);
        listed.GetProperty("triggerContentType").GetString().Should().Be(post);
        TypesOf(listed).Should().Equal([post]);
    }

    [Fact]
    public async Task An_empty_list_with_no_single_type_is_refused()
    {
        await AuthenticateAsync();

        var res = await PostAsync(new { name = NewName("0726-wf"), triggerContentTypes = Array.Empty<string>() });
        var body = await res.Content.ReadAsStringAsync();

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest, "got {0}: {1}", res.StatusCode, body);
        body.Should().Contain("triggerContentType: Trigger content type is required",
            "the same refusal a workflow with no single type gets");
    }

    /// <summary>
    /// Removes the list from a stored workflow's JSON, so the document looks like one saved before
    /// the field existed.
    /// </summary>
    internal static async Task StripListFromStoredDocumentAsync(IntegrationTestFixture factory, Guid id)
    {
        using var scope = factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IDocumentStore>();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();

        session.QueueSqlCommand(
            $"update {store.Options.DatabaseSchemaName}.mt_doc_workflowdefinition "
          + "set data = data - 'TriggerContentTypes' - 'triggerContentTypes' where id = ?",
            id);
        await session.SaveChangesAsync();

        var json = await session.Json.FindByIdAsync<WorkflowDefinition>(id);
        json.Should().NotBeNull();
        json.Should().Contain("riggerContentType", "the single field is still there");
        json.Should().NotContain("riggerContentTypes", "the list has to be gone for this to be an old document");
    }

    private async Task AuthenticateAsync()
    {
        var (token, _) = await TestHelpers.CreateAdminUserAsync(_factory);
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    private static string NewName(string prefix) => $"{prefix}-{Guid.NewGuid():N}";

    private static WorkflowAction EmailAction() => new()
    {
        Type = "Email",
        Parameters = new Dictionary<string, string> { ["To"] = "a@example.com", ["Subject"] = "s", ["Body"] = "b" },
    };

    private Task<HttpResponseMessage> PostAsync(object trigger)
    {
        var body = JsonSerializer.SerializeToNode(trigger)!.AsObject();
        body["triggerEvent"] = "Published";
        body["actions"] = JsonSerializer.SerializeToNode(new[] { EmailAction() });
        return _client.PostAsJsonAsync("/api/workflows", body);
    }

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage res) =>
        (await res.Content.ReadFromJsonAsync<JsonElement>()).Clone();

    private static List<string?> TypesOf(JsonElement workflow)
    {
        workflow.TryGetProperty("triggerContentTypes", out var types).Should().BeTrue(
            "the response has to carry the list: {0}", workflow.GetRawText());
        return types.EnumerateArray().Select(t => t.GetString()).ToList();
    }

    private async Task<JsonElement> FindInListAsync(Guid id)
    {
        // Named to sort first, and the largest page, so the workflow is on page one however many
        // other tests have left behind.
        var res = await _client.GetAsync($"/api/workflows?pageSize={PaginatedRequest.MaxPageSize}");
        res.EnsureSuccessStatusCode();
        var page = await ReadAsync(res);

        var items = page.GetProperty("items").EnumerateArray().ToList();
        items.Should().NotBeEmpty();
        return items.Single(w => w.GetProperty("id").GetGuid() == id);
    }
}
