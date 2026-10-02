using barakoCMS.Events;
using barakoCMS.Features.Workflows;
using barakoCMS.Features.Workflows.Actions;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BarakoCMS.Tests.Features.Workflows;

/// <summary>
/// What the extractor reads for a run: the site's time zone and currency, the entry's author, and
/// the transition that fired, from the store.
/// </summary>
/// <remarks>
/// Each test works in a tenant of its own, so the <c>site</c> entry it reads is the one it stored
/// and not one another class left in the shared database. The entry was created at 00:30 UTC and
/// clocked out at 09:00 UTC, which in Manila is 8:30 in the morning and 5:00 in the afternoon.
///
/// The extractor is held as the interface, which is how every caller holds it.
/// </remarks>
[Collection("Sequential")]
public class TemplatePlaceholderContextTests
{
    private const string ClockOut = "transition:ClockOut";

    private static readonly DateTime ClockedInAt = new(2026, 9, 14, 0, 30, 0, DateTimeKind.Utc);
    private static readonly DateTime ClockedOutAt = new(2026, 9, 14, 9, 0, 0, DateTimeKind.Utc);

    private readonly IntegrationTestFixture _fixture;
    private readonly IDocumentStore _store;
    private readonly string _tenant = $"tpl-{Guid.NewGuid():N}"[..20];

