using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using barakoCMS.Models;

namespace BarakoCMS.Tests;

/// <summary>
/// A rollback is an update, so it runs the gates an update runs.
/// </summary>
/// <remarks>
/// It used to run none of them. Restoring an old version wrote the historical dictionary straight
/// into a new event, so it could put back data the current schema rejects, change a field the caller
/// is not allowed to change, or break an invariant introduced after the version being restored.
///
/// The awkward consequence is deliberate: an operator can be refused a rollback for a reason that
/// predates them. That beats a write path which launders rejected data back in, reachable by anyone
/// who can press Restore.
/// </remarks>
[Collection("Sequential")]
public class RollbackGatesTests
{
    private readonly IntegrationTestFixture _factory;

    public RollbackGatesTests(IntegrationTestFixture factory) => _factory = factory;

    private async Task<HttpClient> AdminAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var s = scope.ServiceProvider.GetRequiredService<IDocumentSession>();

        var roleIds = new List<Guid>();
        foreach (var name in new[] { "SuperAdmin", "Admin" })
        {
            var role = await s.Query<Role>().FirstOrDefaultAsync(r => r.Name == name);
            if (role is null) { role = new Role { Id = Guid.NewGuid(), Name = name }; s.Store(role); }
            roleIds.Add(role.Id);
        }

        var userId = Guid.NewGuid();
        s.Store(new User
        {
            Id = userId,
            Username = $"rb_{Guid.NewGuid():n}",
            Email = $"rb_{Guid.NewGuid():n}@example.com",
            RoleIds = roleIds,
        });
        await s.SaveChangesAsync();

