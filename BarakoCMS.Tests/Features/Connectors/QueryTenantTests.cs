using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using barakoCMS.Core.Interfaces;
using barakoCMS.Infrastructure.Connectors;
using barakoCMS.Infrastructure.Services;
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

    [Fact]
    public async Task A_reference_that_points_at_another_tenants_entry_gives_no_value_and_does_not_refuse_the_run()
    {
        var store = factory.Services.GetRequiredService<IDocumentStore>();
        var a = Tenant();
        var b = Tenant();
        var lines = TypeName();
        var products = TypeName();

        // The same type names in both tenants. The product is only in tenant B.
        var inB = Entry(products, new() { ["Name"] = "Other tenant kettle" });
        var line = Entry(lines, new() { ["Product"] = inB.Id.ToString(), ["Qty"] = "2" });
        var reference = new FieldDefinition { Name = "Product", DisplayName = "Product", Type = "reference", ReferenceType = products };
        await SeedAsync(store, a, Type(products, Field("Name")), [], Ct);
        await SeedAsync(store, a, Type(lines, reference, Field("Qty")), [line], Ct);
        await SeedAsync(store, b, Type(products, Field("Name")), [inB], Ct);

        // The user and the role are in both the tenant and the default tenant, whichever one a service reads them from.
        var role = new Role
        {
            Id = Guid.NewGuid(), Name = "qt" + Guid.NewGuid().ToString("n")[..10],
            Permissions = [.. new[] { lines, products }.Select(t => new ContentTypePermission
            {
                ContentTypeSlug = t, Read = new PermissionRule { Enabled = true },
            })],
        };
        var user = new User { Id = Guid.NewGuid(), Username = role.Name, Email = role.Name + "@example.com", RoleIds = [role.Id] };
        foreach (var session in new[] { store.LightweightSession(a), store.LightweightSession() })
        {
            await using var _ = session;
            session.Store(role);
            session.Store(user);
            await session.SaveChangesAsync(Ct);
        }

        using var scope = factory.Services.CreateScope();
        await using var read = store.QuerySession(a);
        var result = await new QueryRunner(
                read,
                scope.ServiceProvider.GetRequiredService<IPermissionResolver>(),
                scope.ServiceProvider.GetRequiredService<ISensitivityService>())
            .RunAsync(
                new QueryDefinition { ContentType = lines, Fields = ["Qty", "Product.Name"] },
                AsActor(user.Id), Ct);

        result.Ok.Should().BeTrue(result.Refusal);
        result.Rows.Should().ContainSingle();
        result.Rows[0].Should().NotContainKey("Product.Name", "the entry is in another tenant, so it is not there");
    }
}
