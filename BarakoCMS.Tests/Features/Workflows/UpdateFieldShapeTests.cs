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
/// <c>UpdateFieldAction</c> writing to an email or a choice field checks the value as an entry write
/// does, and writes under the key the entry already stores the field as.
/// </summary>
[Collection("Sequential")]
public class UpdateFieldShapeTests
{
    private readonly IntegrationTestFixture _fixture;

    public UpdateFieldShapeTests(IntegrationTestFixture fixture) => _fixture = fixture;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Seeded(IDocumentStore Store, string Tenant, Guid ContentId);

    private async Task<Seeded> SeedAsync()
    {
        var store = _fixture.Services.GetRequiredService<IDocumentStore>();
        var tenant = "update-field-shape-" + Guid.NewGuid().ToString("n")[..8];
        var type = "shaped" + Guid.NewGuid().ToString("n")[..8];
        var contentId = Guid.NewGuid();

        await using var seed = store.LightweightSession(tenant);
        seed.Store(new ContentTypeDefinition
        {
            Id = Guid.NewGuid(),
            Name = type,
            DisplayName = type,
            Fields =
            [
                new FieldDefinition { Name = "Email", DisplayName = "Email", Type = "email" },
                new FieldDefinition
                {
                    Name = "Tier", DisplayName = "Tier", Type = "choice",
                    Options = [new() { Value = "GOLD", Label = "Gold" }, new() { Value = "SILVER", Label = "Silver" }],
                },
            ],
        });

        var writer = new ContentWriter(seed, new ContentSourcingPolicyService(seed));
        await writer.CreateAsync(
            new ContentCreated(
                contentId, type, new Dictionary<string, object> { ["Email"] = "ana@example.com", ["Tier"] = "GOLD" },
                ContentStatus.Draft, Guid.NewGuid(), null, SensitivityLevel.Public, DateTime.UtcNow),
            Ct);

        await seed.SaveChangesAsync(Ct);
        return new Seeded(store, tenant, contentId);
    }

    private static async Task<WorkflowActionResult> SetAsync(Seeded seeded, string field, string value)
    {
        await using var session = seeded.Store.LightweightSession(seeded.Tenant);
        var writer = new ContentWriter(session, new ContentSourcingPolicyService(session));
        var action = new UpdateFieldAction(
            session, writer, new ContentLifecycleRunner([], session), NullLogger<UpdateFieldAction>.Instance);

        return await action.RunAsync(
            new Dictionary<string, string> { ["Field"] = field, ["Value"] = value },
            new Content { Id = seeded.ContentId, LastModifiedBy = Guid.NewGuid() },
            Ct);
    }

    private static async Task<Dictionary<string, object>> StoredAsync(Seeded seeded)
    {
        await using var session = seeded.Store.QuerySession(seeded.Tenant);
        var content = await session.LoadAsync<Content>(seeded.ContentId, Ct);
        content.Should().NotBeNull();
        return content!.Data;
    }

    [Fact]
    public async Task A_header_form_email_fails_the_action_and_leaves_the_entry_as_it_was()
    {
        var seeded = await SeedAsync();

        var result = await SetAsync(seeded, "data.Email", "Ana <ana@example.com>");

        result.Succeeded.Should().BeFalse();
        result.Retryable.Should().BeFalse("the same text is refused the same way on a retry");
        result.Error.Should().Contain("Email").And.NotContain("<ana@example.com>");
        (await StoredAsync(seeded))["Email"].ToString().Should().Be("ana@example.com");
    }

    [Fact]
    public async Task A_value_that_is_not_an_option_fails_the_action_and_an_option_is_stored()
    {
        var seeded = await SeedAsync();

        var refused = await SetAsync(seeded, "data.Tier", "PLATINUM");
        refused.Succeeded.Should().BeFalse();
        refused.Retryable.Should().BeFalse();
        refused.Error.Should().Contain("GOLD, SILVER").And.NotContain("PLATINUM");
        (await StoredAsync(seeded))["Tier"].ToString().Should().Be("GOLD");

        var accepted = await SetAsync(seeded, "data.Tier", "SILVER");
        accepted.Succeeded.Should().BeTrue(accepted.Error);
        (await StoredAsync(seeded))["Tier"].ToString().Should().Be("SILVER");
    }

    [Fact]
    public async Task A_field_named_in_another_case_replaces_the_stored_value_rather_than_adding_a_key()
    {
        var seeded = await SeedAsync();

        var result = await SetAsync(seeded, "data.email", "bo@example.com");

        result.Succeeded.Should().BeTrue(result.Error);
        var stored = await StoredAsync(seeded);
        stored.Keys.Where(k => k.Equals("Email", StringComparison.OrdinalIgnoreCase)).Should().Equal("Email");
        stored["Email"].ToString().Should().Be("bo@example.com");
    }
}
