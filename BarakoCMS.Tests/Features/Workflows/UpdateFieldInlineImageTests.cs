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
/// <c>UpdateFieldAction</c> on an inline image field. Its parameter is text, which an inline image
/// field never holds, so the action fails without writing anything.
/// </summary>
[Collection("Sequential")]
public class UpdateFieldInlineImageTests
{
    private readonly IntegrationTestFixture _fixture;

    public UpdateFieldInlineImageTests(IntegrationTestFixture fixture) => _fixture = fixture;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Setting_an_inline_image_field_fails_the_action_and_leaves_the_entry_as_it_was()
    {
        var store = _fixture.Services.GetRequiredService<IDocumentStore>();
        var tenant = "update-field-image-" + Guid.NewGuid().ToString("n")[..8];
        var type = "branded" + Guid.NewGuid().ToString("n")[..8];
        var contentId = Guid.NewGuid();

        await using (var seed = store.LightweightSession(tenant))
        {
            seed.Store(new ContentTypeDefinition
            {
                Id = Guid.NewGuid(),
                Name = type,
                DisplayName = type,
                Fields = [new FieldDefinition { Name = "Logo", DisplayName = "Logo", Type = "inlineimage" }],
            });

            var seedWriter = new ContentWriter(seed, new ContentSourcingPolicyService(seed));
            await seedWriter.CreateAsync(
                new ContentCreated(
                    contentId, type, new Dictionary<string, object> { ["Title"] = "Acme" }, ContentStatus.Draft,
                    Guid.NewGuid(), null, SensitivityLevel.Public, DateTime.UtcNow),
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
                new Dictionary<string, string> { ["Field"] = "data.Logo", ["Value"] = "data:image/svg+xml;base64,PHN2Zz48L3N2Zz4=" },
                new Content { Id = contentId, LastModifiedBy = Guid.NewGuid() },
                Ct);
        }

        result.Succeeded.Should().BeFalse();
        result.Retryable.Should().BeFalse("the same text is refused the same way on a retry");
        result.Error.Should().Contain("Logo").And.Contain("inline image").And.NotContain("svg");

        await using var read = store.QuerySession(tenant);
        var content = await read.LoadAsync<Content>(contentId, Ct);
        content.Should().NotBeNull();
        content!.Data.Should().HaveCount(1);
        content.Data.Should().ContainKey("Title").And.NotContainKey("Logo");
    }
}
