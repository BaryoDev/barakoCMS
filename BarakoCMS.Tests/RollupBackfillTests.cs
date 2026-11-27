using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using barakoCMS.Core.Validation;
using barakoCMS.Infrastructure.Jobs;
using barakoCMS.Models;
using Probe = BarakoCMS.Tests.TokenFieldProbe;
using R = BarakoCMS.Tests.RollupProbe;

namespace BarakoCMS.Tests;

/// <summary>
/// A rollup added to a type with entries, or changed, is filled by a recompute. Changing its level
/// recounts inside the sensitivity change, in the step order that keeps a wider count from sitting
/// under a field readers can already see.
/// </summary>
/// <remarks>
/// Each test stores its own types under names no other test uses. The field changes that are stepped
/// by hand are stored and not queued, so nothing runs a batch the test did not ask for.
/// </remarks>
[Collection("Integration1")]
public class RollupBackfillTests
{
    private const int Headers = 3;
    private const int BatchSize = 2;

    private readonly IntegrationTestFixture _fixture;

    public RollupBackfillTests(IntegrationTestFixture fixture)
    {
        _fixture = fixture;
        FastEndpointsResolver.ClaimJobQueues(fixture.Services);
    }

    private static CancellationToken Ct => R.Ct;

    /// <summary>
    /// Headers that each have two Public published lines (1 and 2 hours), one Public draft (10) and
    /// one Published line of Sensitive level (100). A Public count is 3, a Sensitive count is 113.
    /// </summary>
    private async Task<(R.Pair Pair, List<Guid> Headers)> SeedAsync(SensitivityLevel level)
    {
        var pair = await R.StorePairAsync(_fixture, level, deliverable: true);
        var admin = await Probe.AdminAsync(_fixture);
        var headers = new List<Guid>();
        for (var i = 0; i < Headers; i++)
        {
            var header = await R.HeaderAsync(_fixture, admin, pair);
            headers.Add(header);
            await R.SeedLineAsync(_fixture, pair, header, 1);
            await R.SeedLineAsync(_fixture, pair, header, 2);
            await R.SeedLineAsync(_fixture, pair, header, 10, ContentStatus.Draft);
            await R.SeedLineAsync(_fixture, pair, header, 100, level: SensitivityLevel.Sensitive);
        }

        await R.DrainAsync(_fixture);
        return (pair, headers);
    }

    private async Task<FieldChange> HoldAsync(string type, SensitivityLevel from, SensitivityLevel to) =>
        await R.StoreChangeAsync(_fixture, new FieldChange
        {
            ContentType = type,
            Field = "Total",
            Kind = FieldChangeKind.Sensitivity,
            FromSensitivity = from,
            ToSensitivity = to,
            DefinitionFirst = SensitivityRank.IsLowering(from, to),
            CountsRollup = true,
            Total = Headers,
        });

    private async Task<FieldChange> StepAsync(Guid id)
    {
        var store = _fixture.Services.GetRequiredService<IDocumentStore>();
        var configuration = _fixture.Services.GetRequiredService<IConfiguration>();
        return (await FieldChanges.RunAsync(store, configuration, R.Tenant, id, maxBatches: 1, Ct, BatchSize))!;
    }

    /// <summary>What an anonymous caller is shown for the Total of each header: a number, or null when none is shown.</summary>
    private async Task<List<decimal?>> ShownAsync(R.Pair pair, IEnumerable<Guid> headers)
    {
        var shown = new List<decimal?>();
        foreach (var header in headers)
        {
            shown.Add(await R.PublicNumberAsync(_fixture, pair.Header, header, "Total"));
        }

        return shown;
    }

    private async Task<List<decimal?>> StoredAsync(IEnumerable<Guid> headers)
    {
        var stored = new List<decimal?>();
        foreach (var header in headers)
        {
            stored.Add((await R.ValuesAsync(_fixture, header)).Total);
        }

        return stored;
    }

