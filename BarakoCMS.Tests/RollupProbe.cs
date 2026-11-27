using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using barakoCMS.Infrastructure.Jobs;
using barakoCMS.Infrastructure.Rollups;
using barakoCMS.Models;
using Probe = BarakoCMS.Tests.TokenFieldProbe;

namespace BarakoCMS.Tests;

/// <summary>
/// Types, entries and the drain for the rollup tests. A type is stored straight into the default
/// tenant under names no other test uses, so a test needs no save-time check to be under test.
/// </summary>
internal static class RollupProbe
{
    /// <summary>The tenant the shared fixture's default session writes to.</summary>
    public const string Tenant = "*DEFAULT*";

    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A header type and the line type whose entries it counts.</summary>
    internal sealed record Pair(string Header, string Line);

    public static FieldDefinition Plain(string name, string type, SensitivityLevel level = SensitivityLevel.Public) =>
        new() { Name = name, DisplayName = name, Type = type, Sensitivity = level };

    public static FieldDefinition Reference(string name, string to, SensitivityLevel level = SensitivityLevel.Public) =>
        new() { Name = name, DisplayName = name, Type = "reference", ReferenceType = to, Sensitivity = level };

    public static FieldDefinition Rollup(
        string name, string type, string from, string via, string fn, string? field = null,
        SensitivityLevel level = SensitivityLevel.Public, int? round = null) => new()
    {
        Name = name,
        DisplayName = name,
        Type = type,
        Sensitivity = level,
        Rollup = new RollupDefinition { From = from, Via = via, Fn = fn, Field = field, Round = round },
    };

    /// <summary>
    /// A header with a count, a sum and a peak of its lines' Hours, and the line type with a Teacher
    /// reference and the Hours. The rollups sit at <paramref name="level"/>.
    /// </summary>
    public static async Task<Pair> StorePairAsync(
        IntegrationTestFixture fixture, SensitivityLevel level = SensitivityLevel.Public, bool deliverable = false,
        SensitivityLevel hours = SensitivityLevel.Public, SensitivityLevel teacher = SensitivityLevel.Public)
    {
        var header = "rh" + Guid.NewGuid().ToString("n")[..10];
        var line = "rl" + Guid.NewGuid().ToString("n")[..10];
        using var scope = fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Store(new ContentTypeDefinition
        {
            Id = Guid.NewGuid(), Name = line, DisplayName = line,
            Fields = [Reference("Teacher", header, teacher), Plain("Hours", "decimal", hours)],
        });
        session.Store(new ContentTypeDefinition
        {
            Id = Guid.NewGuid(), Name = header, DisplayName = header, IsPubliclyDeliverable = deliverable,
            Fields =
            [
                Plain("Name", "string"),
                Rollup("Count", "int", line, "Teacher", "count", null, level),
                Rollup("Total", "decimal", line, "Teacher", "sum", "Hours", level),
                Rollup("Peak", "decimal", line, "Teacher", "max", "Hours", level),
            ],
        });
        await session.SaveChangesAsync(Ct);
        return new Pair(header, line);
    }

    public static Task<Guid> HeaderAsync(IntegrationTestFixture fixture, string admin, Pair pair, ContentStatus status = ContentStatus.Published) =>
        Probe.CreateAsync(fixture, admin, pair.Header, new() { ["Name"] = "h" }, status);

    public static Task<Guid> LineAsync(
        IntegrationTestFixture fixture, string admin, Pair pair, Guid teacher, decimal hours, ContentStatus status = ContentStatus.Published) =>
        Probe.CreateAsync(fixture, admin, pair.Line, new() { ["Teacher"] = teacher.ToString(), ["Hours"] = hours }, status);

    /// <summary>An entry written through the writer with the status and level given, as a write that goes around the API leaves it.</summary>
    public static async Task<Guid> SeedAsync(
        IntegrationTestFixture fixture, string type, Dictionary<string, object> data,
        ContentStatus status = ContentStatus.Published, SensitivityLevel level = SensitivityLevel.Public)
    {
        using var scope = fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var writer = scope.ServiceProvider.GetRequiredService<barakoCMS.Core.Interfaces.IContentWriter>();
        var id = Guid.NewGuid();
        await writer.CreateAsync(
            new barakoCMS.Events.ContentCreated(id, type, data, status, Guid.NewGuid(), null, level, DateTime.UtcNow), Ct);
        await session.SaveChangesAsync(Ct);
        return id;
    }

