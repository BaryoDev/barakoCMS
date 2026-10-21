using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using barakoCMS.Models;

namespace BarakoCMS.Tests.Features.Workflows;

/// <summary>GET /api/contents/{id}/runs lists the runs for an entry, and answers a caller who cannot read it with 404.</summary>
[Collection("Sequential")]
public class ContentRunsEndpointTests(IntegrationTestFixture fixture) : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private readonly HttpClient _admin = fixture.CreateClient();

    public async ValueTask InitializeAsync() =>
        _admin.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", await fixture.StoredUserTokenAsync("Admin", "SuperAdmin"));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task The_list_holds_the_runs_of_the_entry_newest_first()
    {
        var type = NewType();
        var entry = await StoreEntryAsync(type);
        var other = await StoreEntryAsync(type);
        var created = new CreatedRef("order", Guid.CreateVersion7(), "QC-0001");
        var older = await StoreRunAsync(entry, DateTimeOffset.UtcNow.AddMinutes(-10), RunStatus.Failed, created);
        var newer = await StoreRunAsync(entry, DateTimeOffset.UtcNow.AddMinutes(-1), RunStatus.Succeeded);
        await StoreRunAsync(other, DateTimeOffset.UtcNow, RunStatus.Succeeded);

        var runs = await ListAsync(_admin, entry);

        runs.Select(r => r.GetProperty("id").GetGuid()).Should().Equal(newer, older);
        var failed = runs[1];
        failed.GetProperty("status").GetString().Should().Be("Failed");
        var action = failed.GetProperty("actions")[0];
        action.GetProperty("actionType").GetString().Should().Be("Noop");
        action.GetProperty("error").GetString().Should().Be("It broke.");
        var ref0 = action.GetProperty("created")[0];
        ref0.GetProperty("id").GetGuid().Should().Be(created.Id);
        ref0.GetProperty("number").GetString().Should().Be("QC-0001");
    }

    [Fact]
    public async Task Include_created_adds_the_run_that_created_the_entry()
    {
        var type = NewType();
        var parent = await StoreEntryAsync(type);
        var child = await StoreEntryAsync(type);
        var runId = await StoreRunAsync(parent, DateTimeOffset.UtcNow, RunStatus.Succeeded, new CreatedRef(type, child, null));

        (await ListAsync(_admin, child)).Should().BeEmpty();
        var withCreated = await ListAsync(_admin, child, includeCreated: true);

        withCreated.Should().ContainSingle().Which.GetProperty("id").GetGuid().Should().Be(runId);
    }

    [Fact]
    public async Task A_caller_who_cannot_read_the_entry_gets_404()
    {
        var type = NewType();
        var entry = await StoreEntryAsync(type);
        await StoreRunAsync(entry, DateTimeOffset.UtcNow, RunStatus.Succeeded);
        var caller = await CallerAsync(readableType: null, SystemCapabilities.ViewWorkflowRuns);

        var res = await caller.GetAsync($"/api/contents/{entry}/runs", Ct);

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await caller.GetAsync($"/api/contents/{Guid.NewGuid()}/runs", Ct)).StatusCode
            .Should().Be(res.StatusCode, "a hidden entry looks the same as one that does not exist");
    }

    [Fact]
    public async Task A_caller_without_the_capability_gets_403()
    {
        var type = NewType();
        var entry = await StoreEntryAsync(type);
        var caller = await CallerAsync(readableType: type);

        (await caller.GetAsync($"/api/contents/{entry}/runs", Ct)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task An_unknown_id_is_404()
    {
        (await _admin.GetAsync($"/api/contents/{Guid.NewGuid()}/runs", Ct)).StatusCode
            .Should().Be(HttpStatusCode.NotFound);
    }

    private static string NewType() => "runs-" + Guid.NewGuid().ToString("n")[..10];

    private async Task<List<JsonElement>> ListAsync(HttpClient client, Guid id, bool includeCreated = false)
    {
        var res = await client.GetAsync($"/api/contents/{id}/runs?includeCreated={includeCreated.ToString().ToLowerInvariant()}", Ct);
        var text = await res.Content.ReadAsStringAsync(Ct);
        res.StatusCode.Should().Be(HttpStatusCode.OK, text);
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.GetProperty("runs").EnumerateArray().Select(e => e.Clone()).ToList();
    }

    private async Task<Guid> StoreEntryAsync(string type)
    {
        using var scope = fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var id = Guid.NewGuid();
        session.Store(new Content { Id = id, ContentType = type, Status = ContentStatus.Draft, Data = new() });
        await session.SaveChangesAsync(Ct);
        return id;
    }

    private async Task<Guid> StoreRunAsync(Guid contentId, DateTimeOffset at, RunStatus status, CreatedRef? created = null)
    {
        using var scope = fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var failed = status == RunStatus.Failed;
        var run = new WorkflowRun
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = Guid.NewGuid(),
            WorkflowName = "Runs listing",
            ContentId = contentId,
            ContentType = "article",
            TriggerEvent = WorkflowEvents.Published,
            Status = status,
            CreatedAt = at,
            CompletedAt = at,
            Actions =
            [
                new WorkflowActionAttempt
                {
                    Ordinal = 0,
                    ActionType = "Noop",
                    Status = failed ? AttemptStatus.Failed : AttemptStatus.Succeeded,
                    Error = failed ? "It broke." : null,
                    Created = created is null ? null : [created],
                },
            ],
        };
        session.Store(run);
        await session.SaveChangesAsync(Ct);
        return run.Id;
    }

    /// <summary>A signed-in caller whose one role holds these capabilities and, optionally, read on one type.</summary>
    private async Task<HttpClient> CallerAsync(string? readableType, params string[] capabilities)
    {
        using var scope = fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var role = new Role
        {
            Id = Guid.NewGuid(),
            Name = $"Runs {Guid.NewGuid():N}",
            SystemCapabilities = [.. capabilities],
            Permissions = readableType is null
                ? []
                : [new ContentTypePermission { ContentTypeSlug = readableType, Read = new PermissionRule { Enabled = true } }],
        };
        session.Store(role);
        var userId = Guid.NewGuid();
        session.Store(new User
        {
            Id = userId,
            Username = $"runs-{userId:n}",
            Email = $"runs-{userId:n}@example.com",
            RoleIds = [role.Id],
        });
        await session.SaveChangesAsync(Ct);

        var client = fixture.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", fixture.CreateToken(roles: [role.Name], userId: userId.ToString()));
        return client;
    }
}