        var c = _factory.CreateClient();
        c.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer", _factory.CreateToken(roles: ["SuperAdmin", "Admin"], userId: userId.ToString()));
        return c;
    }

    /// <summary>
    /// A version that the current schema would reject cannot be restored.
    /// </summary>
    /// <remarks>
    /// The schema tightens after the version was written, which is the realistic case: a field gains
    /// a type or becomes required, and an old row no longer satisfies it. Restoring it used to
    /// succeed and put the rejected shape back, so the only route that could produce that data was
    /// the one nobody validates.
    /// </remarks>
    [Fact]
    public async Task A_version_the_current_schema_rejects_cannot_be_restored()
    {
        var client = await AdminAsync();
        var type = $"rbtype_{Guid.NewGuid():n}"[..16];

        using (var scope = _factory.Services.CreateScope())
        {
            var s = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            s.Store(new ContentTypeDefinition
            {
                Id = Guid.NewGuid(), Name = type, DisplayName = type,
                Fields = [new FieldDefinition { Name = "Count", DisplayName = "Count", Type = "string" }],
            });
            await s.SaveChangesAsync();
        }

        var created = await client.PostAsJsonAsync("/api/contents", new
        {
            contentType = type,
            data = new Dictionary<string, object> { ["Count"] = "not a number" },
        });
        created.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}",
            created.StatusCode, await created.Content.ReadAsStringAsync());
        using var doc = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var id = doc.RootElement.GetProperty("id").GetGuid();

        // The schema tightens: Count is numeric from now on.
        using (var scope = _factory.Services.CreateScope())
        {
            var s = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            var def = await s.Query<ContentTypeDefinition>().FirstAsync(d => d.Name == type);
            def.Fields[0].Type = "int";
            s.Store(def);
            await s.SaveChangesAsync();
        }

        var versionId = await FirstVersionIdAsync(client, id);

        var rollback = await client.PostAsync($"/api/contents/{id}/rollback/{versionId}", null);

        rollback.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "restoring it would write a value the schema rejects, through the one path that used to "
          + "skip validation entirely");
        (await rollback.Content.ReadAsStringAsync()).Should().Contain("cannot be restored");
    }

    /// <summary>
    /// The control, and the half that makes the test above mean anything.
    /// </summary>
    /// <remarks>
    /// Without it, an endpoint that refused every rollback would satisfy the assertion above while
    /// removing the feature. This project has shipped that shape of gate before.
    /// </remarks>
    [Fact]
    public async Task A_version_the_schema_still_accepts_restores_normally()
    {
        var client = await AdminAsync();
        var type = $"rbok_{Guid.NewGuid():n}"[..16];

        using (var scope = _factory.Services.CreateScope())
        {
            var s = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            s.Store(new ContentTypeDefinition
            {
                Id = Guid.NewGuid(), Name = type, DisplayName = type,
                Fields = [new FieldDefinition { Name = "Title", DisplayName = "Title", Type = "string" }],
            });
            await s.SaveChangesAsync();
        }

        var created = await client.PostAsJsonAsync("/api/contents", new
        {
            contentType = type,
            data = new Dictionary<string, object> { ["Title"] = "first" },
        });
        created.IsSuccessStatusCode.Should().BeTrue();
        using var doc = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var id = doc.RootElement.GetProperty("id").GetGuid();

        var update = await client.PutAsJsonAsync($"/api/contents/{id}", new
        {
            data = new Dictionary<string, object> { ["Title"] = "second" },
        });
        update.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}",
            update.StatusCode, await update.Content.ReadAsStringAsync());

        var versionId = await FirstVersionIdAsync(client, id);
        var rollback = await client.PostAsync($"/api/contents/{id}/rollback/{versionId}", null);

        rollback.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}",
            rollback.StatusCode, await rollback.Content.ReadAsStringAsync());

        using var scope2 = _factory.Services.CreateScope();
        var q = scope2.ServiceProvider.GetRequiredService<IQuerySession>();
        var content = await q.LoadAsync<Content>(id);
        content!.Data["Title"].ToString().Should().Be("first", "the rollback actually took effect");
    }

    /// <summary>
    /// The fourth gate: a rollback needs the update permission on the content type, not just a role
    /// name on the route.
    /// </summary>
    /// <remarks>
    /// The route gates on Roles("SuperAdmin", "Admin") and the handler ran sensitivity, validation
    /// and lifecycle hooks, which is why the comment there claimed parity with an update. An update
    /// runs a fourth gate this did not: IPermissionResolver.CanPerformActionAsync(..., "update").
    ///
    /// So an Admin with no update grant on a type could rewrite an entry of that type by restoring
    /// it, and the second assertion is the part that makes it indefensible rather than merely
    /// inconsistent: the same caller is refused the history that lists what there is to restore. A
    /// write they can perform, over a read they cannot. See #447.
    ///
    /// The seeded Admin role carries no Permissions, so an Admin-only principal is exactly the
    /// pre-upgrade state a real deployment is in, not a contrivance built for this test.
    /// </remarks>
    [Fact]
    public async Task An_admin_without_update_permission_cannot_roll_back()
    {
        var admin = await AdminAsync();
        var type = $"rbperm_{Guid.NewGuid():n}"[..16];

        using (var scope = _factory.Services.CreateScope())
        {
            var s = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            s.Store(new ContentTypeDefinition
            {
                Id = Guid.NewGuid(), Name = type, DisplayName = type,
                Fields = [new FieldDefinition { Name = "Title", DisplayName = "Title", Type = "string" }],
            });
            await s.SaveChangesAsync();
        }

        var created = await admin.PostAsJsonAsync("/api/contents", new
        {
            contentType = type,
            data = new Dictionary<string, object> { ["Title"] = "first" },
        });
        created.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}",
            created.StatusCode, await created.Content.ReadAsStringAsync());
        using var doc = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var id = doc.RootElement.GetProperty("id").GetGuid();

        var updated = await admin.PutAsJsonAsync($"/api/contents/{id}", new
        {
            data = new Dictionary<string, object> { ["Title"] = "second" },
        });
        updated.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}",
            updated.StatusCode, await updated.Content.ReadAsStringAsync());

        // Read through the privileged client, because the whole point is that the other one cannot.
        var versionId = await FirstVersionIdAsync(admin, id);

        var limited = await AdminOnlyAsync();

        var rollback = await limited.PostAsync($"/api/contents/{id}/rollback/{versionId}", null);
        rollback.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "the route's role gate lets an Admin in, but rollback is an update and this role grants "
          + "no update on {0}", type);

        var history = await limited.GetAsync($"/api/contents/{id}/history");
        history.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "and the same caller cannot read the history, which is what made the write it used to "
          + "be allowed a strictly larger grant than the read it was refused");

        using (var scope = _factory.Services.CreateScope())
        {
            var q = scope.ServiceProvider.GetRequiredService<IQuerySession>();
            var content = await q.LoadAsync<Content>(id);
            content!.Data["Title"].ToString().Should().Be("second",
                "the refused rollback must not have taken effect");
        }

        // The control. Without it an endpoint that refused every rollback would satisfy the above.
        var allowed = await admin.PostAsync($"/api/contents/{id}/rollback/{versionId}", null);
        allowed.IsSuccessStatusCode.Should().BeTrue(
            "a caller who does hold the permission still restores normally; got {0}: {1}",
            allowed.StatusCode, await allowed.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// A principal holding the seeded Admin role and nothing else.
    /// </summary>
    /// <remarks>
    /// Admin passes the route's role gate and carries no Permissions, so the resolver refuses it.
    /// SuperAdmin is deliberately absent: PermissionResolver bypasses every check for that name, so
    /// including it would make the permission gate untestable.
    /// </remarks>
    private async Task<HttpClient> AdminOnlyAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var s = scope.ServiceProvider.GetRequiredService<IDocumentSession>();

        var role = await s.Query<Role>().FirstOrDefaultAsync(r => r.Name == "Admin");
        if (role is null) { role = new Role { Id = Guid.NewGuid(), Name = "Admin" }; s.Store(role); }

        var userId = Guid.NewGuid();
        s.Store(new User
        {
            Id = userId,
            Username = $"rbperm_{Guid.NewGuid():n}",
            Email = $"rbperm_{Guid.NewGuid():n}@example.com",
            RoleIds = [role.Id],
        });
        await s.SaveChangesAsync();

        var c = _factory.CreateClient();
        c.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer", _factory.CreateToken(roles: ["Admin"], userId: userId.ToString()));
        return c;
    }

    private static async Task<Guid> FirstVersionIdAsync(HttpClient client, Guid id)
    {
        var body = await (await client.GetAsync($"/api/contents/{id}/history")).Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("items")[0].GetProperty("versionId").GetGuid();
    }
}
