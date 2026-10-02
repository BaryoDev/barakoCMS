using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using barakoCMS.Models;
using BarakoCMS.Portability;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BarakoCMS.Tests.Features.Portability;

/// <summary>
/// A bundle names the roles a field lists, and a stored definition lists them by id.
/// </summary>
/// <remarks>
/// A role id means nothing to the instance a bundle is imported into, so an export writes names and
/// an import turns the names it can match back into ids. Without the first half a bundle carried
/// ids that matched nobody where it landed. Without the second, an imported field went on listing
/// names, and a bundle naming the roles a stored field lists by id was refused as a change to who
/// may read it.
/// </remarks>
[Collection("Sequential")]
public class RoleReferencePortabilityTests
{
    private readonly IntegrationTestFixture _fixture;

    public RoleReferencePortabilityTests(IntegrationTestFixture fixture) => _fixture = fixture;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string NewType() => ("rolref" + Guid.NewGuid().ToString("n"))[..14];

    private async Task<HttpClient> AdminAsync()
    {
        var client = _fixture.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", await _fixture.StoredUserTokenAsync("SuperAdmin", "Admin"));
        return client;
    }

    private async Task<Role> StoreRoleAsync()
    {
        var role = new Role { Id = Guid.NewGuid(), Name = $"Payroll {Guid.NewGuid():N}" };
        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Store(role);
        await session.SaveChangesAsync(Ct);
        return role;
    }

    private static ContentTypeDefinition Staff(string type, params string[] visibleTo) => new()
    {
        Name = type,
        DisplayName = "Staff",
        Fields =
        [
            new FieldDefinition { Name = "Name", DisplayName = "Name", Type = "string" },
            new FieldDefinition
            {
                Name = "Salary",
                DisplayName = "Salary",
                Type = "string",
                Sensitivity = SensitivityLevel.Sensitive,
                Mask = FieldMask.Redact,
                VisibleToRoles = visibleTo.ToList(),
            },
        ],
    };

    private async Task StoreTypeAsync(ContentTypeDefinition definition)
    {
        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        definition.Id = Guid.NewGuid();
        session.Store(definition);
        await session.SaveChangesAsync(Ct);
    }

    private async Task<List<string>> StoredSalaryRolesAsync(string type)
    {
        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        var definition = await session.Query<ContentTypeDefinition>().FirstOrDefaultAsync(d => d.Name == type, Ct);
        definition.Should().NotBeNull("the type {0} is stored", type);
        return definition!.Fields.Single(f => f.Name == "Salary").VisibleToRoles;
    }

    private static async Task<(HttpStatusCode Status, string Body)> ImportAsync(
        HttpClient client, ContentTypeDefinition type)
    {
        var response = await client.PostAsJsonAsync("/api/portability/import", new
        {
            dryRun = false,
            contentTypes = new[] { type },
            contents = Array.Empty<object>(),
        }, Ct);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task An_export_names_the_role_a_stored_field_lists_by_id()
    {
        var role = await StoreRoleAsync();
        var gone = Guid.NewGuid().ToString();
        var type = NewType();
        await StoreTypeAsync(Staff(type, role.Id.ToString(), gone));

        var response = await (await AdminAsync()).GetAsync($"/api/portability/export?types={type}", Ct);
        var raw = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK, raw);
        var bundle = await response.Content.ReadFromJsonAsync<PortabilityBundle>(ApiJson.Options, Ct);

        var exported = bundle!.ContentTypes.Should().ContainSingle().Subject;
        var listed = exported.Fields.Single(f => f.Name == "Salary").VisibleToRoles;
        var expected = new[] { role.Name, gone };
        listed.Should().HaveCount(2);
        listed.Should().Equal(expected, "an id that names a role goes out as its name, and one that names none is kept");

        (await StoredSalaryRolesAsync(type)).Should().Equal(
            new[] { role.Id.ToString(), gone }, "exporting does not rewrite what is stored");
    }

    [Fact]
    public async Task Importing_a_type_that_names_a_role_stores_that_roles_id()
    {
        var role = await StoreRoleAsync();
        var nobody = $"Nobody {Guid.NewGuid():N}";
        var type = NewType();

        var (status, body) = await ImportAsync(await AdminAsync(), Staff(type, role.Name, nobody));

        status.Should().Be(HttpStatusCode.OK, body);
        var stored = await StoredSalaryRolesAsync(type);
        var expected = new[] { role.Id.ToString(), nobody };
        stored.Should().HaveCount(2);
        stored.Should().Equal(expected, "a name a role carries is stored as its id, and a name no role carries is kept");
    }

    [Fact]
    public async Task A_bundle_naming_the_role_a_stored_field_lists_by_id_is_not_refused_as_a_change()
    {
        var role = await StoreRoleAsync();
        var type = NewType();
        await StoreTypeAsync(Staff(type, role.Id.ToString()));

        var (status, body) = await ImportAsync(await AdminAsync(), Staff(type, role.Name));

        status.Should().Be(HttpStatusCode.OK, body);
        var stored = await StoredSalaryRolesAsync(type);
        stored.Should().HaveCount(1);
        stored.Should().Equal(role.Id.ToString());
    }

    [Fact]
    public async Task A_bundle_listing_a_different_role_from_the_stored_field_is_still_refused()
    {
        var role = await StoreRoleAsync();
        var other = await StoreRoleAsync();
        var type = NewType();
        await StoreTypeAsync(Staff(type, role.Id.ToString()));

        var (status, body) = await ImportAsync(await AdminAsync(), Staff(type, other.Name));

        status.Should().Be(HttpStatusCode.BadRequest, body);
        body.Should().Contain("/sensitivity", "an import does not change who may read a field");
        var stored = await StoredSalaryRolesAsync(type);
        stored.Should().HaveCount(1);
        stored.Should().Equal(role.Id.ToString());
    }
}
