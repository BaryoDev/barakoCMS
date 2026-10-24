using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BarakoCMS.Accounting;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Npgsql;
using barakoCMS.Infrastructure.Jobs;
using barakoCMS.Core.Interfaces;
using barakoCMS.Models;
using Xunit;

namespace BarakoCMS.Tests.Numbering;

/// <summary>
/// A content type declares a numbering rule and a created entry gets the next number, by the
/// strategy the rule names; Accounting numbers through the same primitive (#584).
/// </summary>
[Collection("IntegrationSerial")]
public class NumberingRuleTests : IAsyncLifetime
{
    private readonly IntegrationTestFixture _fixture;
    private readonly HttpClient _client;

    public NumberingRuleTests(IntegrationTestFixture fixture)
    {
        _fixture = fixture;
        _client = fixture.CreateClient();

        // A bucket of its own under the global per-IP limit: one test here sends fifty creates.
        var ip = Guid.NewGuid().ToByteArray();
        _client.DefaultRequestHeaders.Add(TestRemoteIpFilter.Header, $"10.{ip[0]}.{ip[1]}.{ip[2]}");
    }

    public async ValueTask InitializeAsync() =>
        _client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Bearer", await _fixture.StoredUserTokenAsync("Admin", "SuperAdmin"));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string NewName() => "numbered" + Guid.NewGuid().ToString("n")[..10];

    private async Task<string> InvoiceTypeAsync(string strategy, HttpClient? client = null)
    {
        client ??= _client;
        var name = NewName();
        (await client.PostAsJsonAsync("/api/content-types", new
        {
            name, displayName = name,
            fields = new object[] { new { name = "Title", type = "string" }, new { name = "Number", type = "string" } },
        }, Ct)).StatusCode.Should().Be(HttpStatusCode.OK);

        var set = await client.PutAsJsonAsync($"/api/content-types/{name}/numbering", new
        {
            field = "number", prefix = "INV-", padding = 5, strategy,
        }, Ct);
        set.StatusCode.Should().Be(HttpStatusCode.OK, await set.Content.ReadAsStringAsync(Ct));
        return name;
    }

    private async Task<string> CreateAsync(string type, HttpClient? client = null, string? sent = null)
    {
        client ??= _client;
        var data = new Dictionary<string, object> { ["Title"] = "t" };
        if (sent is not null)
        {
            data["Number"] = sent;
        }

        var res = await client.PostAsJsonAsync("/api/contents", new { contentType = type, data }, Ct);
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync(Ct));
        var id = (await res.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();

        using var scope = _fixture.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IDocumentStore>();
        await using var session = store.QuerySession(client == _client ? "*DEFAULT*" : client.DefaultRequestHeaders.GetValues("X-Tenant").Single());
        return (await session.LoadAsync<Content>(id, Ct))!.Data["Number"].ToString()!;
    }

    [Fact]
    public async Task A_created_entry_gets_the_next_number_and_the_server_owns_the_field()
    {
        var type = await InvoiceTypeAsync("gapless");

        (await CreateAsync(type)).Should().Be("INV-00001");
        (await CreateAsync(type, sent: "INV-99999")).Should().Be("INV-00002", "a number the request sends is replaced");
        (await CreateAsync(type)).Should().Be("INV-00003");
    }

    [Theory]
    [InlineData("gapless")]
    [InlineData("sequence")]
    [InlineData("hiLo")]
    public async Task Fifty_parallel_creates_get_fifty_different_numbers_and_gapless_leaves_no_gap(string strategy)
    {
        var type = await InvoiceTypeAsync(strategy);

        var numbers = await Task.WhenAll(Enumerable.Range(0, 50).Select(_ => CreateAsync(type)));

        numbers.Should().HaveCount(50);
        numbers.Should().OnlyHaveUniqueItems();
        if (strategy == "gapless")
        {
            numbers.Select(n => int.Parse(n["INV-".Length..])).Order().Should().Equal(Enumerable.Range(1, 50));
        }
    }

    [Fact]
    public async Task An_entry_created_under_a_mis_cased_type_name_is_still_numbered()
    {
        var type = await InvoiceTypeAsync("gapless");

        (await CreateAsync(type.ToUpperInvariant(), sent: "MINE-1")).Should().Be("INV-00001");
    }

