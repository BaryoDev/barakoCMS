using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>Proves conjoined multi-tenancy isolates documents: one tenant cannot see another's data.</summary>
[Collection("Sequential")]
public class TenantIsolationTests
{
    private readonly IntegrationTestFixture _factory;

    public TenantIsolationTests(IntegrationTestFixture factory) => _factory = factory;

    [Fact]
    public async Task content_is_isolated_between_tenants()
    {
        var store = _factory.Services.GetRequiredService<IDocumentStore>();
        var typeA = $"a_{Guid.NewGuid():N}";
        var typeB = $"b_{Guid.NewGuid():N}";

        Guid idA, idB;
        await using (var sA = store.LightweightSession("tenant-a"))
        {
            var c = new Content { Id = Guid.NewGuid(), ContentType = typeA, Data = new() { ["v"] = "A" } };
            sA.Store(c);
            await sA.SaveChangesAsync();
            idA = c.Id;
        }
        await using (var sB = store.LightweightSession("tenant-b"))
        {
            var c = new Content { Id = Guid.NewGuid(), ContentType = typeB, Data = new() { ["v"] = "B" } };
            sB.Store(c);
            await sB.SaveChangesAsync();
            idB = c.Id;
        }

        // tenant-a sees its own doc, not tenant-b's — by id or by query.
        await using (var sA = store.QuerySession("tenant-a"))
        {
            (await sA.LoadAsync<Content>(idA)).Should().NotBeNull();
            (await sA.LoadAsync<Content>(idB)).Should().BeNull("tenant-a must not load tenant-b's document");
            (await sA.Query<Content>().Where(x => x.ContentType == typeB).ToListAsync())
                .Should().BeEmpty("tenant-a must not query tenant-b's documents");
        }

        // tenant-b sees only its own.
        await using (var sB = store.QuerySession("tenant-b"))
        {
            (await sB.LoadAsync<Content>(idB)).Should().NotBeNull();
            (await sB.LoadAsync<Content>(idA)).Should().BeNull("tenant-b must not load tenant-a's document");
        }
    }

    [Fact]
    public async Task users_are_global_across_tenants()
    {
        // Users are SingleTenanted (global identity): visible regardless of the session's tenant.
        var store = _factory.Services.GetRequiredService<IDocumentStore>();
        var user = new User { Id = Guid.NewGuid(), Username = $"u_{Guid.NewGuid():N}", Email = $"{Guid.NewGuid():N}@e.com" };
        await using (var s = store.LightweightSession("tenant-a"))
        {
            s.Store(user);
            await s.SaveChangesAsync();
        }
        await using (var s = store.QuerySession("tenant-b"))
        {
            (await s.LoadAsync<User>(user.Id)).Should().NotBeNull("a global user is visible from any tenant");
        }
    }
}
