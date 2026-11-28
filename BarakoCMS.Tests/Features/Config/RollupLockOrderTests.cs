using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Marten;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using barakoCMS.Infrastructure.Jobs;
using barakoCMS.Infrastructure.Multitenancy;
using barakoCMS.Models;
using R = BarakoCMS.Tests.RollupProbe;
using static BarakoCMS.Tests.Features.Config.ConfigCallers;

namespace BarakoCMS.Tests.Features.Config;

/// <summary>
/// A save of types whose rollups read other types takes every type lock it needs in one sorted
/// pass, so two saves whose types roll up over each other cannot wait on each other.
/// </summary>
/// <remarks>
/// The main tests are not races. Another session holds the lock of the type the save rolls up over,
/// standing in for the other save. The save is started and waits on that lock. With every lock
/// asked for in one sorted pass, the save asked for the source first and holds nothing else, so the
/// type it writes is still free. Asked for one at a time, it held that type exclusive before it
/// asked for the source, and the other save would wait on it for good. Names are chosen so the
/// source sorts before the type that rolls up over it. The last test starts two such saves together
/// as a check that nothing in the whole path undoes the order. Nothing sleeps: the one wait is a
/// bounded poll of pg_locks for the queued save.
/// </remarks>
[Collection("Integration1")]
public class RollupLockOrderTests(IntegrationTestFixture fixture)
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(30);

    private const int Rounds = 6;

    private readonly ConfigCallers _callers = new(fixture);

    private IDocumentStore Store => fixture.Services.GetRequiredService<IDocumentStore>();

    private static string Tag() => Guid.NewGuid().ToString("n")[..10];

    /// <summary>A type with a Name and the fields given.</summary>
    private static ContentTypeDefinition TypeOf(string name, params FieldDefinition[] fields) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        DisplayName = name,
        Fields = [R.Plain("Name", "string"), .. fields],
    };

    /// <summary>A header that counts the entries of <paramref name="line"/> that point at it through Owner.</summary>
    private static FieldDefinition CountOf(string line) => R.Rollup("Visits", "int", line, "Owner", "count");

    private static object Document(params ContentTypeDefinition[] types) =>
        new { version = 1, contentTypes = types.ToDictionary(t => t.Name, t => (object)t) };

    /// <summary>Holds the exclusive lock of a type in a tenant, as another save of it does, until disposed.</summary>
    private async Task<IDisposable> HoldExclusiveAsync(string tenant, string type)
    {
        var scope = fixture.Services.CreateScopeForTenant(tenant);
        await FieldChanges.LockTypeAsync(scope.ServiceProvider.GetRequiredService<IDocumentSession>(), type, Ct);
        return scope;
    }

    private string KeyFor(string tenant, string type)
    {
        using var scope = fixture.Services.CreateScopeForTenant(tenant);
        return $"barakocms:field-change:{scope.ServiceProvider.GetRequiredService<IDocumentSession>().TenantId}:{type.ToLowerInvariant()}";
    }

    /// <summary>How many sessions are queued for the type's lock.</summary>
    private async Task<long> QueuedAsync(string tenant, string type)
    {
        await using var connection = Store.Storage.Database.CreateConnection();
        await connection.OpenAsync(Ct);
        await using var count = connection.CreateCommand();
        count.CommandText = """
            select count(*) from pg_locks
            where locktype = 'advisory' and objsubid = 1 and granted = false
              and classid = ((hashtextextended(@name, 0) >> 32) & 4294967295)::oid
              and objid = (hashtextextended(@name, 0) & 4294967295)::oid
            """;
        count.Parameters.AddWithValue("name", KeyFor(tenant, type));
        return (long)(await count.ExecuteScalarAsync(Ct))!;
    }

    private async Task WaitForQueuedAsync(string tenant, string type)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        limit.CancelAfter(Limit);
        while (await QueuedAsync(tenant, type) < 1)
        {
            limit.Token.ThrowIfCancellationRequested();
            await Task.Yield();
        }
    }

    /// <summary>Whether a second session could take the exclusive lock of the type now.</summary>
    private async Task<bool> CanTakeExclusiveAsync(string tenant, string type)
    {
        await using var connection = Store.Storage.Database.CreateConnection();
        await connection.OpenAsync(Ct);
        await using var transaction = await connection.BeginTransactionAsync(Ct);
        await using var take = connection.CreateCommand();
        take.Transaction = transaction;
        take.CommandText = "select pg_try_advisory_xact_lock(hashtextextended(@name, 0))";
        take.Parameters.AddWithValue("name", KeyFor(tenant, type));
        return (bool)(await take.ExecuteScalarAsync(Ct))!;
    }

    /// <summary>A tenant holding a line type that points at <paramref name="header"/>, and its name, which sorts before the header's.</summary>
    private async Task<string> StoreLineAsync(string tenant, string header)
    {
        var line = "a" + Tag();
        await _callers.StoreInTenantAsync(tenant, TypeOf(line, R.Reference("Owner", header)));
        return line;
    }

    [Fact]
    public async Task Config_apply_asks_for_a_rollup_source_before_it_holds_the_type_that_rolls_up_over_it()
    {
        var tenant = await _callers.TenantAsync();
        var header = "x" + Tag();
        var line = await StoreLineAsync(tenant, header);
        var client = await _callers.SuperAdminAsync(tenant);

        Task<(HttpResponseMessage Response, System.Text.Json.JsonElement Body)> apply;
        using (await HoldExclusiveAsync(tenant, line))
        {
            apply = Task.Run(() => ApplyAsync(client, Document(TypeOf(header, CountOf(line)))), Ct);
            await WaitForQueuedAsync(tenant, line);

            (await CanTakeExclusiveAsync(tenant, header)).Should().BeTrue(
                "the apply asked for the source first, so it holds nothing the other save would wait on");
        }

        var (response, body) = await apply.WaitAsync(Limit, Ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK, body.ToString());
        (await _callers.InTenantAsync(tenant, s => s.Query<ContentTypeDefinition>().AnyAsync(t => t.Name == header, Ct)))
            .Should().BeTrue("the apply went on once the source was free");
    }

    [Fact]
    public async Task Blueprint_apply_asks_for_a_rollup_source_before_it_holds_the_type_that_rolls_up_over_it()
    {
        var tenant = await _callers.TenantAsync();
        var header = "x" + Tag();
        var line = await StoreLineAsync(tenant, header);
        var blueprint = "locks-" + Tag();

        var dir = Path.Combine(Path.GetTempPath(), $"barako-blueprints-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, $"{blueprint}.json"), $$"""
            {
              "name": "{{blueprint}}",
              "contentTypes": [
                {
                  "name": "{{header}}",
                  "displayName": "Header",
                  "fields": [
                    { "name": "Name", "displayName": "Name", "type": "string" },
                    { "name": "Visits", "displayName": "Visits", "type": "int", "rollup": { "from": "{{line}}", "via": "Owner", "fn": "count" } }
                  ]
                }
              ]
            }
            """, Ct);

        // Not disposed, as IntegrationTestFixture.WithSetting says.
        WebApplicationFactory<Program> host = fixture.WithSetting("Blueprints:Path", dir);
        var client = await HostClientAsync(host, tenant);

        Task<HttpResponseMessage> apply;
        using (await HoldExclusiveAsync(tenant, line))
        {
            apply = Task.Run(() => client.PostAsync($"/api/content-types/blueprints/{blueprint}", null, Ct), Ct);
            await WaitForQueuedAsync(tenant, line);

            (await CanTakeExclusiveAsync(tenant, header)).Should().BeTrue(
                "the apply asked for the source first, so it holds nothing the other save would wait on");
        }

        var response = await apply.WaitAsync(Limit, Ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task Changing_the_sensitivity_of_a_rollup_asks_for_its_source_before_it_holds_the_type()
    {
        var tenant = await _callers.TenantAsync();
        var header = "x" + Tag();
        var line = await StoreLineAsync(tenant, header);
        await _callers.StoreInTenantAsync(tenant, TypeOf(header, CountOf(line)));
        var client = await _callers.SuperAdminAsync(tenant);

        Task<HttpResponseMessage> change;
        using (await HoldExclusiveAsync(tenant, line))
        {
            change = Task.Run(() => client.PutAsJsonAsync(
                $"/api/content-types/{header}/fields/Visits/sensitivity", new { sensitivity = "Sensitive" }, Ct), Ct);
            await WaitForQueuedAsync(tenant, line);

            (await CanTakeExclusiveAsync(tenant, header)).Should().BeTrue(
                "the change asked for the source first, so it holds nothing the other save would wait on");
        }

        var response = await change.WaitAsync(Limit, Ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task Two_config_applies_whose_types_roll_up_over_each_other_never_deadlock()
    {
        for (var round = 0; round < Rounds; round++)
        {
            var tenant = await _callers.TenantAsync();
            var a = "a" + Tag();
            var x = "x" + Tag();
            await _callers.StoreInTenantAsync(tenant,
                TypeOf(a, R.Reference("RefX", x)),
                TypeOf(x, R.Reference("RefA", a)));

            // Each document rolls its type up over the other, which the other document does not name.
            var documentA = Document(TypeOf(a, R.Reference("RefX", x), R.Rollup("Total", "int", x, "RefA", "count")));
            var documentX = Document(TypeOf(x, R.Reference("RefA", a), R.Rollup("Total", "int", a, "RefX", "count")));
            var clientA = await _callers.SuperAdminAsync(tenant);
            var clientX = await _callers.SuperAdminAsync(tenant);

            using var barrier = new Barrier(2);
            var first = Task.Run(() => { barrier.SignalAndWait(Limit, Ct); return ApplyAsync(clientA, documentA); }, Ct);
            var second = Task.Run(() => { barrier.SignalAndWait(Limit, Ct); return ApplyAsync(clientX, documentX); }, Ct);
            var results = await Task.WhenAll(first, second).WaitAsync(Limit, Ct);

            foreach (var (response, body) in results)
            {
                ((int)response.StatusCode).Should().BeLessThan(500, body.ToString());
                body.ToString().Should().NotContain("40P01").And.NotContain("deadlock");
            }

            results.Should().Contain(r => r.Response.StatusCode == HttpStatusCode.OK,
                "two saves that do not break each other both go through, or at least one does");
        }
    }

    /// <summary>A SuperAdmin of the tenant talking to the host given.</summary>
    private async Task<HttpClient> HostClientAsync(WebApplicationFactory<Program> host, string tenant)
    {
        var userId = Guid.NewGuid();
        await StoreAdminAsync(userId, tenant);

        var client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", fixture.CreateToken(
                roles: ["SuperAdmin"],
                userId: userId.ToString(),
                additionalClaims: new Dictionary<string, string> { ["tenant"] = tenant }));
        client.DefaultRequestHeaders.Add("X-Tenant", tenant);
        client.DefaultRequestHeaders.Add(TestRemoteIpFilter.Header, $"10.93.{Random.Shared.Next(1, 250)}.{Random.Shared.Next(1, 250)}");
        return client;
    }

    private async Task StoreAdminAsync(Guid userId, string tenant)
    {
        using var scope = fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Store(new User
        {
            Id = userId,
            Username = $"rlo-{userId:n}"[..14],
            Email = $"rlo-{userId:n}@example.com",
            RoleIds = [SystemRoles.SuperAdminRoleId],
        });
        session.Store(new Membership
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            TenantSlug = tenant,
            Status = MembershipStatus.Active,
            RoleIds = [SystemRoles.SuperAdminRoleId],
        });
        await session.SaveChangesAsync(Ct);
    }
}
