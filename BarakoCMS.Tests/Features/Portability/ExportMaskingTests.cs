using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using BarakoCMS.Portability;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using barakoCMS.Models;

namespace BarakoCMS.Tests.Features.Portability;

/// <summary>
/// An export shows each entry the way the content read endpoints would show it to the same caller.
/// </summary>
/// <remarks>
/// The export capability decides who may take a bundle, not which fields that caller may read. A
/// field masked on <c>GET /api/contents/{id}</c> has to come out masked here too, and a caller who
/// may see the field has to get it whole, or the bundle stops being a backup.
/// </remarks>
[Collection("Sequential")]
public class ExportMaskingTests
{
    private const string Secret = "PLAINTEXT-SALARY-918273";

    private readonly IntegrationTestFixture _fixture;

    public ExportMaskingTests(IntegrationTestFixture fixture) => _fixture = fixture;

    private sealed record Seeded(string Type, string PayrollRole);

    /// <summary>
    /// A type with one Redact field visible only to a payroll role, one ordinary entry holding a known
    /// value, and one entry hidden at the document level.
    /// </summary>
    private async Task<Seeded> SeedAsync()
    {
        var type = $"mask{Guid.NewGuid():n}"[..12];
        var payrollRole = $"Payroll {Guid.NewGuid():N}";

        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Store(new ContentTypeDefinition
        {
            Id = Guid.NewGuid(),
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
                    VisibleToRoles = [payrollRole],
                },
            ],
        });
        session.Store(new Content
        {
            Id = Guid.NewGuid(),
            ContentType = type,
            Status = ContentStatus.Published,
            Data = new Dictionary<string, object> { ["Name"] = "Ana", ["Salary"] = Secret },
        });
        session.Store(new Content
        {
            Id = Guid.NewGuid(),
            ContentType = type,
            Status = ContentStatus.Published,
            Sensitivity = SensitivityLevel.Hidden,
            Data = new Dictionary<string, object> { ["Name"] = "Hidden Person", ["Salary"] = Secret },
        });
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        return new Seeded(type, payrollRole);
    }

    /// <summary>A read rule on one content type, with or without a row condition.</summary>
    private static ContentTypePermission Read(string type, Dictionary<string, object>? conditions = null) => new()
    {
        ContentTypeSlug = type,
        Read = new PermissionRule { Enabled = true, Conditions = conditions },
    };

    /// <summary>
    /// A caller whose one role, under the given name, holds these capabilities and these content
    /// permissions.
    /// </summary>
    private async Task<HttpClient> CallerAsync(
        string roleName, string[] capabilities, params ContentTypePermission[] permissions)
    {
        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();

        var role = new Role
        {
            Id = Guid.NewGuid(),
            Name = roleName,
            SystemCapabilities = capabilities.ToList(),
            Permissions = permissions.ToList(),
        };
        session.Store(role);
        var userId = Guid.NewGuid();
        session.Store(new User
        {
            Id = userId,
            Username = $"expmask-{userId:n}",
            Email = $"expmask-{userId:n}@example.com",
            RoleIds = [role.Id],
        });
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);

        var client = _fixture.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", _fixture.CreateToken(roles: [roleName], userId: userId.ToString()));
        return client;
    }

    private static async Task<(string Raw, PortabilityBundle Bundle)> ExportAsync(HttpClient client, string type)
    {
        var response = await client.GetAsync(
            $"/api/portability/export?types={type}", TestContext.Current.CancellationToken);
        var raw = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.OK, raw);
        var bundle = await response.Content.ReadFromJsonAsync<PortabilityBundle>(
            ApiJson.Options, TestContext.Current.CancellationToken);
        return (raw, bundle!);
    }

    [Fact]
    public async Task An_exporter_who_may_not_see_a_field_gets_it_masked()
    {
        var seeded = await SeedAsync();
        var exporter = await CallerAsync(
            $"Exporter {Guid.NewGuid():N}", [PortabilityCapabilities.ExportContent], Read(seeded.Type));

        var (raw, bundle) = await ExportAsync(exporter, seeded.Type);

        raw.Should().NotContain(Secret, "the export capability is not permission to read a masked field");
        var record = bundle.Contents.Should().ContainSingle(
            "the document-level hidden entry is withheld, the ordinary one is exported").Subject;
        record.Data["Name"].ToString().Should().Be("Ana");
        record.Data["Salary"].ToString().Should().Be("***", "the field's mask is Redact");
        record.MaskedFields.Should().Equal("Salary");
        bundle.ContentsWithheld.Should().Be(1);
        raw.Should().NotContain("Hidden Person");
    }

    [Fact]
    public async Task An_exporter_who_may_see_a_field_gets_it_whole()
    {
        var seeded = await SeedAsync();
        var payroll = await CallerAsync(
            seeded.PayrollRole, [PortabilityCapabilities.ExportContent], Read(seeded.Type));

        var (_, bundle) = await ExportAsync(payroll, seeded.Type);

        var record = bundle.Contents.Should().ContainSingle().Subject;
        record.Data["Salary"].ToString().Should().Be(Secret,
            "masking a field from someone allowed to read it turns the backup into a lossy copy");
        record.MaskedFields.Should().BeEmpty();
    }

    /// <summary>
    /// Importing a masked bundle must not store the mask as if it were the value.
    /// </summary>
    [Fact]
    public async Task Importing_a_masked_bundle_leaves_the_masked_field_out()
    {
        var seeded = await SeedAsync();
        var exporter = await CallerAsync(
            $"Exporter {Guid.NewGuid():N}", [PortabilityCapabilities.ExportContent], Read(seeded.Type));
        var (_, bundle) = await ExportAsync(exporter, seeded.Type);
        bundle.Contents.Should().ContainSingle().Which.Data["Salary"].ToString().Should().Be("***");

        var importer = await CallerAsync($"Importer {Guid.NewGuid():N}", [PortabilityCapabilities.ImportContent]);
        var imported = await importer.PostAsJsonAsync(
            "/api/portability/import",
            new { contentTypes = bundle.ContentTypes, contents = bundle.Contents },
            ApiJson.Options,
            TestContext.Current.CancellationToken);
        imported.IsSuccessStatusCode.Should().BeTrue(
            await imported.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        var stored = await session.Query<Content>()
            .Where(c => c.ContentType == seeded.Type)
            .ToListAsync(TestContext.Current.CancellationToken);

        stored.Should().HaveCount(3, "the two seeded entries plus the one imported");
        var copy = stored.Should().ContainSingle(c => c.Data.ContainsKey("Name")
                                                      && c.Data["Name"].ToString() == "Ana"
                                                      && !c.Data.ContainsKey("Salary"),
            "the imported copy carries the name and leaves the masked field unset").Subject;
        copy.Data.Values.Select(v => v?.ToString()).Should().NotContain("***");
        stored.Where(c => c.Data.ContainsKey("Salary"))
            .Select(c => c.Data["Salary"].ToString())
            .Should().Equal(Secret, Secret);
    }

    /// <summary>Two public entries of a type with no sensitive fields, told apart by Team.</summary>
    private async Task<string> SeedTeamsAsync()
    {
        var type = $"rows{Guid.NewGuid():n}"[..12];

        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Store(new ContentTypeDefinition
        {
            Id = Guid.NewGuid(),
            Name = type,
            DisplayName = "Rows",
            Fields =
            [
                new FieldDefinition { Name = "Name", DisplayName = "Name", Type = "string" },
                new FieldDefinition { Name = "Team", DisplayName = "Team", Type = "string" },
            ],
        });
        foreach (var (name, team) in new[] { ("Blue Row", "blue"), ("Red Row", "red") })
        {
            session.Store(new Content
            {
                Id = Guid.NewGuid(),
                ContentType = type,
                Status = ContentStatus.Published,
                Data = new Dictionary<string, object> { ["Name"] = name, ["Team"] = team },
            });
        }
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        return type;
    }

    /// <summary>
    /// A row the caller's read rule excludes is left out of the bundle, the same row List leaves out.
    /// </summary>
    [Fact]
    public async Task An_entry_the_callers_read_rule_excludes_is_withheld()
    {
        var type = await SeedTeamsAsync();
        var blueOnly = await CallerAsync(
            $"Blue {Guid.NewGuid():N}",
            [PortabilityCapabilities.ExportContent],
            Read(type, new Dictionary<string, object>
            {
                ["Team"] = new Dictionary<string, object> { ["_eq"] = "blue" },
            }));

        var (raw, bundle) = await ExportAsync(blueOnly, type);

        bundle.Contents.Should().ContainSingle().Which.Data["Name"].ToString().Should().Be("Blue Row");
        bundle.ContentsWithheld.Should().Be(1);
        raw.Should().NotContain("Red Row", "the export capability is not permission to read every row");
    }

    [Fact]
    public async Task A_caller_whose_read_rule_allows_every_entry_exports_them_all()
    {
        var type = await SeedTeamsAsync();
        var everyone = await CallerAsync(
            $"Everyone {Guid.NewGuid():N}", [PortabilityCapabilities.ExportContent], Read(type));

        var (_, bundle) = await ExportAsync(everyone, type);

        bundle.Contents.Should().HaveCount(2);
        bundle.Contents.Select(c => c.Data["Name"].ToString())
            .Should().BeEquivalentTo(["Blue Row", "Red Row"]);
        bundle.ContentsWithheld.Should().Be(0);
    }

    /// <summary>A SuperAdmin: the seeded role by id for content rules, and the role claim for masking.</summary>
    private async Task<HttpClient> SuperAdminAsync()
    {
        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var userId = Guid.NewGuid();
        session.Store(new User
        {
            Id = userId,
            Username = $"expsa-{userId:n}",
            Email = $"expsa-{userId:n}@example.com",
            RoleIds = [SystemRoles.SuperAdminRoleId],
        });
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);

        var client = _fixture.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", _fixture.CreateToken(roles: ["SuperAdmin"], userId: userId.ToString()));
        return client;
    }

    private async Task<List<Content>> StoredAsync(string type)
    {
        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        return (await session.Query<Content>()
            .Where(c => c.ContentType == type)
            .ToListAsync(TestContext.Current.CancellationToken)).ToList();
    }

    private static async Task ImportAsync(HttpClient client, PortabilityBundle bundle)
    {
        var imported = await client.PostAsJsonAsync(
            "/api/portability/import",
            new { contentTypes = bundle.ContentTypes, contents = bundle.Contents },
            ApiJson.Options,
            TestContext.Current.CancellationToken);
        imported.IsSuccessStatusCode.Should().BeTrue(
            await imported.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_superadmin_export_is_whole_and_unmasked()
    {
        var seeded = await SeedAsync();

        var (_, bundle) = await ExportAsync(await SuperAdminAsync(), seeded.Type);

        bundle.Contents.Should().HaveCount(2, "SuperAdmin reads the Hidden entry too");
        bundle.Contents.Select(c => c.Data["Salary"].ToString()).Should().Equal(Secret, Secret);
        bundle.Contents.Should().OnlyContain(c => c.MaskedFields.Count == 0);
        bundle.ContentsWithheld.Should().Be(0);
    }

    /// <summary>
    /// A backup restored keeps each entry's document-level sensitivity, rather than making it Public.
    /// </summary>
    [Fact]
    public async Task A_restored_hidden_entry_stays_hidden()
    {
        var seeded = await SeedAsync();
        var superAdmin = await SuperAdminAsync();
        var (_, bundle) = await ExportAsync(superAdmin, seeded.Type);
        bundle.Contents.Should().HaveCount(2);

        await ImportAsync(superAdmin, bundle);

        var hiddenPeople = (await StoredAsync(seeded.Type))
            .Where(c => c.Data["Name"].ToString() == "Hidden Person")
            .ToList();
        hiddenPeople.Should().HaveCount(2, "the seeded entry and its restored copy");
        hiddenPeople.Should().OnlyContain(c => c.Sensitivity == SensitivityLevel.Hidden,
            "a restore that makes a Hidden entry Public publishes what was hidden");
        (await StoredAsync(seeded.Type)).Where(c => c.Data["Name"].ToString() == "Ana")
            .Should().HaveCount(2).And.OnlyContain(c => c.Sensitivity == SensitivityLevel.Public);
    }

    /// <summary>
    /// A Last4 field, a Remove field, and one entry that is Sensitive at the document level.
    /// </summary>
    private async Task<string> SeedMasksAsync()
    {
        var type = $"msk{Guid.NewGuid():n}"[..12];

        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Store(new ContentTypeDefinition
        {
            Id = Guid.NewGuid(),
            Name = type,
            DisplayName = "Masks",
            Fields =
            [
                new FieldDefinition { Name = "Name", DisplayName = "Name", Type = "string" },
                new FieldDefinition
                {
                    Name = "Phone", DisplayName = "Phone", Type = "string",
                    Sensitivity = SensitivityLevel.Sensitive, Mask = FieldMask.Last4,
                },
                new FieldDefinition
                {
                    Name = "Note", DisplayName = "Note", Type = "string",
                    Sensitivity = SensitivityLevel.Sensitive, Mask = FieldMask.Remove,
                },
            ],
        });
        session.Store(new Content
        {
            Id = Guid.NewGuid(),
            ContentType = type,
            Status = ContentStatus.Published,
            Data = new Dictionary<string, object>
            {
                ["Name"] = "Open Entry", ["Phone"] = "555-867-5309", ["Note"] = "PRIVATE-NOTE-4411",
            },
        });
        session.Store(new Content
        {
            Id = Guid.NewGuid(),
            ContentType = type,
            Status = ContentStatus.Published,
            Sensitivity = SensitivityLevel.Sensitive,
            Data = new Dictionary<string, object> { ["Name"] = "Sensitive Entry" },
        });
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        return type;
    }

    [Fact]
    public async Task Last4_and_remove_masks_apply_and_a_sensitive_entry_is_withheld()
    {
        var type = await SeedMasksAsync();
        var exporter = await CallerAsync(
            $"Exporter {Guid.NewGuid():N}", [PortabilityCapabilities.ExportContent], Read(type));

        var (raw, bundle) = await ExportAsync(exporter, type);

        var record = bundle.Contents.Should().ContainSingle().Subject;
        record.Data["Phone"].ToString().Should().Be("********5309");
        record.Data.Should().NotContainKey("Note", "the Remove mask drops the key");
        record.MaskedFields.Should().Equal("Phone");
        bundle.ContentsWithheld.Should().Be(1, "the caller does not hold the role a Sensitive entry needs");
        raw.Should().NotContain("PRIVATE-NOTE-4411").And.NotContain("555-867").And.NotContain("Sensitive Entry");

        await ImportAsync(await SuperAdminAsync(), bundle);

        var copies = (await StoredAsync(type))
            .Where(c => c.Data["Name"].ToString() == "Open Entry")
            .ToList();
        copies.Should().HaveCount(2, "the seeded entry and the imported copy");
        copies.Should().ContainSingle(c => !c.Data.ContainsKey("Phone") && !c.Data.ContainsKey("Note"),
            "the imported copy leaves both masked fields unset rather than storing the mask");
    }

    /// <summary>
    /// Documents today's behaviour: export needs a read rule for the type, the same as List.
    /// </summary>
    [Fact]
    public async Task A_caller_with_no_read_rule_for_the_type_exports_none_of_it()
    {
        var type = await SeedTeamsAsync();
        var noRule = await CallerAsync($"No Rule {Guid.NewGuid():N}", [PortabilityCapabilities.ExportContent]);

        var (_, bundle) = await ExportAsync(noRule, type);

        bundle.ContentTypes.Should().ContainSingle("the schema is exported either way");
        bundle.Contents.Should().BeEmpty();
        bundle.ContentsWithheld.Should().Be(2);
    }
}
