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
/// <c>UpdateFieldAction</c> on a type whose entries are numbered by the server. Setting a different
/// number fails without retry, as an update with one is refused. Setting another field keeps the number.
/// </summary>
[Collection("Integration1")]
public class UpdateFieldNumberedTests(IntegrationTestFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<(string Tenant, Guid Id)> SeedAsync()
    {
        var store = fixture.Services.GetRequiredService<IDocumentStore>();
        var tenant = "update-field-number-" + Guid.NewGuid().ToString("n")[..8];
        var type = "numbered" + Guid.NewGuid().ToString("n")[..8];
        var contentId = Guid.NewGuid();

        await using var seed = store.LightweightSession(tenant);
        seed.Store(new ContentTypeDefinition
        {
            Id = Guid.NewGuid(),
            Name = type,
            DisplayName = type,
            Fields =
            [
                new FieldDefinition { Name = "Title", DisplayName = "Title", Type = "string" },
                new FieldDefinition { Name = "InvoiceNo", DisplayName = "Invoice no", Type = "string" },
            ],
            Numbering = new NumberingRule { Field = "InvoiceNo", Prefix = "INV-", Padding = 4 },
        });

        var seedWriter = new ContentWriter(seed, new ContentSourcingPolicyService(seed));
        await seedWriter.CreateAsync(
            new ContentCreated(
                contentId, type, new Dictionary<string, object> { ["Title"] = "first", ["InvoiceNo"] = "INV-0001" },
                ContentStatus.Draft, Guid.NewGuid(), null, SensitivityLevel.Public, DateTime.UtcNow),
            Ct);
        await seed.SaveChangesAsync(Ct);
        return (tenant, contentId);
    }

    private async Task<WorkflowActionResult> RunAsync(string tenant, Guid id, string field, string value)
    {
        var store = fixture.Services.GetRequiredService<IDocumentStore>();
        await using var session = store.LightweightSession(tenant);
        var writer = new ContentWriter(session, new ContentSourcingPolicyService(session));
        var action = new UpdateFieldAction(
            session, writer, new ContentLifecycleRunner([], session), NullLogger<UpdateFieldAction>.Instance);

        return await action.RunAsync(
            new Dictionary<string, string> { ["Field"] = field, ["Value"] = value },
            new Content { Id = id, LastModifiedBy = Guid.NewGuid() },
            Ct);
    }

    private async Task<Content> LoadAsync(string tenant, Guid id)
    {
        var store = fixture.Services.GetRequiredService<IDocumentStore>();
        await using var read = store.QuerySession(tenant);
        return (await read.LoadAsync<Content>(id, Ct))!;
    }

    [Fact]
    public async Task Setting_a_different_number_fails_without_retry_and_keeps_the_stored_one()
    {
        var (tenant, id) = await SeedAsync();

        var result = await RunAsync(tenant, id, "data.InvoiceNo", "INV-0002");

        result.Succeeded.Should().BeFalse();
        result.Retryable.Should().BeFalse("the same value is refused the same way on a retry");
        result.Error.Should().Contain("numbered by the server");
        (await LoadAsync(tenant, id)).Data["InvoiceNo"].ToString().Should().Be("INV-0001");
    }

    [Fact]
    public async Task Setting_another_field_keeps_the_number()
    {
        var (tenant, id) = await SeedAsync();

        var result = await RunAsync(tenant, id, "data.Title", "second");

        result.Succeeded.Should().BeTrue(result.Error);
        var content = await LoadAsync(tenant, id);
        content.Data["Title"].ToString().Should().Be("second");
        content.Data["InvoiceNo"].ToString().Should().Be("INV-0001");
    }
}
