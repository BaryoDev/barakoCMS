using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using barakoCMS.Models;

namespace BarakoCMS.Tests.Features.Connectors;

/// <summary>
/// Request definitions: what gets composed, and what is refused before anything is sent.
/// </summary>
/// <remarks>
/// A template is written against a schema and read by a third party, and the gap between those is
/// where an integration is wrong in ways nobody sees until a customer does. The dry run is what
/// closes it, so most of these drive the dry run and assert on the exact composed call.
///
/// Two of these are the point of the feature rather than nice to have. A field the schema marks
/// Sensitive must not leave even when a template names it, and a value containing a quote must not
/// be able to rewrite the request around it.
/// </remarks>
[Collection("Sequential")]
public class RequestTests
{
    private readonly IntegrationTestFixture _factory;

    public RequestTests(IntegrationTestFixture factory) => _factory = factory;

    /// <summary>
    /// A template naming a Sensitive field is refused, and nothing is sent.
    /// </summary>
    /// <remarks>
    /// Refusing beats redacting. The operator wrote it on purpose, and a request that silently posts
    /// three asterisks where they expected a value looks like it worked. The message names the field
    /// and its level so they can act on it while they still have the template open.
    /// </remarks>
    [Fact]
    public async Task A_template_naming_a_sensitive_field_is_refused()
    {
        var client = await AdminClient();
        var (type, id) = await SeedContentAsync(sensitiveField: true);
        await SeedConnectorAsync(client);
        var slug = await SaveRequestAsync(client, body: "{\"ssn\":\"{{Secret}}\"}");

        var dry = await DryRunAsync(client, slug, id);

        dry.GetProperty("wouldSend").GetBoolean().Should().BeFalse(
            "a field that is not Public cannot leave, even when a template names it");
        var refusal = dry.GetProperty("refusal").GetString()!;
        refusal.Should().Contain("Secret");
        refusal.Should().Contain("Sensitive");
        type.Should().NotBeNullOrEmpty();
    }

    /// <summary>
    /// The control. A Public field composes and would be sent.
    /// </summary>
    /// <remarks>
    /// Without this, a composer that refused everything would pass the test above while making the
    /// feature useless.
    /// </remarks>
    [Fact]
    public async Task A_template_naming_a_public_field_composes()
    {
        var client = await AdminClient();
        var (_, id) = await SeedContentAsync(sensitiveField: false);
        await SeedConnectorAsync(client);
        var slug = await SaveRequestAsync(client, body: "{\"title\":\"{{Title}}\"}");

        var dry = await DryRunAsync(client, slug, id);

        dry.GetProperty("wouldSend").GetBoolean().Should().BeTrue(
            "got refusal: {0}", dry.TryGetProperty("refusal", out var r) ? r.ToString() : "none");
        dry.GetProperty("body").GetString().Should().Contain("a normal title");
    }

    /// <summary>
    /// A value containing a quote cannot rewrite the request around it.
    /// </summary>
    /// <remarks>
    /// This is injection in a different costume. A title of
    /// <c>","admin":true,"x":"</c> substituted raw closes the title string and adds a field the
    /// operator never wrote, which is a request to a third party that this instance did not intend
    /// to make. The assertion parses the body and reads the value back, rather than matching text,
    /// because the point is that the structure survived, not how the escaping happens to spell it.
    /// </remarks>
    [Fact]
    public async Task A_value_containing_a_quote_cannot_add_a_field()
    {
        var client = await AdminClient();
        var hostile = "\",\"admin\":true,\"x\":\"";
        var (_, id) = await SeedContentAsync(sensitiveField: false, title: hostile);
        await SeedConnectorAsync(client);
        var slug = await SaveRequestAsync(client, body: "{\"title\":\"{{Title}}\"}");

        var dry = await DryRunAsync(client, slug, id);

        dry.GetProperty("wouldSend").GetBoolean().Should().BeTrue(
            "got refusal: {0}", dry.TryGetProperty("refusal", out var r) ? r.ToString() : "none");

        using var body = JsonDocument.Parse(dry.GetProperty("body").GetString()!);
        body.RootElement.TryGetProperty("admin", out _).Should().BeFalse(
            "the title closed its own string and added a field the operator never wrote");
        body.RootElement.GetProperty("title").GetString().Should().Be(hostile,
            "and the real value still arrives intact, which is what separates escaping from stripping");
    }

