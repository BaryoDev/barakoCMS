using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using barakoCMS.Core.Validation;
using barakoCMS.Data;
using barakoCMS.Events;
using barakoCMS.Features.Workflows;
using barakoCMS.Features.Workflows.Actions;
using barakoCMS.Infrastructure.Jobs;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using BarakoCMS.Tests.Features.Collections;
using BarakoCMS.Tests.Features.Tenants;
using Probe = BarakoCMS.Tests.TokenFieldProbe;

namespace BarakoCMS.Tests;

/// <summary>
/// One entry through each write path stores the value its formula gives: the HTTP writes, the
/// writes that go around them, the jobs, and the places the server writes by itself.
/// </summary>
/// <remarks>
/// Types are stored straight into the database, so a formula needs no save-time check to be under
/// test here. A path that works the value out is shown by a stored value that is not the one sent.
/// </remarks>
[Collection("Integration1")]
public class ComputedExpressionWriterTests
{
    /// <summary>Qty 3 and Cost 4 under the formula the order type starts with.</summary>
    private const string Worked = "12";

    private readonly IntegrationTestFixture _fixture;

    public ComputedExpressionWriterTests(IntegrationTestFixture fixture) => _fixture = fixture;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static FieldDefinition Plain(string name, string type) =>
        new() { Name = name, DisplayName = name, Type = type };

    private static FieldDefinition Formula(string name, string type, string expression) => new()
    {
        Name = name, DisplayName = name, Type = type, Computed = new ComputedDefinition { Expression = expression },
    };

    private static FieldDefinition[] Order(string expression = "Qty * Cost") =>
    [
        Plain("Qty", "decimal"),
        Plain("Cost", "decimal"),
        Plain("Note", "string"),
        Formula("Total", "decimal", expression),
    ];

    private static FieldDefinition[] Person() =>
    [
        Plain("Name", "string"),
        Formula("Label", "string", "upper(Name)"),
    ];

    private static string? Computed(Content entry, string field = "Total") =>
        entry.Data.TryGetValue(field, out var value) ? value?.ToString() : null;

    private static ContentTypeDefinition Definition(string name, FieldDefinition[] fields) => new()
    {
        Id = Guid.NewGuid(), Name = name, DisplayName = name, Fields = fields.ToList(),
    };

    private static string NewName() => "wr" + Guid.NewGuid().ToString("n")[..10];

    private async Task<List<Content>> EntriesAsync(string type, string? slug = null)
    {
        var store = _fixture.Services.GetRequiredService<IDocumentStore>();
        await using var session = slug is null ? store.QuerySession() : store.QuerySession(slug);
        return (await session.Query<Content>().Where(c => c.ContentType == type).ToListAsync(Ct)).ToList();
    }

    // The HTTP writes.

    [Fact]
    public async Task A_create_stores_the_value_the_formula_gives()
    {
        var type = await Probe.StoreTypeAsync(_fixture, "wr", deliverable: false, Order());
        var admin = await Probe.AdminAsync(_fixture);

        var id = await Probe.CreateAsync(_fixture, admin, type, new() { ["Qty"] = 3, ["Cost"] = 4, ["Note"] = "a" }, ContentStatus.Published);

        Computed(await Probe.StoredAsync(_fixture, id)).Should().Be(Worked);
    }

    [Fact]
    public async Task An_update_works_the_value_out_again_and_refuses_one_sent_in_its_place()
    {
        var type = await Probe.StoreTypeAsync(_fixture, "wr", deliverable: false, Order());
        var admin = await Probe.AdminAsync(_fixture);
        var id = await Probe.CreateAsync(_fixture, admin, type, new() { ["Qty"] = 3, ["Cost"] = 4 }, ContentStatus.Published);

        var forged = await Probe.UpdateAsync(_fixture, admin, id, new() { ["Qty"] = 5, ["Cost"] = 4, ["Total"] = 999 });
        forged.StatusCode.Should().Be(HttpStatusCode.BadRequest, await forged.Content.ReadAsStringAsync(Ct));
        (await forged.Content.ReadAsStringAsync(Ct)).Should().Contain("computed by the server");
        Computed(await Probe.StoredAsync(_fixture, id)).Should().Be(Worked, "the refused write changed nothing");

        var changed = await Probe.UpdateAsync(_fixture, admin, id, new() { ["Qty"] = 5, ["Cost"] = 4 });
        changed.StatusCode.Should().Be(HttpStatusCode.OK, await changed.Content.ReadAsStringAsync(Ct));
        Computed(await Probe.StoredAsync(_fixture, id)).Should().Be("20");
    }

