using System.Net.Http.Json;
using System.Text.Json;
using barakoCMS.Features.Workflows.Actions;
using barakoCMS.Infrastructure.Connectors;
using barakoCMS.Infrastructure.Sync;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BarakoCMS.Tests.Features.Connectors;

/// <summary>
/// Every hole form a template can render is a hole the egress check sees, on every path that
/// composes a request: the composer, the Request step, the dry run and a collection sync.
/// </summary>
/// <remarks>
/// The entry holds a value in each of four fields a request must never send: a Sensitive one, a
/// Hidden one, a Members one and one the owner list hides. Each form below names one of them, and
/// the refusal has to come before the sender or the fetcher is asked to do anything. The counting
/// sender and fetcher are what prove nothing was sent.
/// </remarks>
[Collection("Integration1")]
public class RequestEgressTests(IntegrationTestFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly string[] Withheld = ["Ssn", "Vault", "Perk", "Memo"];

    /// <summary>Forms that name the field, with <c>F</c> standing for it.</summary>
    public static TheoryData<string> NamedForms =>
    [
        "{{F}}",
        "{{data.F}}",
        "{{F | upper}}",
        "{{= data.F}}",
        "{{= upper(data.F) | lower}}",
        "{{= round(data.F * 2, 2)}}",
        "{{#each F}}x{{/each}}",
        "{{#each data.F}}{{data.Title}}{{/each}}",
    ];

    /// <summary>Forms a request does not render at all, so they are refused for being what they are.</summary>
    public static TheoryData<string> BeyondPlainForms =>
    [
        "{{row.F}}",
        "{{= sum(lines.F)}}",
        "{{#each lines}}{{row.Qty}}{{/each}}",
        "{{= data.Title}}",
    ];

    private sealed record Subject(string Type, Guid EntryId, string ConnectorSlug);

    private async Task<Subject> SeedAsync()
    {
        var type = "egr" + Guid.NewGuid().ToString("n")[..10];
        var connectorSlug = "egrc" + Guid.NewGuid().ToString("n")[..10];
        var entryId = Guid.NewGuid();

        using var scope = fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();

        session.Store(new ContentTypeDefinition
        {
            Id = Guid.NewGuid(),
            Name = type,
            DisplayName = type,
            Fields =
            [
                new FieldDefinition { Name = "Title", Type = "string" },
                new FieldDefinition { Name = "Ssn", Type = "string", Sensitivity = SensitivityLevel.Sensitive },
                new FieldDefinition { Name = "Vault", Type = "string", Sensitivity = SensitivityLevel.Hidden },
                new FieldDefinition { Name = "Perk", Type = "string", Sensitivity = SensitivityLevel.Members },
                new FieldDefinition { Name = "Memo", Type = "string", ShownWhenListedIn = "Viewers" },
            ],
        });
        session.Store(new Connector
        {
            Id = Guid.NewGuid(),
            Name = "Egress",
            Slug = connectorSlug,
            BaseUrl = "https://egress.example",
            Auth = ConnectorAuth.None,
        });
        session.Store(new Content
        {
            Id = entryId,
            ContentType = type,
            Status = ContentStatus.Published,
            Data = new Dictionary<string, object>
            {
                ["Title"] = "plain title",
                ["Ssn"] = "123-45-6789",
                ["Vault"] = "vault value",
                ["Perk"] = "member perk",
                ["Memo"] = "owner memo",
            },
        });

        await session.SaveChangesAsync(Ct);
        return new Subject(type, entryId, connectorSlug);
    }

    private static string Body(string form, string field) => "{\"v\":\"" + form.Replace("F", field) + "\"}";

    private static RequestDefinition Definition(Subject subject, string body, string path = "/in") => new()
    {
        Id = Guid.NewGuid(),
        Name = "Egress",
        Slug = "egrq" + Guid.NewGuid().ToString("n")[..10],
        ConnectorSlug = subject.ConnectorSlug,
        Method = "POST",
        PathTemplate = path,
        BodyTemplate = body,
    };

    private async Task<Content> EntryAsync(Subject subject)
    {
        using var scope = fixture.Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<IQuerySession>().LoadAsync<Content>(subject.EntryId, Ct))!;
    }

    private async Task<ComposedRequest> ComposeAsync(Subject subject, RequestDefinition definition)
    {
        using var scope = fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        var connector = await session.Query<Connector>().FirstAsync(c => c.Slug == subject.ConnectorSlug, Ct);
        var entry = await session.LoadAsync<Content>(subject.EntryId, Ct);

        return await scope.ServiceProvider.GetRequiredService<IRequestComposer>()
            .ComposeAsync(definition, connector, entry!, idempotencyKey: null, Ct);
    }

    [Theory]
    [MemberData(nameof(NamedForms))]
    public async Task The_composer_refuses_a_form_that_names_a_withheld_field(string form)
    {
        var subject = await SeedAsync();

        foreach (var field in Withheld)
        {
            var composed = await ComposeAsync(subject, Definition(subject, Body(form, field)));

            composed.Ok.Should().BeFalse("'{0}' names {1}, which a request must not send", form, field);
            composed.Refusal.Should().Contain(field);
            composed.Body.Should().BeNull();
        }
    }

    [Theory]
    [MemberData(nameof(BeyondPlainForms))]
    public async Task The_composer_refuses_a_form_a_request_does_not_render(string form)
    {
        var subject = await SeedAsync();

        foreach (var field in Withheld)
        {
            var composed = await ComposeAsync(subject, Definition(subject, Body(form, field)));

            composed.Ok.Should().BeFalse("'{0}' would be sent as written", form);
            composed.Refusal.Should().NotBeNullOrEmpty();
        }
    }

    [Fact]
    public async Task The_composer_refuses_a_withheld_field_in_a_path_and_in_a_header()
    {
        var subject = await SeedAsync();

        var inPath = await ComposeAsync(subject, Definition(subject, "{}", path: "/p/{{= data.Ssn}}"));
        inPath.Ok.Should().BeFalse();
        inPath.Refusal.Should().Contain("Ssn");

        var inHeader = Definition(subject, "{}");
        inHeader.HeaderTemplates["X-Check"] = "{{Vault | upper}}";
        var composed = await ComposeAsync(subject, inHeader);
        composed.Ok.Should().BeFalse();
        composed.Refusal.Should().Contain("Vault");
    }

    [Fact]
    public async Task A_plain_public_hole_still_composes()
    {
        var subject = await SeedAsync();

        var composed = await ComposeAsync(subject, Definition(subject, "{\"v\":\"{{Title}}\"}"));

        composed.Ok.Should().BeTrue("got: {0}", composed.Refusal);
        composed.Body.Should().Be("{\"v\":\"plain title\"}");
    }

    /// <summary>
    /// The case a scanner that reads only the first part of a template fails: the template is one
    /// character over the cap and the Sensitive hole is the last thing in it.
    /// </summary>
    [Fact]
    public async Task A_template_one_over_the_cap_with_a_sensitive_hole_at_the_end_is_refused()
    {
        var subject = await SeedAsync();
        const string hole = "{{Ssn}}";
        var template = new string('a', TemplateHoles.MaxScanLength + 1 - hole.Length) + hole;
        template.Length.Should().Be(TemplateHoles.MaxScanLength + 1);

        var composed = await ComposeAsync(subject, Definition(subject, template));
        composed.Ok.Should().BeFalse();
        composed.Refusal.Should().Contain(TemplateHoles.MaxScanLength.ToString());
        composed.Refusal.Should().NotContain("123-45-6789");

        var scan = () => TemplateHoles.Scan(template);
        scan.Should().Throw<TemplateScanException>().Which.TimedOut.Should().BeFalse();

        var egress = () => TemplateHoles.FirstNonPublic([template], new ContentTypeDefinition
        {
            Name = subject.Type,
            Fields = [new FieldDefinition { Name = "Ssn", Type = "string", Sensitivity = SensitivityLevel.Sensitive }],
        });
        egress.Should().Throw<TemplateScanException>("the check never reads the template in part and never counts it clean");

        var render = () => barakoCMS.Infrastructure.Services.TemplateExpression.Pieces(template);
        render.Should().Throw<TemplateScanException>("rendering refuses it too");
    }

    [Fact]
    public async Task A_template_exactly_at_the_cap_is_still_read_in_full()
    {
        var subject = await SeedAsync();
        const string hole = "{{Ssn}}";
        var template = new string('a', TemplateHoles.MaxScanLength - hole.Length) + hole;

        var composed = await ComposeAsync(subject, Definition(subject, template));

        composed.Ok.Should().BeFalse();
        composed.Refusal.Should().Contain("Ssn", "the hole at the end of a template at the cap is seen");
    }

    [Fact]
    public async Task A_scan_that_times_out_is_refused_and_nothing_is_sent()
    {
        var subject = await SeedAsync();
        var definition = Definition(subject, "{\"v\":\"{{Title}}\"}");

        TemplateHoles.TimeOutScans.Value = true;
        try
        {
            var composed = await ComposeAsync(subject, definition);

            composed.Ok.Should().BeFalse("a check that cannot finish does not count a template as clean");
            composed.Refusal.Should().Contain("too long to read");

            var sender = new CountingSender();
            using var scope = fixture.Services.CreateScope();
            await StoreAsync(scope, definition);
            var action = Action(scope, sender);
            var result = await action.RunAsync(
                new Dictionary<string, string> { ["Request"] = definition.Slug }, await EntryAsync(subject), Ct);

            result.Succeeded.Should().BeFalse();
            sender.Sent.Should().Be(0);
        }
        finally
        {
            TemplateHoles.TimeOutScans.Value = false;
        }
    }

    private static async Task StoreAsync(IServiceScope scope, RequestDefinition definition)
    {
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Store(definition);
        await session.SaveChangesAsync(Ct);
    }

    private static RequestAction Action(IServiceScope scope, IConnectorSender sender) => new(
        scope.ServiceProvider.GetRequiredService<IQuerySession>(),
        scope.ServiceProvider.GetRequiredService<IRequestComposer>(),
        sender,
        NullLogger<RequestAction>.Instance);

    [Theory]
    [MemberData(nameof(NamedForms))]
    public async Task The_request_step_sends_nothing_for_a_form_that_names_a_withheld_field(string form)
    {
        var subject = await SeedAsync();
        var entry = await EntryAsync(subject);
        var sender = new CountingSender();

        foreach (var field in Withheld)
        {
            var definition = Definition(subject, Body(form, field));
            using var scope = fixture.Services.CreateScope();
            await StoreAsync(scope, definition);

            var result = await Action(scope, sender).RunAsync(
                new Dictionary<string, string> { ["Request"] = definition.Slug }, entry, Ct);

            result.Succeeded.Should().BeFalse("'{0}' names {1}", form, field);
            result.Error.Should().Contain(field);
        }

        sender.Sent.Should().Be(0, "the refusal comes before any bytes are sent");
    }

    [Theory]
    [MemberData(nameof(BeyondPlainForms))]
    public async Task The_request_step_sends_nothing_for_a_form_a_request_does_not_render(string form)
    {
        var subject = await SeedAsync();
        var entry = await EntryAsync(subject);
        var sender = new CountingSender();
        var definition = Definition(subject, Body(form, "Ssn"));
        using var scope = fixture.Services.CreateScope();
        await StoreAsync(scope, definition);

        var result = await Action(scope, sender).RunAsync(
            new Dictionary<string, string> { ["Request"] = definition.Slug }, entry, Ct);

        result.Succeeded.Should().BeFalse();
        sender.Sent.Should().Be(0);
    }

    [Fact]
    public async Task The_request_step_still_sends_a_plain_public_hole()
    {
        var subject = await SeedAsync();
        var entry = await EntryAsync(subject);
        var sender = new CountingSender();
        var definition = Definition(subject, "{\"v\":\"{{Title}}\"}");
        using var scope = fixture.Services.CreateScope();
        await StoreAsync(scope, definition);

        var result = await Action(scope, sender).RunAsync(
            new Dictionary<string, string> { ["Request"] = definition.Slug }, entry, Ct);

        result.Succeeded.Should().BeTrue("got: {0}", result.Error);
        sender.Sent.Should().Be(1);
        sender.LastBody.Should().Be("{\"v\":\"plain title\"}");
    }

    [Theory]
    [MemberData(nameof(NamedForms))]
    public async Task The_dry_run_refuses_a_form_that_names_a_withheld_field(string form)
    {
        var subject = await SeedAsync();
        var client = await fixture.CreateClientAsync("SuperAdmin", "Admin");

        foreach (var field in Withheld)
        {
            var definition = Definition(subject, Body(form, field));
            using (var scope = fixture.Services.CreateScope()) await StoreAsync(scope, definition);

            var dry = await DryRunAsync(client, definition.Slug, subject.EntryId);

            dry.GetProperty("wouldSend").GetBoolean().Should().BeFalse("'{0}' names {1}", form, field);
            dry.GetProperty("refusal").GetString().Should().Contain(field);
            dry.TryGetProperty("body", out var body).Should().BeTrue();
            body.ValueKind.Should().Be(JsonValueKind.Null, "a refused request shows no composed body");
        }
    }

    [Theory]
    [MemberData(nameof(BeyondPlainForms))]
    public async Task The_dry_run_refuses_a_form_a_request_does_not_render(string form)
    {
        var subject = await SeedAsync();
        var client = await fixture.CreateClientAsync("SuperAdmin", "Admin");
        var definition = Definition(subject, Body(form, "Ssn"));
        using (var scope = fixture.Services.CreateScope()) await StoreAsync(scope, definition);

        var dry = await DryRunAsync(client, definition.Slug, subject.EntryId);

        dry.GetProperty("wouldSend").GetBoolean().Should().BeFalse();
        dry.GetProperty("refusal").GetString().Should().NotBeNullOrEmpty();
    }

    private static async Task<JsonElement> DryRunAsync(HttpClient client, string slug, Guid contentId)
    {
        var res = await client.PostAsync($"/api/requests/{slug}/dry-run/{contentId}", null, Ct);
        res.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}", res.StatusCode, await res.Content.ReadAsStringAsync(Ct));
        return JsonDocument.Parse(await res.Content.ReadAsStringAsync(Ct)).RootElement.Clone();
    }

    [Theory]
    [MemberData(nameof(NamedForms))]
    public async Task A_collection_sync_fetches_nothing_for_a_form_that_names_a_withheld_field(string form)
    {
        var subject = await SeedAsync();
        var fetcher = new CountingFetcher();

        foreach (var field in Withheld)
        {
            var outcome = await SyncAsync(subject, Body(form, field), fetcher);

            outcome.Succeeded.Should().BeFalse("'{0}' names {1}", form, field);
            outcome.Error.Should().Contain(field);
        }

        fetcher.Fetched.Should().Be(0, "the refusal comes before any bytes are sent");
    }

    [Theory]
    [MemberData(nameof(BeyondPlainForms))]
    public async Task A_collection_sync_fetches_nothing_for_a_form_a_request_does_not_render(string form)
    {
        var subject = await SeedAsync();
        var fetcher = new CountingFetcher();

        var outcome = await SyncAsync(subject, Body(form, "Ssn"), fetcher);

        outcome.Succeeded.Should().BeFalse();
        fetcher.Fetched.Should().Be(0);
    }

    private async Task<CollectionSyncOutcome> SyncAsync(Subject subject, string body, CountingFetcher fetcher)
    {
        var definition = Definition(subject, body);
        using var scope = fixture.Services.CreateScope();
        await StoreAsync(scope, definition);

        var runner = ActivatorUtilities.CreateInstance<CollectionSyncRunner>(scope.ServiceProvider, (IConnectorFetcher)fetcher);
        return await runner.RunAsync(new CollectionSync
        {
            Id = Guid.NewGuid(),
            Name = "Egress",
            Slug = "egrs" + Guid.NewGuid().ToString("n")[..10],
            ContentType = subject.Type,
            Source = SyncSource.Request,
            RequestSlug = definition.Slug,
            KeyField = "Title",
            FieldMap = new Dictionary<string, string> { ["Title"] = "id" },
        }, Ct);
    }

    private sealed class CountingSender : IConnectorSender
    {
        public int Sent { get; private set; }

        public string? LastBody { get; private set; }

        public Task<ConnectorCallResult> ProbeAsync(Connector connector, CancellationToken ct) =>
            Task.FromResult(new ConnectorCallResult(true, 200, 1, null));

        public Task<ConnectorCallResult> SendAsync(
            Connector connector, ComposedRequest request, SuccessRule rule, string? successJsonPath, CancellationToken ct)
        {
            Sent++;
            LastBody = request.Body;
            return Task.FromResult(new ConnectorCallResult(true, 200, 1, null));
        }
    }

    private sealed class CountingFetcher : IConnectorFetcher
    {
        public int Fetched { get; private set; }

        public Task<ConnectorFetchResult> FetchAsync(Connector? connector, ComposedRequest request, int maxBytes, CancellationToken ct)
        {
            Fetched++;
            return Task.FromResult(new ConnectorFetchResult(true, 200, 1, null, "[]"));
        }
    }
}
