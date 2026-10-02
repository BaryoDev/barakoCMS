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
/// <c>UpdateFieldAction</c> writing to a money field that declares a currency. Its parameter is
/// text, and text stored there would be refused on every later save of the entry, so the action
/// stores a number or fails.
/// </summary>
[Collection("Sequential")]
public class UpdateFieldMoneyTests
{
    private readonly IntegrationTestFixture _fixture;

    public UpdateFieldMoneyTests(IntegrationTestFixture fixture) => _fixture = fixture;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Seeded(IDocumentStore Store, string Tenant, Guid ContentId);

    private async Task<Seeded> SeedAsync(string? currency)
    {
        var store = _fixture.Services.GetRequiredService<IDocumentStore>();
        var tenant = "update-field-money-" + Guid.NewGuid().ToString("n")[..8];
        var type = "priced" + Guid.NewGuid().ToString("n")[..8];
        var contentId = Guid.NewGuid();

        await using var seed = store.LightweightSession(tenant);
        seed.Store(new ContentTypeDefinition
        {
            Id = Guid.NewGuid(),
            Name = type,
            DisplayName = type,
            Fields = [new FieldDefinition { Name = "Price", DisplayName = "Price", Type = "money", Currency = currency }],
        });

        var writer = new ContentWriter(seed, new ContentSourcingPolicyService(seed));
        await writer.CreateAsync(
            new ContentCreated(
                contentId, type, new Dictionary<string, object> { ["Price"] = 10m }, ContentStatus.Draft,
                Guid.NewGuid(), null, SensitivityLevel.Public, DateTime.UtcNow),
            Ct);

        await seed.SaveChangesAsync(Ct);
        return new Seeded(store, tenant, contentId);
    }

    private static async Task<WorkflowActionResult> SetPriceAsync(Seeded seeded, string value)
    {
        await using var session = seeded.Store.LightweightSession(seeded.Tenant);
        var writer = new ContentWriter(session, new ContentSourcingPolicyService(session));
        var action = new UpdateFieldAction(
            session, writer, new ContentLifecycleRunner([], session), NullLogger<UpdateFieldAction>.Instance);

        return await action.RunAsync(
            new Dictionary<string, string> { ["Field"] = "data.Price", ["Value"] = value },
            new Content { Id = seeded.ContentId, LastModifiedBy = Guid.NewGuid() },
            Ct);
    }

    private static async Task<object> StoredPriceAsync(Seeded seeded)
    {
        await using var session = seeded.Store.QuerySession(seeded.Tenant);
        var content = await session.LoadAsync<Content>(seeded.ContentId, Ct);
        content.Should().NotBeNull();
        return content!.Data["Price"];
    }

    [Fact]
    public async Task Plain_decimal_text_is_stored_as_a_number_in_a_field_with_a_currency()
    {
        var seeded = await SeedAsync("USD");

        var result = await SetPriceAsync(seeded, "19.90");

        result.Succeeded.Should().BeTrue(result.Error);
        var stored = await StoredPriceAsync(seeded);
        stored.Should().BeOfType<decimal>("text in this field would be refused on the entry's next save");
        stored.Should().Be(19.90m);
    }

    [Fact]
    public async Task Text_that_is_not_an_amount_fails_the_action_and_leaves_the_entry_as_it_was()
    {
        var seeded = await SeedAsync("USD");

        var result = await SetPriceAsync(seeded, "about 20");

        result.Succeeded.Should().BeFalse();
        result.Retryable.Should().BeFalse("the same text parses the same way on a retry");
        result.Error.Should().Contain("Price").And.Contain("USD").And.NotContain("about 20");
        (await StoredPriceAsync(seeded)).Should().Be(10m);
    }

    [Fact]
    public async Task An_amount_with_more_decimal_places_than_the_currency_has_fails_the_action()
    {
        var seeded = await SeedAsync("USD");

        var result = await SetPriceAsync(seeded, "19.999");

        result.Succeeded.Should().BeFalse();
        result.Retryable.Should().BeFalse();
        result.Error.Should().Contain("Price").And.Contain("2 decimal places").And.NotContain("19.999");
        (await StoredPriceAsync(seeded)).Should().Be(10m);
    }

    [Fact]
    public async Task A_money_field_with_no_currency_keeps_the_text_it_was_given_as_it_did()
    {
        var seeded = await SeedAsync(null);

        var result = await SetPriceAsync(seeded, "19.999");

        result.Succeeded.Should().BeTrue(result.Error);
        (await StoredPriceAsync(seeded)).Should().Be("19.999");
    }
}
