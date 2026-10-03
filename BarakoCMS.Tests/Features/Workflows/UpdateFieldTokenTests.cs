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
/// <c>UpdateFieldAction</c> told to write a token field fails, and the token stays as generated.
/// </summary>
/// <remarks>
/// Failing rather than doing nothing: a workflow that reports success after writing nothing is
/// the outcome the action's own result type exists to rule out. The failure is permanent, since
/// the field is a token on every retry, and it does not name the value the action was given.
/// </remarks>
[Collection("Sequential")]
public class UpdateFieldTokenTests
{
    private const string Forged = "forged0token0forged0token0forged";

    private readonly IntegrationTestFixture _fixture;

    public UpdateFieldTokenTests(IntegrationTestFixture fixture) => _fixture = fixture;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Seeded(IDocumentStore Store, string Tenant, Guid ContentId);

    private async Task<Seeded> SeedAsync()
    {
        var store = _fixture.Services.GetRequiredService<IDocumentStore>();
        var tenant = "update-field-token-" + Guid.NewGuid().ToString("n")[..8];
        var type = "ticket" + Guid.NewGuid().ToString("n")[..8];
        var contentId = Guid.NewGuid();

        await using var seed = store.LightweightSession(tenant);
        seed.Store(new ContentTypeDefinition
        {
            Id = Guid.NewGuid(),
            Name = type,
            DisplayName = type,
            Fields =
            [
                new FieldDefinition { Name = "Name", DisplayName = "Name", Type = "string" },
                new FieldDefinition
                {
                    Name = "ClaimToken", DisplayName = "Claim token", Type = "token", Sensitivity = SensitivityLevel.Hidden,
                },
            ],
        });
        await seed.SaveChangesAsync(Ct);

        var writer = new ContentWriter(seed, new ContentSourcingPolicyService(seed));
        await writer.CreateAsync(
            new ContentCreated(
                contentId, type, new Dictionary<string, object> { ["Name"] = "Ana" }, ContentStatus.Draft,
                Guid.NewGuid(), null, SensitivityLevel.Public, DateTime.UtcNow),
            Ct);

        await seed.SaveChangesAsync(Ct);
        return new Seeded(store, tenant, contentId);
    }

    private static async Task<WorkflowActionResult> RunAsync(Seeded seeded, Dictionary<string, string> parameters)
    {
        await using var session = seeded.Store.LightweightSession(seeded.Tenant);
        var writer = new ContentWriter(session, new ContentSourcingPolicyService(session));
        var action = new UpdateFieldAction(
            session, writer, new ContentLifecycleRunner([], session), NullLogger<UpdateFieldAction>.Instance);

        return await action.RunAsync(
            parameters, new Content { Id = seeded.ContentId, LastModifiedBy = Guid.NewGuid() }, Ct);
    }

    private static async Task<string> StoredTokenAsync(Seeded seeded)
    {
        await using var session = seeded.Store.QuerySession(seeded.Tenant);
        var content = await session.LoadAsync<Content>(seeded.ContentId, Ct);
        content.Should().NotBeNull();
        content!.Data.Should().ContainKey("ClaimToken", "the writer generated one when the entry was created");
        return content.Data["ClaimToken"].ToString()!;
    }

    [Fact]
    public async Task Writing_a_token_field_fails_the_action_for_good_and_leaves_the_token()
    {
        var seeded = await SeedAsync();
        var token = await StoredTokenAsync(seeded);

        var result = await RunAsync(seeded, new() { ["Field"] = "data.ClaimToken", ["Value"] = Forged });

        result.Succeeded.Should().BeFalse();
        result.Retryable.Should().BeFalse("the field is a token on every retry");
        result.Error.Should().Contain("ClaimToken").And.Contain("token").And.NotContain(Forged);
        (await StoredTokenAsync(seeded)).Should().Be(token);
    }

    [Fact]
    public async Task Writing_a_token_field_with_no_value_fails_the_same_way()
    {
        var seeded = await SeedAsync();
        var token = await StoredTokenAsync(seeded);

        var result = await RunAsync(seeded, new() { ["Field"] = "ClaimToken" });

        result.Succeeded.Should().BeFalse();
        result.Retryable.Should().BeFalse();
        (await StoredTokenAsync(seeded)).Should().Be(token);
    }

    [Fact]
    public async Task Another_field_of_the_same_entry_is_still_written()
    {
        var seeded = await SeedAsync();
        var token = await StoredTokenAsync(seeded);

        var result = await RunAsync(seeded, new() { ["Field"] = "data.Name", ["Value"] = "Ana Cruz" });

        result.Succeeded.Should().BeTrue(result.Error);
        (await StoredTokenAsync(seeded)).Should().Be(token, "a write to another field keeps the token");
    }
}