    [Fact]
    public async Task An_update_keeps_the_stored_number()
    {
        var type = await InvoiceTypeAsync("gapless");
        var res = await _client.PostAsJsonAsync("/api/contents", new { contentType = type, data = new Dictionary<string, object> { ["Title"] = "t" } }, Ct);
        var id = (await res.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();

        var update = await _client.PutAsJsonAsync($"/api/contents/{id}", new
        {
            id, data = new Dictionary<string, object> { ["Title"] = "renamed", ["Number"] = "INV-99999" },
        }, Ct);

        update.StatusCode.Should().Be(HttpStatusCode.OK, await update.Content.ReadAsStringAsync(Ct));
        using var scope = _fixture.Services.CreateScope();
        var stored = await scope.ServiceProvider.GetRequiredService<IQuerySession>().LoadAsync<Content>(id, Ct);
        stored!.Data["Title"].ToString().Should().Be("renamed", "the rest of the update applies");
        stored.Data["Number"].ToString().Should().Be("INV-00001");
    }

    [Fact]
    public async Task A_rename_carries_the_rule_and_a_remove_or_convert_is_refused_while_it_names_the_field()
    {
        var type = await InvoiceTypeAsync("gapless");
        (await CreateAsync(type)).Should().Be("INV-00001");

        foreach (var body in new object[] { new { remove = true, allowDestructive = true }, new { toType = "text" } })
        {
            var refused = await _client.PostAsJsonAsync($"/api/content-types/{type}/fields/Number/change", body, Ct);
            refused.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await refused.Content.ReadAsStringAsync(Ct)).Should().Contain("numbering rule");
        }

        var renamed = await _client.PostAsJsonAsync($"/api/content-types/{type}/fields/Number/change", new { newName = "InvoiceNo" }, Ct);
        renamed.StatusCode.Should().Be(HttpStatusCode.Accepted, await renamed.Content.ReadAsStringAsync(Ct));
        var id = (await renamed.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("change").GetProperty("id").GetGuid();
        // Run the change here instead of waiting on the job queue, which a full suite run keeps busy past any wait worth writing.
        var store = _fixture.Services.GetRequiredService<IDocumentStore>();
        var configuration = _fixture.Services.GetRequiredService<IConfiguration>();
        var completed = false;
        for (var attempt = 0; attempt < 20 && !completed; attempt++)
        {
            try
            {
                completed = (await FieldChanges.RunAsync(store, configuration, "*DEFAULT*", id, maxBatches: null, Ct))?.State == FieldChangeState.Completed;
            }
            catch (JasperFx.ConcurrencyException)
            {
                // The queued job committed the same batch first; the next run reads its progress.
            }
        }

        completed.Should().BeTrue("the rename has to finish before an entry is created under the new name");

        var res = await _client.PostAsJsonAsync("/api/contents", new { contentType = type, data = new Dictionary<string, object> { ["Title"] = "t" } }, Ct);
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync(Ct));
        var created = (await res.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        (await session.Query<ContentTypeDefinition>().SingleAsync(d => d.Name == type, Ct)).Numbering!.Field.Should().Be("InvoiceNo");
        var stored = await session.LoadAsync<Content>(created, Ct);
        stored!.Data.Should().ContainKey("InvoiceNo").And.NotContainKey("Number");
        stored.Data["InvoiceNo"].ToString().Should().Be("INV-00002");
    }

    [Fact]
    public async Task Two_tenants_count_apart()
    {
        var first = await TenantAdminAsync();
        var second = await TenantAdminAsync();
        var a = await InvoiceTypeAsync("gapless", first);
        var b = await InvoiceTypeAsync("gapless", second);

        (await CreateAsync(a, first)).Should().Be("INV-00001");
        (await CreateAsync(a, first)).Should().Be("INV-00002");
        (await CreateAsync(b, second)).Should().Be("INV-00001", "another tenant's numbers are not this tenant's");
    }

    private async Task<HttpClient> TenantAdminAsync()
    {
        var slug = await Features.Tenants.TenantProfileSeed.TenantAsync(_fixture, Ct);
        return await Features.Tenants.TenantProfileSeed.AdminInAsync(_fixture, slug, Ct);
    }

    [Fact]
    public async Task A_dry_run_takes_no_number()
    {
        var type = await InvoiceTypeAsync("gapless");

        (await _client.PostAsJsonAsync("/api/contents", new
        {
            contentType = type, dryRun = true, data = new Dictionary<string, object> { ["Title"] = "t" },
        }, Ct)).StatusCode.Should().Be(HttpStatusCode.OK);

        (await CreateAsync(type)).Should().Be("INV-00001");
    }

    [Theory]
    [InlineData("""{ "field": "Nope" }""", "no field 'Nope'")]
    [InlineData("""{ "field": "Title", "prefix": "THIS-PREFIX-IS-FAR-TOO-LONG-" }""", "at most 20")]
    [InlineData("""{ "field": "Title", "padding": 0 }""", "Padding")]
    [InlineData("""{ "field": "Title", "timeZoneId": "Mars/Olympus" }""", "Mars/Olympus")]
    [InlineData("""{ "field": "Count" }""", "has to be string or text")]
    [InlineData("""{ "field": "Code" }""", "is required")]
    public async Task A_rule_the_type_cannot_hold_is_refused(string json, string expected)
    {
        var name = NewName();
        (await _client.PostAsJsonAsync("/api/content-types", new
        {
            name, displayName = name,
            fields = new object[]
            {
                new { name = "Title", type = "string" },
                new { name = "Count", type = "int" },
                new { name = "Code", type = "string", isRequired = true },
            },
        }, Ct)).StatusCode.Should().Be(HttpStatusCode.OK);

        var res = await _client.PutAsync($"/api/content-types/{name}/numbering",
            new StringContent(json, System.Text.Encoding.UTF8, "application/json"), Ct);

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await res.Content.ReadAsStringAsync(Ct)).Should().Contain(expected);
    }