    [Fact]
    public async Task Loosening_a_rollup_never_shows_an_anonymous_reader_a_value_that_counts_a_sensitive_entry_or_a_draft()
    {
        var (pair, headers) = await SeedAsync(SensitivityLevel.Sensitive);
        (await StoredAsync(headers)).Should().OnlyContain(v => v == 113, "the Sensitive count includes the draft and the Sensitive line");
        var change = await HoldAsync(pair.Header, SensitivityLevel.Sensitive, SensitivityLevel.Public);

        var sawPublicCount = false;
        for (var call = 0; call < 20 && change.State == FieldChangeState.Running; call++)
        {
            change = await StepAsync(change.Id);

            var shown = await ShownAsync(pair, headers);
            shown.Should().OnlyContain(v => v == null || v == 3, $"after call {call} an anonymous reader sees no wider count");
            sawPublicCount |= shown.Any(v => v == 3);
        }

        change.State.Should().Be(FieldChangeState.Completed);
        sawPublicCount.Should().BeTrue("the field is Public at the end and shows the Public count");
        (await StoredAsync(headers)).Should().OnlyContain(v => v == 3);
    }

    [Fact]
    public async Task Loosening_recounts_every_header_before_the_definition_changes()
    {
        var (pair, headers) = await SeedAsync(SensitivityLevel.Sensitive);
        var change = await HoldAsync(pair.Header, SensitivityLevel.Sensitive, SensitivityLevel.Public);

        // Batches of two over three headers: two calls make the first step, the second finishes it.
        for (var call = 0; call < 3; call++)
        {
            change = await StepAsync(change.Id);
        }

        change.Step.Should().Be(1, "the recount is done and the definition step is next");
        (await R.StoredChangeAsync(_fixture, change.Id)).Processed.Should().Be(0, "a recount is counted with the search text step, not twice");
        (await StoredAsync(headers)).Should().OnlyContain(v => v == 3);
        (await Probe.StoredTypeAsync(_fixture, pair.Header)).Fields.First(f => f.Name == "Total").Sensitivity
            .Should().Be(SensitivityLevel.Sensitive, "the field is still held to its old level");
    }

    [Fact]
    public async Task Tightening_a_rollup_never_shows_an_anonymous_reader_a_wider_count_while_the_field_is_public()
    {
        var (pair, headers) = await SeedAsync(SensitivityLevel.Public);
        (await StoredAsync(headers)).Should().OnlyContain(v => v == 3, "the Public count leaves out the draft and the Sensitive line");
        var change = await HoldAsync(pair.Header, SensitivityLevel.Public, SensitivityLevel.Sensitive);

        for (var call = 0; call < 20 && change.State == FieldChangeState.Running; call++)
        {
            change = await StepAsync(change.Id);

            var level = (await Probe.StoredTypeAsync(_fixture, pair.Header)).Fields.First(f => f.Name == "Total").Sensitivity;
            var shown = await ShownAsync(pair, headers);
            if (level == SensitivityLevel.Public)
            {
                shown.Should().OnlyContain(v => v == 3, $"after call {call} the field is still Public, and its value counts nothing wider");
            }
            else
            {
                shown.Should().OnlyContain(v => v == null, $"after call {call} the field is held back");
            }
        }

        change.State.Should().Be(FieldChangeState.Completed);
        (await StoredAsync(headers)).Should().OnlyContain(v => v == 113, "the final value counts the draft and the Sensitive line");
        (await ShownAsync(pair, headers)).Should().OnlyContain(v => v == null);
    }

    [Fact]
    public async Task The_sensitivity_endpoint_starts_one_change_that_recounts_in_its_own_step()
    {
        var (pair, headers) = await SeedAsync(SensitivityLevel.Public);
        var admin = R.Client(_fixture, await Probe.AdminAsync(_fixture));

        var res = await admin.PutAsJsonAsync(
            $"/api/content-types/{pair.Header}/fields/Total/sensitivity", new { sensitivity = "Sensitive" }, Ct);
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync(Ct));

