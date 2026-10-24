using System.Text.Json;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using barakoCMS.Data;
using barakoCMS.Models;
using static BarakoCMS.Tests.Features.Config.ConfigCallers;

namespace BarakoCMS.Tests.Features.Config;

/// <summary>
/// <c>Seed:File</c>: the seeder applies a config document with tenants, domains and members on the
/// first start, and a second start with the same file changes nothing (#905).
/// </summary>
[Collection("Integration3")]
public class SeedFileTests
{
    private readonly IntegrationTestFixture _fixture;
    private readonly ConfigCallers _callers;

    public SeedFileTests(IntegrationTestFixture fixture)
    {
        _fixture = fixture;
        _callers = new ConfigCallers(fixture);
    }

    private sealed class HostShim(IServiceProvider services) : IHost
    {
        public IServiceProvider Services { get; } = services;

        public void Dispose() { }

        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private static string Unique(string prefix) => $"{prefix}{Guid.NewGuid():n}"[..(prefix.Length + 10)];

    private async Task<string> FileAsync(object document)
    {
        var path = Path.Combine(Path.GetTempPath(), $"seed-{Guid.NewGuid():n}.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(document), Ct);
        return path;
    }

    /// <summary>A start of the host with the file named, and the demo content left out.</summary>
    private Task StartAsync(string path) =>
        DataSeeder.SeedAsync(new HostShim(_fixture.WithSettings(new Dictionary<string, string?>
        {
            ["Seed:File"] = path,
            ["Seed:DemoContent"] = "false",
        }).Services));

    private static object Document(string slug, string domain, string email, string role, string type) => new
    {
        version = 1,
        tenants = new Dictionary<string, object>
        {
            [slug] = new
            {
                name = "Seeded Shop",
                domains = new[] { domain },
                members = new[] { new { email, roles = new[] { role } } },
                config = new
                {
                    roles = new Dictionary<string, object> { [role] = new { description = "Runs the front desk" } },
                    contentTypes = new Dictionary<string, object> { [type] = Type(type, ("Title", "string")) },
                },
            },
        },
    };

    [Fact]
    public async Task A_seed_file_creates_its_tenant_domain_and_member_and_a_second_start_changes_nothing()
    {
        var slug = Unique("seed-");
        var domain = $"{slug}.example.com";
        var email = $"{slug}@example.com";
        var role = Unique("Seed Desk ");
        var type = Unique("customer");
        var path = await FileAsync(Document(slug, domain, email, role, type));

        await StartAsync(path);

        var tenant = await _callers.InTenantAsync(null, s => s.Query<Tenant>().SingleOrDefaultAsync(t => t.Slug == slug, Ct));
        tenant.Should().NotBeNull("the file declares the tenant");
        tenant!.Domains.Should().Equal(domain);
        var user = await _callers.InTenantAsync(null, s => s.Query<User>().SingleOrDefaultAsync(u => u.Email == email, Ct));
        user.Should().NotBeNull("the member is created by email");
        var seededRole = (await _callers.InTenantAsync(null, s => s.Query<Role>().SingleAsync(r => r.Name == role, Ct)))!;
        seededRole.TenantSlug.Should().Be(slug, "a role the tenant's config creates is that tenant's");
        var roleId = seededRole.Id;
        var membership = await _callers.InTenantAsync(null, s => s.Query<Membership>().SingleOrDefaultAsync(m => m.UserId == user!.Id && m.TenantSlug == slug, Ct));
        membership.Should().NotBeNull();
        membership!.RoleIds.Should().Equal(roleId);
        (await _callers.InTenantAsync(slug, s => s.Query<ContentTypeDefinition>().AnyAsync(t => t.Name == type, Ct)))
            .Should().BeTrue("the tenant's own config is applied inside it");

        var before = await SnapshotAsync(slug);
        await StartAsync(path);
        var after = await SnapshotAsync(slug);

        after.Should().Be(before, "a second start with the same file changes nothing");
    }

    [Fact]
    public async Task A_second_start_does_not_undo_a_change_made_since()
    {
        var slug = Unique("seed-");
        var email = $"{slug}@example.com";
        var path = await FileAsync(Document(slug, $"{slug}.example.com", email, Unique("Seed Desk "), Unique("visit")));
        await StartAsync(path);

        await using (var session = _fixture.Services.GetRequiredService<IDocumentStore>().LightweightSession())
        {
            var tenant = await session.Query<Tenant>().SingleAsync(t => t.Slug == slug, Ct);
            tenant.Name = "Renamed in the console";
            session.Store(tenant);
            await session.SaveChangesAsync(Ct);
        }

        await StartAsync(path);

        (await _callers.InTenantAsync(null, s => s.Query<Tenant>().SingleAsync(t => t.Slug == slug, Ct)))!.Name
            .Should().Be("Renamed in the console", "the file was applied once, and the restart leaves what the API changed");
    }

    [Fact]
    public async Task A_file_that_cannot_be_applied_stops_the_seed_and_writes_nothing()
    {
        var slug = Unique("seed-");
        var path = await FileAsync(new
        {
            version = 1,
            tenants = new Dictionary<string, object> { [slug] = new { domains = new[] { "https://not-a-host.example.com/path" } } },
        });

        var seeding = () => StartAsync(path);

        (await seeding.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain(slug);
        (await _callers.InTenantAsync(null, s => s.Query<Tenant>().AnyAsync(t => t.Slug == slug, Ct))).Should().BeFalse();
    }

    private static object WorkflowFile(string slug, string type, Dictionary<string, object> workflows, Dictionary<string, object>? extraTypes = null)
    {
        var types = new Dictionary<string, object> { [type] = Type(type, ("Title", "string")) };
        foreach (var (name, definition) in extraTypes ?? [])
        {
            types[name] = definition;
        }

        return new
        {
            version = 1,
            tenants = new Dictionary<string, object> { [slug] = new { name = slug, config = new { contentTypes = types, workflows } } },
        };
    }

    private static object Hook(string type) => new
    {
        triggerContentTypes = new[] { type }, triggerEvents = new[] { "Created" },
        actions = new[] { new { type = "Webhook", parameters = new Dictionary<string, string> { ["Url"] = "https://hooks.example.com/seed" } } },
    };

    private async Task<string?> RecordedHashAsync() =>
        (await _callers.InTenantAsync(null, s => s.Query<SystemSetting>().FirstOrDefaultAsync(x => x.Key == "Seed:File:Sha256", Ct)))?.Value;

    private static string Sha(string path) =>
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)));

