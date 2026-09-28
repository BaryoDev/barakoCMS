using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Threading;
using barakoCMS.Core.Interfaces;
using barakoCMS.Models;
using BarakoCMS.Portability;
using FluentAssertions;
using Marten;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BarakoCMS.Tests.Features.Portability;

/// <summary>
/// An import writes what <c>POST /api/content-types</c> and <c>POST /api/contents</c> would write
/// for the same caller, and refuses what they would refuse (issue #933).
/// </summary>
/// <remarks>
/// Import is the one route that writes a whole schema and its content at once. Each test here sends
/// a bundle the create endpoints refuse or change, and checks the import does the same.
/// </remarks>
[Collection("Sequential")]
public class ImportWritePathTests
{
    private const string Secret = "PLAINTEXT-SALARY-551902";
    private const string HookType = "importhookprobe";

    private readonly IntegrationTestFixture _fixture;

    public ImportWritePathTests(IntegrationTestFixture fixture) => _fixture = fixture;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string NewType(string prefix) => (prefix + Guid.NewGuid().ToString("n"))[..14];

    private async Task<HttpClient> AdminAsync(WebApplicationFactory<Program>? host = null)
    {
        var client = (host ?? _fixture).CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", await _fixture.StoredUserTokenAsync("SuperAdmin", "Admin"));
        return client;
    }

    /// <summary>A caller whose one role, under the given name, holds only import_content.</summary>
    private async Task<HttpClient> ImporterAsync(string roleName)
    {
        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();

        var role = new Role
        {
            Id = Guid.NewGuid(),
            Name = roleName,
            SystemCapabilities = [PortabilityCapabilities.ImportContent],
        };
        session.Store(role);
        var userId = Guid.NewGuid();
        session.Store(new User
        {
            Id = userId,
            Username = $"impwrite-{userId:n}",
            Email = $"impwrite-{userId:n}@example.com",
            RoleIds = [role.Id],
        });
        await session.SaveChangesAsync(Ct);

        var client = _fixture.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", _fixture.CreateToken(roles: [roleName], userId: userId.ToString()));
        return client;
    }

    private static FieldDefinition Text(string name, bool required = false) =>
        new() { Name = name, DisplayName = name, Type = "string", IsRequired = required };

    private static FieldDefinition Salary(string payrollRole) => new()
    {
        Name = "Salary",
        DisplayName = "Salary",
        Type = "string",
        Sensitivity = SensitivityLevel.Sensitive,
        Mask = FieldMask.Redact,
        VisibleToRoles = [payrollRole],
    };

    private async Task StoreTypeAsync(ContentTypeDefinition definition, params Dictionary<string, object>[] entries)
    {
        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        definition.Id = Guid.NewGuid();
        session.Store(definition);
        foreach (var data in entries)
        {
            session.Store(new Content
            {
                Id = Guid.NewGuid(),
                ContentType = definition.Name,
                Status = ContentStatus.Published,
                Data = data,
            });
        }

        await session.SaveChangesAsync(Ct);
    }

    private async Task<ContentTypeDefinition?> StoredTypeAsync(string type)
    {
        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        return await session.Query<ContentTypeDefinition>().FirstOrDefaultAsync(d => d.Name == type, Ct);
    }

    private async Task<List<Content>> StoredEntriesAsync(string type)
    {
        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        return (await session.Query<Content>().Where(c => c.ContentType == type).ToListAsync(Ct)).ToList();
    }

    private static object Record(string type, Dictionary<string, object> data) =>
        new { contentType = type, status = "Published", data };

