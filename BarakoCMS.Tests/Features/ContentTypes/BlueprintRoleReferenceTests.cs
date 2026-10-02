using System.Net;
using System.Net.Http.Headers;
using FluentAssertions;
using Marten;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using barakoCMS.Models;

namespace BarakoCMS.Tests.Features.ContentTypes;

/// <summary>
/// Applying a blueprint stores a field's role list the way every other writer of a content type
/// does: as role ids.
/// </summary>
/// <remarks>
/// A blueprint file names roles. Stored as written, the list went on naming them, so renaming the
/// role changed who read the field, and the one-time migration never reached a type applied after
/// it ran.
///
/// The apply runs in a tenant made for the test, for the reason <see cref="ContentTypeBlueprintTests"/>
/// gives.
/// </remarks>
[Collection("Sequential")]
public class BlueprintRoleReferenceTests
{
    private readonly IntegrationTestFixture _factory;

    public BlueprintRoleReferenceTests(IntegrationTestFixture factory) => _factory = factory;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Applying_a_blueprint_stores_a_listed_role_name_as_that_roles_id()
    {
        var tag = Guid.NewGuid().ToString("N")[..8];
        var blueprint = $"staffing-{tag}";
        var typeName = $"staff-{tag}";
        var nobody = $"Nobody {Guid.NewGuid():N}";
        var role = new Role { Id = Guid.NewGuid(), Name = $"Payroll {Guid.NewGuid():N}" };
        var tenant = $"bpr-{Guid.NewGuid():N}"[..14];
        var userId = Guid.NewGuid();

        using (var scope = _factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Store(role);
            session.Store(new Tenant { Id = Guid.NewGuid(), Slug = tenant, Name = tenant, IsActive = true });
            session.Store(new User
            {
                Id = userId,
                Username = $"bpr-{userId:n}",
                Email = $"bpr-{userId:n}@example.com",
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

        var dir = Path.Combine(Path.GetTempPath(), $"barako-blueprints-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, $"{blueprint}.json"), $$"""
            {
              "name": "{{blueprint}}",
              "description": "Staff, with a salary only payroll reads.",
              "contentTypes": [
                {
                  "name": "{{typeName}}",
                  "displayName": "Staff",
                  "fields": [
                    { "name": "Name", "displayName": "Name", "type": "string", "isRequired": true },
                    {
                      "name": "Salary", "displayName": "Salary", "type": "string",
                      "sensitivity": "Sensitive", "visibleToRoles": ["{{role.Name}}", "{{nobody}}"]
                    }
                  ]
                }
              ]
            }
            """, Ct);

        // Not disposed, as IntegrationTestFixture.WithSetting says.
        WebApplicationFactory<Program> host = _factory.WithSetting("Blueprints:Path", dir);
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", _factory.CreateToken(
                roles: ["SuperAdmin"],
                userId: userId.ToString(),
                additionalClaims: new Dictionary<string, string> { ["tenant"] = tenant }));
        client.DefaultRequestHeaders.Add("X-Tenant", tenant);
        client.DefaultRequestHeaders.Add(
            TestRemoteIpFilter.Header, $"10.11.{Random.Shared.Next(1, 250)}.{Random.Shared.Next(1, 250)}");

        var applied = await client.PostAsync($"/api/content-types/blueprints/{blueprint}", null, Ct);
        applied.StatusCode.Should().Be(HttpStatusCode.OK, await applied.Content.ReadAsStringAsync(Ct));

        await using var read = _factory.Services.GetRequiredService<IDocumentStore>().QuerySession(tenant);
        var stored = await read.Query<ContentTypeDefinition>().FirstOrDefaultAsync(d => d.Name == typeName, Ct);
        stored.Should().NotBeNull("the blueprint's type is stored in the tenant it was applied in");

        var listed = stored!.Fields.Single(f => f.Name == "Salary").VisibleToRoles;
        var expected = new[] { role.Id.ToString(), nobody };
        listed.Should().HaveCount(2);
        listed.Should().Equal(
            expected, "a name a role carries is stored as that role's id, and a name no role carries is kept");
    }
}
