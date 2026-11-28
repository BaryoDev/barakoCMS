using System.Net;
using System.Text.Json;
using BarakoCMS.Portability;
using BarakoCMS.Tests.Features.Config;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using barakoCMS.Models;

namespace BarakoCMS.Tests.Features.Portability;

/// <summary>
/// A config export of entries shows each one the way the content read endpoints would show it to the
/// same caller.
/// </summary>
/// <remarks>
/// The export capability decides who may take a document, not which fields that caller may read. A
/// field masked on <c>GET /api/contents/{id}</c> has to come out masked here too, and a caller who
/// may see the field has to get it whole, or the document stops being a backup. Each test works in a
/// tenant of its own, so what an export holds is only what the test stored.
/// </remarks>
[Collection("Integration1")]
public class ExportMaskingTests
{
    private const string Secret = "PLAINTEXT-SALARY-918273";

    private readonly IntegrationTestFixture _fixture;
    private readonly ConfigCallers _callers;

    public ExportMaskingTests(IntegrationTestFixture fixture)
    {
        _fixture = fixture;
        _callers = new ConfigCallers(fixture);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Seeded(string Tenant, string Type, string PayrollRole);

    /// <summary>
    /// A tenant with a type with one Redact field visible only to a payroll role, one ordinary entry
    /// holding a known value, and one entry hidden at the document level.
    /// </summary>
    private async Task<Seeded> SeedAsync()
    {
        var tenant = await _callers.TenantAsync();
        var type = $"mask{Guid.NewGuid():n}"[..12];
        var payrollRole = $"Payroll {Guid.NewGuid():N}";

        await _callers.StoreInTenantAsync(tenant,
            new ContentTypeDefinition
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
            },
            new Content
            {
                Id = Guid.NewGuid(),
                ContentType = type,
                Status = ContentStatus.Published,
                Data = new Dictionary<string, object> { ["Name"] = "Ana", ["Salary"] = Secret },
            },
            new Content
            {
                Id = Guid.NewGuid(),
                ContentType = type,
                Status = ContentStatus.Published,
                Sensitivity = SensitivityLevel.Hidden,
                Data = new Dictionary<string, object> { ["Name"] = "Hidden Person", ["Salary"] = Secret },
            });
        return new Seeded(tenant, type, payrollRole);
    }

    /// <summary>A read rule on one content type, with or without a row condition.</summary>
    private static ContentTypePermission Read(string type, Dictionary<string, object>? conditions = null) => new()
    {
        ContentTypeSlug = type,
        Read = new PermissionRule { Enabled = true, Conditions = conditions },
    };

    /// <summary>
    /// A caller of <paramref name="tenant"/> whose one role, under the given name, holds these
    /// capabilities and these content permissions. The config engine asks for manage_config as well.
    /// </summary>
    private async Task<HttpClient> CallerAsync(
        string tenant, string roleName, string[] capabilities, params ContentTypePermission[] permissions)
    {
        var role = new Role
        {
            Id = Guid.NewGuid(),
            Name = roleName,
            SystemCapabilities = capabilities.Append(SystemCapabilities.ManageConfig).ToList(),
            Permissions = permissions.ToList(),
        };
        await _callers.StoreInTenantAsync(null, role);
        return await _callers.ClientAsync(tenant, [role.Id]);
    }

    private static string Exporter() => $"Exporter {Guid.NewGuid():N}";

    /// <summary>The entries section of <c>GET /api/config/export</c>, with the whole body for the raw checks.</summary>
    private static async Task<(string Raw, List<JsonElement> Entries, List<string> Gaps)> ExportAsync(HttpClient client)
    {
        var (response, body) = await ConfigCallers.ExportAsync(client, "contents");
        var raw = body.ToString();
        response.StatusCode.Should().Be(HttpStatusCode.OK, raw);
        var entries = body.TryGetProperty("contents", out var section)
            ? section.EnumerateObject().Select(p => p.Value).ToList()
            : [];
        var gaps = body.GetProperty("gaps").TryGetProperty("contents", out var listed)
            ? listed.EnumerateArray().Select(g => g.GetString()!).ToList()
            : [];
        return (raw, entries, gaps);
    }

