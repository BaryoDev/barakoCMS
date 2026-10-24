using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using barakoCMS.Models;

namespace BarakoCMS.Tests.Features.Public;

/// <summary>
/// <c>GET /api/public/{type}</c> takes its count and its page in one query (#508), and
/// <c>changedSince</c> keeps only what was updated after an instant (#560).
/// </summary>
[Collection("IntegrationSerial")]
public partial class PublicListRoundTripTests
{
    private static int _ipCounter;

    private readonly IntegrationTestFixture _fixture;

    public PublicListRoundTripTests(IntegrationTestFixture fixture) => _fixture = fixture;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [GeneratedRegex(@"\bmt_doc_contents\b")]
    private static partial Regex ContentTable();

    private HttpClient Anonymous()
    {
        var n = Interlocked.Increment(ref _ipCounter);
        var client = _fixture.CreateClient();
        client.DefaultRequestHeaders.Add(TestRemoteIpFilter.Header, $"198.18.{n / 250 % 250}.{n % 250 + 1}");
        return client;
    }

    private async Task<string> TypeAsync(params DateTime[] updated)
    {
        var type = $"rt{Guid.NewGuid():n}"[..12];
        var store = _fixture.Services.GetRequiredService<IDocumentStore>();
        await using var session = store.LightweightSession();
        session.Store(new ContentTypeDefinition
        {
            Id = Guid.NewGuid(),
            Name = type,
            DisplayName = type,
            IsPubliclyDeliverable = true,
            Fields =
            [
                new FieldDefinition { Name = "Title", DisplayName = "Title", Type = "string" },
            ],
        });

        for (var i = 0; i < updated.Length; i++)
        {
            session.Store(new Content
            {
                Id = Guid.NewGuid(),
                ContentType = type,
                Status = ContentStatus.Published,
                Sensitivity = SensitivityLevel.Public,
                Data = new Dictionary<string, object> { ["Title"] = $"entry-{i}" },
                CreatedAt = updated[i],
                UpdatedAt = updated[i],
            });
        }

        // A draft updated last of all, which no published list may return.
        session.Store(new Content
        {
            Id = Guid.NewGuid(),
            ContentType = type,
            Status = ContentStatus.Draft,
            Sensitivity = SensitivityLevel.Public,
            Data = new Dictionary<string, object> { ["Title"] = "draft" },
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });

        await session.SaveChangesAsync(Ct);
        return type;
    }

    /// <summary>
    /// The statements against the content table that one request ran, told apart from every other
    /// test's by a trace id the request carries in.
    /// </summary>
    private async Task<(HttpResponseMessage Response, List<string> Statements)> TracedGetAsync(string url)
    {
        // The private site check caches its answer; this makes sure it is cached before the count,
        // so the count can include every statement and none is excused. A path of its own, so the page itself is not output cached.
        (await Anonymous().GetAsync("/api/public/gate-warmup", Ct)).Dispose();

        var traceId = ActivityTraceId.CreateRandom();
        var statements = new ConcurrentQueue<string>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name is "Npgsql" or "Microsoft.AspNetCore",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                if (activity.Source.Name != "Npgsql" || activity.TraceId != traceId)
                {
                    return;
                }

                var text = activity.GetTagItem("db.statement") as string
                           ?? activity.GetTagItem("db.query.text") as string
                           ?? string.Empty;
                statements.Enqueue(text);
            },
        };
        ActivitySource.AddActivityListener(listener);

        var client = Anonymous();
        client.DefaultRequestHeaders.Add("traceparent", $"00-{traceId}-{ActivitySpanId.CreateRandom()}-01");
        var response = await client.GetAsync(url, Ct);

        statements.Should().NotBeEmpty("the request's own database spans were heard, or the count below proves nothing");
        // The private site gate reads the content table once per anonymous request, before the page.
        return (response, statements.Where(s => ContentTable().IsMatch(s) && !s.Contains(PrivateSiteGate)).ToList());
    }

    private const string PrivateSiteGate = "lower(e.key) = 'mode'";

    private static async Task<JsonElement> OkJsonAsync(HttpResponseMessage response)
    {
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
    }

    [Fact]
    public async Task A_page_and_its_total_come_from_one_query_against_the_content_table()
    {
        var now = DateTime.UtcNow;
        var type = await TypeAsync(now.AddHours(-3), now.AddHours(-2), now.AddHours(-1));

        var (response, statements) = await TracedGetAsync($"/api/public/{type}?pageSize=2");

        var page = await OkJsonAsync(response);
        page.GetProperty("items").GetArrayLength().Should().Be(2);
        page.GetProperty("totalItems").GetInt32().Should().Be(3, "the total counts past the page, and not the draft");
        statements.Should().HaveCount(1, "the count rides on the page's query");
    }

    [Fact]
    public async Task A_page_past_the_end_still_reports_the_total()
    {
        var now = DateTime.UtcNow;
        var type = await TypeAsync(now.AddHours(-2), now.AddHours(-1));

        var page = await OkJsonAsync(await Anonymous().GetAsync($"/api/public/{type}?page=5&pageSize=2", Ct));

        page.GetProperty("items").GetArrayLength().Should().Be(0);
        page.GetProperty("totalItems").GetInt32().Should().Be(2);
    }

    [Fact]
    public async Task ChangedSince_keeps_only_entries_updated_after_the_instant_and_counts_them()
    {
        var now = DateTime.UtcNow;
        var type = await TypeAsync(now.AddDays(-3), now.AddDays(-2), now.AddHours(-2), now.AddHours(-1));
        var since = Uri.EscapeDataString(new DateTimeOffset(now.AddDays(-1)).ToString("O"));

        var all = await OkJsonAsync(await Anonymous().GetAsync($"/api/public/{type}", Ct));
        all.GetProperty("totalItems").GetInt32().Should().Be(4, "without the parameter the list is what it was");

        var changed = await OkJsonAsync(await Anonymous().GetAsync($"/api/public/{type}?changedSince={since}", Ct));

        var titles = changed.GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("data").GetProperty("Title").GetString())
            .ToList();
        titles.Should().HaveCount(2);
        titles.Should().BeEquivalentTo(["entry-2", "entry-3"]);
        changed.GetProperty("totalItems").GetInt32().Should().Be(2);
    }

    [Fact]
    public async Task ChangedSince_with_an_offset_is_read_as_the_instant_it_names()
    {
        var now = DateTime.UtcNow;
        var type = await TypeAsync(now.AddHours(-2), now.AddHours(-1));
        var instant = new DateTimeOffset(now.AddMinutes(-90)).ToOffset(TimeSpan.FromHours(8));
        var since = Uri.EscapeDataString(instant.ToString("O"));

        var changed = await OkJsonAsync(await Anonymous().GetAsync($"/api/public/{type}?changedSince={since}", Ct));

        var titles = changed.GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("data").GetProperty("Title").GetString())
            .ToList();
        titles.Should().Equal(["entry-1"], "only the entry updated an hour ago is after the instant, in any offset");
    }

    [Fact]
    public async Task ChangedSince_that_is_not_a_date_is_a_400()
    {
        var type = await TypeAsync(DateTime.UtcNow);

        var response = await Anonymous().GetAsync($"/api/public/{type}?changedSince=yesterday", Ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
