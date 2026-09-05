using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using barakoCMS.Infrastructure.Connectors;
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
    /// A query variable is refused when the request names no query.
    /// </summary>
    /// <remarks>
    /// Leaving the hole in would post the text "{{query.rows}}" to a third party, which looks like a
    /// delivery and is a defect. The refusal says a query is needed and none is named, rather than
    /// silently sending the hole as a literal.
    /// </remarks>
    [Fact]
    public async Task A_query_variable_is_refused_when_the_request_names_no_query()
    {
        var client = await AdminClient();
        var (_, id) = await SeedContentAsync(sensitiveField: false);
        await SeedConnectorAsync(client);
        var slug = await SaveRequestAsync(client, body: "{\"rows\":\"{{query.rows}}\"}");

        var dry = await DryRunAsync(client, slug, id);

        dry.GetProperty("wouldSend").GetBoolean().Should().BeFalse();
        dry.GetProperty("refusal").GetString().Should().Contain("does not name one");
    }

    /// <summary>A query variable is refused when the named query does not exist.</summary>
    [Fact]
    public async Task A_query_variable_is_refused_when_the_named_query_does_not_exist()
    {
        var client = await AdminClient();
        var (_, id) = await SeedContentAsync(sensitiveField: false);
        await SeedConnectorAsync(client);
        var slug = await SaveRequestAsync(
            client, body: "{\"rows\":\"{{query.rows}}\"}", querySlug: "no-such-query");

        var dry = await DryRunAsync(client, slug, id);

        dry.GetProperty("wouldSend").GetBoolean().Should().BeFalse();
        dry.GetProperty("refusal").GetString().Should().Contain("no-such-query")
            .And.Contain("does not exist");
    }

    /// <summary>
    /// A hole naming a field the query does not select is refused, the same as one naming a query
    /// that does not exist.
    /// </summary>
    /// <remarks>
    /// <see cref="QueryDefinition.Fields"/> is an allowlist, not a convenience: an operator names
    /// what leaves. A hole reaching past it for a field the query never selected is exactly the
    /// class of defect the refusal for a missing query already exists to catch.
    /// </remarks>
    [Fact]
    public async Task A_field_the_query_does_not_select_is_refused()
    {
        var client = await AdminClient();
        var (type, id) = await SeedContentAsync(sensitiveField: false);
        await SeedConnectorAsync(client);
        var querySlug = await SaveQueryAsync(client, type, fields: ["Title"]);
        var slug = await SaveRequestAsync(
            client, body: "{\"x\":\"{{query.NotSelected}}\"}", querySlug: querySlug);

        var dry = await DryRunAsync(client, slug, id);

        dry.GetProperty("wouldSend").GetBoolean().Should().BeFalse();
        var refusal = dry.GetProperty("refusal").GetString()!;
        refusal.Should().Contain("NotSelected");
        refusal.Should().Contain(querySlug);
    }

    /// <summary>
    /// <c>{{query.rows}}</c> composes into a JSON array of the query's rows, one object per row.
    /// </summary>
    /// <remarks>
    /// The shape is exactly what the preview endpoint returns for the same query: an array, each
    /// element holding the fields the query selects. It is inserted unescaped rather than quoted as
    /// a JSON string, because it is already valid JSON and quoting it would hand the recipient a
    /// string full of JSON instead of an array they can read directly.
    /// </remarks>
    [Fact]
    public async Task A_query_variable_composes_the_selected_rows()
    {
        var client = await AdminClient();
        var (type, id, titles) = await SeedQueryableContentAsync(rowCount: 2, triggerTitle: "row-trigger");
        await SeedConnectorAsync(client);
        var querySlug = await SaveQueryAsync(client, type, fields: ["Title"]);
        var slug = await SaveRequestAsync(client, body: "{\"rows\": {{query.rows}}}", querySlug: querySlug);

        var dry = await DryRunAsync(client, slug, id);

        dry.GetProperty("wouldSend").GetBoolean().Should().BeTrue(
            "got refusal: {0}", dry.TryGetProperty("refusal", out var r) ? r.ToString() : "none");

        using var body = JsonDocument.Parse(dry.GetProperty("body").GetString()!);
        var rows = body.RootElement.GetProperty("rows");

        rows.GetArrayLength().Should().Be(3, "the trigger content is itself a row of the same type");
        rows.EnumerateArray().Select(r => r.GetProperty("Title").GetString())
            .Should().BeEquivalentTo(titles, "every matching row's Title arrives, not just the trigger's");
    }

    /// <summary>
    /// A field the query does not select never leaves through <c>{{query.rows}}</c>, even one that
    /// is Public and present on every matching row.
    /// </summary>
    /// <remarks>
    /// <see cref="QueryDefinition.Fields"/> is the allowlist for what a query returns, and this is
    /// the security boundary the whole feature is built around: the projection restricts a row to
    /// exactly the named fields before <see cref="RequestComposer"/> ever sees it, so a field left
    /// off the list cannot reach a template no matter what it names. Read
    /// <c>Infrastructure/Connectors/QueryRunner.cs</c>, the loop over <c>definition.Fields</c> inside
    /// <c>RunAsync</c>: drop that allowlist for a raw copy of every field on the row, and this test
    /// starts seeing Salary.
    /// </remarks>
    [Fact]
    public async Task A_field_the_query_does_not_select_never_leaves_through_query_rows()
    {
        var client = await AdminClient();
        var (type, id, _) = await SeedQueryableContentAsync(rowCount: 1, triggerTitle: "with-salary");
        await SeedConnectorAsync(client);
        // Salary is Public on this schema (SeedQueryableContentAsync), so a leak here is the
        // allowlist failing, not the separate Sensitivity refusal covered elsewhere in this file.
        var querySlug = await SaveQueryAsync(client, type, fields: ["Title"]);
        var slug = await SaveRequestAsync(client, body: "{\"rows\": {{query.rows}}}", querySlug: querySlug);

        var dry = await DryRunAsync(client, slug, id);

        dry.GetProperty("wouldSend").GetBoolean().Should().BeTrue(
            "got refusal: {0}", dry.TryGetProperty("refusal", out var r) ? r.ToString() : "none");

        using var body = JsonDocument.Parse(dry.GetProperty("body").GetString()!);
        var rows = body.RootElement.GetProperty("rows");

        rows.GetArrayLength().Should().BeGreaterThan(0, "otherwise there is nothing for the next line to check");
        foreach (var row in rows.EnumerateArray())
        {
            row.TryGetProperty("Salary", out _).Should().BeFalse(
                "the query names only Title, and naming what leaves is the whole point of Fields");
        }
    }

    /// <summary>
    /// A content field carrying a line break is refused rather than sent, when it lands in a header.
    /// </summary>
    /// <remarks>
    /// A header has no quoting to fall back to: a value of <c>"safe\r\nX-Injected: evil"</c> reaches
    /// the sender's <c>TryAddWithoutValidation</c> exactly as composed, and becomes a second header
    /// on a call sent with the connector's own credentials attached, whoever supplied the value.
    /// This predates queries; the fix is one check on every composed header, not one path through it.
    /// </remarks>
    [Fact]
    public async Task A_content_field_carrying_a_line_break_is_refused_in_a_header()
    {
        var client = await AdminClient();
        var hostile = "safe\r\nX-Injected: evil";
        var (_, id) = await SeedContentAsync(sensitiveField: false, title: hostile);
        await SeedConnectorAsync(client);
        var slug = await SaveRequestAsync(
            client, body: null, headerTemplates: new() { ["X-Custom"] = "{{Title}}" });

        var dry = await DryRunAsync(client, slug, id);

        dry.GetProperty("wouldSend").GetBoolean().Should().BeFalse(
            "a line break in a header value could forge a second header on the real request");
        var refusal = dry.GetProperty("refusal").GetString()!;
        refusal.Should().Contain("X-Custom", "the operator has to know which header was refused");
        refusal.Should().NotContain("X-Injected",
            "the refusal names the header, never the value that broke it");
    }

    /// <summary>
    /// A query field reaches a header exactly like a content field, and the same refusal catches it.
    /// </summary>
    /// <remarks>
    /// This is the shape the review that asked for this test actually demonstrated: a content field
    /// selected by a query, surfaced through <c>{{query.SomeField}}</c> in a header template, sent
    /// through a real <c>HttpClient</c> against a loopback server, and received as two headers.
    /// </remarks>
    [Fact]
    public async Task A_query_field_carrying_a_line_break_is_refused_in_a_header()
    {
        var client = await AdminClient();
        var hostile = "safe\r\nX-Injected: evil";
        var (type, id, _) = await SeedQueryableContentAsync(rowCount: 0, triggerTitle: hostile);
        await SeedConnectorAsync(client);
        var querySlug = await SaveQueryAsync(client, type, fields: ["Title"]);
        var slug = await SaveRequestAsync(
            client, body: null, querySlug: querySlug, headerTemplates: new() { ["X-Custom"] = "{{query.Title}}" });

        var dry = await DryRunAsync(client, slug, id);

        dry.GetProperty("wouldSend").GetBoolean().Should().BeFalse(
            "a line break from a query field is exactly as dangerous in a header as one from content");
        var refusal = dry.GetProperty("refusal").GetString()!;
        refusal.Should().Contain("X-Custom");
        refusal.Should().NotContain("X-Injected");
    }

    /// <summary>
    /// A scalar query field is refused when the query matched no rows, rather than composing empty.
    /// </summary>
    /// <remarks>
    /// Composing empty would make "the query matched nothing" and "the field is genuinely empty"
    /// produce the identical value, with nothing in the sent request to tell them apart afterwards.
    /// For a payload a payment or accounting provider reads, that is a silent wrong value, which is
    /// the failure this whole composer exists to avoid. <c>{{query.rows}}</c> does not need this: an
    /// empty array is still a real, distinguishable answer to "how many rows matched".
    /// </remarks>
    [Fact]
    public async Task A_scalar_query_field_is_refused_when_the_query_matched_no_rows()
    {
        var client = await AdminClient();
        var (type, id) = await SeedContentAsync(sensitiveField: false);
        await SeedConnectorAsync(client);
        // No filters, but a ContentType that has nothing stored for it, so the query is well formed
        // and simply matches zero rows rather than being refused for some other reason.
        var emptyType = NewSlug("empty");
        await SeedEmptyContentTypeAsync(emptyType);
        var querySlug = await SaveQueryAsync(client, emptyType, fields: ["Title"]);
        var slug = await SaveRequestAsync(
            client, body: "{\"x\":\"{{query.Title}}\"}", querySlug: querySlug);

        var dry = await DryRunAsync(client, slug, id);

        dry.GetProperty("wouldSend").GetBoolean().Should().BeFalse(
            "a single field has no value to compose when the query matched nothing");
        dry.GetProperty("refusal").GetString().Should().Contain("matched no rows");
    }

    /// <summary>
    /// A query is invisible to a request composed in a different tenant, even one holding the exact
    /// same slug.
    /// </summary>
    /// <remarks>
    /// Composed directly against a tenant-scoped session, the way <see cref="ConnectorTests"/>
    /// proves the connector slug index is per tenant, rather than over HTTP: it is the session's
    /// tenant scoping under test here, and a request only ever reaches one tenant's session at a
    /// time. Two tenants deliberately hold a query under the identical slug so that a leak composes
    /// successfully with the wrong tenant's rows, rather than merely returning a different slug.
    /// </remarks>
    [Fact]
    public async Task A_query_is_invisible_to_a_request_composed_in_a_different_tenant()
    {
        var store = _factory.Services.GetRequiredService<IDocumentStore>();
        var config = _factory.Services.GetRequiredService<IConfiguration>();
        var type = NewSlug("rqt");
        var slug = NewSlug("q");

        Guid ownerContentId;
        await using (var owner = store.LightweightSession("req-tenancy-owner"))
        {
            owner.Store(new ContentTypeDefinition
            {
                Id = Guid.NewGuid(),
                Name = type,
                DisplayName = "x",
                Fields = [new FieldDefinition { Name = "Title", Type = "string" }],
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            owner.Store(new QueryDefinition
            {
                Id = Guid.NewGuid(),
                Slug = slug,
                ContentType = type,
                Fields = ["Title"],
                Limit = 10,
            });
            var ownerContent = new barakoCMS.Models.Content
            {
                Id = Guid.NewGuid(),
                ContentType = type,
                Status = ContentStatus.Published,
                Data = new() { ["Title"] = "owner's row" },
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };
            owner.Store(ownerContent);
            await owner.SaveChangesAsync();
            ownerContentId = ownerContent.Id;
        }

        // The other tenant has the schema and a content item of the same type, but no query named
        // 'slug'. Without this, the owner's schema-not-found refusal could be mistaken for tenant
        // isolation.
        Guid otherContentId;
        await using (var other = store.LightweightSession("req-tenancy-other"))
        {
            other.Store(new ContentTypeDefinition
            {
                Id = Guid.NewGuid(),
                Name = type,
                DisplayName = "x",
                Fields = [new FieldDefinition { Name = "Title", Type = "string" }],
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            var otherContent = new barakoCMS.Models.Content
            {
                Id = Guid.NewGuid(),
                ContentType = type,
                Status = ContentStatus.Published,
                Data = new() { ["Title"] = "other's row" },
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };
            other.Store(otherContent);
            await other.SaveChangesAsync();
            otherContentId = otherContent.Id;
        }

        await using var otherSession = store.QuerySession("req-tenancy-other");
        var composer = new RequestComposer(otherSession, config, new QueryRunner(otherSession));

        var definition = new RequestDefinition
        {
            Id = Guid.NewGuid(),
            Slug = NewSlug("req"),
            ConnectorSlug = "unused",
            Method = "POST",
            PathTemplate = "/feed",
            BodyTemplate = "{\"rows\": {{query.rows}}}",
            QuerySlug = slug,
        };
        var connector = new Connector
        {
            Id = Guid.NewGuid(), Slug = "unused", BaseUrl = "https://example.com", Auth = ConnectorAuth.None,
        };
        var otherLoaded = await otherSession.LoadAsync<barakoCMS.Models.Content>(otherContentId);

        var composed = await composer.ComposeAsync(
            definition, connector, otherLoaded!, idempotencyKey: null, TestContext.Current.CancellationToken);

        composed.Ok.Should().BeFalse(
            "tenant 'req-tenancy-other' has no query named '{0}'; tenant 'req-tenancy-owner' having "
            + "one under the same slug must not make it visible here", slug);
        composed.Refusal.Should().Contain(slug).And.Contain("does not exist");
        ownerContentId.Should().NotBeEmpty();
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

    /// <summary>
    /// Two attempts of the same action send the identical idempotency key: not a derivative of it,
    /// not one with a per-attempt suffix.
    /// </summary>
    /// <remarks>
    /// This is what WorkflowRunQueue.cs actually does: it computes one key per action when a run is
    /// queued (<c>IdempotencyKey = $"{run.Id:N}-{i}"</c>) and WorkflowRunner.cs hands that same value
    /// to every attempt. Simulated here by composing twice with the same key rather than one, since a
    /// key that is merely present once would pass even if a future change started deriving a
    /// per-attempt value from it.
    ///
    /// Depends on <c>RequestComposer.cs</c>'s <c>headers[idempotencyHeader] = idempotencyKey;</c>
    /// line: remove it and the header is missing from both composed requests, not just one.
    /// </remarks>
    [Fact]
    public async Task Two_attempts_of_the_same_action_send_the_same_idempotency_key()
    {
        var store = _factory.Services.GetRequiredService<IDocumentStore>();
        var config = _factory.Services.GetRequiredService<IConfiguration>();
        var tenant = "req-idem-" + Guid.NewGuid().ToString("n")[..8];
        var type = NewSlug("idem");

        await using (var seed = store.LightweightSession(tenant))
        {
            seed.Store(new ContentTypeDefinition
            {
                Id = Guid.NewGuid(),
                Name = type,
                DisplayName = "x",
                Fields = [new FieldDefinition { Name = "Title", Type = "string" }],
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            await seed.SaveChangesAsync();
        }

        var content = new barakoCMS.Models.Content
        {
            Id = Guid.NewGuid(),
            ContentType = type,
            Status = ContentStatus.Published,
            Data = new() { ["Title"] = "irrelevant to this test" },
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        var definition = new RequestDefinition
        {
            Id = Guid.NewGuid(),
            Slug = NewSlug("req"),
            ConnectorSlug = "unused",
            Method = "POST",
            PathTemplate = "/feed",
            BodyTemplate = "{}",
        };
        var connector = new Connector
        {
            Id = Guid.NewGuid(), Slug = "unused", BaseUrl = "https://example.com", Auth = ConnectorAuth.None,
        };

        // One key, the way the runner computes one per action when the run is queued, then hands the
        // same value to every attempt of it.
        var idempotencyKey = $"{Guid.NewGuid():N}-0";

        await using var session = store.QuerySession(tenant);
        var composer = new RequestComposer(session, config, new QueryRunner(session));

        var firstAttempt = await composer.ComposeAsync(
            definition, connector, content, idempotencyKey, TestContext.Current.CancellationToken);
        var secondAttempt = await composer.ComposeAsync(
            definition, connector, content, idempotencyKey, TestContext.Current.CancellationToken);

        firstAttempt.Ok.Should().BeTrue("got refusal: {0}", firstAttempt.Refusal);
        secondAttempt.Ok.Should().BeTrue("got refusal: {0}", secondAttempt.Refusal);

        firstAttempt.Headers.Should().ContainKey("Idempotency-Key",
            "the default is on, so an operator gets the protection without asking for it");
        firstAttempt.Headers["Idempotency-Key"].Should().Be(idempotencyKey);
        secondAttempt.Headers["Idempotency-Key"].Should().Be(firstAttempt.Headers["Idempotency-Key"],
            "a retry of the same action must carry the same key the first attempt did, unchanged, "
            + "or a receiver has no way to recognise a duplicate");
    }

    /// <summary>
    /// A connector with the idempotency header switched off sends no such header, even when the
    /// runner supplied a key.
    /// </summary>
    /// <remarks>
    /// A provider that rejects an unknown header needs this to be a deliberate opt-out rather than an
    /// accident of clearing a text field, which is why the sentinel is the literal value <c>off</c>
    /// rather than an empty string.
    ///
    /// Depends on <c>RequestComposer.cs</c>'s <c>IdempotencyHeaderName</c>, specifically
    /// <c>string.Equals(configured.Trim(), "off", StringComparison.OrdinalIgnoreCase) ? null : ...</c>:
    /// remove that branch and this connector's setting stops meaning anything, and the header comes
    /// back.
    /// </remarks>
    [Fact]
    public async Task A_connector_with_the_idempotency_header_disabled_sends_none()
    {
        var store = _factory.Services.GetRequiredService<IDocumentStore>();
        var config = _factory.Services.GetRequiredService<IConfiguration>();
        var tenant = "req-idem-off-" + Guid.NewGuid().ToString("n")[..8];
        var type = NewSlug("idemoff");

        await using (var seed = store.LightweightSession(tenant))
        {
            seed.Store(new ContentTypeDefinition
            {
                Id = Guid.NewGuid(),
                Name = type,
                DisplayName = "x",
                Fields = [new FieldDefinition { Name = "Title", Type = "string" }],
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            await seed.SaveChangesAsync();
        }

        var content = new barakoCMS.Models.Content
        {
            Id = Guid.NewGuid(),
            ContentType = type,
            Status = ContentStatus.Published,
            Data = new() { ["Title"] = "irrelevant to this test" },
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        var definition = new RequestDefinition
        {
            Id = Guid.NewGuid(),
            Slug = NewSlug("req"),
            ConnectorSlug = "unused",
            Method = "POST",
            PathTemplate = "/feed",
            BodyTemplate = "{}",
        };
        var connector = new Connector
        {
            Id = Guid.NewGuid(),
            Slug = "unused",
            BaseUrl = "https://example.com",
            Auth = ConnectorAuth.None,
            Settings = new() { [ConnectorSettingKeys.IdempotencyHeader] = "off" },
        };

        var idempotencyKey = $"{Guid.NewGuid():N}-0";

        await using var session = store.QuerySession(tenant);
        var composer = new RequestComposer(session, config, new QueryRunner(session));

        var composed = await composer.ComposeAsync(
            definition, connector, content, idempotencyKey, TestContext.Current.CancellationToken);

        composed.Ok.Should().BeTrue("got refusal: {0}", composed.Refusal);
        composed.Headers.Should().NotContainKey("Idempotency-Key");
        composed.Headers.Values.Should().NotContain(idempotencyKey,
            "the key must not leak out under some other header name either");
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
        HttpClient client, string? body, string path = "/feed", string? querySlug = null,
        Dictionary<string, string>? headerTemplates = null)
    {
        var slug = NewSlug("req");

        var res = await client.PostAsJsonAsync("/api/requests", new
        {
            name = "Post it",
            slug,
            connectorSlug = _connectorSlug,
            method = "POST",
            pathTemplate = path,
            headerTemplates = headerTemplates ?? new Dictionary<string, string>(),
            bodyTemplate = body,
            querySlug,
        }, TestContext.Current.CancellationToken);

        res.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}",
            res.StatusCode, await res.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return slug;
    }

    /// <summary>Saves a query with no filters, selecting only the named fields, and returns its slug.</summary>
    private async Task<string> SaveQueryAsync(HttpClient client, string type, string[] fields)
    {
        var slug = NewSlug("q");

        var res = await client.PostAsJsonAsync("/api/queries", new
        {
            name = "For a request",
            slug,
            contentType = type,
            filters = Array.Empty<object>(),
            fields,
            limit = 100,
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

    /// <summary>
    /// A content type with a Public Title and a Public Salary, one "trigger" item (returned as the
    /// dry run's content id) plus <paramref name="rowCount"/> more of the same type, so a query with
    /// no filters matches all of them.
    /// </summary>
    /// <remarks>
    /// Salary is Public here, unlike <see cref="SeedContentAsync"/>'s Secret: the leak this schema
    /// exists to catch is the query's own Fields allowlist failing to hold, not the separate
    /// Sensitivity refusal, which has its own test above.
    /// </remarks>
    private async Task<(string Type, Guid TriggerId, List<string> Titles)> SeedQueryableContentAsync(
        int rowCount, string triggerTitle)
    {
        var type = NewSlug("rqt");

        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();

        session.Store(new ContentTypeDefinition
        {
            Id = Guid.NewGuid(),
            Name = type,
            DisplayName = "Query subject",
            Fields =
            [
                new FieldDefinition { Name = "Title", Type = "string" },
                new FieldDefinition { Name = "Salary", Type = "decimal" },
            ],
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });

        var titles = new List<string> { triggerTitle };
        var triggerId = Guid.NewGuid();
        session.Store(new barakoCMS.Models.Content
        {
            Id = triggerId,
            ContentType = type,
            Status = ContentStatus.Published,
            Data = new Dictionary<string, object> { ["Title"] = triggerTitle, ["Salary"] = 50000 },
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });

        for (var i = 0; i < rowCount; i++)
        {
            var title = $"{triggerTitle}-{i}";
            titles.Add(title);
            session.Store(new barakoCMS.Models.Content
            {
                Id = Guid.NewGuid(),
                ContentType = type,
                Status = ContentStatus.Published,
                Data = new Dictionary<string, object> { ["Title"] = title, ["Salary"] = 50000 + i },
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
        }

        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        return (type, triggerId, titles);
    }

    /// <summary>A content type with a Public Title field and nothing stored of it, so a query
    /// against it is well formed and simply matches zero rows.</summary>
    private async Task SeedEmptyContentTypeAsync(string type)
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();

        session.Store(new ContentTypeDefinition
        {
            Id = Guid.NewGuid(),
            Name = type,
            DisplayName = "Empty",
            Fields = [new FieldDefinition { Name = "Title", Type = "string" }],
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });

        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
    }
}
