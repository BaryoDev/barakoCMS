using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BarakoCMS.Tests.Features.Connectors;

/// <summary>
/// Moving a connector to a different origin needs its stored credentials entered again.
/// </summary>
/// <remarks>
/// Whoever edits a connector cannot read its stored secret, and an update that omits a secret keeps
/// it. Together those let an edit send a credential somewhere its owner never entered it for, so a
/// change of scheme, host or port refuses to keep the stored secrets implicitly. A path change on
/// the same origin keeps them, since that is the common "correct the base URL" edit.
///
/// The outbound half is a stub in place of the ExternalApi client's primary handler, so the probe
/// that the test button sends is recorded rather than dialled.
/// </remarks>
[Collection("Sequential")]
public class ConnectorAddressChangeTests
{
    private const string Token = "tok_address_change_credential";

    private readonly IntegrationTestFixture _factory;

    public ConnectorAddressChangeTests(IntegrationTestFixture factory) => _factory = factory;

    private static readonly ConcurrentQueue<(string Host, string? Authorization)> Sent = new();

    private sealed class RecordingStub : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Sent.Enqueue((request.RequestUri!.IdnHost, request.Headers.Authorization?.ToString()));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    private static readonly Lock HostLock = new();
    private static WebApplicationFactory<Program>? _host;

    private WebApplicationFactory<Program> Host
    {
        get
        {
            lock (HostLock)
            {
                return _host ??= _factory.WithWebHostBuilder(b => b.ConfigureServices(services =>
                    services.AddHttpClient("ExternalApi").ConfigurePrimaryHttpMessageHandler(() => new RecordingStub())));
            }
        }
    }

    [Fact]
    public async Task Moving_to_another_host_without_the_secret_is_refused_and_the_secret_stays_home()
    {
        var client = await AdminAsync();
        var slug = NewSlug();
        var home = $"{slug}-home.example";
        var elsewhere = $"{slug}-elsewhere.example";
        await CreateAsync(client, slug, $"https://{home}/api");

        var moved = await PutAsync(client, slug, $"https://{elsewhere}/api", secrets: null);
        var body = await moved.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        moved.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "the stored Token was entered for {0}, and nobody entered it for {1}", home, elsewhere);
        body.Should().Contain("Token", "the refusal names the secret that has to be entered again");

        (await StoredAsync(slug)).BaseUrl.Should().Be($"https://{home}/api", "a refused update changes nothing");

        await ProbeAsync(client, slug);