    public static Task<Guid> SeedLineAsync(
        IntegrationTestFixture fixture, Pair pair, Guid teacher, decimal hours,
        ContentStatus status = ContentStatus.Published, SensitivityLevel level = SensitivityLevel.Public) =>
        SeedAsync(fixture, pair.Line, new() { ["Teacher"] = teacher.ToString(), ["Hours"] = hours }, status, level);

    /// <summary>Runs the drain over the default tenant, as the queued job would.</summary>
    public static async Task DrainAsync(IntegrationTestFixture fixture)
    {
        var store = fixture.Services.GetRequiredService<IDocumentStore>();
        var configuration = fixture.Services.GetRequiredService<IConfiguration>();
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await RollupDrain.RunAsync(store, configuration, Tenant, Ct);
                return;
            }
            catch (Exception e) when (attempt < 5 && TestHelpers.LostConcurrencyCheck(e))
            {
                // A write to the header committed after the drain read it. The next run reads it again.
            }
        }
    }

    /// <summary>The ids that have a mark waiting.</summary>
    public static async Task<List<Guid>> MarkedAsync(IntegrationTestFixture fixture)
    {
        using var scope = fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        return (await session.Query<RollupMark>().ToListAsync(Ct)).Select(m => m.Id).ToList();
    }

    public static Task<Content> StoredAsync(IntegrationTestFixture fixture, Guid id) => Probe.StoredAsync(fixture, id);

    /// <summary>A stored number, or null when the entry holds none.</summary>
    public static decimal? Num(Content entry, string field)
    {
        var key = entry.Data.Keys.FirstOrDefault(k => string.Equals(k, field, StringComparison.OrdinalIgnoreCase));
        var text = key is null || entry.Data[key] is null ? null : Convert.ToString(entry.Data[key], CultureInfo.InvariantCulture);
        return decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : null;
    }

    /// <summary>The entry's rollups, read from the document and not from a response.</summary>
    public static async Task<(decimal? Count, decimal? Total, decimal? Peak)> ValuesAsync(IntegrationTestFixture fixture, Guid header)
    {
        var entry = await StoredAsync(fixture, header);
        return (Num(entry, "Count"), Num(entry, "Total"), Num(entry, "Peak"));
    }

    /// <summary>The entry's data as an anonymous caller reads it from the public list of its type.</summary>
    public static async Task<JsonElement?> PublicDataAsync(IntegrationTestFixture fixture, string type, Guid id)
    {
        using var client = fixture.CreateClient();
        var res = await client.GetAsync($"/api/public/{type}?pageSize=100", Ct);
        var body = await res.Content.ReadAsStringAsync(Ct);
        res.StatusCode.Should().Be(HttpStatusCode.OK, body);
        foreach (var item in JsonDocument.Parse(body).RootElement.GetProperty("items").EnumerateArray())
        {
            if (item.GetProperty("id").GetGuid() == id)
            {
                return item.GetProperty("data").Clone();
            }
        }

        return null;
    }

    /// <summary>The value an anonymous caller is shown for a field of the entry, or null when none is shown.</summary>
    public static async Task<decimal?> PublicNumberAsync(IntegrationTestFixture fixture, string type, Guid id, string field)
    {
        var data = await PublicDataAsync(fixture, type, id);
        data.Should().NotBeNull("the entry is published and its type is delivered");
        foreach (var property in data!.Value.EnumerateObject())
        {
            if (string.Equals(property.Name, field, StringComparison.OrdinalIgnoreCase)
                && property.Value.ValueKind == JsonValueKind.Number)
            {
                return property.Value.GetDecimal();
            }
        }

        return null;
    }

    /// <summary>A stored user who may read the types given, with the system capability given or none.</summary>
    public static Task<string> UserAsync(IntegrationTestFixture fixture, string? capability, params string[] types) =>
        ReaderAsync(fixture, capability, types.Select(t => (t, (List<string>?)null)).ToArray());

    /// <summary>
    /// A stored user who may read each type given, and only the fields listed with it (every field
    /// when the list is null), with the system capability given or none.
    /// </summary>
    public static Task<string> ReaderAsync(
        IntegrationTestFixture fixture, string? capability, params (string Type, List<string>? Fields)[] reads) =>
        ReaderWithAsync(fixture, capability is null ? [] : [capability], reads);

    /// <summary>The same, with each of the system capabilities given.</summary>
    public static async Task<string> ReaderWithAsync(
        IntegrationTestFixture fixture, string[] capabilities, params (string Type, List<string>? Fields)[] reads)
    {
        using var scope = fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var role = new Role
        {
            Id = Guid.NewGuid(),
            Name = $"dbrole_{Guid.NewGuid():N}",
            SystemCapabilities = capabilities.Where(SystemCapabilities.IsKnown).ToList(),
            Permissions = reads.Select(r => new ContentTypePermission
            {
                ContentTypeSlug = r.Type,
                Read = new PermissionRule { Enabled = true, ReadableFields = r.Fields },
                Create = new PermissionRule { Enabled = false },
                Update = new PermissionRule { Enabled = false },
                Delete = new PermissionRule { Enabled = false },
            }).ToList(),
        };
        session.Store(role);
        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = $"user_{Guid.NewGuid()}",
            Email = $"{Guid.NewGuid()}@example.com",
            RoleIds = [role.Id],
        };
        session.Store(user);
        await session.SaveChangesAsync(Ct);
        return fixture.CreateToken([role.Name], user.Id.ToString());
    }

    /// <summary>Runs a stored field change to its end, a batch at a time.</summary>
    public static async Task RunToEndAsync(IntegrationTestFixture fixture, Guid id, int? maxBatches = null, int batchSize = 200)
    {
        var store = fixture.Services.GetRequiredService<IDocumentStore>();
        var configuration = fixture.Services.GetRequiredService<IConfiguration>();
        for (var attempt = 0; attempt < 30; attempt++)
        {
            try
            {
                if ((await FieldChanges.RunAsync(store, configuration, Tenant, id, maxBatches, Ct, batchSize))?.State == FieldChangeState.Completed)
                {
                    return;
                }
            }
            catch (Exception e) when (TestHelpers.LostConcurrencyCheck(e))
            {
                // The queued job committed the same batch first; the next run reads its progress.
            }
        }

        throw new Xunit.Sdk.XunitException($"Field change {id} did not complete.");
    }

    /// <summary>The recompute the definition write started for the type, run to its end.</summary>
    public static async Task RunStartedRecomputeAsync(IntegrationTestFixture fixture, string type)
    {
        var change = await ChangeAsync(fixture, type, FieldChangeKind.Recompute);
        change.Should().NotBeNull("the definition write started a recompute");
        await RunToEndAsync(fixture, change!.Id);
    }

    public static async Task<FieldChange?> ChangeAsync(IntegrationTestFixture fixture, string type, FieldChangeKind kind)
    {
        using var scope = fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        return await session.Query<FieldChange>().Where(c => c.ContentType == type && c.Kind == kind).FirstOrDefaultAsync(Ct);
    }

    public static async Task<FieldChange> StoreChangeAsync(IntegrationTestFixture fixture, FieldChange change)
    {
        change.Id = Guid.NewGuid();
        change.State = FieldChangeState.Running;
        change.StartedAt = DateTimeOffset.UtcNow;
        change.UpdatedAt = DateTimeOffset.UtcNow;
        using var scope = fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Store(change);
        await session.SaveChangesAsync(Ct);
        return change;
    }

    public static async Task<FieldChange> StoredChangeAsync(IntegrationTestFixture fixture, Guid id)
    {
        using var scope = fixture.Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<IQuerySession>().LoadAsync<FieldChange>(id, Ct))!;
    }

    public static HttpClient Client(IntegrationTestFixture fixture, string token) => Probe.ClientFor(fixture, token);

    public static Task<HttpResponseMessage> PostAsync(HttpClient client, string url, object body) =>
        client.PostAsJsonAsync(url, body, Ct);
}
