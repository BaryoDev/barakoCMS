using System.Net.Http.Json;
using System.Text.Json;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;

namespace BarakoCMS.Tests.Features.Reports;

/// <summary>Shared setup for the report tests: a type with fresh name, entries stored directly, callers by role.</summary>
internal sealed class ReportTestSupport(IntegrationTestFixture factory)
{
    private static int _sequence;

    public async Task<string> TypeAsync()
    {
        var type = $"report{Guid.NewGuid().ToString("n")[..10]}";

        using var scope = factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Store(new ContentTypeDefinition
        {
            Id = Guid.NewGuid(), Name = type, DisplayName = type,
            Fields =
            [
                new FieldDefinition { Name = "Region", DisplayName = "Region", Type = "string" },
                new FieldDefinition { Name = "Amount", DisplayName = "Amount", Type = "decimal" },
                new FieldDefinition { Name = "Notes", DisplayName = "Notes", Type = "string" },
                new FieldDefinition
                {
                    Name = "Salary", DisplayName = "Salary", Type = "decimal", Sensitivity = SensitivityLevel.Sensitive,
                },
            ],
        });
        await session.SaveChangesAsync();
        return type;
    }

    public async Task<Guid> StoreAsync(
        string type, Guid createdBy, string region, decimal amount, decimal salary = 0, DateTime? created = null,
        string? notes = null)
    {
        using var scope = factory.Services.CreateScope();
        await using var session = scope.ServiceProvider.GetRequiredService<IDocumentStore>().LightweightSession();

        var at = created ?? new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(Interlocked.Increment(ref _sequence));
        var content = new Content
        {
            Id = Guid.NewGuid(), ContentType = type, Status = ContentStatus.Published,
            CreatedBy = createdBy, CreatedAt = at, UpdatedAt = at,
            Data = new Dictionary<string, object>
            {
                ["Region"] = region, ["Amount"] = amount, ["Salary"] = salary, ["Notes"] = notes ?? "",
            },
        };
        session.Store(content);
        await session.SaveChangesAsync();
        return content.Id;
    }

    public async Task<HttpClient> SuperAdminAsync()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer", await factory.StoredUserTokenAsync("SuperAdmin"));
        return client;
    }

    public async Task<(HttpClient Client, Guid UserId)> CallerAsync(params ContentTypePermission[] permissions)
    {
        using var scope = factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();

        var role = new Role { Id = Guid.NewGuid(), Name = $"Report_{Guid.NewGuid():n}", Permissions = [.. permissions] };
        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = $"report_{Guid.NewGuid():n}",
            Email = $"report_{Guid.NewGuid():n}@example.com",
            RoleIds = [role.Id],
        };
        session.Store(role);
        session.Store(user);
        await session.SaveChangesAsync();

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer", factory.CreateToken(roles: [role.Name], userId: user.Id.ToString()));
        return (client, user.Id);
    }

    public static async Task SaveReportAsync(HttpClient admin, string name, object definition)
    {
        var res = await admin.PostAsJsonAsync("/api/reports", definition);
        res.IsSuccessStatusCode.Should().BeTrue("saving report {0} returned {1}: {2}",
            name, res.StatusCode, await res.Content.ReadAsStringAsync());
    }

    public static string NewName() => $"r-{Guid.NewGuid().ToString("n")[..12]}";

    public static async Task<JsonElement> JsonAsync(HttpResponseMessage res)
    {
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        return doc.RootElement.Clone();
    }
}
