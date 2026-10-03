using barakoCMS.Core.Interfaces;
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
/// <c>UpdateFieldAction</c> writing to a <c>file</c> field. A workflow runs for no signed-in user,
/// so it may attach a public file and nothing else, as its email attachments may.
/// </summary>
[Collection("Sequential")]
public class UpdateFieldFileTests
{
    private readonly IntegrationTestFixture _fixture;

    public UpdateFieldFileTests(IntegrationTestFixture fixture) => _fixture = fixture;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Seeded(IDocumentStore Store, string Tenant, Guid ContentId, string Held);

    private async Task<Seeded> SeedAsync()
    {
        var store = _fixture.Services.GetRequiredService<IDocumentStore>();
        var tenant = "update-field-file-" + Guid.NewGuid().ToString("n")[..8];
        var type = "filed" + Guid.NewGuid().ToString("n")[..8];
        var contentId = Guid.NewGuid();
        var held = Guid.NewGuid().ToString();

        await using var seed = store.LightweightSession(tenant);
        seed.Store(new ContentTypeDefinition
        {
            Id = Guid.NewGuid(),
            Name = type,
            DisplayName = type,
            Fields = [new FieldDefinition { Name = "Cover", DisplayName = "Cover", Type = "file" }],
        });

        var writer = new ContentWriter(seed, new ContentSourcingPolicyService(seed));
        await writer.CreateAsync(
            new ContentCreated(
                contentId, type, new Dictionary<string, object> { ["Cover"] = held }, ContentStatus.Draft,
                Guid.NewGuid(), null, SensitivityLevel.Public, DateTime.UtcNow),
            Ct);

        await seed.SaveChangesAsync(Ct);
        return new Seeded(store, tenant, contentId, held);
    }

    private static async Task<WorkflowActionResult> SetCoverAsync(Seeded seeded, string value, IFileStore? files)
    {
        await using var session = seeded.Store.LightweightSession(seeded.Tenant);
        var writer = new ContentWriter(session, new ContentSourcingPolicyService(session));
        var action = new UpdateFieldAction(
            session, writer, new ContentLifecycleRunner([], session), NullLogger<UpdateFieldAction>.Instance, files);

        return await action.RunAsync(
            new Dictionary<string, string> { ["Field"] = "data.Cover", ["Value"] = value },
            new Content { Id = seeded.ContentId, LastModifiedBy = Guid.NewGuid() },
            Ct);
    }

    private static async Task<object> StoredCoverAsync(Seeded seeded)
    {
        await using var session = seeded.Store.QuerySession(seeded.Tenant);
        var content = await session.LoadAsync<Content>(seeded.ContentId, Ct);
        content.Should().NotBeNull();
        return content!.Data["Cover"];
    }

    [Fact]
    public async Task A_public_file_is_attached()
    {
        var seeded = await SeedAsync();
        var files = new FakeFileStore();
        var open = files.Add(isPublic: true).ToString();

        var result = await SetCoverAsync(seeded, open, files);

        result.Succeeded.Should().BeTrue(result.Error);
        (await StoredCoverAsync(seeded)).ToString().Should().Be(open);
        files.SingleReads.Should().Equal("public");
    }

    [Fact]
    public async Task A_private_file_a_missing_id_and_text_fail_the_action_for_good_and_leave_the_entry_as_it_was()
    {
        var seeded = await SeedAsync();
        var files = new FakeFileStore();
        var locked = files.Add(isPublic: false, owner: Guid.NewGuid()).ToString();

        var values = new[] { locked, Guid.NewGuid().ToString(), "not-an-id" };
        values.Should().HaveCount(3);

        foreach (var value in values)
        {
            var result = await SetCoverAsync(seeded, value, files);

            result.Succeeded.Should().BeFalse("{0} is not a public file", value);
            result.Retryable.Should().BeFalse("the same value is refused the same way on a retry");
            result.Error.Should().Contain("Cover").And.Contain("public stored file").And.NotContain(value);
            (await StoredCoverAsync(seeded)).ToString().Should().Be(seeded.Held);
        }
    }

    [Fact]
    public async Task With_no_file_store_a_new_file_fails_and_the_one_the_entry_holds_is_kept()
    {
        var seeded = await SeedAsync();
        var open = new FakeFileStore().Add(isPublic: true).ToString();

        var refused = await SetCoverAsync(seeded, open, new NoFileStore());
        refused.Succeeded.Should().BeFalse();
        refused.Retryable.Should().BeFalse();

        var kept = await SetCoverAsync(seeded, seeded.Held, new NoFileStore());
        kept.Succeeded.Should().BeTrue(kept.Error);
        (await StoredCoverAsync(seeded)).ToString().Should().Be(seeded.Held);
    }
}