    private static string DataOf(JsonElement entry, string field) => entry.GetProperty("data").GetProperty(field).ToString();

    private static List<string> MaskedOf(JsonElement entry) =>
        entry.TryGetProperty("maskedFields", out var masked) && masked.ValueKind == JsonValueKind.Array
            ? masked.EnumerateArray().Select(m => m.GetString()!).ToList()
            : [];

    /// <summary>Applies the contents an export gave, as the same tenant's caller, and expects it to land.</summary>
    private static async Task ApplyBackAsync(HttpClient client, JsonElement exported)
    {
        var (applied, body) = await ConfigCallers.ApplyAsync(client, exported);
        applied.StatusCode.Should().Be(HttpStatusCode.OK, body.ToString());
    }

    private async Task<JsonElement> ExportedDocumentAsync(HttpClient client)
    {
        var (response, body) = await ConfigCallers.ExportAsync(client, "contents");
        response.StatusCode.Should().Be(HttpStatusCode.OK, body.ToString());
        return body;
    }

    private async Task<List<Content>> StoredAsync(string tenant, string type) =>
        (await _callers.InTenantAsync(tenant,
            s => s.Query<Content>().Where(c => c.ContentType == type).ToListAsync(Ct)))!.ToList();

    [Fact]
    public async Task An_exporter_who_may_not_see_a_field_gets_it_masked()
    {
        var seeded = await SeedAsync();
        var exporter = await CallerAsync(
            seeded.Tenant, Exporter(), [PortabilityCapabilities.ExportContent], Read(seeded.Type));

        var (raw, entries, gaps) = await ExportAsync(exporter);

        raw.Should().NotContain(Secret, "the export capability is not permission to read a masked field");
        var record = entries.Should().ContainSingle(
            "the document-level hidden entry is withheld, the ordinary one is exported").Subject;
        DataOf(record, "Name").Should().Be("Ana");
        DataOf(record, "Salary").Should().Be("***", "the field's mask is Redact");
        MaskedOf(record).Should().Equal("Salary");
        gaps.Should().ContainSingle(g => g.StartsWith("1 entries are left out", StringComparison.Ordinal));
        raw.Should().NotContain("Hidden Person");
    }

    [Fact]
    public async Task An_exporter_who_may_see_a_field_gets_it_whole()
    {
        var seeded = await SeedAsync();
        var payroll = await CallerAsync(
            seeded.Tenant, seeded.PayrollRole, [PortabilityCapabilities.ExportContent], Read(seeded.Type));

        var (_, entries, _) = await ExportAsync(payroll);

        var record = entries.Should().ContainSingle().Subject;
        DataOf(record, "Salary").Should().Be(Secret,
            "masking a field from someone allowed to read it turns the backup into a lossy copy");
        MaskedOf(record).Should().BeEmpty();
    }

    /// <summary>
    /// Applying a masked document must not store the mask as if it were the value.
    /// </summary>
    [Fact]
    public async Task Applying_a_masked_document_leaves_the_masked_field_out()
    {
        var seeded = await SeedAsync();
        var exporter = await CallerAsync(
            seeded.Tenant, Exporter(), [PortabilityCapabilities.ExportContent], Read(seeded.Type));
        var document = await ExportedDocumentAsync(exporter);
        document.GetProperty("contents").EnumerateObject().Should().ContainSingle()
            .Which.Value.GetProperty("data").GetProperty("Salary").ToString().Should().Be("***");

        var importer = await CallerAsync(
            seeded.Tenant, $"Importer {Guid.NewGuid():N}",
            [PortabilityCapabilities.ExportContent, PortabilityCapabilities.ImportContent]);
        await ApplyBackAsync(importer, document);

        var stored = await StoredAsync(seeded.Tenant, seeded.Type);
        stored.Should().HaveCount(3, "the two seeded entries plus the one applied");
        var copy = stored.Should().ContainSingle(c => c.Data.ContainsKey("Name")
                                                      && c.Data["Name"].ToString() == "Ana"
                                                      && !c.Data.ContainsKey("Salary"),
            "the applied copy carries the name and leaves the masked field unset").Subject;
        copy.Data.Values.Select(v => v?.ToString()).Should().NotContain("***");
        stored.Where(c => c.Data.ContainsKey("Salary"))
            .Select(c => c.Data["Salary"].ToString())
            .Should().Equal(Secret, Secret);
    }

