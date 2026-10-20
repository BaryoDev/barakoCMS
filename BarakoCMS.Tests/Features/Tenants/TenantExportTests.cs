using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using barakoCMS.Core.Interfaces;
using barakoCMS.Infrastructure.Multitenancy;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BarakoCMS.Tests.Features.Tenants;

/// <summary>
/// A tenant can be exported as one unit (#897): every document carrying its id, every global row
/// keyed by its slug, nothing of another tenant, and no stored secret.
/// </summary>
[Collection("Sequential")]
public class TenantExportTests
{
    private readonly IntegrationTestFixture _fixture;

    public TenantExportTests(IntegrationTestFixture fixture) => _fixture = fixture;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string NewSlug() => $"exp-{Guid.NewGuid():N}"[..16];

    private TenantExportWalker Walker(params ITenantDataContributor[] contributors) =>
        new(_fixture.Services.GetRequiredService<IDocumentStore>(), contributors);

    /// <summary>A tenant with one redirect, one membership, one API key and one connector secret.</summary>
    private async Task<string> SeededTenantAsync(string path)
    {
        var slug = NewSlug();
        var store = _fixture.Services.GetRequiredService<IDocumentStore>();

        await using (var registry = store.LightweightSession())
        {
            registry.Store(new Tenant { Id = Guid.NewGuid(), Slug = slug, Name = slug });
            registry.Store(new Membership { Id = Guid.NewGuid(), UserId = Guid.NewGuid(), TenantSlug = slug });
            registry.Store(new ApiKey { Name = $"key-for-{slug}", KeyHash = Guid.NewGuid().ToString("N"), TenantSlug = slug });
            await registry.SaveChangesAsync(Ct);
        }

        await using (var tenant = store.LightweightSession(slug))
        {
            tenant.Store(new UrlRedirect { Id = Guid.NewGuid(), FromPath = path, ToPath = "/elsewhere" });
            tenant.Store(new ConnectorSecret { Id = Guid.NewGuid(), ConnectorId = Guid.NewGuid(), Key = "ApiToken", ProtectedValue = $"cipher-{slug}" });
            await tenant.SaveChangesAsync(Ct);
        }

        return slug;
    }

    private static async Task<List<TenantExportRow>> WalkAsync(TenantExportWalker walker, string slug)
    {
        var rows = new List<TenantExportRow>();
        await foreach (var row in walker.WalkAsync(slug, Ct))
            rows.Add(row);
        return rows;
    }

    [Fact]
    public async Task The_walk_returns_the_documents_and_global_rows_of_the_tenant()
    {
        var slug = await SeededTenantAsync("/mine");

        var rows = await WalkAsync(Walker(), slug);

        rows.Should().NotBeEmpty();
        rows.Should().ContainSingle(r => r.Section == "url_redirects" && r.Scope == TenantExportScopes.Tenant && r.Json.Contains("/mine"));
        rows.Should().ContainSingle(r => r.Section == "memberships" && r.Scope == TenantExportScopes.Global && r.Json.Contains(slug));
        rows.Should().ContainSingle(r => r.Section == "api_keys" && r.Scope == TenantExportScopes.Global && r.Json.Contains(slug));
        rows.Should().ContainSingle(r => r.Section == "tenants" && r.Json.Contains(slug));
    }

    [Fact]
    public async Task The_walk_carries_nothing_of_another_tenant()
    {
        var mine = await SeededTenantAsync("/mine-only");
        var other = await SeededTenantAsync("/theirs-only");

        var rows = await WalkAsync(Walker(), mine);

        rows.Should().NotBeEmpty();
        rows.Should().NotContain(r => r.Json.Contains("/theirs-only") || r.Json.Contains(other));
    }

    [Fact]
    public async Task A_connector_secret_is_withheld_so_its_ciphertext_never_leaves()
    {
        var slug = await SeededTenantAsync("/secret-check");

        var rows = await WalkAsync(Walker(), slug);

        rows.Should().NotBeEmpty();
        rows.Should().NotContain(r => r.Section == "connector_secrets");
        rows.Should().NotContain(r => r.Json.Contains($"cipher-{slug}"));
    }