    /// <summary>
    /// The formula changes after the version being restored was written, so the stored value is
    /// still the old one and the restore is not refused for carrying it. What the entry ends with
    /// is the value the formula gives over the restored inputs.
    /// </summary>
    [Fact]
    public async Task A_rollback_works_the_value_out_from_the_restored_inputs()
    {
        var type = await Probe.StoreTypeAsync(_fixture, "wr", deliverable: false, Order());
        var admin = await Probe.AdminAsync(_fixture);
        var id = await Probe.CreateAsync(_fixture, admin, type, new() { ["Qty"] = 3, ["Cost"] = 4, ["Note"] = "a" }, ContentStatus.Published);
        (await Probe.UpdateAsync(_fixture, admin, id, new() { ["Qty"] = 3, ["Cost"] = 4, ["Note"] = "b" }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        using (var scope = _fixture.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            var stored = await session.Query<ContentTypeDefinition>().FirstAsync(d => d.Name == type, Ct);
            stored.Fields.Single(f => f.Name == "Total").Computed!.Expression = "Qty + Cost";
            session.Store(stored);
            await session.SaveChangesAsync(Ct);
        }

        var client = Probe.ClientFor(_fixture, admin);
        var history = JsonDocument.Parse(await (await client.GetAsync($"/api/contents/{id}/history", Ct)).Content.ReadAsStringAsync(Ct));
        var first = history.RootElement.GetProperty("items")[0].GetProperty("versionId").GetGuid();

        var rollback = await client.PostAsync($"/api/contents/{id}/rollback/{first}", null, Ct);

        rollback.StatusCode.Should().Be(HttpStatusCode.OK, await rollback.Content.ReadAsStringAsync(Ct));
        var after = await Probe.StoredAsync(_fixture, id);
        after.Data["Note"].ToString().Should().Be("a", "the restore put the old version back");
        Computed(after).Should().Be("7");
    }

    private static FieldDefinition[] FnOrder() =>
    [
        Plain("Qty", "decimal"),
        Plain("Cost", "decimal"),
        Plain("Note", "string"),
        new() { Name = "Total", DisplayName = "Total", Type = "decimal", Computed = new ComputedDefinition { Fn = "add", Args = ["Qty", "Cost"] } },
    ];

    private async Task<HttpResponseMessage> RollbackToFirstAsync(string admin, Guid id)
    {
        var client = Probe.ClientFor(_fixture, admin);
        var history = JsonDocument.Parse(await (await client.GetAsync($"/api/contents/{id}/history", Ct)).Content.ReadAsStringAsync(Ct));
        var first = history.RootElement.GetProperty("items")[0].GetProperty("versionId").GetGuid();
        return await client.PostAsync($"/api/contents/{id}/rollback/{first}", null, Ct);
    }

    /// <summary>
    /// The restored version holds the value its own inputs gave. The entry holds other inputs now, so
    /// that value differs from the stored one, and the restore used to be refused for it.
    /// </summary>
    [Theory]
    [InlineData(false, "12")]
    [InlineData(true, "7")]
    public async Task A_rollback_across_an_input_change_works_the_value_out_from_the_restored_inputs(bool functionRule, string expected)
    {
        var type = await Probe.StoreTypeAsync(_fixture, "wr", deliverable: false, functionRule ? FnOrder() : Order());
        var admin = await Probe.AdminAsync(_fixture);
        var id = await Probe.CreateAsync(_fixture, admin, type, new() { ["Qty"] = 3, ["Cost"] = 4, ["Note"] = "a" }, ContentStatus.Published);
        (await Probe.UpdateAsync(_fixture, admin, id, new() { ["Qty"] = 5, ["Cost"] = 4, ["Note"] = "b" }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        Computed(await Probe.StoredAsync(_fixture, id)).Should().NotBe(expected, "the inputs changed, so the stored value moved");

        var rollback = await RollbackToFirstAsync(admin, id);

        rollback.StatusCode.Should().Be(HttpStatusCode.OK, await rollback.Content.ReadAsStringAsync(Ct));
        var after = await Probe.StoredAsync(_fixture, id);
        after.Data["Note"].ToString().Should().Be("a");
        after.Data["Qty"].ToString().Should().Be("3");
        Computed(after).Should().Be(expected);
    }

    [Fact]
    public async Task A_rollback_after_a_completed_recompute_works_the_value_out_from_the_restored_inputs()
    {
        var type = await Probe.StoreTypeAsync(_fixture, "wr", deliverable: false, Order());
        var admin = await Probe.AdminAsync(_fixture);
        var id = await Probe.CreateAsync(_fixture, admin, type, new() { ["Qty"] = 3, ["Cost"] = 4, ["Note"] = "a" }, ContentStatus.Published);
        (await Probe.UpdateAsync(_fixture, admin, id, new() { ["Qty"] = 3, ["Cost"] = 4, ["Note"] = "b" }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var store = _fixture.Services.GetRequiredService<IDocumentStore>();
        var slug = JasperFx.StorageConstants.DefaultTenantId;
        await using (var session = store.LightweightSession(slug))
        {
            var definition = await session.Query<ContentTypeDefinition>().FirstAsync(d => d.Name == type, Ct);
            var before = definition.Fields.Select(f => new FieldDefinition { Name = f.Name, DisplayName = f.DisplayName, Type = f.Type, Computed = f.Computed }).ToList();
            definition.Fields.Single(f => f.Name == "Total").Computed = new ComputedDefinition { Expression = "Qty + Cost" };
            session.Store(definition);
            (await ComputedRecompute.StartAsync(session, definition, before, Guid.NewGuid(), Ct)).Should().BeNull();
            await session.SaveChangesAsync(Ct);
        }

        await RunRecomputeAsync(store, slug, type);
        Computed(await Probe.StoredAsync(_fixture, id)).Should().Be("7", "the recompute moved the stored value off the version's");

        var rollback = await RollbackToFirstAsync(admin, id);

        rollback.StatusCode.Should().Be(HttpStatusCode.OK, await rollback.Content.ReadAsStringAsync(Ct));
        Computed(await Probe.StoredAsync(_fixture, id)).Should().Be("7");
    }

    // The writes that go around the endpoints.

    [Fact]
    public async Task A_bulk_import_stores_the_value_the_formula_gives()
    {
        var type = await Probe.StoreTypeAsync(_fixture, "wr", deliverable: false, Order());
        var admin = Probe.ClientFor(_fixture, await Probe.AdminAsync(_fixture));

        var res = await admin.PostAsJsonAsync("/api/import/content", new
        {
            contentType = type,
            records = new[] { new Dictionary<string, object> { ["Qty"] = 3, ["Cost"] = 4 } },
        }, Ct);

        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync(Ct));
        Computed((await EntriesAsync(type)).Single()).Should().Be(Worked);
    }

    [Fact]
    public async Task A_bundle_import_stores_the_value_the_formula_gives()
    {
        var slug = await TenantProfileSeed.TenantAsync(_fixture, Ct);
        var admin = await TenantProfileSeed.AdminInAsync(_fixture, slug, Ct);
        var type = NewName();
        object[] fields =
        [
            new { name = "Qty", type = "decimal" },
            new { name = "Cost", type = "decimal" },
            new { name = "Total", type = "decimal", computed = new { expression = "Qty * Cost" } },
        ];
        (await admin.PostAsJsonAsync("/api/content-types", new { name = type, displayName = type, fields }, Ct))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var imported = await admin.PostAsJsonAsync("/api/portability/import", new
        {
            dryRun = false,
            contentTypes = new[] { new { name = type, displayName = type, fields } },
            contents = new[]
            {
                new { contentType = type, data = new Dictionary<string, object> { ["Qty"] = 3, ["Cost"] = 4 }, status = "Published" },
            },
        }, Ct);

        imported.StatusCode.Should().Be(HttpStatusCode.OK, await imported.Content.ReadAsStringAsync(Ct));
        Computed((await EntriesAsync(type, slug)).Single()).Should().Be(Worked);
    }

    [Fact]
    public async Task A_collection_sync_stores_the_value_on_create_and_again_on_update()
    {
        var syncs = new CollectionSyncTests(_fixture);
        var answer = CollectionSyncTests.TwoPackages;
        var setup = await syncs.ArrangeAsync(
            () => (HttpStatusCode.OK, answer),
            fields:
            [
                Plain("packageId", "string"),
                Plain("downloads", "int"),
                Plain("summary", "string"),
                Formula("Doubled", "int", "downloads * 2"),
            ]);

        (await syncs.RunAsync(setup)).GetProperty("created").GetInt32().Should().Be(2);
        var created = await syncs.EntriesAsync(setup.Type);
        Computed(created.Single(c => c.Data["packageId"].ToString() == "BarakoCMS"), "Doubled").Should().Be("2400");
        Computed(created.Single(c => c.Data["packageId"].ToString() == "BarakoCMS.Files"), "Doubled").Should().Be("620");

        answer = """
        { "data": [
          { "id": "BarakoCMS",       "totalDownloads": 1500, "description": "Headless CMS" },
          { "id": "BarakoCMS.Files", "totalDownloads": 310,  "description": "File storage" } ] }
        """;
        await syncs.RunAsync(setup);

        var updated = await syncs.EntriesAsync(setup.Type);
        Computed(updated.Single(c => c.Data["packageId"].ToString() == "BarakoCMS"), "Doubled").Should().Be("3000");
        Computed(updated.Single(c => c.Data["packageId"].ToString() == "BarakoCMS.Files"), "Doubled").Should().Be("620");
    }

    [Fact]
    public async Task An_in_process_create_stores_the_value_the_formula_gives()
    {
        var type = await Probe.StoreTypeAsync(_fixture, "wr", deliverable: false, Order());

        var id = await Probe.SeedAsync(_fixture, type, new() { ["Qty"] = 3, ["Cost"] = 4, ["Total"] = 999 });

        Computed(await Probe.StoredAsync(_fixture, id)).Should().Be(Worked, "the writer owns the value, whatever the event carried");
    }

    [Fact]
    public async Task A_workflow_create_entry_stores_the_value_the_formula_gives()
    {
        var type = await Probe.StoreTypeAsync(_fixture, "wr", deliverable: false, Person());
        using var scope = _fixture.Services.CreateScope();
        var action = ActivatorUtilities.CreateInstance<CreateEntryAction>(scope.ServiceProvider);

        var result = await action.RunAsync(
            new() { ["ContentType"] = type, ["Data.Name"] = "Ana" },
            new Content { Id = Guid.NewGuid(), ContentType = "Order" }, Ct);

        result.Succeeded.Should().BeTrue(result.Error);
        var stored = await Probe.StoredAsync(_fixture, Guid.Parse(result.Outputs["Id"]));
        Computed(stored, "Label").Should().Be("ANA");
    }

    [Fact]
    public async Task A_workflow_create_content_stores_the_value_the_formula_gives()
    {
        var type = await Probe.StoreTypeAsync(_fixture, "wr", deliverable: false, Person());
        var runId = Guid.CreateVersion7();
        using var scope = _fixture.Services.CreateScope();
        var action = ActivatorUtilities.CreateInstance<CreateContentAction>(scope.ServiceProvider);

        var result = await action.RunAsync(
            new()
            {
                ["ContentType"] = type, ["Data.Name"] = "Ana",
                ["IdempotencyKey"] = $"{runId:N}-0", ["RunId"] = runId.ToString(),
            },
            new Content { Id = Guid.NewGuid(), ContentType = "Order" }, Ct);

        result.Succeeded.Should().BeTrue(result.Error);
        var stored = await Probe.StoredAsync(_fixture, Guid.Parse(result.Outputs["id"]));
        Computed(stored, "Label").Should().Be("ANA");
    }

    // The workflow field write, with a stream and without one.

    private async Task<(IDocumentStore Store, string Tenant, Guid Id)> SeedPersonAsync(bool withStream)
    {
        var store = _fixture.Services.GetRequiredService<IDocumentStore>();
        var tenant = "wr-wf-" + Guid.NewGuid().ToString("n")[..8];
        var type = NewName();
        var id = Guid.NewGuid();

        await using var seed = store.LightweightSession(tenant);
        seed.Store(Definition(type, Person()));
        await seed.SaveChangesAsync(Ct);

        if (withStream)
        {
            var writer = new ContentWriter(seed, new ContentSourcingPolicyService(seed), new ConfigurationBuilder().Build());
            await writer.CreateAsync(
                new ContentCreated(
                    id, type, new Dictionary<string, object> { ["Name"] = "Ana" }, ContentStatus.Draft,
                    Guid.NewGuid(), null, SensitivityLevel.Public, DateTime.UtcNow),
                Ct);
        }
        else
        {
            seed.Store(new Content
            {
                Id = id, ContentType = type, Status = ContentStatus.Draft,
                Data = new Dictionary<string, object> { ["Name"] = "Ana", ["Label"] = "ANA" },
            });
        }

        await seed.SaveChangesAsync(Ct);
        return (store, tenant, id);
    }

    private static async Task<WorkflowActionResult> UpdateNameAsync((IDocumentStore Store, string Tenant, Guid Id) seeded)
    {
        await using var session = seeded.Store.LightweightSession(seeded.Tenant);
        var writer = new ContentWriter(session, new ContentSourcingPolicyService(session), new ConfigurationBuilder().Build());
        var action = new UpdateFieldAction(
            session, writer, new ContentLifecycleRunner([], session),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<UpdateFieldAction>.Instance);

        return await action.RunAsync(
            new() { ["Field"] = "data.Name", ["Value"] = "Ana Cruz" },
            new Content { Id = seeded.Id, LastModifiedBy = Guid.NewGuid() }, Ct);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_workflow_field_write_works_the_value_out_with_a_stream_or_none(bool withStream)
    {
        var seeded = await SeedPersonAsync(withStream);
        await using (var check = seeded.Store.QuerySession(seeded.Tenant))
        {
            (await check.Events.FetchStreamStateAsync(seeded.Id, Ct) is not null).Should().Be(withStream);
        }

        var result = await UpdateNameAsync(seeded);

        result.Succeeded.Should().BeTrue(result.Error);
        await using var after = seeded.Store.QuerySession(seeded.Tenant);
        var stored = await after.LoadAsync<Content>(seeded.Id, Ct);
        stored!.Data["Name"].ToString().Should().Be("Ana Cruz");
        Computed(stored, "Label").Should().Be("ANA CRUZ");
    }

    // The jobs.

    [Fact]
    public async Task A_scheduled_publish_leaves_the_stored_value_as_the_formula_gave_it()
    {
        var type = await Probe.StoreTypeAsync(_fixture, "wr", deliverable: false, Order());
        var id = await Probe.SeedAsync(_fixture, type, new() { ["Qty"] = 3, ["Cost"] = 4 });
        using (var scope = _fixture.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            var content = await session.LoadAsync<Content>(id, Ct);
            content!.ScheduledPublishAt = DateTime.UtcNow.AddMinutes(-2);
            session.Store(content);
            await session.SaveChangesAsync(Ct);
        }

        using (var scope = _fixture.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            (await ScheduledContentService.SweepTenantAsync(session, DateTime.UtcNow, Ct)).Should().BeGreaterThan(0);
        }

        var after = await Probe.StoredAsync(_fixture, id);
        after.Status.Should().Be(ContentStatus.Published);
        Computed(after).Should().Be(Worked);
    }

    [Fact]
    public async Task The_recompute_job_works_a_stale_stored_value_out_again()
    {
        var slug = await TenantProfileSeed.TenantAsync(_fixture, Ct);
        var store = _fixture.Services.GetRequiredService<IDocumentStore>();
        var type = NewName();
        var id = Guid.NewGuid();
        await using (var seed = store.LightweightSession(slug))
        {
            seed.Store(Definition(type, Order()));
            seed.Store(new Content
            {
                Id = id, ContentType = type, Status = ContentStatus.Published,
                Data = new Dictionary<string, object> { ["Qty"] = 3m, ["Cost"] = 4m, ["Total"] = 999m },
            });
            await seed.SaveChangesAsync(Ct);
        }

        await using (var session = store.LightweightSession(slug))
        {
            var definition = await session.Query<ContentTypeDefinition>().SingleAsync(d => d.Name == type, Ct);
            (await ComputedRecompute.StartAsync(session, definition, before: null, Guid.NewGuid(), Ct)).Should().BeNull();
            await session.SaveChangesAsync(Ct);
        }

        await RunRecomputeAsync(store, slug, type);

        Computed((await EntriesAsync(type, slug)).Single()).Should().Be(Worked);
    }

    private async Task RunRecomputeAsync(IDocumentStore store, string slug, string type)
    {
        var configuration = _fixture.Services.GetRequiredService<IConfiguration>();
        Guid id;
        await using (var session = store.QuerySession(slug))
        {
            var change = await session.Query<FieldChange>()
                .Where(c => c.ContentType == type && c.Kind == FieldChangeKind.Recompute)
                .FirstOrDefaultAsync(Ct);
            change.Should().NotBeNull();
            id = change!.Id;
        }

        for (var attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                if ((await FieldChanges.RunAsync(store, configuration, slug, id, maxBatches: null, Ct))?.State == FieldChangeState.Completed)
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

    // The server writing by itself.

    [Fact]
    public async Task The_seeded_sample_records_get_the_value_of_a_formula_on_their_type()
    {
        var slug = await TenantProfileSeed.TenantAsync(_fixture, Ct);
        var store = _fixture.Services.GetRequiredService<IDocumentStore>();
        await using var session = store.LightweightSession(slug);

        var type = DataSeeder.AttendanceContentType();
        type.Fields.Add(Formula("FullName", "string", "concat(FirstName, LastName)"));
        session.Store(type);
        await session.SaveChangesAsync(Ct);

        await DataSeeder.SeedAttendanceRecordsAsync(session);
        await session.SaveChangesAsync(Ct);

        var records = await EntriesAsync(DataSeeder.AttendanceContentTypeName, slug);
        records.Should().HaveCount(DataSeeder.SampleAttendanceRecords().Count);
        records.Select(r => Computed(r, "FullName")).Should().OnlyContain(v => v != null && v.StartsWith("SampleEmployee"));
    }

    // Tenants.

    [Fact]
    public async Task The_same_type_in_two_tenants_works_each_value_out_by_its_own_formula()
    {
        var one = await TenantProfileSeed.TenantAsync(_fixture, Ct);
        var two = await TenantProfileSeed.TenantAsync(_fixture, Ct);
        var adminOne = await TenantProfileSeed.AdminInAsync(_fixture, one, Ct);
        var adminTwo = await TenantProfileSeed.AdminInAsync(_fixture, two, Ct);
        var type = NewName();

        async Task<Guid> ArrangeAsync(HttpClient admin, string expression)
        {
            object[] fields =
            [
                new { name = "Qty", type = "decimal" },
                new { name = "Cost", type = "decimal" },
                new { name = "Total", type = "decimal", computed = new { expression } },
            ];
            var made = await admin.PostAsJsonAsync("/api/content-types", new { name = type, displayName = type, fields }, Ct);
            made.StatusCode.Should().Be(HttpStatusCode.OK, await made.Content.ReadAsStringAsync(Ct));

            var created = await admin.PostAsJsonAsync("/api/contents", new
            {
                contentType = type, data = new Dictionary<string, object> { ["Qty"] = 3, ["Cost"] = 4 },
            }, Ct);
            var body = await created.Content.ReadAsStringAsync(Ct);
            created.StatusCode.Should().Be(HttpStatusCode.OK, body);
            return JsonDocument.Parse(body).RootElement.GetProperty("id").GetGuid();
        }

        await ArrangeAsync(adminOne, "Qty * Cost");
        await ArrangeAsync(adminTwo, "Qty + Cost");

        Computed((await EntriesAsync(type, one)).Single()).Should().Be("12");
        Computed((await EntriesAsync(type, two)).Single()).Should().Be("7");
    }

    // What a failing formula logs.

    private sealed class Capture<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Lines.Add((logLevel, formatter(state, exception)));
    }

    private const string SecretQty = "7777.77";

    [Fact]
    public void A_formula_that_gives_no_value_logs_the_field_the_rule_and_the_position_and_no_value()
    {
        var log = new Capture<object>();
        var data = new Dictionary<string, object> { ["Qty"] = decimal.Parse(SecretQty, System.Globalization.CultureInfo.InvariantCulture), ["Cost"] = 0m };

        ComputedFields.Apply(Order("Qty / Cost"), data, log);

        data.Should().NotContainKey("Total");
        var line = log.Lines.Should().ContainSingle().Subject;
        line.Level.Should().Be(LogLevel.Warning);
        line.Message.Should().Contain("Total").And.Contain("division-by-zero").And.Contain("position");
        line.Message.Should().NotContain("7777").And.NotContain("Qty / Cost");
    }

    [Fact]
    public void A_stored_formula_that_no_longer_checks_logs_its_rule_and_never_its_text()
    {
        var log = new Capture<object>();
        var data = new Dictionary<string, object> { ["Qty"] = 3m, ["Cost"] = 4m };

        ComputedFields.Apply(Order("Qty * secretword("), data, log);

        data.Should().NotContainKey("Total");
        var line = log.Lines.Should().ContainSingle().Subject;
        line.Message.Should().Contain("Total").And.Contain("formula rule");
        line.Message.Should().NotContain("secretword");
    }

    [Fact]
    public void A_formula_that_gives_a_value_logs_nothing()
    {
        var log = new Capture<object>();
        var data = new Dictionary<string, object> { ["Qty"] = 3m, ["Cost"] = 4m };

        ComputedFields.Apply(Order(), data, log);

        Computed(new Content { Data = data }).Should().Be(Worked);
        log.Lines.Should().BeEmpty();
    }

    [Fact]
    public async Task The_writer_logs_a_failing_formula_the_same_way()
    {
        var store = _fixture.Services.GetRequiredService<IDocumentStore>();
        var tenant = "wr-log-" + Guid.NewGuid().ToString("n")[..8];
        var type = NewName();
        var log = new Capture<ContentWriter>();
        var id = Guid.NewGuid();

        await using var session = store.LightweightSession(tenant);
        session.Store(Definition(type, Order("Qty / Cost")));
        await session.SaveChangesAsync(Ct);

        var writer = new ContentWriter(
            session, new ContentSourcingPolicyService(session), new ConfigurationBuilder().Build(), logger: log);
        await writer.CreateAsync(
            new ContentCreated(
                id, type, new Dictionary<string, object> { ["Qty"] = decimal.Parse(SecretQty, System.Globalization.CultureInfo.InvariantCulture), ["Cost"] = 0m },
                ContentStatus.Draft, Guid.NewGuid(), null, SensitivityLevel.Public, DateTime.UtcNow),
            Ct);
        await session.SaveChangesAsync(Ct);

        var stored = (await EntriesAsync(type, tenant)).Single();
        stored.Data.Should().NotContainKey("Total");
        var line = log.Lines.Should().ContainSingle().Subject;
        line.Message.Should().Contain("Total").And.Contain("division-by-zero");
        line.Message.Should().NotContain("7777").And.NotContain("Qty / Cost");
    }
}