    private static async Task<(HttpStatusCode Status, string Body)> ImportAsync(
        HttpClient client, IEnumerable<object> types, IEnumerable<object> records, bool dryRun = false)
    {
        var response = await client.PostAsJsonAsync("/api/portability/import", new
        {
            dryRun,
            contentTypes = types.ToArray(),
            contents = records.ToArray(),
        }, Ct);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(Ct));
    }

    // Write-side sensitivity -------------------------------------------------------------------

    [Fact]
    public async Task A_field_the_importer_may_not_see_is_dropped_the_way_create_drops_it()
    {
        var type = NewType("impsens");
        var payroll = $"Payroll {Guid.NewGuid():N}";
        await StoreTypeAsync(new ContentTypeDefinition
        {
            Name = type, DisplayName = "Staff", Fields = [Text("Name"), Salary(payroll)],
        });
        var importer = await ImporterAsync($"Importer {Guid.NewGuid():N}");

        var (status, body) = await ImportAsync(importer, [],
            [Record(type, new() { ["Name"] = "Ana", ["Salary"] = Secret })]);

        status.Should().Be(HttpStatusCode.OK, body);
        var entries = await StoredEntriesAsync(type);
        entries.Should().ContainSingle();
        entries[0].Data.Should().ContainKey("Name");
        entries[0].Data.Keys.Should().NotContain(k => k.Equals("Salary", StringComparison.OrdinalIgnoreCase),
            "create drops a field its caller may not see, so a caller cannot set a value that would be masked from them");
    }

    [Fact]
    public async Task A_field_the_importer_may_not_see_is_dropped_on_a_type_the_bundle_creates()
    {
        var type = NewType("impsnew");
        var payroll = $"Payroll {Guid.NewGuid():N}";
        var importer = await ImporterAsync($"Importer {Guid.NewGuid():N}");

        var (status, body) = await ImportAsync(importer,
            [new ContentTypeDefinition { Name = type, DisplayName = "Staff", Fields = [Text("Name"), Salary(payroll)] }],
            [Record(type, new() { ["Name"] = "Ana", ["Salary"] = Secret })]);

        status.Should().Be(HttpStatusCode.OK, body);
        var entries = await StoredEntriesAsync(type);
        entries.Should().ContainSingle();
        entries[0].Data.Keys.Should().NotContain(k => k.Equals("Salary", StringComparison.OrdinalIgnoreCase),
            "the type arriving in the same bundle is the schema the entry is written under");
    }

    /// <summary>The positive control: a caller who may read the field keeps it.</summary>
    [Fact]
    public async Task An_importer_who_may_see_the_field_keeps_it()
    {
        var type = NewType("impskeep");
        var payroll = $"Payroll {Guid.NewGuid():N}";
        await StoreTypeAsync(new ContentTypeDefinition
        {
            Name = type, DisplayName = "Staff", Fields = [Text("Name"), Salary(payroll)],
        });
        var importer = await ImporterAsync(payroll);

        var (status, body) = await ImportAsync(importer, [],
            [Record(type, new() { ["Name"] = "Ana", ["Salary"] = Secret })]);

        status.Should().Be(HttpStatusCode.OK, body);
        var entries = await StoredEntriesAsync(type);
        entries.Should().ContainSingle();
        entries[0].Data["Salary"].ToString().Should().Be(Secret);
    }

    // Entry validation -------------------------------------------------------------------------

    [Fact]
    public async Task A_record_that_fails_its_type_validation_refuses_the_whole_import()
    {
        var type = NewType("impval");
        await StoreTypeAsync(new ContentTypeDefinition
        {
            Name = type, DisplayName = "Posts", Fields = [Text("Title", required: true), Text("Body")],
        });
        var admin = await AdminAsync();

        var (status, body) = await ImportAsync(admin, [],
        [
            Record(type, new() { ["Title"] = "fine", ["Body"] = "x" }),
            Record(type, new() { ["Body"] = "no title" }),
        ]);

        status.Should().Be(HttpStatusCode.BadRequest, body);
        body.Should().Contain("contents[1]").And.Contain("is required");
        (await StoredEntriesAsync(type)).Should().BeEmpty(
            "an import is all or nothing, so rerunning it after the fix does not duplicate the valid records");
    }

    [Fact]
    public async Task A_dry_run_refuses_what_the_real_run_would_refuse()
    {
        var type = NewType("impdry");
        await StoreTypeAsync(new ContentTypeDefinition
        {
            Name = type, DisplayName = "Posts", Fields = [Text("Title", required: true)],
        });
        var admin = await AdminAsync();

        var (status, body) = await ImportAsync(admin, [], [Record(type, new() { ["Other"] = "x" })], dryRun: true);

        status.Should().Be(HttpStatusCode.BadRequest, body);
        body.Should().Contain("contents[0]");
    }

    // Lifecycle hooks and initial state --------------------------------------------------------

    private sealed class StampHook : IContentLifecycleHook
    {
        public string ContentType => HookType;

        public Task<IReadOnlyList<string>> OnBeforeSaveAsync(ContentLifecycleContext context, CancellationToken ct)
        {
            if (context.Data.TryGetValue("Title", out var title) && title?.ToString() == "refuse")
                return Task.FromResult<IReadOnlyList<string>>(["The probe hook refused this entry."]);

            context.Data["Stamp"] = "hooked";
            return Task.FromResult<IReadOnlyList<string>>([]);
        }
    }

    private static readonly Lock HostGate = new();
    private static WebApplicationFactory<Program>? _hookHost;

    private WebApplicationFactory<Program> HookHost()
    {
        lock (HostGate)
        {
            return _hookHost ??= _fixture.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
                services.AddScoped<IContentLifecycleHook, StampHook>()));
        }
    }

    private static ContentTypeDefinition HookTypeWithLifecycle() => new()
    {
        Name = HookType,
        DisplayName = "Hook Probe",
        Fields = [Text("Title"), Text("Stamp")],
        Lifecycle = new LifecycleDefinition
        {
            States = ["Intake", "Review"],
            InitialState = "Review",
            Transitions = [new StateTransition { Name = "Send", From = "Review", To = "Intake" }],
        },
    };

    [Fact]
    public async Task An_imported_entry_runs_the_lifecycle_hooks_and_starts_in_its_initial_state()
    {
        var admin = await AdminAsync(HookHost());
        var marker = $"hook-{Guid.NewGuid():n}";

        var (status, body) = await ImportAsync(admin, [HookTypeWithLifecycle()],
            [Record(HookType, new() { ["Title"] = marker })]);

        status.Should().Be(HttpStatusCode.OK, body);
        var entry = (await StoredEntriesAsync(HookType)).Should()
            .ContainSingle(c => c.Data["Title"].ToString() == marker).Subject;
        entry.Data["Stamp"].ToString().Should().Be("hooked", "create runs the type's lifecycle hooks before the write");
        entry.LifecycleState.Should().Be("Review", "create starts an entry in its type's declared initial state");

        (await StoredTypeAsync(HookType))!.Lifecycle.Should().NotBeNull(
            "the bundle's lifecycle is part of the type it carries");
    }

    [Fact]
    public async Task A_lifecycle_hook_can_refuse_an_imported_entry()
    {
        var admin = await AdminAsync(HookHost());

        var (status, body) = await ImportAsync(admin, [HookTypeWithLifecycle()],
            [Record(HookType, new() { ["Title"] = "refuse" })]);

        status.Should().Be(HttpStatusCode.BadRequest, body);
        body.Should().Contain("The probe hook refused this entry.");
        (await StoredEntriesAsync(HookType)).Should().NotContain(c => c.Data["Title"].ToString() == "refuse");
    }

    // Content type validation ------------------------------------------------------------------

    [Fact]
    public async Task A_bundle_that_gives_a_stored_field_an_unknown_type_is_refused_and_leaves_the_type_alone()
    {
        var type = NewType("imptype");
        await StoreTypeAsync(new ContentTypeDefinition { Name = type, DisplayName = "Posts", Fields = [Text("Title")] });
        var admin = await AdminAsync();

        var (status, body) = await ImportAsync(admin,
            [new ContentTypeDefinition
            {
                Name = type, DisplayName = "Posts",
                Fields = [new FieldDefinition { Name = "Title", DisplayName = "Title", Type = "nonsense" }],
            }],
            []);

        status.Should().Be(HttpStatusCode.BadRequest, body);
        body.Should().Contain("contentTypes[0]").And.Contain("nonsense");
        (await StoredTypeAsync(type))!.Fields.Should().ContainSingle().Which.Type.Should().Be("string");
    }

    [Fact]
    public async Task A_new_type_that_create_would_refuse_is_not_created()
    {
        var type = NewType("impnewbad");
        var admin = await AdminAsync();

        var (status, body) = await ImportAsync(admin,
            [new ContentTypeDefinition
            {
                Name = type, DisplayName = "Links",
                Fields = [new FieldDefinition { Name = "Target", DisplayName = "Target", Type = "reference" }],
            }],
            []);

        status.Should().Be(HttpStatusCode.BadRequest, body);
        body.Should().Contain("referenceType");
        (await StoredTypeAsync(type)).Should().BeNull();
    }

    [Fact]
    public async Task A_custom_lifecycle_survives_an_import_over_an_existing_type()
    {
        var type = NewType("implife");
        await StoreTypeAsync(new ContentTypeDefinition { Name = type, DisplayName = "Invoices", Fields = [Text("Title")] });
        var admin = await AdminAsync();

        var (status, body) = await ImportAsync(admin,
            [new ContentTypeDefinition
            {
                Name = type, DisplayName = "Invoices", Fields = [Text("Title")],
                Lifecycle = new LifecycleDefinition
                {
                    States = ["Draft", "Approved"],
                    InitialState = "Draft",
                    Transitions = [new StateTransition { Name = "Approve", From = "Draft", To = "Approved" }],
                },
            }],
            []);

        status.Should().Be(HttpStatusCode.OK, body);
        var stored = await StoredTypeAsync(type);
        stored!.Lifecycle.Should().NotBeNull();
        stored.Lifecycle!.States.Should().Equal("Draft", "Approved");
    }

    [Fact]
    public async Task A_bundle_that_lowers_a_sensitive_field_is_refused()
    {
        var type = NewType("implower");
        await StoreTypeAsync(new ContentTypeDefinition
        {
            Name = type, DisplayName = "Staff", Fields = [Text("Name"), Salary("HR")],
        });
        var admin = await AdminAsync();

        var (status, body) = await ImportAsync(admin,
            [new ContentTypeDefinition { Name = type, DisplayName = "Staff", Fields = [Text("Name"), Text("Salary")] }],
            []);

        status.Should().Be(HttpStatusCode.BadRequest, body);
        body.Should().Contain("Salary").And.Contain("/sensitivity");
        (await StoredTypeAsync(type))!.Fields.Single(f => f.Name == "Salary").Sensitivity
            .Should().Be(SensitivityLevel.Sensitive);
    }

    [Fact]
    public async Task A_bundle_that_raises_a_public_field_is_refused_so_its_old_values_are_not_left_searchable()
    {
        var type = NewType("impraise");
        await StoreTypeAsync(
            new ContentTypeDefinition { Name = type, DisplayName = "Staff", Fields = [Text("Name"), Text("Salary")] },
            new Dictionary<string, object> { ["Name"] = "Ana", ["Salary"] = Secret });
        var admin = await AdminAsync();

        var (status, body) = await ImportAsync(admin,
            [new ContentTypeDefinition { Name = type, DisplayName = "Staff", Fields = [Text("Name"), Salary("HR")] }],
            []);

        status.Should().Be(HttpStatusCode.BadRequest, body);
        body.Should().Contain("Salary");
        (await StoredTypeAsync(type))!.Fields.Single(f => f.Name == "Salary").Sensitivity
            .Should().Be(SensitivityLevel.Public);
    }

    [Fact]
    public async Task A_required_field_with_no_default_on_a_type_with_entries_is_refused()
    {
        var type = NewType("impreq");
        await StoreTypeAsync(
            new ContentTypeDefinition { Name = type, DisplayName = "Posts", Fields = [Text("Title")] },
            new Dictionary<string, object> { ["Title"] = "existing" });
        var admin = await AdminAsync();

        var (status, body) = await ImportAsync(admin,
            [new ContentTypeDefinition { Name = type, DisplayName = "Posts", Fields = [Text("Title"), Text("Author", required: true)] }],
            []);

        status.Should().Be(HttpStatusCode.BadRequest, body);
        body.Should().Contain("Author");
        (await StoredTypeAsync(type))!.Fields.Should().ContainSingle();
    }

    [Fact]
    public async Task A_bundle_that_declares_a_stored_sensitive_field_twice_is_refused()
    {
        var type = NewType("impdup");
        await StoreTypeAsync(new ContentTypeDefinition
        {
            Name = type, DisplayName = "Staff", Fields = [Text("Name"), Salary("HR")],
        });
        var admin = await AdminAsync();

        var (status, body) = await ImportAsync(admin,
            [new ContentTypeDefinition
            {
                Name = type, DisplayName = "Staff", Fields = [Text("Name"), Salary("HR"), Text("Salary")],
            }],
            []);

        status.Should().Be(HttpStatusCode.BadRequest, body);
        body.Should().Contain("more than once");
        (await StoredTypeAsync(type))!.Fields.Should().HaveCount(2);
    }

    [Fact]
    public async Task A_bundle_type_matches_a_stored_type_under_the_normalized_name()
    {
        var type = NewType("impnorm");
        await StoreTypeAsync(new ContentTypeDefinition
        {
            Name = type + "-staff", DisplayName = "Staff", Fields = [Text("Name"), Salary("HR")],
        });
        var admin = await AdminAsync();

        var (status, body) = await ImportAsync(admin,
            [new ContentTypeDefinition
            {
                Name = type.ToUpperInvariant() + " STAFF", DisplayName = "Staff", Fields = [Text("Name"), Text("Salary")],
            }],
            []);

        status.Should().Be(HttpStatusCode.BadRequest, body);
        body.Should().Contain("/sensitivity", "the bundle type is the stored type, so lowering its field is refused");
    }

    [Fact]
    public async Task A_bundle_field_with_no_role_list_is_still_dropped_for_a_caller_who_may_not_see_it()
    {
        var type = NewType("impnull");
        var importer = await ImporterAsync($"Importer {Guid.NewGuid():N}");

        var response = await importer.PostAsync("/api/portability/import", new StringContent($$"""
            {
              "contentTypes": [{
                "name": "{{type}}", "displayName": "Staff",
                "fields": [
                  { "name": "Name", "displayName": "Name", "type": "string" },
                  { "name": "Salary", "displayName": "Salary", "type": "string", "sensitivity": {{(int)SensitivityLevel.Sensitive}}, "visibleToRoles": null }
                ]
              }],
              "contents": [{ "contentType": "{{type}}", "status": "Published", "data": { "Name": "Ana", "Salary": "{{Secret}}" } }]
            }
            """, System.Text.Encoding.UTF8, "application/json"), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        var entries = await StoredEntriesAsync(type);
        entries.Should().ContainSingle();
        entries[0].Data.Keys.Should().NotContain(k => k.Equals("Salary", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task A_bundle_without_a_lifecycle_keeps_the_stored_one_and_a_different_one_is_refused()
    {
        var type = NewType("implkeep");
        var lifecycle = new LifecycleDefinition
        {
            States = ["Draft", "Approved"],
            InitialState = "Draft",
            Transitions = [new StateTransition { Name = "Approve", From = "Draft", To = "Approved" }],
        };
        await StoreTypeAsync(new ContentTypeDefinition
        {
            Name = type, DisplayName = "Invoices", Fields = [Text("Title")], Lifecycle = lifecycle,
        });
        var admin = await AdminAsync();

        var (keptStatus, keptBody) = await ImportAsync(admin,
            [new ContentTypeDefinition { Name = type, DisplayName = "Invoices", Fields = [Text("Title")] }],
            []);
        keptStatus.Should().Be(HttpStatusCode.OK, keptBody);
        (await StoredTypeAsync(type))!.Lifecycle.Should().NotBeNull("a bundle from before lifecycles must not wipe one");

        var (status, body) = await ImportAsync(admin,
            [new ContentTypeDefinition
            {
                Name = type, DisplayName = "Invoices", Fields = [Text("Title")],
                Lifecycle = new LifecycleDefinition { States = ["Draft"], InitialState = "Draft" },
            }],
            []);
        status.Should().Be(HttpStatusCode.BadRequest, body);
        body.Should().Contain("lifecycle");
        (await StoredTypeAsync(type))!.Lifecycle!.States.Should().Equal("Draft", "Approved");
    }

    // Hooks see earlier entries of the same bundle --------------------------------------------

    private async Task EnsureAccountingTypesAsync()
    {
        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        foreach (var def in new[]
                 {
                     BarakoCMS.Accounting.AccountingContentTypes.AccountDefinition(),
                     BarakoCMS.Accounting.AccountingContentTypes.JournalEntryDefinition(),
                 })
        {
            if (!await session.Query<ContentTypeDefinition>().AnyAsync(t => t.Name == def.Name, Ct))
                session.Store(def);
        }

        await session.SaveChangesAsync(Ct);
    }

    private static object Account(string code, string? parent = null) => Record(
        BarakoCMS.Accounting.AccountingContentTypes.Account,
        parent is null
            ? new() { ["Code"] = code, ["Name"] = $"Account {code}", ["Type"] = "Asset", ["IsActive"] = true }
            : new() { ["Code"] = code, ["Name"] = $"Account {code}", ["Type"] = "Asset", ["IsActive"] = true, ["ParentCode"] = parent });

    private static object Journal(string memo, string debit, string credit) => Record(
        BarakoCMS.Accounting.AccountingContentTypes.JournalEntry,
        new()
        {
            ["Date"] = "2031-03-01",
            ["Memo"] = memo,
            ["Lines"] = new object[]
            {
                new Dictionary<string, object> { ["AccountCode"] = debit, ["Debit"] = 10m, ["Credit"] = 0m },
                new Dictionary<string, object> { ["AccountCode"] = credit, ["Debit"] = 0m, ["Credit"] = 10m },
            },
        });

    [Fact]
    public async Task Journal_entries_in_one_bundle_are_numbered_in_sequence_against_accounts_from_the_same_bundle()
    {
        await EnsureAccountingTypesAsync();
        var admin = await AdminAsync();
        var cash = $"C{Guid.NewGuid():N}"[..12];
        var income = $"I{Guid.NewGuid():N}"[..12];
        var memo = $"batch-{Guid.NewGuid():n}";

        var (status, body) = await ImportAsync(admin, [],
        [
            Account(cash),
            Account(income, parent: cash),
            Journal(memo, cash, income),
            Journal(memo, cash, income),
            Journal(memo, cash, income),
        ]);

        status.Should().Be(HttpStatusCode.OK, body);
        var numbers = (await StoredEntriesAsync(BarakoCMS.Accounting.AccountingContentTypes.JournalEntry))
            .Where(c => c.Data.TryGetValue("Memo", out var m) && m?.ToString() == memo)
            .Select(c => c.Data["EntryNumber"].ToString()!)
            .ToList();
        numbers.Should().HaveCount(3);
        numbers.Should().OnlyHaveUniqueItems("each entry's hook sees the entries numbered before it");
        var sequence = numbers.Select(n => int.Parse(n[^6..])).Order().ToList();
        (sequence[2] - sequence[0]).Should().Be(2, "three entries take three consecutive numbers");
    }

    [Fact]
    public async Task An_entry_a_hook_refuses_because_of_an_earlier_entry_in_the_bundle_refuses_the_import()
    {
        await EnsureAccountingTypesAsync();
        var admin = await AdminAsync();
        var code = $"D{Guid.NewGuid():N}"[..12];

        var (status, body) = await ImportAsync(admin, [], [Account(code), Account(code)]);

        status.Should().Be(HttpStatusCode.BadRequest, body);
        body.Should().Contain("contents[1]").And.Contain($"Account code '{code}' is already in use.");
        (await StoredEntriesAsync(BarakoCMS.Accounting.AccountingContentTypes.Account))
            .Where(c => c.Data.TryGetValue("Code", out var stored) && stored?.ToString() == code)
            .Should().BeEmpty("the import is all or nothing");
    }

    private const string PageType = "pagetreeprobe";

    private async Task EnsurePageTypeAsync()
    {
        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        if (await session.Query<ContentTypeDefinition>().AnyAsync(d => d.Name == PageType, Ct))
            return;

        // The same definition PagesModuleTests declares, so whichever class runs first leaves the
        // type the other expects.
        session.Store(new ContentTypeDefinition
        {
            Id = Guid.NewGuid(),
            Name = PageType,
            DisplayName = "Page tree probe",
            IsPubliclyDeliverable = true,
            Fields =
            [
                new FieldDefinition { Name = "Title", DisplayName = "Title", Type = "string" },
                new FieldDefinition { Name = "Slug", DisplayName = "Slug", Type = "slug" },
                new FieldDefinition { Name = "ParentPage", DisplayName = "Parent page", Type = "reference", ReferenceType = PageType },
                new FieldDefinition { Name = "ShowInNavigation", DisplayName = "Show in navigation", Type = "bool" },
                new FieldDefinition { Name = "NavigationOrder", DisplayName = "Navigation order", Type = "int" },
                new FieldDefinition { Name = "Secret", DisplayName = "Secret", Type = "string", Sensitivity = SensitivityLevel.Sensitive },
            ],
        });
        await session.SaveChangesAsync(Ct);
    }

    [Fact]
    public async Task A_page_and_its_parent_in_one_bundle_resolve_to_each_other_whatever_their_order()
    {
        await EnsurePageTypeAsync();
        var admin = await AdminAsync();
        var parentSourceId = Guid.NewGuid();
        var slug = $"imp-{Guid.NewGuid():n}"[..16];

        var (status, body) = await ImportAsync(admin, [],
        [
            new
            {
                id = Guid.NewGuid(),
                contentType = PageType,
                status = "Published",
                data = new Dictionary<string, object>
                {
                    ["Title"] = "Child", ["Slug"] = slug + "-child", ["ParentPage"] = parentSourceId.ToString(),
                },
            },
            new
            {
                id = parentSourceId,
                contentType = PageType,
                status = "Published",
                data = new Dictionary<string, object> { ["Title"] = "Parent", ["Slug"] = slug },
            },
        ]);

        status.Should().Be(HttpStatusCode.OK, body);
        var pages = (await StoredEntriesAsync(PageType))
            .Where(c => c.Data.TryGetValue("Slug", out var stored) && stored?.ToString()?.StartsWith(slug, StringComparison.Ordinal) == true)
            .ToList();
        pages.Should().HaveCount(2);
        var parent = pages.Single(p => p.Data["Slug"].ToString() == slug);
        var child = pages.Single(p => p.Data["Slug"].ToString() == slug + "-child");
        parent.Id.Should().NotBe(parentSourceId, "an import creates new entries");
        child.Data["ParentPage"].ToString().Should().Be(parent.Id.ToString(),
            "a reference to an entry in the same bundle points at that entry as imported");
    }

    [Fact]
    public async Task An_export_carries_each_entry_id_so_a_reference_inside_the_bundle_can_follow_it()
    {
        var type = NewType("impexpid");
        var entry = new Dictionary<string, object> { ["Title"] = "exported" };
        await StoreTypeAsync(new ContentTypeDefinition { Name = type, DisplayName = "Posts", Fields = [Text("Title")] }, entry);
        var storedId = (await StoredEntriesAsync(type)).Single().Id;
        var admin = await AdminAsync();

        var response = await admin.GetAsync($"/api/portability/export?types={type}", Ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var bundle = await response.Content.ReadFromJsonAsync<PortabilityBundle>(ApiJson.Options, Ct);

        bundle!.Contents.Should().ContainSingle().Which.Id.Should().Be(storedId);
    }

    // Bounds -----------------------------------------------------------------------------------

    private static readonly Lock LimitGate = new();
    private static WebApplicationFactory<Program>? _limitHost;

    [Fact]
    public async Task A_bundle_over_the_record_limit_is_refused_before_anything_is_written()
    {
        WebApplicationFactory<Program> host;
        lock (LimitGate)
        {
            host = _limitHost ??= _fixture.WithSetting("Portability:MaxImportRecords", "2");
        }

        var type = NewType("implimit");
        var admin = await AdminAsync(host);

        var (status, body) = await ImportAsync(admin,
            [new ContentTypeDefinition { Name = type, DisplayName = "Posts", Fields = [Text("Title")] }],
            Enumerable.Range(0, 3).Select(i => Record(type, new() { ["Title"] = $"t{i}" })));

        status.Should().Be(HttpStatusCode.BadRequest, body);
        body.Should().Contain("at most 2");
        (await StoredTypeAsync(type)).Should().BeNull();
    }
}