    /// <summary>
    /// A path value is URL-escaped, not JSON-escaped.
    /// </summary>
    /// <remarks>
    /// The two are different and using the wrong one is silent. A JSON-escaped path puts backslashes
    /// in a URL; an unescaped one lets a value with a slash in it address a different endpoint.
    /// </remarks>
    [Fact]
    public async Task A_path_value_is_escaped_for_a_url()
    {
        var client = await AdminClient();
        var (_, id) = await SeedContentAsync(sensitiveField: false, title: "a/b c");
        await SeedConnectorAsync(client);
        var slug = await SaveRequestAsync(client, body: null, path: "/posts/{{Title}}");

        var dry = await DryRunAsync(client, slug, id);

        var url = dry.GetProperty("url").GetString()!;
        url.Should().Contain("a%2Fb%20c", "a slash in a value must not address a different endpoint");
        url.Should().NotContain("a/b c");
    }

    /// <summary>
    /// A query variable is refused rather than sent as a literal.
    /// </summary>
    /// <remarks>
    /// Queries are #328. Leaving the hole in would post the text "{{query.rows}}" to a third party,
    /// which looks like a delivery and is a defect. The refusal names the issue so the operator
    /// knows it is unbuilt rather than broken.
    /// </remarks>
    [Fact]
    public async Task A_query_variable_is_refused_while_queries_do_not_exist()
    {
        var client = await AdminClient();
        var (_, id) = await SeedContentAsync(sensitiveField: false);
        await SeedConnectorAsync(client);
        var slug = await SaveRequestAsync(client, body: "{\"rows\":\"{{query.rows}}\"}");

        var dry = await DryRunAsync(client, slug, id);

        dry.GetProperty("wouldSend").GetBoolean().Should().BeFalse();
        dry.GetProperty("refusal").GetString().Should().Contain("328");
    }

    /// <summary>
    /// A body template that composes into malformed JSON is refused before it is sent.
    /// </summary>
    [Fact]
    public async Task A_body_that_is_not_valid_json_is_refused()
    {
        var client = await AdminClient();
        var (_, id) = await SeedContentAsync(sensitiveField: false);
        await SeedConnectorAsync(client);
        var slug = await SaveRequestAsync(client, body: "{\"title\": {{Title}}}");

        var dry = await DryRunAsync(client, slug, id);

        dry.GetProperty("wouldSend").GetBoolean().Should().BeFalse(
            "a provider's answer to malformed JSON describes their parser, not this template");
        dry.GetProperty("refusal").GetString().Should().Contain("valid JSON");
    }

