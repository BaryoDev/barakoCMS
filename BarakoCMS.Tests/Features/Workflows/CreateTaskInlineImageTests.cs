using barakoCMS.Features.Workflows;
using barakoCMS.Features.Workflows.Actions;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BarakoCMS.Tests.Features.Workflows;

/// <summary>
/// <c>CreateTaskAction</c> copying <c>Data.*</c> parameters into the entry it creates. A parameter
/// is text, which an inline image field never holds, so naming one fails the action and creates
/// nothing.
/// </summary>
[Collection("Sequential")]
public class CreateTaskInlineImageTests
{
    private readonly IntegrationTestFixture _fixture;

    public CreateTaskInlineImageTests(IntegrationTestFixture fixture) => _fixture = fixture;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Seeded(IDocumentStore Store, string Tenant, string Type);

    private async Task<Seeded> SeedAsync()
    {
        var store = _fixture.Services.GetRequiredService<IDocumentStore>();
        var tenant = "create-task-image-" + Guid.NewGuid().ToString("n")[..8];
        var type = "ticket" + Guid.NewGuid().ToString("n")[..8];

        await using var seed = store.LightweightSession(tenant);
        seed.Store(new ContentTypeDefinition
        {
            Id = Guid.NewGuid(),
            Name = type,
            DisplayName = type,
            Fields =
            [
                new FieldDefinition { Name = "Title", DisplayName = "Title", Type = "string" },
                new FieldDefinition { Name = "Priority", DisplayName = "Priority", Type = "string" },
                new FieldDefinition { Name = "Logo", DisplayName = "Logo", Type = "inlineimage" },
            ],
        });
        await seed.SaveChangesAsync(Ct);
        return new Seeded(store, tenant, type);
    }

    private static async Task<WorkflowActionResult> RunAsync(Seeded seeded, string key, string value)
    {
        await using var session = seeded.Store.LightweightSession(seeded.Tenant);
        var action = new CreateTaskAction(
            session, NullLogger<CreateTaskAction>.Instance,
            new ContentWriter(session, new ContentSourcingPolicyService(session)));

        return await action.RunAsync(
            new Dictionary<string, string> { ["ContentType"] = seeded.Type, ["Title"] = "Follow up", [key] = value },
            new Content { Id = Guid.NewGuid(), ContentType = "trigger", LastModifiedBy = Guid.NewGuid() },
            Ct);
    }

    private static async Task<IReadOnlyList<Content>> EntriesAsync(Seeded seeded)
    {
        await using var session = seeded.Store.QuerySession(seeded.Tenant);
        return await session.Query<Content>().Where(c => c.ContentType == seeded.Type).ToListAsync(Ct);
    }

    [Fact]
    public async Task A_copied_text_field_is_stored_on_the_new_entry()
    {
        var seeded = await SeedAsync();

        var result = await RunAsync(seeded, "Data.Priority", "High");

        result.Succeeded.Should().BeTrue(result.Error);
        var entries = await EntriesAsync(seeded);
        entries.Should().HaveCount(1);
        entries[0].Data["Priority"].Should().Be("High");
    }

    [Fact]
    public async Task Copying_into_an_inline_image_field_fails_the_action_and_creates_nothing()
    {
        var seeded = await SeedAsync();

        var result = await RunAsync(seeded, "Data.logo", "data:image/svg+xml;base64,PHN2Zz48L3N2Zz4=");

        result.Succeeded.Should().BeFalse();
        result.Retryable.Should().BeFalse("the same text is refused the same way on a retry");
        result.Error.Should().Contain("Logo").And.Contain("inline image").And.NotContain("svg");
        (await EntriesAsync(seeded)).Should().BeEmpty();
    }
}
