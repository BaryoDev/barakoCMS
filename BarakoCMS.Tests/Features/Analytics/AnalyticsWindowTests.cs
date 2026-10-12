using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace BarakoCMS.Tests.Features.Analytics;

/// <summary>
/// An explicit <c>from</c> and <c>to</c> on the analytics routes, and the previous window beside a
/// series (#677). The stub records what was asked of Umami, which is where a wrong window shows.
/// </summary>
[Collection("Sequential")]
public class AnalyticsWindowTests
{
    private const string SiteId = "site-1";

    private static readonly DateTimeOffset From = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset To = new(2026, 9, 8, 0, 0, 0, TimeSpan.Zero);

    private readonly IntegrationTestFixture _fixture;
    private readonly WebApplicationFactory<Program> _configured;

    public AnalyticsWindowTests(IntegrationTestFixture fixture)
    {
        _fixture = fixture;
        _configured = fixture.WithSettings(new Dictionary<string, string?>
        {
            { "Umami:Enabled", "true" },
            { "Umami:BaseUrl", "https://umami.test.example" },
            { "Umami:Username", "analytics-reader" },
            { "Umami:Password", "NotARealUmamiPassword_window" },
        });
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<HttpClient> AdminAsync()
    {
        var client = _configured.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", await _fixture.StoredUserTokenAsync("Admin", "SuperAdmin"));
        return client;
    }

    private static string Q(DateTimeOffset at) => Uri.EscapeDataString(at.ToString("O"));

    private static List<string> PageviewCalls() =>
        UmamiStubHandler.Requests.Select(r => r.Uri).Where(u => u.Contains("/pageviews", StringComparison.Ordinal)).ToList();

    private static async Task<JsonElement> OkAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.Clone();
    }

    [Fact]
    public async Task A_series_for_an_explicit_window_asks_umami_for_that_window_and_says_which_it_was()
    {
        var admin = await AdminAsync();
        UmamiStubHandler.Clear();

        var series = await OkAsync(await admin.GetAsync(
            $"/api/analytics/{SiteId}/series?from={Q(From)}&to={Q(To)}", Ct));

        var calls = PageviewCalls();
        calls.Should().ContainSingle();
        calls[0].Should().Contain($"startAt={From.ToUnixTimeMilliseconds()}&endAt={To.ToUnixTimeMilliseconds()}&unit=day");
        series.GetProperty("from").GetDateTimeOffset().Should().Be(From);
        series.GetProperty("to").GetDateTimeOffset().Should().Be(To);
        series.TryGetProperty("previous", out _).Should().BeFalse("nobody asked for the previous window");
    }

    [Fact]
    public async Task Compare_previous_adds_the_window_of_the_same_length_just_before()
    {
        var admin = await AdminAsync();
        UmamiStubHandler.Clear();

        var series = await OkAsync(await admin.GetAsync(
            $"/api/analytics/{SiteId}/series?from={Q(From)}&to={Q(To)}&compare=previous", Ct));

        var before = From.AddDays(-7);
        var calls = PageviewCalls();
        calls.Should().HaveCount(2);
        calls.Should().Contain(c => c.Contains($"startAt={before.ToUnixTimeMilliseconds()}&endAt={From.ToUnixTimeMilliseconds()}&unit=day"));

        var previous = series.GetProperty("previous");
        previous.GetProperty("from").GetDateTimeOffset().Should().Be(before);
        previous.GetProperty("to").GetDateTimeOffset().Should().Be(From);
        previous.GetProperty("pageviews").GetArrayLength().Should().Be(1);
        previous.GetProperty("pageviews")[0].GetProperty("y").GetInt64().Should().Be(12);
    }

    [Fact]
    public async Task A_window_of_two_days_or_less_is_bucketed_by_hour()
    {
        var admin = await AdminAsync();
        UmamiStubHandler.Clear();

        var series = await OkAsync(await admin.GetAsync(
            $"/api/analytics/{SiteId}/series?from={Q(From)}&to={Q(From.AddHours(36))}", Ct));

        series.GetProperty("unit").GetString().Should().Be("hour");
        PageviewCalls().Should().ContainSingle().Which.Should().Contain("unit=hour");
    }

    [Fact]
    public async Task The_summary_and_metric_routes_read_the_same_window()
    {
        var admin = await AdminAsync();
        UmamiStubHandler.Clear();
        var window = $"startAt={From.ToUnixTimeMilliseconds()}&endAt={To.ToUnixTimeMilliseconds()}";

        await OkAsync(await admin.GetAsync($"/api/analytics/{SiteId}/summary?from={Q(From)}&to={Q(To)}", Ct));
        await OkAsync(await admin.GetAsync($"/api/analytics/{SiteId}/metric?type=path&from={Q(From)}&to={Q(To)}", Ct));

        var calls = UmamiStubHandler.Requests.Select(r => r.Uri).ToList();
        calls.Should().Contain(c => c.Contains("/stats?", StringComparison.Ordinal) && c.Contains(window, StringComparison.Ordinal));
        calls.Should().Contain(c => c.Contains("/metrics?", StringComparison.Ordinal) && c.Contains(window, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Without_from_and_to_the_range_token_still_decides()
    {
        var admin = await AdminAsync();
        UmamiStubHandler.Clear();

        var series = await OkAsync(await admin.GetAsync($"/api/analytics/{SiteId}/series?range=30d", Ct));

        (series.GetProperty("to").GetDateTimeOffset() - series.GetProperty("from").GetDateTimeOffset())
            .Should().Be(TimeSpan.FromDays(30));
    }

    [Theory]
    [InlineData("series", true, false)]
    [InlineData("series", false, true)]
    [InlineData("summary", true, false)]
    [InlineData("metric", false, true)]
    public async Task One_end_without_the_other_is_refused(string route, bool sendFrom, bool sendTo)
    {
        var admin = await AdminAsync();
        var query = (sendFrom ? $"&from={Q(From)}" : "") + (sendTo ? $"&to={Q(To)}" : "");

        var response = await admin.GetAsync($"/api/analytics/{SiteId}/{route}?type=path{query}", Ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_backwards_or_overlong_window_is_refused()
    {
        var admin = await AdminAsync();

        (await admin.GetAsync($"/api/analytics/{SiteId}/series?from={Q(To)}&to={Q(From)}", Ct))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await admin.GetAsync($"/api/analytics/{SiteId}/series?from={Q(From.AddDays(-401))}&to={Q(From)}", Ct))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await admin.GetAsync($"/api/analytics/{SiteId}/series?from={Q(From.AddDays(-400))}&to={Q(From)}", Ct))
            .StatusCode.Should().Be(HttpStatusCode.OK, "400 days is the limit, not past it");
    }
}