    [Fact]
    public void Every_tenant_keyed_document_is_in_the_plan_unless_it_is_withheld()
    {
        var walker = Walker();
        var planned = walker.Plan().Select(p => p.Type).ToHashSet();

        var conjoined = walker.Mappings()
            .Where(m => m.TenancyStyle == JasperFx.MultiTenancy.TenancyStyle.Conjoined)
            .Select(m => m.DocumentType)
            .ToList();

        conjoined.Should().NotBeEmpty();
        conjoined.Where(t => !TenantExportWalker.Withheld.Contains(t)).Should().OnlyContain(t => planned.Contains(t));
        planned.Should().Contain([typeof(Membership), typeof(ApiKey), typeof(AuditEvent), typeof(Station), typeof(Tenant)]);
        planned.Should().NotContain(TenantExportWalker.Withheld);
    }

    [Fact]
    public async Task A_module_contributor_adds_its_rows_under_its_section()
    {
        var slug = await SeededTenantAsync("/with-module");

        var rows = await WalkAsync(Walker(new FakeContributor()), slug);

        rows.Should().ContainSingle(r => r.Section == "fake_files" && r.Scope == TenantExportScopes.Module && r.Json.Contains(slug));
    }

    [Fact]
    public async Task The_export_endpoint_streams_lines_that_end_in_a_summary()
    {
        var slug = await SeededTenantAsync("/over-http");
        var client = await SuperAdminAsync();

        var response = await client.GetAsync($"/api/tenants/{slug}/export", Ct);
        var body = await response.Content.ReadAsStringAsync(Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/x-ndjson");
        var lines = body.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        lines.Should().NotBeEmpty();
        lines.Should().OnlyContain(l => JsonDocument.Parse(l).RootElement.ValueKind == JsonValueKind.Object);
        body.Should().Contain("/over-http");
        body.Should().NotContain($"cipher-{slug}");
        var summary = JsonDocument.Parse(lines[^1]).RootElement;
        summary.GetProperty("section").GetString().Should().Be("_summary");
        summary.GetProperty("counts").GetProperty("url_redirects").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task The_export_endpoint_answers_404_for_a_tenant_that_does_not_exist()
    {
        var client = await SuperAdminAsync();

        var response = await client.GetAsync($"/api/tenants/{NewSlug()}/export", Ct);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task The_export_endpoint_refuses_a_caller_without_manage_tenants()
    {
        var slug = await SeededTenantAsync("/refused");
        var client = _fixture.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", _fixture.CreateToken(roles: ["User"], userId: Guid.NewGuid().ToString()));

        var response = await client.GetAsync($"/api/tenants/{slug}/export", Ct);

        response.StatusCode.Should().BeOneOf(HttpStatusCode.Forbidden, HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task An_export_is_recorded_in_the_audit_log_of_the_tenant()
    {
        var slug = await SeededTenantAsync("/audited");
        var client = await SuperAdminAsync();

        (await client.GetAsync($"/api/tenants/{slug}/export", Ct)).StatusCode.Should().Be(HttpStatusCode.OK);

        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        var entries = await session.Query<AuditEvent>().Where(e => e.TenantSlug == slug && e.Action == "tenant.exported").ToListAsync(Ct);
        entries.Should().ContainSingle();
    }

    private async Task<HttpClient> SuperAdminAsync()
    {
        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var userId = Guid.NewGuid();
        session.Store(new User
        {
            Id = userId,
            Username = $"exp-super-{userId:N}"[..20],
            Email = $"exp-super-{userId:N}@example.com",
            RoleIds = [SystemRoles.SuperAdminRoleId],
        });
        await session.SaveChangesAsync(Ct);

        var client = _fixture.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", _fixture.CreateToken(roles: ["SuperAdmin"], userId: userId.ToString()));
        return client;
    }

    private sealed class FakeContributor : ITenantDataContributor
    {
        public string Section => "fake_files";

        public async IAsyncEnumerable<string> ExportAsync(string tenantSlug, [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            yield return $"{{\"tenant\":\"{tenantSlug}\"}}";
        }
    }
}