        var sent = Sent.Where(s => s.Host == home || s.Host == elsewhere).ToList();
        sent.Should().NotBeEmpty("the test button sends a probe, so there is something to inspect");
        sent.Should().NotContain(s => s.Host == elsewhere, "the stored credential must not reach the new host");
        sent.Should().Contain(s => s.Host == home && s.Authorization == $"Bearer {Token}",
            "the connector still works where it was");
    }

    [Fact]
    public async Task A_path_change_on_the_same_origin_keeps_the_secret()
    {
        var client = await AdminAsync();
        var slug = NewSlug();
        var home = $"{slug}-home.example";
        await CreateAsync(client, slug, $"https://{home}/api");

        var moved = await PutAsync(client, slug, $"https://{home}/v2", secrets: null);
        moved.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}",
            moved.StatusCode, await moved.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        (await SecretCountAsync(slug)).Should().Be(1, "the origin did not change, so the credential still belongs there");

        await ProbeAsync(client, slug);

        var sent = Sent.Where(s => s.Host == home).ToList();
        sent.Should().NotBeEmpty();
        sent.Should().Contain(s => s.Authorization == $"Bearer {Token}");
    }

    [Fact]
    public async Task Moving_to_another_host_with_the_secret_entered_again_succeeds()
    {
        var client = await AdminAsync();
        var slug = NewSlug();
        var elsewhere = $"{slug}-elsewhere.example";
        await CreateAsync(client, slug, $"https://{slug}-home.example/api");

        var moved = await PutAsync(client, slug, $"https://{elsewhere}/api",
            secrets: new() { ["Token"] = "tok_entered_for_the_new_host" });
        moved.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}",
            moved.StatusCode, await moved.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        await ProbeAsync(client, slug);

        var sent = Sent.Where(s => s.Host == elsewhere).ToList();
        sent.Should().NotBeEmpty();
        sent.Should().OnlyContain(s => s.Authorization == "Bearer tok_entered_for_the_new_host",
            "only the value entered for the new host goes there");
    }

    [Fact]
    public async Task Moving_to_another_host_while_clearing_the_secret_succeeds()
    {
        var client = await AdminAsync();
        var slug = NewSlug();
        await CreateAsync(client, slug, $"https://{slug}-home.example/api");

        var moved = await PutAsync(client, slug, $"https://{slug}-elsewhere.example/api",
            secrets: new() { ["Token"] = "" }, auth: "None");
        moved.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}",
            moved.StatusCode, await moved.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        (await SecretCountAsync(slug)).Should().Be(0);
    }

    [Theory]
    [InlineData("https://{0}-home.example:8443/api", "a different port")]
    [InlineData("http://{0}-home.example/api", "a different scheme")]
    [InlineData("https://{0}-home.example.other.example/api", "a host that only starts the same")]
    [InlineData("https://{0}-home.example@{0}-elsewhere.example/api", "a different host behind userinfo that looks like the old one")]
    public async Task A_different_origin_needs_the_secret_again(string template, string because)
    {
        var client = await AdminAsync();
        var slug = NewSlug();
        await CreateAsync(client, slug, $"https://{slug}-home.example/api");

        var moved = await PutAsync(client, slug, string.Format(template, slug), secrets: null);

        moved.StatusCode.Should().Be(HttpStatusCode.BadRequest, because);
        (await SecretCountAsync(slug)).Should().Be(1);
    }

    [Theory]
    [InlineData("https://{0}-HOME.example/other", "host case is not significant")]
    [InlineData("https://{0}-home.example:443/api", "443 is the https default")]
    public async Task The_same_origin_written_differently_keeps_the_secret(string template, string because)
    {
        var client = await AdminAsync();
        var slug = NewSlug();
        await CreateAsync(client, slug, $"https://{slug}-home.example/api");

        var moved = await PutAsync(client, slug, string.Format(template, slug), secrets: null);

        moved.IsSuccessStatusCode.Should().BeTrue("{0}; got {1}: {2}", because,
            moved.StatusCode, await moved.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        (await SecretCountAsync(slug)).Should().Be(1);
    }

    [Fact]
    public async Task An_international_host_matches_its_punycode_form()
    {
        var client = await AdminAsync();
        var slug = NewSlug();
        await CreateAsync(client, slug, $"https://{slug}.bücher.example/api");

        var moved = await PutAsync(client, slug, $"https://{slug}.xn--bcher-kva.example/api", secrets: null);

        moved.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}",
            moved.StatusCode, await moved.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        (await SecretCountAsync(slug)).Should().Be(1);
    }

    [Theory]
    [InlineData("https://{0}-other.example/")]
    [InlineData("//{0}-other.example/")]
    [InlineData("/\\{0}-other.example/")]
    [InlineData("health")]
    public async Task A_probe_path_that_is_not_a_path_on_the_base_url_is_refused(string template)
    {
        var client = await AdminAsync();
        var slug = NewSlug();
        await CreateAsync(client, slug, $"https://{slug}-home.example/api");

        var res = await PutAsync(client, slug, $"https://{slug}-home.example/api", secrets: null,
            probePath: string.Format(template, slug));
        var body = await res.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest, "got: {0}", body);
        body.Should().Contain("ProbePath");
    }

    [Fact]
    public async Task A_relative_probe_path_is_sent_to_the_base_url_host()
    {
        var client = await AdminAsync();
        var slug = NewSlug();
        var home = $"{slug}-home.example";
        await CreateAsync(client, slug, $"https://{home}/api");

        var res = await PutAsync(client, slug, $"https://{home}/api", secrets: null, probePath: "/health");
        res.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}",
            res.StatusCode, await res.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        await ProbeAsync(client, slug);

        var sent = Sent.Where(s => s.Host == home).ToList();
        sent.Should().NotBeEmpty();
        sent.Should().Contain(s => s.Authorization == $"Bearer {Token}");
    }

    /// <summary>
    /// A row saved before the probe path was checked can still hold an absolute URL, so the probe
    /// itself refuses to leave the base URL's origin.
    /// </summary>
    [Theory]
    [InlineData("https://{0}-other.example/")]
    [InlineData("//{0}-other.example/")]
    public async Task A_stored_probe_path_on_another_origin_is_not_sent(string template)
    {
        var client = await AdminAsync();
        var slug = NewSlug();
        var home = $"{slug}-home.example";
        var other = $"{slug}-other.example";
        await CreateAsync(client, slug, $"https://{home}/api");

        await ProbeAsync(client, slug);
        Sent.Where(s => s.Host == home).Should().NotBeEmpty("the control probe reaches the stub");

        using (var scope = _factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            var stored = await session.Query<Connector>()
                .FirstAsync(c => c.Slug == slug, TestContext.Current.CancellationToken);
            stored.ProbePath = string.Format(template, slug);
            session.Store(stored);
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var res = await client.PostAsync($"/api/connectors/{slug}/test", null, TestContext.Current.CancellationToken);
        var body = await res.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        res.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}", res.StatusCode, body);

        using var doc = JsonDocument.Parse(body);
        doc.RootElement.GetProperty("succeeded").GetBoolean().Should().BeFalse(body);
        Sent.Should().NotContain(s => s.Host == other, "nothing, and so no credential, goes to the other host");
    }

    private static string NewSlug() => "move" + Guid.NewGuid().ToString("n")[..10];

    private static object Payload(
        string slug, string baseUrl, Dictionary<string, string>? secrets, string auth = "BearerToken",
        string probePath = "/") => new
    {
        name = "Moving connector",
        slug,
        baseUrl,
        auth,
        settings = new Dictionary<string, string>(),
        enabled = true,
        probePath,
        secrets,
    };

    private static async Task CreateAsync(HttpClient client, string slug, string baseUrl)
    {
        var res = await client.PostAsJsonAsync("/api/connectors",
            Payload(slug, baseUrl, new() { ["Token"] = Token }), TestContext.Current.CancellationToken);

        res.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}",
            res.StatusCode, await res.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    private static Task<HttpResponseMessage> PutAsync(
        HttpClient client, string slug, string baseUrl, Dictionary<string, string>? secrets, string auth = "BearerToken",
        string probePath = "/") =>
        client.PutAsJsonAsync($"/api/connectors/{slug}", Payload(slug, baseUrl, secrets, auth, probePath),
            TestContext.Current.CancellationToken);

    private static async Task ProbeAsync(HttpClient client, string slug)
    {
        var res = await client.PostAsync($"/api/connectors/{slug}/test", null, TestContext.Current.CancellationToken);
        var body = await res.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        res.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}", res.StatusCode, body);
        using var doc = JsonDocument.Parse(body);
        doc.RootElement.GetProperty("succeeded").GetBoolean().Should().BeTrue("the stub answers 200: {0}", body);
    }

    private async Task<Connector> StoredAsync(string slug)
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        return (await session.Query<Connector>()
            .FirstOrDefaultAsync(c => c.Slug == slug, TestContext.Current.CancellationToken))!;
    }

    private async Task<int> SecretCountAsync(string slug)
    {
        var connector = await StoredAsync(slug);

        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        return await session.Query<ConnectorSecret>()
            .CountAsync(s => s.ConnectorId == connector.Id, TestContext.Current.CancellationToken);
    }

    private async Task<HttpClient> AdminAsync()
    {
        var client = Host.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", await _factory.StoredUserTokenAsync("SuperAdmin", "Admin"));
        return client;
    }
}