    public TemplatePlaceholderContextTests(IntegrationTestFixture fixture)
    {
        _fixture = fixture;
        _store = fixture.Services.GetRequiredService<IDocumentStore>();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// Red without the change: the issue's sentence, mailed to the author, in the tenant's zone.
    /// </summary>
    [Fact]
    public async Task A_clock_out_message_names_the_duration_the_times_and_the_author()
    {
        await using var session = _store.LightweightSession(_tenant);
        await StoreSiteAsync(session, new() { ["TimeZone"] = "Asia/Manila", ["Currency"] = "php" });
        var maria = await StoreUserAsync(session, "maria");
        var ramon = await StoreUserAsync(session, "ramon");
        var (entry, sequence) = await ClockedOutEntryAsync(session, maria.Id, ramon.Id);

        ITemplateVariableExtractor extractor = new TemplateVariableExtractor(session);

        var resolved = await ActionParameters.ResolveAsync(extractor, "Email", new Dictionary<string, string>
        {
            ["To"] = "{{createdBy.email}}",
            ["Subject"] = "{{transition.name}} by {{transition.by.name}} for {{createdBy.name}}",
            ["Body"] = "You worked {{duration createdAt transition.at}} today, from {{createdAt | date \"h:mm tt\"}} "
                     + "to {{transition.at | date \"h:mm tt\"}}. Pay: {{data.Pay | money}}.",
        }, entry, ClockOut, sequence, Ct);

        resolved.Should().HaveCount(3);
        resolved["To"].Should().Be(maria.Email);
        resolved["Subject"].Should().Be($"ClockOut by {ramon.Username} for {maria.Username}");
        resolved["Body"].Should().Be(
            "You worked 8 hours 30 minutes today, from 8:30 AM to 5:00 PM. Pay: PHP 1,250.00.");
    }

    /// <summary>
    /// Red without the change. Two ClockOut events are on the stream: the run for the first names
    /// the first, and only a caller with no sequence gets the last.
    /// </summary>
    [Fact]
    public async Task The_transition_is_the_event_that_fired_the_run_and_not_the_latest_one()
    {
        await using var session = _store.LightweightSession(_tenant);
        var ramon = await StoreUserAsync(session, "ramon");
        var later = await StoreUserAsync(session, "later");
        var (entry, sequence) = await ClockedOutEntryAsync(session, Guid.Empty, ramon.Id);

        var again = ClockedOutAt.AddHours(2);
        session.Events.Append(entry.Id, new ContentTransitioned(entry.Id, "ClockOut", "In", "Out", later.Id, again));
        await session.SaveChangesAsync(Ct);

        const string template = "{{transition.name}} {{transition.at}} {{transition.by.name}} {{transition.by.email}}";
        ITemplateVariableExtractor extractor = new TemplateVariableExtractor(session);

        await extractor.PrepareAsync(entry, ClockOut, sequence, [template], Ct);
        extractor.ResolveVariables(template, entry)
            .Should().Be($"ClockOut {ClockedOutAt:o} {ramon.Username} {ramon.Email}");

        await extractor.PrepareAsync(entry, ClockOut, eventSequence: 0, [template], Ct);
        extractor.ResolveVariables(template, entry)
            .Should().Be($"ClockOut {again:o} {later.Username} {later.Email}");
    }

    /// <summary>
    /// Red without the change: these holes used to be left as written. No user created the entry,
    /// and the account that clocked it out is gone.
    /// </summary>
    [Fact]
    public async Task A_change_with_no_user_behind_it_resolves_the_names_to_nothing()
    {
        await using var session = _store.LightweightSession(_tenant);
        var (entry, sequence) = await ClockedOutEntryAsync(session, Guid.Empty, Guid.NewGuid());

        const string template = "[{{createdBy.name}}][{{createdBy.email}}][{{transition.by.name}}][{{transition.by.email}}][{{transition.name}}]";
        ITemplateVariableExtractor extractor = new TemplateVariableExtractor(session);

        await extractor.PrepareAsync(entry, ClockOut, sequence, [template], Ct);

        extractor.ResolveVariables(template, entry).Should().Be("[][][][][ClockOut]");
    }

    /// <summary>
    /// The author half is red without the change. The transition half is a guard that passes both
    /// ways: on a trigger that is not a transition those holes are left as written, as they always were.
    /// </summary>
    [Fact]
    public async Task The_transition_placeholders_are_left_as_written_on_another_trigger()
    {
        await using var session = _store.LightweightSession(_tenant);
        var maria = await StoreUserAsync(session, "maria");
        var (entry, sequence) = await ClockedOutEntryAsync(session, maria.Id, maria.Id);

        const string template = "{{createdBy.name}} {{transition.name}} {{transition.by.email}}";
        ITemplateVariableExtractor extractor = new TemplateVariableExtractor(session);

        await extractor.PrepareAsync(entry, WorkflowEvents.Updated, sequence, [template], Ct);

        extractor.ResolveVariables(template, entry)
            .Should().Be($"{maria.Username} {{{{transition.name}}}} {{{{transition.by.email}}}}");
    }

    /// <summary>Red without the change, both lines.</summary>
    [Fact]
    public async Task The_time_zone_is_the_published_sites_and_utc_without_one()
    {
        var parameters = new Dictionary<string, string> { ["Title"] = "{{createdAt | date \"h:mm tt\"}}" };

        await using var session = _store.LightweightSession(_tenant);
        var (entry, sequence) = await ClockedOutEntryAsync(session, Guid.Empty, Guid.Empty);
        ITemplateVariableExtractor extractor = new TemplateVariableExtractor(session);

        (await ActionParameters.ResolveAsync(extractor, "CreateTask", parameters, entry, ClockOut, sequence, Ct))["Title"]
            .Should().Be("12:30 AM", "a tenant with no site entry formats in UTC");

        await StoreSiteAsync(session, new() { ["timezone"] = "Asia/Manila" });
        (await ActionParameters.ResolveAsync(extractor, "CreateTask", parameters, entry, ClockOut, sequence, Ct))["Title"]
            .Should().Be("8:30 AM", "the setting is found whatever case its key was stored in");
    }

    /// <summary>
    /// The second assertion is red without the change. The first is a guard that passes both ways:
    /// a site zone the server does not know leaves the date as written, and never shows it in UTC.
    /// </summary>
    [Fact]
    public async Task A_site_time_zone_the_server_does_not_know_leaves_the_date_as_written()
    {
        const string template = "{{createdAt | date \"h:mm tt\"}}";

        await using var session = _store.LightweightSession(_tenant);
        await StoreSiteAsync(session, new() { ["TimeZone"] = "Asia/Manilla" });
        var (entry, sequence) = await ClockedOutEntryAsync(session, Guid.Empty, Guid.Empty);
        ITemplateVariableExtractor extractor = new TemplateVariableExtractor(session);

        var resolved = await ActionParameters.ResolveAsync(extractor, "CreateTask", new Dictionary<string, string>
        {
            ["Title"] = template,
            ["Data.Named"] = "{{createdAt | date \"h:mm tt\" \"Asia/Manila\"}}",
        }, entry, ClockOut, sequence, Ct);

        resolved.Should().HaveCount(2);
        resolved["Title"].Should().Be(template);
        resolved["Data.Named"].Should().Be("8:30 AM");
    }

    /// <summary>
    /// Red without the change: a draft site entry is not what the site shows, so its zone is not read.
    /// </summary>
    [Fact]
    public async Task A_draft_site_entry_sets_no_time_zone()
    {
        await using var session = _store.LightweightSession(_tenant);
        await StoreSiteAsync(session, new() { ["TimeZone"] = "Asia/Manila" }, ContentStatus.Draft);
        var (entry, sequence) = await ClockedOutEntryAsync(session, Guid.Empty, Guid.Empty);
        ITemplateVariableExtractor extractor = new TemplateVariableExtractor(session);

        var resolved = await ActionParameters.ResolveAsync(
            extractor, "CreateTask", new Dictionary<string, string> { ["Title"] = "{{createdAt | date \"h:mm tt\"}}" },
            entry, ClockOut, sequence, Ct);

        resolved["Title"].Should().Be("12:30 AM");
    }

    /// <summary>
    /// Red without the change, since the author's name was not resolved at all. A username holding
    /// markup, a quote, a line break and a placeholder lands in each sink exactly as a field holding
    /// the same text does.
    /// </summary>
    [Fact]
    public async Task A_hostile_username_is_encoded_in_each_sink_exactly_as_a_field_is()
    {
        var hostile = $"<script>alert(\"x\")</script>\r\nBcc: x@example.com {{{{data.Secret}}}} {Guid.NewGuid():N}";

        await using var session = _store.LightweightSession(_tenant);
        var author = new User { Id = Guid.NewGuid(), Username = hostile, Email = $"{Guid.NewGuid():N}@example.com" };
        session.Store(author);
        var (entry, sequence) = await ClockedOutEntryAsync(session, author.Id, author.Id);
        entry.Data["Hostile"] = hostile;
        entry.Data["Secret"] = "TOP-SECRET";

        ITemplateVariableExtractor extractor = new TemplateVariableExtractor(session);

        async Task<Dictionary<string, string>> ResolveAsync(string actionType, string hole) =>
            await ActionParameters.ResolveAsync(extractor, actionType, new Dictionary<string, string>
            {
                ["To"] = hole,
                ["Subject"] = $"Re: {hole}",
                ["Body"] = $"<p>{hole}</p>",
                ["Url"] = $"https://example.com/hook?who={hole}",
                ["Value"] = hole,
            }, entry, ClockOut, sequence, Ct);

        foreach (var actionType in new[] { "Email", "Webhook", "UpdateField" })
        {
            var asAField = await ResolveAsync(actionType, "{{data.Hostile}}");
            asAField.Should().HaveCount(5);

            foreach (var hole in new[] { "{{createdBy.name}}", "{{transition.by.name}}" })
            {
                var resolved = await ResolveAsync(actionType, hole);

                resolved.Should().HaveCount(5);
                resolved.Should().Equal(asAField, "'{0}' in a '{1}' action is encoded as a field is", hole, actionType);
                resolved.Values.Should().NotContain(value => value.Contains("TOP-SECRET"));
            }
        }

        var email = await ResolveAsync("Email", "{{createdBy.name}}");
        email["Body"].Should().StartWith("<p>&lt;script&gt;alert(&quot;x&quot;)&lt;/script&gt;\r\nBcc: x@example.com {{data.Secret}} ");
        email["Subject"].Should().StartWith("Re: <script>alert(\"x\")</script> Bcc: x@example.com {{data.Secret}} ");
        email["To"].Should().NotContain("\r").And.NotContain("\n");
    }

    /// <summary>
    /// Red without the change. A Conditional is handed the entry and nothing else, and its child
    /// still reaches the author through the extractor of the same scope.
    /// </summary>
    [Fact]
    public async Task A_conditionals_child_reads_the_author_the_run_prepared()
    {
        await using var session = _store.LightweightSession(_tenant);
        var maria = await StoreUserAsync(session, "maria");
        var ramon = await StoreUserAsync(session, "ramon");
        var (entry, sequence) = await ClockedOutEntryAsync(session, maria.Id, ramon.Id);

        ITemplateVariableExtractor extractor = new TemplateVariableExtractor(session);
        var recorder = new RecordingEmailService();
        var services = new ServiceCollection();
        services.AddSingleton<IWorkflowAction>(new EmailAction(recorder, NullLogger<EmailAction>.Instance));
        services.AddSingleton(extractor);
        var conditional = new ConditionalAction(services.BuildServiceProvider(), NullLogger<ConditionalAction>.Instance);

        var children = System.Text.Json.JsonSerializer.Serialize(new[]
        {
            new { Type = "Email", Parameters = new Dictionary<string, string>
            {
                ["To"] = "{{createdBy.email}}",
                ["Subject"] = "Clocked out by {{transition.by.name}}",
                ["Body"] = "{{hours createdAt transition.at}} hours",
            } },
        });

        var parameters = await ActionParameters.ResolveAsync(extractor, "Conditional", new Dictionary<string, string>
        {
            ["Condition"] = "{{contentType}} == " + entry.ContentType,
            ["ThenActions"] = children,
        }, entry, ClockOut, sequence, Ct);

        (await conditional.RunAsync(parameters, entry, Ct)).Succeeded.Should().BeTrue();

        recorder.Messages.Should().HaveCount(1);
        var sent = recorder.Messages.Single();
        sent.To.Should().Be(maria.Email);
        sent.Subject.Should().Be($"Clocked out by {ramon.Username}");
        sent.Body.Should().Be("8.5 hours");
    }

    /// <summary>
    /// Red without the change. The engine called directly knows no sequence, and names the last
    /// event of the transition that triggered it.
    /// </summary>
    [Fact]
    public async Task The_engine_resolves_the_author_and_the_transition_for_its_actions()
    {
        await using var session = _store.LightweightSession(_tenant);
        var maria = await StoreUserAsync(session, "maria");
        var ramon = await StoreUserAsync(session, "ramon");
        var (entry, _) = await ClockedOutEntryAsync(session, maria.Id, ramon.Id);

        session.Store(new WorkflowDefinition
        {
            Id = Guid.NewGuid(),
            Name = "clock out",
            TriggerContentType = entry.ContentType,
            TriggerEvent = ClockOut,
            Actions =
            [
                new WorkflowAction
                {
                    Type = "Email",
                    Parameters = new()
                    {
                        ["To"] = "{{createdBy.email}}",
                        ["Subject"] = "{{transition.name}} by {{transition.by.name}}",
                        ["Body"] = "{{duration createdAt transition.at}}",
                    },
                },
            ],
        });
        await session.SaveChangesAsync(Ct);

        using var scope = _fixture.Services.CreateScope();
        var recorder = new RecordingEmailService();
        var engine = new WorkflowEngine(
            session,
            [new EmailAction(recorder, NullLogger<EmailAction>.Instance)],
            new TemplateVariableExtractor(session),
            scope.ServiceProvider.GetRequiredService<IWorkflowDebugger>(),
            scope.ServiceProvider.GetRequiredService<barakoCMS.Infrastructure.Security.ISecretProtector>(),
            NullLogger<WorkflowEngine>.Instance);

        await engine.ProcessEventAsync(entry.ContentType, ClockOut, entry, Ct);

        recorder.Messages.Should().HaveCount(1);
        var sent = recorder.Messages.Single();
        sent.To.Should().Be(maria.Email);
        sent.Subject.Should().Be($"ClockOut by {ramon.Username}");
        sent.Body.Should().Be("8 hours 30 minutes");
    }

    /// <summary>
    /// The first assertion is red without the change. The second is a guard that passes both ways:
    /// what was read for one entry is not used for another.
    /// </summary>
    [Fact]
    public async Task Another_entry_does_not_read_the_author_prepared_for_this_one()
    {
        await using var session = _store.LightweightSession(_tenant);
        var maria = await StoreUserAsync(session, "maria");
        var (entry, sequence) = await ClockedOutEntryAsync(session, maria.Id, maria.Id);
        var other = new Content { Id = Guid.NewGuid(), ContentType = entry.ContentType };

        ITemplateVariableExtractor extractor = new TemplateVariableExtractor(session);
        await extractor.PrepareAsync(entry, ClockOut, sequence, ["{{createdBy.email}}"], Ct);

        extractor.ResolveVariables("{{createdBy.email}}", entry).Should().Be(maria.Email);
        extractor.ResolveVariables("{{createdBy.email}}", other).Should().Be("{{createdBy.email}}");
    }

    /// <summary>
    /// Red without the change. A simulation is handed an entry the caller wrote, so the author it
    /// names is not looked up: the address shown is the sample one, not the real user's.
    /// </summary>
    [Fact]
    public async Task A_simulation_shows_a_sample_author_and_never_a_stored_users_address()
    {
        await using var session = _store.LightweightSession(_tenant);
        var maria = await StoreUserAsync(session, "maria");
        var (entry, _) = await ClockedOutEntryAsync(session, maria.Id, maria.Id);

        const string template = "{{createdBy.email}} {{transition.by.email}} {{transition.name}} {{transition.at}}";
        ITemplateVariableExtractor extractor = new TemplateVariableExtractor(session);

        await extractor.PrepareSampleAsync(entry, ClockOut, [template], Ct);
        var shown = extractor.ResolveVariables(template, entry);

        shown.Should().Be($"sample.user@example.com sample.user@example.com ClockOut {entry.UpdatedAt:o}");
        shown.Should().NotContain(maria.Email);
    }

    private static async Task<User> StoreUserAsync(IDocumentSession session, string name)
    {
        var id = Guid.NewGuid();
        var user = new User { Id = id, Username = $"{name}-{id:N}", Email = $"{name}-{id:N}@example.com" };
        session.Store(user);
        await session.SaveChangesAsync(Ct);
        return user;
    }

    private static async Task<Content> StoreSiteAsync(
        IDocumentSession session, Dictionary<string, object> data, ContentStatus status = ContentStatus.Published)
    {
        var site = new Content { Id = Guid.NewGuid(), ContentType = "site", Status = status, Data = data };
        session.Store(site);
        await session.SaveChangesAsync(Ct);
        return site;
    }

    /// <summary>An entry with its stream: created, then clocked out. Returns the sequence of the clock out.</summary>
    private static async Task<(Content Entry, long Sequence)> ClockedOutEntryAsync(
        IDocumentSession session, Guid author, Guid actor)
    {
        var id = Guid.NewGuid();
        var type = $"time{Guid.NewGuid():N}"[..16];
        var data = new Dictionary<string, object> { ["Pay"] = 1250 };

        session.Events.StartStream<Content>(
            id,
            new ContentCreated(id, type, data, ContentStatus.Draft, author, null, SensitivityLevel.Public, ClockedInAt),
            new ContentTransitioned(id, "ClockOut", "In", "Out", actor, ClockedOutAt));

        var entry = new Content
        {
            Id = id,
            ContentType = type,
            Data = data,
            CreatedAt = ClockedInAt,
            UpdatedAt = ClockedOutAt,
            CreatedBy = author,
            LastModifiedBy = actor,
            LifecycleState = "Out",
        };
        session.Store(entry);
        await session.SaveChangesAsync(Ct);

        var stream = await session.Events.FetchStreamAsync(id, token: Ct);
        stream.Should().HaveCount(2);

        return (entry, stream.Single(e => e.Data is ContentTransitioned).Sequence);
    }
}