    [Fact]
    public async Task An_edited_file_whose_change_is_destructive_skips_that_change_and_applies_the_rest()
    {
        var slug = Unique("seed-");
        var type = Unique("intake");
        var kept = Unique("keep-");
        await StartAsync(await FileAsync(WorkflowFile(slug, type, new() { [kept] = Hook(type) })));

        var added = Unique("added");
        var edited = await FileAsync(WorkflowFile(slug, type, new(), new() { [added] = Type(added, ("Title", "string")) }));
        await StartAsync(edited);

        (await _callers.InTenantAsync(slug, s => s.Query<WorkflowDefinition>().AnyAsync(w => w.Name == kept, Ct)))
            .Should().BeTrue("deleting the workflow is destructive, so the seeder skips it");
        (await _callers.InTenantAsync(slug, s => s.Query<ContentTypeDefinition>().AnyAsync(t => t.Name == added, Ct)))
            .Should().BeTrue("and still applies the rest of the edited file");
        (await RecordedHashAsync()).Should().Be(Sha(edited), "the edited file counts as applied");
    }

    [Fact]
    public async Task A_section_written_for_a_signed_in_caller_is_skipped_and_the_rest_applied()
    {
        var slug = Unique("seed-");
        var type = Unique("ward");
        var path = await FileAsync(new
        {
            version = 1,
            tenants = new Dictionary<string, object>
            {
                [slug] = new
                {
                    name = slug,
                    config = new
                    {
                        contentTypes = new Dictionary<string, object> { [type] = Type(type, ("Title", "string")) },
                        siteSettings = new Dictionary<string, object> { ["site"] = new { data = new Dictionary<string, object> { ["Name"] = "x" } } },
                    },
                },
            },
        });

        await StartAsync(path);

        (await _callers.InTenantAsync(slug, s => s.Query<ContentTypeDefinition>().AnyAsync(t => t.Name == type, Ct)))
            .Should().BeTrue("siteSettings is skipped, not a reason to stop the seed");
        (await RecordedHashAsync()).Should().Be(Sha(path));
    }