    [Fact]
    public async Task Setting_a_rule_needs_manage_content_types()
    {
        var type = await InvoiceTypeAsync("sequence");
        var user = _fixture.CreateClient();
        user.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer", await _fixture.StoredUserTokenAsync("User"));

        (await user.PutAsJsonAsync($"/api/content-types/{type}/numbering", new { field = "Number" }, Ct))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await _fixture.CreateClient().PutAsJsonAsync($"/api/content-types/{type}/numbering", new { field = "Number" }, Ct))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ---- the strategies, below the API ------------------------------------------------------

    [Fact]
    public async Task A_sequence_number_is_not_given_back_by_a_write_that_fails()
    {
        var strategies = _fixture.Services.GetRequiredService<INumberStrategies>();
        var store = _fixture.Services.GetRequiredService<IDocumentStore>();
        var series = new NumberSeries("seq." + Guid.NewGuid().ToString("n")[..12]);

        await using (var abandoned = store.LightweightSession())
        {
            (await strategies.For(NumberStrategy.Sequence).NextAsync(abandoned, series, Ct)).Value.Should().Be(1);
        }

        await using var next = store.LightweightSession();
        (await strategies.For(NumberStrategy.Sequence).NextAsync(next, series, Ct)).Value.Should().Be(2, "a sequence keeps what it handed out");

        await using (var gaplessAbandoned = store.LightweightSession())
        {
            var gapless = new NumberSeries("gap." + Guid.NewGuid().ToString("n")[..12]);
            (await strategies.For(NumberStrategy.Gapless).NextAsync(gaplessAbandoned, gapless, Ct)).Value.Should().Be(1);
            await using var after = store.LightweightSession();
            await gaplessAbandoned.DisposeAsync();
            (await strategies.For(NumberStrategy.Gapless).NextAsync(after, gapless, Ct)).Value.Should().Be(1, "gapless gives an unsaved number back");
        }
    }

    [Fact]
    public async Task Two_hi_lo_allocators_like_two_instances_never_hand_out_the_same_number()
    {
        var store = _fixture.Services.GetRequiredService<IDocumentStore>();
        var first = new barakoCMS.Infrastructure.Numbering.HiLoNumberAllocator(store, TimeProvider.System);
        var second = new barakoCMS.Infrastructure.Numbering.HiLoNumberAllocator(store, TimeProvider.System);
        var series = new NumberSeries("hilo." + Guid.NewGuid().ToString("n")[..12]);

        var values = await Task.WhenAll(Enumerable.Range(0, 120).Select(async i =>
        {
            await using var session = store.LightweightSession();
            return (await (i % 2 == 0 ? first : second).NextAsync(session, series, Ct)).Value;
        }));

        values.Should().HaveCount(120);
        values.Should().OnlyHaveUniqueItems();
    }

    // ---- Accounting ------------------------------------------------------------------------