    /// <summary>Two public entries of a type with no sensitive fields, told apart by Team.</summary>
    private async Task<(string Tenant, string Type)> SeedTeamsAsync()
    {
        var tenant = await _callers.TenantAsync();
        var type = $"rows{Guid.NewGuid():n}"[..12];

        var documents = new List<object>
        {
            new ContentTypeDefinition
            {
                Id = Guid.NewGuid(),
                Name = type,
                DisplayName = "Rows",
                Fields =
                [
                    new FieldDefinition { Name = "Name", DisplayName = "Name", Type = "string" },
                    new FieldDefinition { Name = "Team", DisplayName = "Team", Type = "string" },
                ],
            },
        };
        foreach (var (name, team) in new[] { ("Blue Row", "blue"), ("Red Row", "red") })
        {
            documents.Add(new Content
            {
                Id = Guid.NewGuid(),
                ContentType = type,
                Status = ContentStatus.Published,
                Data = new Dictionary<string, object> { ["Name"] = name, ["Team"] = team },
            });
        }

        await _callers.StoreInTenantAsync(tenant, documents.ToArray());
        return (tenant, type);
    }

    /// <summary>
    /// A row the caller's read rule excludes is left out of the document, the same row List leaves out.
    /// </summary>
    [Fact]
    public async Task An_entry_the_callers_read_rule_excludes_is_withheld()
    {
        var (tenant, type) = await SeedTeamsAsync();
        var blueOnly = await CallerAsync(
            tenant,
            $"Blue {Guid.NewGuid():N}",
            [PortabilityCapabilities.ExportContent],
            Read(type, new Dictionary<string, object>
            {
                ["Team"] = new Dictionary<string, object> { ["_eq"] = "blue" },
            }));

        var (raw, entries, gaps) = await ExportAsync(blueOnly);

        DataOf(entries.Should().ContainSingle().Subject, "Name").Should().Be("Blue Row");
        gaps.Should().ContainSingle(g => g.StartsWith("1 entries are left out", StringComparison.Ordinal));
        raw.Should().NotContain("Red Row", "the export capability is not permission to read every row");
    }

    [Fact]
    public async Task A_caller_whose_read_rule_allows_every_entry_exports_them_all()
    {
        var (tenant, type) = await SeedTeamsAsync();
        var everyone = await CallerAsync(
            tenant, $"Everyone {Guid.NewGuid():N}", [PortabilityCapabilities.ExportContent], Read(type));

        var (_, entries, gaps) = await ExportAsync(everyone);

        entries.Should().HaveCount(2);
        entries.Select(c => DataOf(c, "Name")).Should().BeEquivalentTo(["Blue Row", "Red Row"]);
        gaps.Should().BeEmpty();
    }

    [Fact]
    public async Task A_superadmin_export_is_whole_and_unmasked()
    {
        var seeded = await SeedAsync();

        var (_, entries, gaps) = await ExportAsync(await _callers.SuperAdminAsync(seeded.Tenant));

        entries.Should().HaveCount(2, "SuperAdmin reads the Hidden entry too");
        entries.Select(c => DataOf(c, "Salary")).Should().Equal(Secret, Secret);
        entries.Should().OnlyContain(c => MaskedOf(c).Count == 0);
        gaps.Should().BeEmpty();
    }

    /// <summary>
    /// A backup restored keeps each entry's document-level sensitivity, rather than making it Public.
    /// </summary>
    [Fact]
    public async Task A_restored_hidden_entry_stays_hidden()
    {
        var seeded = await SeedAsync();
        var superAdmin = await _callers.SuperAdminAsync(seeded.Tenant);
        var document = await ExportedDocumentAsync(superAdmin);
        document.GetProperty("contents").EnumerateObject().Should().HaveCount(2);

        await ApplyBackAsync(superAdmin, document);

        var stored = await StoredAsync(seeded.Tenant, seeded.Type);
        var hiddenPeople = stored.Where(c => c.Data["Name"].ToString() == "Hidden Person").ToList();
        hiddenPeople.Should().HaveCount(2, "the seeded entry and its restored copy");
        hiddenPeople.Should().OnlyContain(c => c.Sensitivity == SensitivityLevel.Hidden,
            "a restore that makes a Hidden entry Public publishes what was hidden");
        stored.Where(c => c.Data["Name"].ToString() == "Ana")
            .Should().HaveCount(2).And.OnlyContain(c => c.Sensitivity == SensitivityLevel.Public);
    }