    [Fact]
    public async Task A_section_relying_on_a_skipped_destructive_change_stops_the_seed_and_says_why()
    {
        var slug = Unique("seed-");
        var type = Unique("chart");
        var role = Unique("Seed Reader ");
        Dictionary<string, object> Roles() => new() { [role] = new { description = "Reads charts" } };
        object File(string field, object? rules) => new
        {
            version = 1,
            tenants = new Dictionary<string, object>
            {
                [slug] = new
                {
                    name = slug,
                    config = rules is null
                        ? (object)new { roles = Roles(), contentTypes = new Dictionary<string, object> { [type] = Type(type, ("Title", "string"), (field, "string")) } }
                        : new { roles = Roles(), contentTypes = new Dictionary<string, object> { [type] = Type(type, ("Title", "string"), (field, "string")) }, permissionRules = rules },
                },
            },
        };
        await StartAsync(await FileAsync(File("Alpha", null)));

        // Beta replaces Alpha: one change to the type, destructive because Alpha goes, so all of it is
        // skipped, Beta included, and the rule naming Beta has nothing to name.
        var edited = await FileAsync(File("Beta", new Dictionary<string, object>
        {
            [$"{role}/{type}"] = new { read = new { enabled = true, readableFields = new[] { "Beta" } } },
        }));
        var seeding = () => StartAsync(edited);

        var message = (await seeding.Should().ThrowAsync<InvalidOperationException>()).Which.Message;
        message.Should().Contain("'Beta'").And.Contain("A destructive change was skipped in this tenant");
        (await _callers.InTenantAsync(slug, s => s.Query<ContentTypeDefinition>().SingleAsync(t => t.Name == type, Ct)))!
            .Fields.Select(f => f.Name).Should().Equal("Title", "Alpha");
        (await RecordedHashAsync()).Should().NotBe(Sha(edited));
    }

