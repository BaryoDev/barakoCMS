using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using barakoCMS.Infrastructure.Connectors;
using barakoCMS.Models;

namespace BarakoCMS.Tests.Features.Connectors;

/// <summary>
/// Connectors: where a third party's credentials live, and everywhere they must not turn up.
/// </summary>
/// <remarks>
/// The design claim is that a bug returning a Connector cannot leak a token, because the token is
/// not in the object. These tests check that claim from the outside: against the raw response body
/// and the raw document, not through the service that decrypts.
///
/// Reading a secret back through the protector would pass just as well against an implementation
/// that stored the plaintext, which is the failure being ruled out.
/// </remarks>
[Collection("Sequential")]
public class ConnectorTests
{
    private const string Token = "tok_a_live_credential_nobody_should_see";

    private readonly IntegrationTestFixture _factory;

    public ConnectorTests(IntegrationTestFixture factory) => _factory = factory;

    [Fact]
    public async Task A_stored_credential_is_encrypted_in_the_document()
    {
        var client = await AdminClient();
        var slug = await CreateAsync(client, secrets: new() { ["Token"] = Token });

        var raw = await RawSecretJsonAsync();

        raw.Should().NotBeNull("the connector was created with a secret, so there is a row to read");
        raw.Should().NotContain(Token, "a database dump must not hand over a working credential");
        raw.Should().Contain("ProtectedValue", "the ciphertext is what is stored in its place");
        slug.Should().NotBeNullOrEmpty();
    }

    /// <summary>
    /// The control. Without it, a create that silently stored nothing would pass the test above.
    /// </summary>
    [Fact]
    public async Task A_stored_credential_decrypts_again()
    {
        var client = await AdminClient();
        var slug = await CreateAsync(client, secrets: new() { ["Token"] = Token });

        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        var protector = scope.ServiceProvider.GetRequiredService<IConnectorSecretProtector>();

        var connector = await session.Query<Connector>()
            .FirstOrDefaultAsync(c => c.Slug == slug, TestContext.Current.CancellationToken);
        var secret = await session.Query<ConnectorSecret>()
            .FirstOrDefaultAsync(s => s.ConnectorId == connector!.Id, TestContext.Current.CancellationToken);

        protector.Unprotect(secret!.ProtectedValue).Should().Be(Token,
            "encrypting it is only useful if it comes back");
    }

    /// <summary>
    /// No read endpoint returns a secret, and neither does the write that was handed one.
    /// </summary>
    [Fact]
    public async Task No_endpoint_returns_a_stored_secret()
    {
        var client = await AdminClient();

        var created = await client.PostAsJsonAsync("/api/connectors", NewConnector(NewSlug(), new() { ["Token"] = Token }),
            TestContext.Current.CancellationToken);
        var createdBody = await created.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        created.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}", created.StatusCode, createdBody);

        var slug = SlugOf(createdBody);

        var one = await Body(client, $"/api/connectors/{slug}");
        var list = await Body(client, "/api/connectors");

        // The write is the easiest one to echo from: it had the plaintext in hand.
        createdBody.Should().NotContain(Token, "the endpoint that was handed the secret must not hand it back");
        one.Should().NotContain(Token);
        list.Should().NotContain(Token);

