using barakoCMS.Events;
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
/// <c>UpdateFieldAction</c> writing to a reference that holds a list. Its parameter is one piece of
/// text, which an entry write refuses there, so the action fails rather than storing it.
/// </summary>
[Collection("Sequential")]
public class UpdateFieldReferenceListTests
{
    private readonly IntegrationTestFixture _fixture;

    public UpdateFieldReferenceListTests(IntegrationTestFixture fixture) => _fixture = fixture;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Setting_a_list_of_references_fails_the_action_and_leaves_the_entry_as_it_was()
    {
        var store = _fixture.Services.GetRequiredService<IDocumentStore>();
        var tenant = "update-field-reflist-" + Guid.NewGuid().ToString("n")[..8];
        var type = "panel" + Guid.NewGuid().ToString("n")[..8];
        var contentId = Guid.NewGuid();
        var kept = Guid.NewGuid().ToString();

        await using (var seed = store.LightweightSession(tenant))
        {
            seed.Store(new ContentTypeDefinition
            {
                Id = Guid.NewGuid(),
                Name = type,
                DisplayName = type,
                Fields =
                [
                    new FieldDefinition
                    {
                        Name = "Speakers", DisplayName = "Speakers", Type = "reference", ReferenceType = "speaker",
                        Multiple = true,
                    },
                ],
            });

            var seedWriter = new ContentWriter(seed, new ContentSourcingPolicyService(seed));
            await seedWriter.CreateAsync(
                new ContentCreated(
                    contentId, type, new Dictionary<string, object> { ["Speakers"] = new List<object> { kept } },
                    ContentStatus.Draft, Guid.NewGuid(), null, SensitivityLevel.Public, DateTime.UtcNow),
                Ct);

            await seed.SaveChangesAsync(Ct);
        }

        WorkflowActionResult result;
        await using (var session = store.LightweightSession(tenant))
        {
            var writer = new ContentWriter(session, new ContentSourcingPolicyService(session));
            var action = new UpdateFieldAction(
                session, writer, new ContentLifecycleRunner([], session), NullLogger<UpdateFieldAction>.Instance);

            result = await action.RunAsync(
                new Dictionary<string, string> { ["Field"] = "data.Speakers", ["Value"] = Guid.NewGuid().ToString() },
                new Content { Id = contentId, LastModifiedBy = Guid.NewGuid() },
                Ct);
        }

        result.Succeeded.Should().BeFalse();
        result.Retryable.Should().BeFalse("the same text is one value on every retry");
        result.Error.Should().Contain("Speakers").And.Contain("list of references");

        await using var read = store.QuerySession(tenant);
        var stored = await read.LoadAsync<Content>(contentId, Ct);
        stored.Should().NotBeNull();
        barakoCMS.Core.Validation.FieldTypeRegistry.TryReadChoice(stored!.Data["Speakers"], out var ids, out var isList)
            .Should().BeTrue();
        isList.Should().BeTrue();
        ids.Should().Equal(kept);
    }
}