        var change = await R.ChangeAsync(_fixture, pair.Header, FieldChangeKind.Sensitivity);
        change.Should().NotBeNull();
        change!.CountsRollup.Should().BeTrue("the plan cannot read the definition, so the change carries the fact");
        (await R.ChangeAsync(_fixture, pair.Header, FieldChangeKind.Recompute)).Should().BeNull("no second change is created for the recount");
        FieldChanges.Plan(change).Should().Equal(
            FieldChanges.Work.SearchText, FieldChanges.Work.Define, FieldChanges.Work.Recompute);

        await R.RunToEndAsync(_fixture, change.Id);

        (await StoredAsync(headers)).Should().OnlyContain(v => v == 113);
        var done = await R.StoredChangeAsync(_fixture, change.Id);
        done.Processed.Should().BeLessThanOrEqualTo(done.Total, "an entry is not counted again for the recount");
    }

    [Fact]
    public void Loosening_plans_the_recount_first_and_a_field_without_a_rollup_plans_what_it_did()
    {
        var loosening = new FieldChange
        {
            Kind = FieldChangeKind.Sensitivity, DefinitionFirst = true, CountsRollup = true,
        };
        var tightening = new FieldChange { Kind = FieldChangeKind.Sensitivity, CountsRollup = true };
        FieldChanges.Plan(loosening).Should().Equal(FieldChanges.Work.Recompute, FieldChanges.Work.Define, FieldChanges.Work.SearchText);
        FieldChanges.Plan(tightening).Should().Equal(FieldChanges.Work.SearchText, FieldChanges.Work.Define, FieldChanges.Work.Recompute);
        FieldChanges.Plan(new FieldChange { Kind = FieldChangeKind.Sensitivity, DefinitionFirst = true })
            .Should().Equal(FieldChanges.Work.Define, FieldChanges.Work.SearchText);
        FieldChanges.Plan(new FieldChange { Kind = FieldChangeKind.Sensitivity })
            .Should().Equal(FieldChanges.Work.SearchText, FieldChanges.Work.Define);
    }

    [Fact]
    public async Task Adding_a_rollup_to_a_type_with_entries_fills_them_in_a_recompute()
    {
        var line = await Probe.StoreTypeAsync(_fixture, "rl", false);
        var header = await Probe.StoreTypeAsync(_fixture, "rh", false, R.Plain("Name", "string"));
        await StoreLineFieldsAsync(line, header);
        var admin = await Probe.AdminAsync(_fixture);
        var first = await Probe.CreateAsync(_fixture, admin, header, new() { ["Name"] = "a" }, ContentStatus.Published);
        var second = await Probe.CreateAsync(_fixture, admin, header, new() { ["Name"] = "b" }, ContentStatus.Published);
        await Probe.CreateAsync(_fixture, admin, line, new() { ["Teacher"] = first.ToString(), ["Hours"] = 4 }, ContentStatus.Published);
        await Probe.CreateAsync(_fixture, admin, line, new() { ["Teacher"] = first.ToString(), ["Hours"] = 6 }, ContentStatus.Published);

        var res = await R.Client(_fixture, admin).PostAsJsonAsync($"/api/content-types/{header}/fields", new
        {
            fieldName = "Total",
            displayName = "Total",
            type = "decimal",
            rollup = new { from = line, via = "Teacher", fn = "sum", field = "Hours" },
        }, Ct);
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync(Ct));
        (await R.StoredAsync(_fixture, first)).Data.Should().NotContainKey("Total", "nothing has recounted yet");

        await R.RunStartedRecomputeAsync(_fixture, header);

        R.Num(await R.StoredAsync(_fixture, first), "Total").Should().Be(10);
        R.Num(await R.StoredAsync(_fixture, second), "Total").Should().Be(0, "a header with no lines sums to 0");
    }

    [Fact]
    public async Task Changing_the_function_of_a_rollup_recomputes_the_type()
    {
        var pair = await R.StorePairAsync(_fixture);
        var admin = await Probe.AdminAsync(_fixture);
        var header = await R.HeaderAsync(_fixture, admin, pair);
        await R.LineAsync(_fixture, admin, pair, header, 4);
        await R.LineAsync(_fixture, admin, pair, header, 6);
        await R.DrainAsync(_fixture);
        (await R.ValuesAsync(_fixture, header)).Peak.Should().Be(6);

        var applied = await R.Client(_fixture, admin).PostAsJsonAsync("/api/config/apply", new
        {
            document = new
            {
                version = 1,
                contentTypes = new Dictionary<string, object>
                {
                    [pair.Header] = new
                    {
                        name = pair.Header,
                        displayName = pair.Header,
                        fields = new object[]
                        {
                            new { name = "Name", displayName = "Name", type = "string" },
                            new { name = "Count", displayName = "Count", type = "int", rollup = new { from = pair.Line, via = "Teacher", fn = "count" } },
                            new { name = "Total", displayName = "Total", type = "decimal", rollup = new { from = pair.Line, via = "Teacher", fn = "sum", field = "Hours" } },
                            new { name = "Peak", displayName = "Peak", type = "decimal", rollup = new { from = pair.Line, via = "Teacher", fn = "min", field = "Hours" } },
                        },
                    },
                },
            },
            allowDestructive = true,
        }, Ct);
        applied.StatusCode.Should().Be(HttpStatusCode.OK, await applied.Content.ReadAsStringAsync(Ct));

        await R.RunStartedRecomputeAsync(_fixture, pair.Header);

        (await R.ValuesAsync(_fixture, header)).Peak.Should().Be(4, "the function is now min");
    }

    [Fact]
    public async Task A_rollup_recompute_reports_its_status_and_resumes_like_a_formula_recompute()
    {
        var pair = await R.StorePairAsync(_fixture);
        var admin = await Probe.AdminAsync(_fixture);
        var header = await R.HeaderAsync(_fixture, admin, pair);
        await R.LineAsync(_fixture, admin, pair, header, 7);
        await R.DrainAsync(_fixture);
        var held = await R.StoreChangeAsync(_fixture, new FieldChange
        {
            ContentType = pair.Header, Field = "Total", Kind = FieldChangeKind.Recompute, Total = 1,
        });
        var client = R.Client(_fixture, admin);

        var status = await client.GetFromJsonAsync<JsonElement>($"/api/content-types/field-changes/{held.Id}", Ct);
        status.GetProperty("state").GetString().Should().Be("running");
        status.GetProperty("kind").GetString().Should().Be("recompute");

        var resumed = await client.PostAsync($"/api/content-types/field-changes/{held.Id}/resume", null, Ct);
        resumed.StatusCode.Should().Be(HttpStatusCode.Accepted, await resumed.Content.ReadAsStringAsync(Ct));
        await R.RunToEndAsync(_fixture, held.Id);
        (await R.StoredChangeAsync(_fixture, held.Id)).State.Should().Be(FieldChangeState.Completed);
    }

    [Fact]
    public async Task A_rollup_recompute_is_abandoned_and_the_type_takes_writes_again()
    {
        var pair = await R.StorePairAsync(_fixture);
        var admin = await Probe.AdminAsync(_fixture);
        var held = await R.StoreChangeAsync(_fixture, new FieldChange
        {
            ContentType = pair.Header, Field = "Total", Kind = FieldChangeKind.Recompute, Total = 1,
        });
        var client = R.Client(_fixture, admin);

        var abandoned = await client.PostAsync($"/api/content-types/field-changes/{held.Id}/abandon", null, Ct);

        abandoned.StatusCode.Should().Be(HttpStatusCode.OK, await abandoned.Content.ReadAsStringAsync(Ct));
        (await R.StoredChangeAsync(_fixture, held.Id)).State.Should().Be(FieldChangeState.Abandoned);
        await R.HeaderAsync(_fixture, admin, pair);
    }

    private async Task StoreLineFieldsAsync(string line, string header)
    {
        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var type = await session.Query<ContentTypeDefinition>().FirstAsync(d => d.Name == line, Ct);
        type.Fields = [R.Reference("Teacher", header), R.Plain("Hours", "decimal")];
        session.Store(type);
        await session.SaveChangesAsync(Ct);
    }
}