    private async Task ExecAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(Ct);
    }

    private static string Migration(string file) =>
        File.ReadAllText(Path.Combine(RepositoryRoot(), "migrations", "5.0.0", file));

    [Fact]
    public async Task Journal_entry_numbering_continues_from_the_modules_own_count_after_the_migration()
    {
        var tenant = "acct-" + Guid.NewGuid().ToString("n")[..8];
        await ExecAsync(
            "insert into public.mt_doc_accounting_number_sequences (tenant_id, id, data, mt_dotnet_type) "
            + $"values ('{tenant}', 'JE-2031', '{{\"Id\":\"JE-2031\",\"Value\":41}}'::jsonb, 'BarakoCMS.Accounting.Domain.NumberSequence')");

        await ExecAsync(Migration("accounting-number-sequences.sql"));
        await ExecAsync(Migration("accounting-number-sequences.sql"));

        var store = _fixture.Services.GetRequiredService<IDocumentStore>();
        var numbers = _fixture.Services.GetRequiredService<INumberAllocator>();
        await using var session = store.LightweightSession(tenant);
        var next = await numbers.NextAsync(session, JournalEntryHook.EntryNumbers, new DateTimeOffset(2031, 3, 1, 0, 0, 0, TimeSpan.Zero), Ct);
        await session.SaveChangesAsync(Ct);

        next.Formatted.Should().Be("JE-2031-000042", "the format is the one the module always wrote, and the count carries on");

        await ExecAsync(Migration("rollback-accounting-number-sequences.sql"));
        await using var read = store.QuerySession(tenant);
        (await read.LoadAsync<BarakoCMS.Accounting.Domain.NumberSequence>("JE-2031", Ct))!.Value
            .Should().Be(42, "a rollback hands the older module the count 5.0 reached");
    }

    [Fact]
    public async Task A_posted_journal_entry_is_numbered_by_the_core_allocator_in_the_modules_format()
    {
        var suffix = Guid.NewGuid().ToString("N")[..6];
        foreach (var (code, kind) in new[] { ($"1000-{suffix}", "Asset"), ($"4000-{suffix}", "Income") })
        {
            (await _client.PostAsJsonAsync("/api/contents", new
            {
                contentType = AccountingContentTypes.Account, status = 1,
                data = new { Code = code, Name = code, Type = kind, IsActive = true },
            }, Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
        }

        async Task<string> PostAsync()
        {
            var res = await _client.PostAsJsonAsync("/api/accounting/journal-entries", new
            {
                Date = "2033-03-01", Memo = "dues",
                Lines = new object[]
                {
                    new { AccountCode = $"1000-{suffix}", Debit = 10m, Credit = 0m },
                    new { AccountCode = $"4000-{suffix}", Debit = 0m, Credit = 10m },
                },
            }, Ct);
            res.IsSuccessStatusCode.Should().BeTrue(await res.Content.ReadAsStringAsync(Ct));
            return (await res.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("entryNumber").GetString()!;
        }

        var first = await PostAsync();
        var second = await PostAsync();

        first.Should().MatchRegex("^JE-2033-[0-9]{6}$");
        int.Parse(second[^6..]).Should().Be(int.Parse(first[^6..]) + 1);

        await using var connection = new NpgsqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(
            "select last_value from public.barako_number_counters where series = 'accounting.je' and period = '2033' and tenant_id = '*DEFAULT*'", connection);
        ((long)(await command.ExecuteScalarAsync(Ct))!).Should().Be(int.Parse(second[^6..]), "the count lives in the core counter");
    }

    [Fact]
    public async Task The_allocator_the_legacy_accounting_constructors_use_shares_the_core_count()
    {
        var tenant = "acct-" + Guid.NewGuid().ToString("n")[..8];
        var store = _fixture.Services.GetRequiredService<IDocumentStore>();
        var at = new DateTimeOffset(2034, 3, 1, 0, 0, 0, TimeSpan.Zero);

        async Task<string> NextAsync(INumberAllocator numbers)
        {
            await using var session = store.LightweightSession(tenant);
            var next = await numbers.NextAsync(session, JournalEntryHook.EntryNumbers, at, Ct);
            await session.SaveChangesAsync(Ct);
            return next.Formatted;
        }

        var injected = await NextAsync(_fixture.Services.GetRequiredService<INumberAllocator>());
        var legacy = await NextAsync(barakoCMS.Infrastructure.Numbering.NumberAllocators.Gapless());

        injected.Should().Be("JE-2034-000001");
        legacy.Should().Be("JE-2034-000002", "a post through a legacy constructor counts on from the endpoint's number");
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "migrations")))
        {
            directory = directory.Parent;
        }

        return directory!.FullName;
    }
}
