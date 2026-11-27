using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using barakoCMS.Infrastructure.Connectors;
using barakoCMS.Models;
using static BarakoCMS.Tests.Features.Connectors.QueryKit;

namespace BarakoCMS.Tests.Features.Connectors;

/// <summary>A run reads the tenant of the session it was given, and no other.</summary>
[Collection("Integration1")]
public class QueryTenantTests(IntegrationTestFixture factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_run_in_one_tenant_never_returns_another_tenants_rows()
    {
        var store = factory.Services.GetRequiredService<IDocumentStore>();
        var a = Tenant();
        var b = Tenant();
        var type = TypeName();
        var inA = Entry(type, new() { ["Name"] = "same" });
        var inB = Entry(type, new() { ["Name"] = "same" });
        await SeedAsync(store, a, Type(type, Field("Name")), [inA], Ct);
        await SeedAsync(store, b, Type(type, Field("Name")), [inB], Ct);

        var definition = new QueryDefinition
        {
            ContentType = type, Fields = ["id", "Name"], Filters = [WhereParam("Name", "eq", "data.Name")],
        };

        await using var sessionA = store.QuerySession(a);
        var fromA = await new QueryRunner(sessionA).RunAsync(definition, new QueryRunContext(Values(("data.Name", "same"))), Ct);
        fromA.Ids.Should().Equal(inA.Id);
        fromA.Rows.Should().ContainSingle().Which["id"].Should().Be(inA.Id.ToString());

        await using var sessionB = store.QuerySession(b);
        (await new QueryRunner(sessionB).RunAsync(definition, new QueryRunContext(Values(("data.Name", "same"))), Ct))
            .Ids.Should().Equal(inB.Id);
    }
}