    /// <summary>
    /// A request naming a connector that does not exist is refused when saved.
    /// </summary>
    /// <remarks>
    /// Otherwise it is a workflow that fails at run time with a message about something the operator
    /// cannot see from the screen they were on.
    /// </remarks>
    [Fact]
    public async Task A_request_naming_a_missing_connector_is_refused_when_saved()
    {
        var client = await AdminClient();

        var res = await client.PostAsJsonAsync("/api/requests", new
        {
            name = "Post it",
            slug = NewSlug("req"),
            connectorSlug = "no-such-connector",
            method = "POST",
            pathTemplate = "/feed",
            bodyTemplate = "{}",
        }, TestContext.Current.CancellationToken);

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    /// <summary>
    /// A success rule that needs a JSON path is refused without one.
    /// </summary>
    /// <remarks>
    /// An operator who picks this rule has a provider that answers 200 with an error in the body.
    /// Accepting it with no path would behave exactly like the plain 2xx rule, so the setting would
    /// appear to be in force while changing nothing, which is the defect this project keeps finding.
    /// </remarks>
    [Fact]
    public async Task A_json_path_rule_without_a_path_is_refused()
    {
        var client = await AdminClient();
        await SeedConnectorAsync(client);

        var res = await client.PostAsJsonAsync("/api/requests", new
        {
            name = "Post it",
            slug = NewSlug("req"),
            connectorSlug = _connectorSlug,
            method = "POST",
            pathTemplate = "/feed",
            bodyTemplate = "{}",
            success = "TwoHundredAndJsonPathAbsent",
        }, TestContext.Current.CancellationToken);

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await res.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
            .Should().Contain("SuccessJsonPath");
    }

    /// <summary>
    /// TRACE is refused, along with anything else not on the allowlist.
    /// </summary>
    /// <remarks>
    /// TRACE against some proxies echoes the request headers, including the Authorization header the
    /// sender attaches. That is a way to read a credential back out of a connector that is built
    /// specifically never to return one.
    /// </remarks>
    [Fact]
    public async Task A_method_outside_the_allowlist_is_refused()
    {
        var client = await AdminClient();
        await SeedConnectorAsync(client);

        var res = await client.PostAsJsonAsync("/api/requests", new
        {
            name = "Echo it",
            slug = NewSlug("req"),
            connectorSlug = _connectorSlug,
            method = "TRACE",
            pathTemplate = "/",
        }, TestContext.Current.CancellationToken);

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    /// <summary>The success rule decides, not the status code alone.</summary>
    [Theory]
    [InlineData(SuccessRule.TwoHundredRange, 200, "{\"error\":\"nope\"}", "error", true)]
    [InlineData(SuccessRule.TwoHundredAndJsonPathAbsent, 200, "{\"error\":\"nope\"}", "error", false)]
    [InlineData(SuccessRule.TwoHundredAndJsonPathAbsent, 200, "{\"id\":\"1\"}", "error", true)]
    [InlineData(SuccessRule.TwoHundredAndJsonPathAbsent, 500, "{}", "error", false)]
    [InlineData(SuccessRule.AnyResponse, 500, "", null, true)]
    [InlineData(SuccessRule.TwoHundredRange, 404, "", null, false)]
    public void A_provider_that_answers_200_with_an_error_is_not_a_success(
        SuccessRule rule, int status, string body, string? path, bool expected)
    {
        var type = typeof(barakoCMS.Models.Connector).Assembly
            .GetType("barakoCMS.Infrastructure.Connectors.SuccessEvaluator")!;
        var method = type.GetMethod("Succeeded",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;

        var actual = (bool)method.Invoke(null, [rule, status, body, path])!;

        actual.Should().Be(expected);
    }

    private string _connectorSlug = string.Empty;

    private async Task<HttpClient> AdminClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", await _factory.StoredUserTokenAsync("SuperAdmin", "Admin"));
        return client;
    }

    private static string NewSlug(string prefix) => prefix + Guid.NewGuid().ToString("n")[..10];

    private async Task SeedConnectorAsync(HttpClient client)
    {
        _connectorSlug = NewSlug("conn");

        var res = await client.PostAsJsonAsync("/api/connectors", new
        {
            name = "Example",
            slug = _connectorSlug,
            baseUrl = "https://example.com",
            auth = "None",
            settings = new Dictionary<string, string>(),
            enabled = true,
            probePath = "/",
        }, TestContext.Current.CancellationToken);

        res.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}",
            res.StatusCode, await res.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    private async Task<string> SaveRequestAsync(
        HttpClient client, string? body, string path = "/feed")
    {
        var slug = NewSlug("req");

        var res = await client.PostAsJsonAsync("/api/requests", new
        {
            name = "Post it",
            slug,
            connectorSlug = _connectorSlug,
            method = "POST",
            pathTemplate = path,
            bodyTemplate = body,
        }, TestContext.Current.CancellationToken);

        res.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}",
            res.StatusCode, await res.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return slug;
    }

    private async Task<JsonElement> DryRunAsync(HttpClient client, string slug, Guid contentId)
    {
        var res = await client.PostAsync($"/api/requests/{slug}/dry-run/{contentId}", null,
            TestContext.Current.CancellationToken);

        res.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}",
            res.StatusCode, await res.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        return JsonDocument.Parse(
            await res.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).RootElement.Clone();
    }

    /// <summary>A content type with a Title and a Secret, and one entry of it.</summary>
    private async Task<(string Type, Guid Id)> SeedContentAsync(bool sensitiveField, string title = "a normal title")
    {
        var type = NewSlug("rq");

        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();

        session.Store(new ContentTypeDefinition
        {
            Id = Guid.NewGuid(),
            Name = type,
            DisplayName = "Request subject",
            Fields =
            [
                new FieldDefinition { Name = "Title", Type = "string" },
                new FieldDefinition
                {
                    Name = "Secret",
                    Type = "string",
                    Sensitivity = sensitiveField ? SensitivityLevel.Sensitive : SensitivityLevel.Public,
                },
            ],
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });

        var id = Guid.NewGuid();
        session.Store(new barakoCMS.Models.Content
        {
            Id = id,
            ContentType = type,
            Status = ContentStatus.Published,
            Data = new Dictionary<string, object> { ["Title"] = title, ["Secret"] = "123-45-6789" },
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });

        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        return (type, id);
    }
}
