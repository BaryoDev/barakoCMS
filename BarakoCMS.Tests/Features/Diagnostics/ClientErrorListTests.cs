using Xunit;
using FluentAssertions;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BarakoCMS.Diagnostics;
using Marten;
using Microsoft.Extensions.DependencyInjection;

namespace BarakoCMS.Tests.Features.Diagnostics;

/// <summary>
/// GET /api/client-errors over real HTTP. The search filter (?q=) is the point: it was written with the
/// StringComparison overload of string.Contains, which Marten's LINQ provider cannot translate, so any
/// search threw at runtime. Nothing covered it because the module's schema was never registered in the
/// test fixture. Also covers the resolved/severity filters and the admin-only gate.
/// </summary>
[Collection("Sequential")]
public class ClientErrorListTests
{
    private readonly IntegrationTestFixture _factory;

    public ClientErrorListTests(IntegrationTestFixture factory) => _factory = factory;

    /// <summary>An admin GET, so the call sites do not stack three awaits to read a body.</summary>
    private async Task<HttpResponseMessage> AdminGetAsync(string url) =>
        await (await AdminClient()).GetAsync(url);

    private async Task<HttpClient> AdminClient()
    {
        var c = _factory.CreateClient();
        c.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", await _factory.StoredUserTokenAsync("Admin"));
        return c;
    }

    private async Task SeedAsync(params (string Message, string Severity, bool Resolved)[] rows)
    {
        using var scope = _factory.Services.CreateScope();
        var s = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        foreach (var (message, severity, resolved) in rows)
        {
            s.Store(new ClientError
            {
                Id = Guid.NewGuid(),
                Fingerprint = Guid.NewGuid().ToString("N"),
                Message = message,
                Severity = severity,
                Resolved = resolved,
            });
        }
        await s.SaveChangesAsync();
    }

    [Fact]
    public async Task Search_ByMessage_Works()
    {
        await SeedAsync(
            ("TypeError: cannot read property of undefined", "error", false),
            ("NetworkError when fetching resource", "error", false));

        var res = await AdminGetAsync("/api/client-errors?q=TypeError");

        res.StatusCode.Should().Be(HttpStatusCode.OK, "a search must not throw a LINQ translation error");
        var body = await res.Content.ReadAsStringAsync();
        body.Should().Contain("TypeError");
        body.Should().NotContain("NetworkError", "the search filters the result set");
    }

    [Fact]
    public async Task Search_IsCaseInsensitive()
    {
        await SeedAsync(("Uncaught ReferenceError in checkout", "error", false));

        var res = await AdminGetAsync("/api/client-errors?q=referenceerror");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        (await res.Content.ReadAsStringAsync()).Should().Contain("ReferenceError");
    }

    [Fact]
    public async Task Filters_ByResolvedAndSeverity()
    {
        var tag = Guid.NewGuid().ToString("N")[..8];
        await SeedAsync(
            ($"open-{tag}", "error", false),
            ($"done-{tag}", "error", true),
            ($"warn-{tag}", "warning", false));

        var open = await (await AdminGetAsync($"/api/client-errors?resolved=false&q={tag}")).Content.ReadAsStringAsync();
        open.Should().Contain($"open-{tag}").And.Contain($"warn-{tag}");
        open.Should().NotContain($"done-{tag}");

        var warnings = await (await AdminGetAsync($"/api/client-errors?severity=warning&q={tag}")).Content.ReadAsStringAsync();
        warnings.Should().Contain($"warn-{tag}");
        warnings.Should().NotContain($"open-{tag}");
    }

    [Fact]
    public async Task ReportedError_ShowsUpInTheList()
    {
        // The capture half posts here anonymously; the admin's Errors page reads the list. This is the
        // round trip that proves the two halves actually meet.
        var marker = $"capture-{Guid.NewGuid():N}"[..20];
        var anon = _factory.CreateClient();

        var post = await anon.PostAsJsonAsync("/api/client-errors", new
        {
            items = new[]
            {
                new { kind = "error", severity = "error", message = marker, source = "app.js", url = "/admin" },
            },
        });
        post.IsSuccessStatusCode.Should().BeTrue();

        var listed = await (await AdminGetAsync($"/api/client-errors?q={marker}")).Content.ReadAsStringAsync();
        listed.Should().Contain(marker);
    }

    [Fact]
    public async Task RepeatedReport_IsDedupedByFingerprint()
    {
        var marker = $"dupe-{Guid.NewGuid():N}"[..18];
        var anon = _factory.CreateClient();
        object Body() => new { items = new[] { new { kind = "error", severity = "error", message = marker, source = "app.js" } } };

        await anon.PostAsJsonAsync("/api/client-errors", Body());
        await anon.PostAsJsonAsync("/api/client-errors", Body());

        var json = await (await AdminGetAsync($"/api/client-errors?q={marker}")).Content.ReadFromJsonAsync<JsonElement>();
        json.GetProperty("totalItems").GetInt32().Should().Be(1, "the same fault is one row with a count, not two rows");
    }

    [Fact]
    public async Task RequiresAdminRole()
    {
        var anon = _factory.CreateClient();
        (await anon.GetAsync("/api/client-errors")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var editor = _factory.CreateClient();
        editor.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", await _factory.StoredUserTokenAsync("Editor"));
        (await editor.GetAsync("/api/client-errors")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