    [Fact]
    public async Task A_refusal_in_the_middle_keeps_what_went_before_records_no_hash_and_a_fixed_file_converges()
    {
        var first = Unique("seed-");
        var second = Unique("seed-");
        var good = Unique("good");
        var bad = new ContentTypeDefinition { Id = Guid.NewGuid(), Name = Unique("bad"), DisplayName = "Bad", Fields = [new FieldDefinition { Name = "lowercase", Type = "string" }] };
        object File(object secondTypes) => new
        {
            version = 1,
            tenants = new Dictionary<string, object>
            {
                [first] = new { name = first, config = new { contentTypes = new Dictionary<string, object> { [good] = Type(good, ("Title", "string")) } } },
                [second] = new { name = second, config = new { contentTypes = secondTypes } },
            },
        };
        var broken = await FileAsync(File(new Dictionary<string, object> { [bad.Name] = bad }));

        var seeding = () => StartAsync(broken);
        (await seeding.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("PascalCase");

        (await _callers.InTenantAsync(first, s => s.Query<ContentTypeDefinition>().AnyAsync(t => t.Name == good, Ct)))
            .Should().BeTrue("the tenant before the refusal committed as it went");
        (await RecordedHashAsync()).Should().NotBe(Sha(broken), "a file that stopped half way is not recorded as applied");

        var fixedFile = await FileAsync(File(new Dictionary<string, object> { [good] = Type(good, ("Title", "string")) }));
        await StartAsync(fixedFile);

        (await _callers.InTenantAsync(second, s => s.Query<ContentTypeDefinition>().AnyAsync(t => t.Name == good, Ct))).Should().BeTrue();
        (await RecordedHashAsync()).Should().Be(Sha(fixedFile), "the next start applies it from the top and records it");
    }

    private async Task<string> SnapshotAsync(string slug)
    {
        var store = _fixture.Services.GetRequiredService<IDocumentStore>();
        await using var session = store.QuerySession();
        var tenant = await session.Query<Tenant>().SingleAsync(t => t.Slug == slug, Ct);
        var tenantVersion = (await session.MetadataForAsync(tenant, Ct))?.CurrentVersion;
        var memberships = await session.Query<Membership>().Where(m => m.TenantSlug == slug).ToListAsync(Ct);
        var membershipVersions = new List<Guid?>();
        foreach (var membership in memberships)
        {
            membershipVersions.Add((await session.MetadataForAsync(membership, Ct))?.CurrentVersion);
        }

        var users = await session.Query<User>().CountAsync(Ct);
        var audit = await session.Query<AuditEvent>().CountAsync(e => e.TenantSlug == slug || e.TenantSlug == Tenant.DefaultSlug, Ct);
        await using var tenantSession = store.QuerySession(slug);
        var types = await tenantSession.Query<ContentTypeDefinition>().CountAsync(Ct);

        memberships.Should().NotBeEmpty("a snapshot of a seeded tenant has its member in it");
        return $"{tenantVersion}|{string.Join(",", membershipVersions)}|{users}|{audit}|{types}";
    }

    [Fact]
    public async Task A_member_naming_a_role_only_another_tenant_has_stops_the_seed()
    {
        var owner = Unique("seed-");
        var slug = Unique("seed-");
        var role = Unique("Elsewhere ");
        await _callers.StoreInTenantAsync(null, new Role { Id = Guid.NewGuid(), Name = role, TenantSlug = owner });
        var email = $"{slug}@example.com";
        var path = await FileAsync(new
        {
            version = 1,
            tenants = new Dictionary<string, object> { [slug] = new { name = slug, members = new[] { new { email, roles = new[] { role } } } } },
        });

        var seeding = () => StartAsync(path);

        var message = (await seeding.Should().ThrowAsync<InvalidOperationException>()).Which.Message;
        message.Should().Contain("names roles that do not exist").And.Contain(role);
        (await _callers.InTenantAsync(null, s => s.Query<User>().AnyAsync(u => u.Email == email, Ct)))
            .Should().BeFalse("the member is not added holding another tenant's role");
    }

    [Fact]
    public async Task A_member_naming_a_role_the_tenant_and_the_platform_both_have_gets_the_tenants()
    {
        var slug = Unique("seed-");
        var name = Unique("Front Desk ");
        var platform = new Role { Id = Guid.NewGuid(), Name = name, IsPlatform = true };
        var own = new Role { Id = Guid.NewGuid(), Name = name, TenantSlug = slug };
        await _callers.StoreInTenantAsync(null, platform, own);
        var email = $"{slug}@example.com";

        await StartAsync(await FileAsync(new
        {
            version = 1,
            tenants = new Dictionary<string, object> { [slug] = new { name = slug, members = new[] { new { email, roles = new[] { name } } } } },
        }));

        var user = await _callers.InTenantAsync(null, s => s.Query<User>().SingleAsync(u => u.Email == email, Ct));
        var membership = await _callers.InTenantAsync(null, s => s.Query<Membership>().SingleAsync(m => m.UserId == user!.Id && m.TenantSlug == slug, Ct));
        membership!.RoleIds.Should().Equal(new[] { own.Id }, "the tenant's own role comes ahead of a platform role of the same name");
    }
}