        one.Should().Contain("Token", "the NAME of the secret is returned, so a screen can say one is set");
        one.Should().Contain("secretKeys");
    }

    /// <summary>
    /// The audit trail records who pointed a credential where, and never the credential.
    /// </summary>
    [Fact]
    public async Task Creating_a_connector_is_audited_without_the_secret()
    {
        var client = await AdminClient();
        var slug = await CreateAsync(client, secrets: new() { ["Token"] = Token });

        var entries = await AuditAsync(slug);

        entries.Should().NotBeEmpty("who added a credential pointing where is the first question a review asks");
        entries.Should().Contain(e => e.Action == "connector.created");

        var serialised = System.Text.Json.JsonSerializer.Serialize(entries);
        serialised.Should().NotContain(Token,
            "an audit entry that quotes a credential puts it in the one table designed never to be deleted from");
        serialised.Should().Contain("Token", "the name of the secret held is recorded, which is the useful half");
    }

    /// <summary>
    /// A base URL pointing at a private address is refused, and the refusal happens at send time.
    /// </summary>
    /// <remarks>
    /// Saved without complaint on purpose: a name resolving to a private address at save time is not
    /// the attack, a name whose answer changes afterwards is. The guard that matters runs when the
    /// socket opens, so the test drives the test button rather than the create.
    /// </remarks>
    [Fact]
    public async Task A_connector_pointing_at_a_private_address_fails_at_send_time()
    {
        var client = await AdminClient();
        var slug = await CreateAsync(client, baseUrl: "http://169.254.169.254", secrets: null);

        var res = await client.PostAsync($"/api/connectors/{slug}/test", null, TestContext.Current.CancellationToken);
        var body = await res.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        res.IsSuccessStatusCode.Should().BeTrue("the test button reports a failure, it does not become one");

        using var doc = System.Text.Json.JsonDocument.Parse(body);
        doc.RootElement.GetProperty("succeeded").GetBoolean().Should().BeFalse(
            "the cloud metadata service is exactly what an SSRF guard exists to keep a connector away from");
    }

    /// <summary>
    /// A test reports the status code and the round trip, and never a response body.
    /// </summary>
    /// <remarks>
    /// A 401 from an OAuth provider frequently contains the credential that was sent, so a helpful
    /// error that quotes the response is how a token reaches a log aggregator in one step.
    /// </remarks>
    [Fact]
    public async Task A_test_result_carries_no_response_body()
    {
        var client = await AdminClient();
        var slug = await CreateAsync(client, baseUrl: "http://127.0.0.1:9", secrets: null);

        var body = await (await client.PostAsync($"/api/connectors/{slug}/test", null, TestContext.Current.CancellationToken))
            .Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        using var doc = System.Text.Json.JsonDocument.Parse(body);
        doc.RootElement.TryGetProperty("elapsedMs", out _).Should().BeTrue("the round trip is reported");
        doc.RootElement.EnumerateObject().Select(p => p.Name)
            .Should().BeEquivalentTo(["succeeded", "statusCode", "elapsedMs", "error"],
                "anything else here is a place a response body could arrive in");
    }

    /// <summary>
    /// Omitting a secret on update leaves it alone; sending it empty clears it.
    /// </summary>
    /// <remarks>
    /// They have to differ. The screen cannot show the current value, so it has no way to send it
    /// back unchanged, and an absent key meaning "delete" would wipe the credential every time
    /// somebody corrected the base URL.
    /// </remarks>
    [Fact]
    public async Task Updating_without_the_secret_leaves_it_alone_and_an_empty_one_clears_it()
    {
        var client = await AdminClient();
        var slug = await CreateAsync(client, secrets: new() { ["Token"] = Token });

        var renamed = await client.PutAsJsonAsync($"/api/connectors/{slug}",
            NewConnector(slug, secrets: null, name: "Renamed"), TestContext.Current.CancellationToken);
        renamed.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}",
            renamed.StatusCode, await renamed.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        (await SecretCountAsync(slug)).Should().Be(1, "an omitted field is not an instruction to delete a credential");

        var cleared = await client.PutAsJsonAsync($"/api/connectors/{slug}",
            NewConnector(slug, secrets: new() { ["Token"] = "" }), TestContext.Current.CancellationToken);
        cleared.IsSuccessStatusCode.Should().BeTrue();

        (await SecretCountAsync(slug)).Should().Be(0, "an empty value is the way to remove one");
    }

    /// <summary>
    /// Deleting a connector takes its credentials with it, in the same transaction.
    /// </summary>
    /// <remarks>
    /// Leaving them behind keeps decryptable credentials in the database belonging to a connector
    /// nobody can see any more: still a liability, no longer visible.
    /// </remarks>
    [Fact]
    public async Task Deleting_a_connector_removes_its_secrets()
    {
        var client = await AdminClient();
        var slug = await CreateAsync(client, secrets: new() { ["Token"] = Token });

        (await SecretCountAsync(slug)).Should().Be(1, "the secret was stored, so the delete has something to remove");

        var res = await client.DeleteAsync($"/api/connectors/{slug}", TestContext.Current.CancellationToken);
        res.IsSuccessStatusCode.Should().BeTrue("got {0}", res.StatusCode);

        (await SecretCountAsync(slug)).Should().Be(0, "nothing decryptable is left behind");
    }

    /// <summary>
    /// A content editor cannot configure a connector.
    /// </summary>
    [Fact]
    public async Task A_content_editor_cannot_configure_a_connector()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", await _factory.StoredUserTokenAsync("Editor"));

        var res = await client.PostAsJsonAsync("/api/connectors", NewConnector(NewSlug(), null),
            TestContext.Current.CancellationToken);

        res.StatusCode.Should().Be(HttpStatusCode.Forbidden, "this is credential management, not content editing");
    }

    /// <summary>
    /// A key that matches another control's is refused at startup, naming both.
    /// </summary>
    /// <remarks>
    /// SECURITY.md records the lesson from Mfa:Key falling back to JWT:Key: one rotation retires two
    /// unrelated controls and the operator finds out from whichever breaks first. This turns the
    /// note into a check. The test host's own key is deliberately its own, so the rule is exercised
    /// every run rather than only here.
    /// </remarks>
    [Fact]
    public void A_connector_key_shared_with_another_control_is_refused_at_startup()
    {
        var shared = "a-shared-secret-that-is-long-enough-to-pass";
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Connectors:Key"] = shared,
            ["JWT:Key"] = shared,
        }).Build();

        var act = () => ConnectorOptions.FromConfiguration(config).Validate(config);

        act.Should().Throw<InvalidOperationException>().WithMessage("*JWT:Key*");
    }

    [Fact]
    public void A_connector_key_that_is_too_short_is_refused_at_startup()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Connectors:Key"] = "too-short",
        }).Build();

        var act = () => ConnectorOptions.FromConfiguration(config).Validate(config);

        act.Should().Throw<InvalidOperationException>().WithMessage("*at least*");
    }

    /// <summary>
    /// The control for the two above: no key at all is not an error, it means the feature is off.
    /// </summary>
    /// <remarks>
    /// Without this, a Validate that threw unconditionally would pass both, and every deployment
    /// that does not use connectors would fail to start.
    /// </remarks>
    [Fact]
    public void No_connector_key_is_not_a_startup_error()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build();

        var act = () => ConnectorOptions.FromConfiguration(config).Validate(config);

        act.Should().NotThrow("an absent key means connectors are off, not that the deployment is broken");
    }

    /// <summary>
    /// Two tenants can each hold a connector with the same slug.
    /// </summary>
    /// <remarks>
    /// The unique index on the slug has to be scoped per tenant. Marten does not infer that from the
    /// document being multi-tenanted: without `TenancyScope.PerTenant` the index is global, and the
    /// first tenant to name a connector "company-jira" stops every other tenant using that name,
    /// with a 409 that says the slug is taken by something they cannot see.
    ///
    /// Written against the store rather than over HTTP, because it is the index that is under test
    /// and a request only reaches one tenant at a time.
    /// </remarks>
    [Fact]
    public async Task Two_tenants_can_hold_the_same_connector_slug()
    {
        var slug = NewSlug();
        var store = _factory.Services.GetRequiredService<IDocumentStore>();

        foreach (var tenant in new[] { "conn-tenancy-a", "conn-tenancy-b" })
        {
            await using var session = store.LightweightSession(tenant);
            session.Store(new Connector
            {
                Id = Guid.NewGuid(),
                Name = "Company Jira",
                Slug = slug,
                BaseUrl = "https://example.com",
                Auth = ConnectorAuth.None,
            });

            // The second SaveChanges is the assertion. A global unique index throws here, and the
            // message names a constraint rather than anything about tenants.
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var check = store.QuerySession("conn-tenancy-b");
        var mine = await check.Query<Connector>().CountAsync(c => c.Slug == slug, TestContext.Current.CancellationToken);

        mine.Should().Be(1, "each tenant sees its own, and neither blocked the other");
    }

    private async Task<HttpClient> AdminClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", await _factory.StoredUserTokenAsync("SuperAdmin", "Admin"));
        return client;
    }

    private static string NewSlug() => "conn" + Guid.NewGuid().ToString("n")[..10];

    private static object NewConnector(
        string slug, Dictionary<string, string>? secrets, string baseUrl = "https://example.com", string name = "Company Jira") => new
    {
        name,
        slug,
        baseUrl,
        auth = secrets is null ? "None" : "BearerToken",
        settings = new Dictionary<string, string>(),
        enabled = true,
        probePath = "/",
        secrets,
    };

    private async Task<string> CreateAsync(
        HttpClient client, Dictionary<string, string>? secrets = null, string baseUrl = "https://example.com")
    {
        var slug = NewSlug();
        var res = await client.PostAsJsonAsync("/api/connectors", NewConnector(slug, secrets, baseUrl),
            TestContext.Current.CancellationToken);

        res.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}",
            res.StatusCode, await res.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return slug;
    }

    private static string SlugOf(string body)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("slug").GetString()!;
    }

    private static async Task<string> Body(HttpClient client, string path) =>
        await (await client.GetAsync(path, TestContext.Current.CancellationToken))
            .Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

    private async Task<int> SecretCountAsync(string slug)
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        var connector = await session.Query<Connector>()
            .FirstOrDefaultAsync(c => c.Slug == slug, TestContext.Current.CancellationToken);

        if (connector is null) return 0;

        return await session.Query<ConnectorSecret>()
            .CountAsync(s => s.ConnectorId == connector.Id, TestContext.Current.CancellationToken);
    }

    private async Task<List<AuditEvent>> AuditAsync(string slug)
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();

        return (await session.Query<AuditEvent>()
                .Where(e => e.Action.StartsWith("connector."))
                .ToListAsync(TestContext.Current.CancellationToken))
            .Where(e => e.Metadata != null && e.Metadata["slug"].ToString() == slug)
            .ToList();
    }

    /// <summary>The secret row as Postgres holds it, not as Marten hands it back.</summary>
    private async Task<string?> RawSecretJsonAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IDocumentStore>();
        await using var conn = store.Storage.Database.CreateConnection();
        await conn.OpenAsync(TestContext.Current.CancellationToken);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "select data::text from public.mt_doc_connector_secrets limit 1";
        return (await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken)) as string;
    }
}