    /// <summary>
    /// A Last4 field, a Remove field, and one entry that is Sensitive at the document level.
    /// </summary>
    private async Task<(string Tenant, string Type)> SeedMasksAsync()
    {
        var tenant = await _callers.TenantAsync();
        var type = $"msk{Guid.NewGuid():n}"[..12];

        await _callers.StoreInTenantAsync(tenant,
            new ContentTypeDefinition
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
            },
            new Content
            {
                Id = Guid.NewGuid(),
                ContentType = type,
                Status = ContentStatus.Published,
                Data = new Dictionary<string, object>
                {
                    ["Name"] = "Open Entry", ["Phone"] = "555-867-5309", ["Note"] = "PRIVATE-NOTE-4411",
                },
            },
            new Content
            {
                Id = Guid.NewGuid(),
                ContentType = type,
                Status = ContentStatus.Published,
                Sensitivity = SensitivityLevel.Sensitive,
                Data = new Dictionary<string, object> { ["Name"] = "Sensitive Entry" },
            });
        return (tenant, type);
    }

    [Fact]
    public async Task Last4_and_remove_masks_apply_and_a_sensitive_entry_is_withheld()
    {
        var (tenant, type) = await SeedMasksAsync();
        var exporter = await CallerAsync(tenant, Exporter(), [PortabilityCapabilities.ExportContent], Read(type));
        var document = await ExportedDocumentAsync(exporter);
        var (raw, entries, gaps) = await ExportAsync(exporter);

        var record = entries.Should().ContainSingle().Subject;
        DataOf(record, "Phone").Should().Be("********5309");
        record.GetProperty("data").TryGetProperty("Note", out _).Should().BeFalse("the Remove mask drops the key");
        MaskedOf(record).Should().Equal("Phone");
        gaps.Should().ContainSingle(g => g.StartsWith("1 entries are left out", StringComparison.Ordinal),
            "the caller does not hold the role a Sensitive entry needs");
        raw.Should().NotContain("PRIVATE-NOTE-4411").And.NotContain("555-867").And.NotContain("Sensitive Entry");

        await ApplyBackAsync(await _callers.SuperAdminAsync(tenant), document);

        var copies = (await StoredAsync(tenant, type))
            .Where(c => c.Data["Name"].ToString() == "Open Entry")
            .ToList();
        copies.Should().HaveCount(2, "the seeded entry and the applied copy");
        copies.Should().ContainSingle(c => !c.Data.ContainsKey("Phone") && !c.Data.ContainsKey("Note"),
            "the applied copy leaves both masked fields unset rather than storing the mask");
    }

    /// <summary>
    /// Documents today's behaviour: export needs a read rule for the type, the same as List.
    /// </summary>
    [Fact]
    public async Task A_caller_with_no_read_rule_for_the_type_exports_none_of_it()
    {
        var (tenant, type) = await SeedTeamsAsync();
        var noRule = await CallerAsync(
            tenant, $"No Rule {Guid.NewGuid():N}",
            [PortabilityCapabilities.ExportContent, SystemCapabilities.ManageContentTypes]);

        var (response, body) = await ConfigCallers.ExportAsync(noRule, "contentTypes,contents");

        response.StatusCode.Should().Be(HttpStatusCode.OK, body.ToString());
        body.GetProperty("contentTypes").EnumerateObject().Should().ContainSingle(
            p => p.Name == type, "the schema is exported either way");
        body.TryGetProperty("contents", out var contents).Should().BeTrue();
        contents.EnumerateObject().Should().BeEmpty();
        body.GetProperty("gaps").GetProperty("contents").EnumerateArray().Select(g => g.GetString()).Should()
            .ContainSingle(g => g!.StartsWith("2 entries are left out", StringComparison.Ordinal));
    }
}
