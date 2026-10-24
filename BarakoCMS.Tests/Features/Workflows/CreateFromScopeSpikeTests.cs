using barakoCMS.Infrastructure.Multitenancy;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BarakoCMS.Tests.Features.Workflows;

/// <summary>Staging an entry from a tenant scope with no HttpContext numbers it and sets its initial state.</summary>
[Collection("Integration1")]
public class CreateFromScopeSpikeTests(IntegrationTestFixture fixture)
{
    [Fact]
    public async Task Stage_in_a_tenant_scope_numbers_and_sets_the_initial_state()
    {
        const string tenant = "spike-create-scope";
        var ct = TestContext.Current.CancellationToken;
        var store = fixture.Services.GetRequiredService<IDocumentStore>();
        await using (var seed = store.LightweightSession(tenant))
        {
            seed.Store(new ContentTypeDefinition
            {
                Id = Guid.NewGuid(),
                Name = "SpikeTicket",
                DisplayName = "Spike ticket",
                Fields = [new FieldDefinition { Name = "No", DisplayName = "No", Type = "string" }],
                Numbering = new NumberingRule { Field = "No", Prefix = "T-", Padding = 3 },
                Lifecycle = new LifecycleDefinition { States = ["Waiting", "Done"], InitialState = "Waiting" },
            });
            await seed.SaveChangesAsync(ct);
        }

        using var scope = fixture.Services.CreateScopeForTenant(tenant);
        var creator = scope.ServiceProvider.GetRequiredService<IContentCreator>();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();

        var created = await creator.StageAsync(
            new ContentCreateRequest { ContentType = "SpikeTicket", Data = new() }, Guid.Empty, null, ct);
        await session.SaveChangesAsync(ct);

        created.LifecycleState.Should().Be("Waiting");
        created.Data["No"].ToString().Should().Be("T-001");
    }
}
