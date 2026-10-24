using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;

namespace BarakoCMS.Tests.Features.Workflows;

/// <summary>
/// An entry a workflow run created keeps the run's id, set only by a caller inside the server and
/// never from a request body.
/// </summary>
[Collection("Integration3")]
public class SourceRunTests(IntegrationTestFixture fixture) : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private readonly HttpClient _admin = fixture.CreateClient();

    public async ValueTask InitializeAsync() =>
        _admin.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Bearer", await fixture.StoredUserTokenAsync("Admin", "SuperAdmin"));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task A_staged_entry_keeps_the_run_that_created_it()
    {
        var runId = Guid.CreateVersion7();

        var stored = await StageAsync(await StoreTypeAsync(), runId);

        stored.SourceRunId.Should().Be(runId);
    }

    [Fact]
    public async Task A_staged_entry_with_no_run_has_none()
    {
        var stored = await StageAsync(await StoreTypeAsync(), runId: null);

        stored.SourceRunId.Should().BeNull();
    }

    [Fact]
    public async Task A_create_request_cannot_set_the_run()
    {
        var type = await StoreTypeAsync();

        var created = await _admin.PostAsJsonAsync("/api/contents", new
        {
            contentType = type,
            sourceRunId = Guid.CreateVersion7(),
            data = new Dictionary<string, object> { ["Title"] = "Sent" },
        }, Ct);
        created.IsSuccessStatusCode.Should().BeTrue(await created.Content.ReadAsStringAsync(Ct));
        using var body = JsonDocument.Parse(await created.Content.ReadAsStringAsync(Ct));

        (await LoadAsync(body.RootElement.GetProperty("id").GetGuid())).SourceRunId.Should().BeNull();
    }

    private async Task<Content> StageAsync(string type, Guid? runId)
    {
        Guid id;
        using (var scope = fixture.Services.CreateScope())
        {
            var created = await scope.ServiceProvider.GetRequiredService<IContentCreator>().StageAsync(
                new ContentCreateRequest
                {
                    ContentType = type,
                    Data = new Dictionary<string, object> { ["Title"] = "Staged" },
                    SourceRunId = runId,
                },
                Guid.NewGuid(), batch: null, Ct);
            await scope.ServiceProvider.GetRequiredService<IDocumentSession>().SaveChangesAsync(Ct);
            id = created.Id;
        }

        return await LoadAsync(id);
    }

    private async Task<Content> LoadAsync(Guid id)
    {
        using var scope = fixture.Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<IDocumentSession>().LoadAsync<Content>(id, Ct))!;
    }

    private async Task<string> StoreTypeAsync()
    {
        var name = "sourcerun-" + Guid.NewGuid().ToString("n")[..10];
        using var scope = fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Store(new ContentTypeDefinition
        {
            Id = Guid.NewGuid(), Name = name, DisplayName = name,
            Fields = [new FieldDefinition { Name = "Title", DisplayName = "Title", Type = "string" }],
        });
        await session.SaveChangesAsync(Ct);
        return name;
    }
}
