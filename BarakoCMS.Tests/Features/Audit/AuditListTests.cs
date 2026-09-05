using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using barakoCMS.Models;

namespace BarakoCMS.Tests.Features.Audit;

[Collection("Sequential")]
public class AuditListTests
{
    private readonly IntegrationTestFixture _factory;
    private readonly HttpClient _client;

    public AuditListTests(IntegrationTestFixture factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private async Task SeedAsync(params AuditEvent[] events)
    {
        using var scope = _factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IDocumentStore>();
        using var session = store.LightweightSession();
        foreach (var e in events) session.Store(e);
        await session.SaveChangesAsync();
    }

    private async Task<barakoCMS.Models.PaginatedResponse<barakoCMS.Features.Audit.List.AuditEventDto>> ListAsync(string query)
    {
        // A user that exists, holding the seeded SuperAdmin role. The capability gate answers from
        // the stored user's roles rather than from the claim, and the role-name fallback that used
        // to cover the difference is off by default from 4.0.
        var token = await _factory.StoredUserTokenAsync("SuperAdmin");
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var res = await _client.GetAsync($"/api/audit{query}");
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await res.Content.ReadFromJsonAsync<barakoCMS.Models.PaginatedResponse<barakoCMS.Features.Audit.List.AuditEventDto>>())!;
    }

    [Fact]
    public async Task Non_admin_roles_are_forbidden()
    {
        // Viewer is not a seeded role, so it holds nothing and reaches nothing. That is the point
        // of the test, and it is now true for the reason the endpoint gives rather than by accident.
        var token = await _factory.StoredUserTokenAsync("Viewer");
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var res = await _client.GetAsync("/api/audit");
        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Filters_by_exact_action()
    {
        var tenant = $"tenant_{Guid.NewGuid():N}";
        await SeedAsync(
            new AuditEvent { TenantSlug = tenant, Action = "auth.login.succeeded", CreatedAt = DateTime.UtcNow },
            new AuditEvent { TenantSlug = tenant, Action = "auth.login.failed", CreatedAt = DateTime.UtcNow });

        var result = await ListAsync($"?tenant={tenant}&action=auth.login.failed");

        result.Items.Should().ContainSingle().Which.Action.Should().Be("auth.login.failed");
    }

    [Fact]
    public async Task Filters_by_actor()
    {
        var tenant = $"tenant_{Guid.NewGuid():N}";
        var actorA = Guid.NewGuid();
        var actorB = Guid.NewGuid();
        await SeedAsync(
            new AuditEvent { TenantSlug = tenant, Action = "role.deleted", ActorUserId = actorA, CreatedAt = DateTime.UtcNow },
            new AuditEvent { TenantSlug = tenant, Action = "role.deleted", ActorUserId = actorB, CreatedAt = DateTime.UtcNow });

        var result = await ListAsync($"?tenant={tenant}&actorUserId={actorA}");

        result.Items.Should().ContainSingle().Which.ActorUserId.Should().Be(actorA);
    }

    [Fact]
    public async Task Filters_by_tenant()
    {
        var tenantA = $"tenant_{Guid.NewGuid():N}";
        var tenantB = $"tenant_{Guid.NewGuid():N}";
        await SeedAsync(
            new AuditEvent { TenantSlug = tenantA, Action = "auth.login.succeeded", CreatedAt = DateTime.UtcNow },
            new AuditEvent { TenantSlug = tenantB, Action = "auth.login.succeeded", CreatedAt = DateTime.UtcNow });

        var result = await ListAsync($"?tenant={tenantA}");

        result.Items.Should().OnlyContain(e => e.TenantSlug == tenantA);
    }

    [Fact]
    public async Task Filters_by_date_range()
    {
        var tenant = $"tenant_{Guid.NewGuid():N}";
        var old = DateTime.UtcNow.AddDays(-10);
        var recent = DateTime.UtcNow;
        await SeedAsync(
            new AuditEvent { TenantSlug = tenant, Action = "auth.login.succeeded", CreatedAt = old },
            new AuditEvent { TenantSlug = tenant, Action = "auth.login.succeeded", CreatedAt = recent });

        var result = await ListAsync($"?tenant={tenant}&from={DateTime.UtcNow.AddDays(-1):O}");

        result.Items.Should().ContainSingle().Which.CreatedAt.Should().BeCloseTo(recent, TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task A_row_at_the_window_edge_is_returned_for_a_non_UTC_offset()
    {
        var tenant = $"tenant_{Guid.NewGuid():N}";
        var boundary = new DateTime(2026, 9, 5, 12, 0, 0, DateTimeKind.Utc);
        await SeedAsync(
            new AuditEvent { TenantSlug = tenant, Action = "auth.login.succeeded", CreatedAt = boundary },
            new AuditEvent { TenantSlug = tenant, Action = "auth.login.succeeded", CreatedAt = boundary.AddSeconds(1) });

        // 12:00 UTC as UTC+8, expressed with an offset rather than "Z". A caller in that zone would
        // send exactly this for "everything up to noon UTC". If the comparison used this value
        // as-is instead of converting it to UTC first, it would compare 20:00 against a row stored
        // at 12:00 and both rows above would pass the "to" filter, hiding the bug this test exists
        // to catch.
        var to = new DateTimeOffset(boundary).ToOffset(TimeSpan.FromHours(8)).ToString("yyyy-MM-ddTHH:mm:sszzz");

        var result = await ListAsync($"?tenant={tenant}&to={Uri.EscapeDataString(to)}");

        result.TotalItems.Should().Be(1, "to was {0}, at or before the row exactly on the boundary", to);
        result.Items.Should().ContainSingle().Which.CreatedAt.Should().Be(boundary);
    }

    [Fact]
    public async Task Orders_newest_first_and_paginates()
    {
        var tenant = $"tenant_{Guid.NewGuid():N}";
        var baseTime = DateTime.UtcNow.AddMinutes(-10);
        await SeedAsync(Enumerable.Range(0, 5)
            .Select(i => new AuditEvent { TenantSlug = tenant, Action = $"e.{i}", CreatedAt = baseTime.AddMinutes(i) })
            .ToArray());

        var page1 = await ListAsync($"?tenant={tenant}&page=1&pageSize=2");
        var page2 = await ListAsync($"?tenant={tenant}&page=2&pageSize=2");

        page1.Items.Select(e => e.Action).Should().Equal("e.4", "e.3");
        page2.Items.Select(e => e.Action).Should().Equal("e.2", "e.1");
        page1.TotalItems.Should().Be(5);
    }
}
