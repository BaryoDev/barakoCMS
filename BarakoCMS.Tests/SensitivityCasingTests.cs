using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using barakoCMS.Models;

namespace BarakoCMS.Tests;

/// <summary>
/// A field is the same field to validation, delivery and masking.
/// </summary>
/// <remarks>
/// It was not. Validation matches the schema with OrdinalIgnoreCase and so does public delivery,
/// which documents the mismatch as normal and expected in DeliveryQuery. Masking matched ordinally.
///
/// So a record holding "salary" against a schema field declared "Salary" was validated as that
/// field, delivered as that field, and not masked as that field. The third reader is the one
/// deciding whether to hide it.
/// </remarks>
[Collection("Sequential")]
public class SensitivityCasingTests
{
    private readonly IntegrationTestFixture _factory;

    public SensitivityCasingTests(IntegrationTestFixture factory) => _factory = factory;

    private async Task<(HttpClient Client, string Type, Guid Id)> SeedAsync(string storedKey)
    {
        var type = $"cas_{Guid.NewGuid():n}"[..14];
        var id = Guid.NewGuid();

        using (var scope = _factory.Services.CreateScope())
        {
            var s = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            s.Store(new ContentTypeDefinition
            {
                Id = Guid.NewGuid(), Name = type, DisplayName = type,
                Fields =
                [
                    new FieldDefinition { Name = "Title", DisplayName = "Title", Type = "string" },
                    new FieldDefinition
                    {
                        Name = "Salary", DisplayName = "Salary", Type = "string",
                        Sensitivity = SensitivityLevel.Sensitive,
                    },
                ],
            });
            s.Store(new Content
            {
                Id = id, ContentType = type,
                Status = ContentStatus.Published, Sensitivity = SensitivityLevel.Public,
                // Deliberately not the schema's spelling.
                Data = new Dictionary<string, object> { ["Title"] = "role", [storedKey] = "999999" },
            });

            // Read permission on this type, and nothing else. The caller has to be able to see the
            // record for masking to be the thing under test: without it the list comes back empty
            // and every masking assertion passes because there is nothing to mask. The control
            // below is what caught exactly that.
            var role = new Role
            {
                Id = Guid.NewGuid(),
                Name = $"Viewer_{Guid.NewGuid():n}"[..14],
                Permissions =
                [
                    new ContentTypePermission
                    {
                        ContentTypeSlug = type,
                        Read = new PermissionRule { Enabled = true },
                    },
                ],
            };
            s.Store(role);

            var userId = Guid.NewGuid();
            s.Store(new User
            {
                Id = userId,
                Username = $"cas_{Guid.NewGuid():n}",
                Email = $"cas_{Guid.NewGuid():n}@example.com",
                RoleIds = [role.Id],
            });
            await s.SaveChangesAsync();

            var c = _factory.CreateClient();
            c.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Bearer", _factory.CreateToken(roles: [role.Name], userId: userId.ToString()));
            return (c, type, id);
        }
    }

    [Theory]
    [InlineData("salary")]
    [InlineData("SALARY")]
    [InlineData("Salary")]
    public async Task A_sensitive_field_is_masked_whatever_casing_it_is_stored_under(string storedKey)
    {
        var (client, type, _) = await SeedAsync(storedKey);

        var body = await (await client.GetAsync($"/api/contents?contentType={type}")).Content.ReadAsStringAsync();

        body.Should().NotContain("999999",
            "the schema marks Salary Sensitive, and a record spelling the key differently is the "
          + "mismatch delivery already treats as normal");
    }

    /// <summary>
    /// The control. Without it a masker that removed everything would pass every case above.
    /// </summary>
    [Fact]
    public async Task A_public_field_is_still_returned()
    {
        var (client, type, _) = await SeedAsync("salary");

        var body = await (await client.GetAsync($"/api/contents?contentType={type}")).Content.ReadAsStringAsync();

        body.Should().Contain("role", "Title is Public and has to survive the masking pass");
    }
}
