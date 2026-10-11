using barakoCMS.Core.Interfaces;
using barakoCMS.Features.Workflows;
using barakoCMS.Features.Workflows.Actions;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BarakoCMS.Tests.Features.Workflows;

/// <summary>
/// Placeholders that follow a reference, loops over a reference field, and links built from
/// configured bases.
/// </summary>
/// <remarks>
/// The reference tests work in the default tenant with content types of their own, and resolve the
/// extractor from a scope, so the permission and sensitivity services are the ones the API reads
/// with. The user who fired the workflow is the entry's last editor, since no event sequence is
/// given. Every test here is red before the change, where a <c>data.Field.Other</c>, a loop and a
/// link were all left as written.
/// </remarks>
[Collection("Sequential")]
public class TemplateReferenceAndLinkTests
{
    private readonly IntegrationTestFixture _fixture;
    private readonly IDocumentStore _store;

    public TemplateReferenceAndLinkTests(IntegrationTestFixture fixture)
    {
        _fixture = fixture;
        _store = fixture.Services.GetRequiredService<IDocumentStore>();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_followed_reference_renders_a_field_of_an_entry_the_user_may_read()
    {
        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var (order, supplier) = await TypesAsync(session);
        var user = await UserAsync(session, [Reads(order), Reads(supplier)]);

        var acme = await EntryAsync(session, supplier, new() { ["Name"] = "Acme", ["Email"] = "orders@acme.example" });
        var entry = await EntryAsync(session, order, new() { ["Number"] = "PO-7", ["Supplier"] = acme.Id.ToString() }, user.Id);

        var extractor = Extractor(scope);
        const string template = "Order {{data.Number}} to {{data.Supplier.Email}}";
        await extractor.PrepareAsync(entry, WorkflowEvents.Updated, 0, [template], Ct);

        extractor.ResolveVariables(template, entry).Should().Be("Order PO-7 to orders@acme.example");
    }

    [Fact]
    public async Task A_reference_the_user_may_not_read_renders_empty_the_same_as_a_missing_one()
    {
        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var (order, supplier) = await TypesAsync(session);
        var reader = await UserAsync(session, [Reads(order), Reads(supplier)]);
        var orderOnly = await UserAsync(session, [Reads(order)]);

        var acme = await EntryAsync(session, supplier, new() { ["Name"] = "Acme", ["Email"] = "orders@acme.example" });
        var readable = await EntryAsync(session, order, new() { ["Supplier"] = acme.Id.ToString() }, reader.Id);
        var unreadable = await EntryAsync(session, order, new() { ["Supplier"] = acme.Id.ToString() }, orderOnly.Id);
        var missing = await EntryAsync(session, order, new() { ["Supplier"] = Guid.NewGuid().ToString() }, reader.Id);
        var nobody = await EntryAsync(session, order, new() { ["Supplier"] = acme.Id.ToString() }, Guid.Empty);

        const string template = "[{{data.Supplier.Email}}][{{#each data.Supplier}}{{data.Name}}{{/each}}]";

        (await ResolvedAsync(scope, readable, template)).Should().Be("[orders@acme.example][Acme]");
        (await ResolvedAsync(scope, unreadable, template)).Should().Be("[][]");
        (await ResolvedAsync(scope, missing, template)).Should().Be("[][]");
        (await ResolvedAsync(scope, nobody, template)).Should().Be("[][]");
    }

    [Fact]
    public async Task A_sensitive_field_of_a_readable_entry_is_not_rendered_for_a_user_who_may_not_see_it()
    {
        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var (order, supplier) = await TypesAsync(session);
        var plain = await UserAsync(session, [Reads(order), Reads(supplier)]);
        var cleared = await UserAsync(session, [Reads(order), Reads(supplier)], SystemCapabilities.ViewSensitive);

        var acme = await EntryAsync(session, supplier, new() { ["Name"] = "Acme", ["Phone"] = "0917 555 0101" });
        var byPlain = await EntryAsync(session, order, new() { ["Supplier"] = acme.Id.ToString() }, plain.Id);
        var byCleared = await EntryAsync(session, order, new() { ["Supplier"] = acme.Id.ToString() }, cleared.Id);

        const string template = "{{data.Supplier.Name}}|{{data.Supplier.Phone}}|{{#each data.Supplier}}{{data.Phone}}{{/each}}";

        var shown = await ResolvedAsync(scope, byPlain, template);
        shown.Should().Be("Acme||");
        shown.Should().NotContain("0917");

        (await ResolvedAsync(scope, byCleared, template)).Should().Be("Acme|0917 555 0101|0917 555 0101");
    }

    /// <summary>
    /// Red before the review fix: the loop rendered the real content type of a document the API
    /// answers as hidden.
    /// </summary>
    [Fact]
    public async Task A_hidden_document_reached_through_a_reference_shows_as_HIDDEN_with_no_data()
    {
        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var (order, supplier) = await TypesAsync(session);
        var plain = await UserAsync(session, [Reads(order), Reads(supplier)]);
        var cleared = await UserAsync(session, [Reads(order), Reads(supplier)], SystemCapabilities.ViewHidden);

        var acme = await EntryAsync(session, supplier, new() { ["Name"] = "Acme" }, sensitivity: SensitivityLevel.Hidden);
        var byPlain = await EntryAsync(session, order, new() { ["Supplier"] = acme.Id.ToString() }, plain.Id);
        var byCleared = await EntryAsync(session, order, new() { ["Supplier"] = acme.Id.ToString() }, cleared.Id);

        const string template = "[{{#each data.Supplier}}{{contentType}}:{{data.Name}}{{/each}}][{{data.Supplier.Name}}]";

        var shown = await ResolvedAsync(scope, byPlain, template);
        shown.Should().Be("[HIDDEN:][]");
        shown.Should().NotContain(supplier);

        (await ResolvedAsync(scope, byCleared, template)).Should().Be($"[{supplier}:Acme][Acme]");
    }

    /// <summary>
    /// The loop half is red before the review fix: a field a Read rule's field set leaves out was
    /// missing on the item, and the placeholder was sent as written.
    /// </summary>
    [Fact]
    public async Task A_field_a_read_rule_does_not_show_renders_empty_in_a_follow_and_in_a_loop()
    {
        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var (order, supplier) = await TypesAsync(session);
        var narrowed = Reads(supplier);
        narrowed.Read.ReadableFields = ["Name"];
        var user = await UserAsync(session, [Reads(order), narrowed]);

        var acme = await EntryAsync(session, supplier, new() { ["Name"] = "Acme", ["Email"] = "orders@acme.example" });
        var entry = await EntryAsync(session, order, new() { ["Supplier"] = acme.Id.ToString() }, user.Id);

        const string template = "{{data.Supplier.Name}}|{{data.Supplier.Email}}|{{#each data.Supplier}}{{data.Name}}/{{data.Email}}{{/each}}";

        var shown = await ResolvedAsync(scope, entry, template);
        shown.Should().Be("Acme||Acme/");
        shown.Should().NotContain("acme.example");
    }

    /// <summary>
    /// The queued path. The run names the event a reader made; a later edit by a user who may not
    /// read the runners is the entry's last change. Red if the actor were read from the entry: the
    /// loop would render nothing. The note lands on the succeeded attempt.
    /// </summary>
    [Fact]
    public async Task A_queued_run_reads_as_the_user_on_its_event_and_records_the_loop_note_on_the_attempt()
    {
        var runner = NewTypeName();
        var batch = NewTypeName();
        var to = $"batch-{Guid.NewGuid():N}@example.com";
        User reader;
        User other;
        long sequence;
        var runId = Guid.NewGuid();

        await using (var session = _store.LightweightSession())
        {
            await TypeAsync(session, runner, new FieldDefinition { Name = "FirstName", DisplayName = "First name", Type = "string" });
            await TypeAsync(session, batch, new FieldDefinition
            {
                Name = "Runners", DisplayName = "Runners", Type = "reference", ReferenceType = runner, Multiple = true,
            });
            reader = await UserAsync(session, [Reads(batch), Reads(runner)]);
            other = await UserAsync(session, [Reads(batch)]);

            var ids = new List<string>();
            for (var i = 0; i < 51; i++)
            {
                ids.Add((await EntryAsync(session, runner, new() { ["FirstName"] = $"R{i:00}" })).Id.ToString());
            }

            var id = Guid.NewGuid();
            var data = new Dictionary<string, object> { ["Runners"] = ids };
            var at = DateTime.UtcNow;
            session.Events.StartStream<Content>(
                id,
                new barakoCMS.Events.ContentCreated(id, batch, data, ContentStatus.Published, other.Id, null, SensitivityLevel.Public, at),
                new barakoCMS.Events.ContentUpdated(id, data, reader.Id, null, at.AddMinutes(1)),
                new barakoCMS.Events.ContentUpdated(id, data, other.Id, null, at.AddMinutes(2)));
            session.Store(new Content
            {
                Id = id,
                ContentType = batch,
                Status = ContentStatus.Published,
                Data = data,
                CreatedBy = other.Id,
                LastModifiedBy = other.Id,
            });
            await session.SaveChangesAsync(Ct);

            var stream = await session.Events.FetchStreamAsync(id, token: Ct);
            stream.Should().HaveCount(3);
            sequence = stream.First(e => e.Data is barakoCMS.Events.ContentUpdated u && u.UpdatedBy == reader.Id).Sequence;

            var run = new WorkflowRun
            {
                Id = runId,
                WorkflowDefinitionId = Guid.NewGuid(),
                WorkflowName = "batch summary through the runner",
                CreatedAt = DateTimeOffset.UnixEpoch,
                ContentId = id,
                ContentType = batch,
                TriggerEvent = WorkflowEvents.Updated,
                TriggeringEventSequence = sequence,
                Actions =
                [
                    new WorkflowActionAttempt
                    {
                        Ordinal = 0,
                        ActionType = "Email",
                        IdempotencyKey = $"{Guid.NewGuid():N}",
                        Parameters = new()
                        {
                            ["To"] = to,
                            ["Subject"] = "Batch",
                            ["Body"] = "{{#each data.Runners}}{{data.FirstName}},{{/each}}",
                        },
                    },
                ],
            };
            run.Recompute();
            session.Store(run);
            await session.SaveChangesAsync(Ct);
        }

        var workflowRunner = new WorkflowRunner(
            _fixture.Services,
            _fixture.Services.GetRequiredService<Microsoft.Extensions.Logging.ILogger<WorkflowRunner>>(),
            _fixture.Services.GetRequiredService<IConfiguration>());

        List<RecordingEmailService.Sent> sent = [];
        for (var i = 0; i < 100 && sent.Count == 0; i++)
        {
            await workflowRunner.RunOnceAsync(Ct);
            sent = _fixture.Email.Messages.Where(m => m.To == to).ToList();
            if (sent.Count == 0) await Task.Delay(100, Ct);
        }

        sent.Should().HaveCount(1);
        var names = sent[0].Body.Split(',', StringSplitOptions.RemoveEmptyEntries);
        names.Should().HaveCount(TemplateExpression.MaxLoopItems);
        names.Should().Equal(Enumerable.Range(0, 50).Select(i => $"R{i:00}"));

        WorkflowRun? stored = null;
        for (var i = 0; i < 50 && stored?.Actions.FirstOrDefault()?.Status != AttemptStatus.Succeeded; i++)
        {
            await using var query = _store.QuerySession();
            stored = await query.LoadAsync<WorkflowRun>(runId, Ct);
            if (stored?.Actions.FirstOrDefault()?.Status != AttemptStatus.Succeeded) await Task.Delay(100, Ct);
        }

        stored.Should().NotBeNull();
        stored!.Actions.Should().HaveCount(1);
        stored.Actions[0].Status.Should().Be(AttemptStatus.Succeeded);
        stored.Actions[0].Error.Should().Be(
            "The loop over data.Runners rendered from the first 50 of its 51 references and stopped there.");
    }

    [Fact]
    public async Task A_loop_renders_each_entry_and_stops_at_fifty_with_a_note_in_the_run_log()
    {
        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var runner = NewTypeName();
        var batch = NewTypeName();
        await TypeAsync(session, runner, new FieldDefinition { Name = "FirstName", DisplayName = "First name", Type = "string" });
        await TypeAsync(session, batch, new FieldDefinition
        {
            Name = "Runners", DisplayName = "Runners", Type = "reference", ReferenceType = runner, Multiple = true,
        });
        var user = await UserAsync(session, [Reads(batch), Reads(runner)]);

        var ids = new List<string>();
        for (var i = 0; i < 60; i++)
        {
            ids.Add((await EntryAsync(session, runner, new() { ["FirstName"] = $"R{i:00}" })).Id.ToString());
        }

        var entry = await EntryAsync(session, batch, new() { ["Runners"] = ids }, user.Id);
        var empty = await EntryAsync(session, batch, new() { ["Runners"] = new List<string>() }, user.Id);

        var workflow = new WorkflowDefinition
        {
            Id = Guid.NewGuid(),
            Name = "batch summary",
            TriggerContentType = batch,
            TriggerEvent = WorkflowEvents.Updated,
            Actions =
            [
                new WorkflowAction
                {
                    Type = "Email",
                    Parameters = new()
                    {
                        ["To"] = "leader@example.com",
                        ["Subject"] = "Batch",
                        ["Body"] = "{{#each data.Runners}}{{data.FirstName}},{{/each}}",
                    },
                },
            ],
        };
        session.Store(workflow);
        await session.SaveChangesAsync(Ct);

        var recorder = new RecordingEmailService();
        var debugger = scope.ServiceProvider.GetRequiredService<IWorkflowDebugger>();
        var engine = new WorkflowEngine(
            session,
            [new EmailAction(recorder, NullLogger<EmailAction>.Instance)],
            Extractor(scope),
            debugger,
            scope.ServiceProvider.GetRequiredService<barakoCMS.Infrastructure.Security.ISecretProtector>(),
            NullLogger<WorkflowEngine>.Instance);

        await engine.ProcessEventAsync(batch, WorkflowEvents.Updated, entry, Ct);

        recorder.Messages.Should().HaveCount(1);
        var names = recorder.Messages.Single().Body.Split(',', StringSplitOptions.RemoveEmptyEntries);
        names.Should().HaveCount(TemplateExpression.MaxLoopItems);
        names.Should().Equal(Enumerable.Range(0, 50).Select(i => $"R{i:00}"));

        var runs = await debugger.GetExecutionHistoryAsync(workflow.Id, ct: Ct);
        runs.Should().HaveCount(1);
        runs[0].Actions.Should().HaveCount(1);
        runs[0].Actions[0].Success.Should().BeTrue();
        runs[0].Actions[0].ErrorMessage.Should().Be(
            "The loop over data.Runners rendered from the first 50 of its 60 references and stopped there.");

        (await ResolvedAsync(scope, empty, "[{{#each data.Runners}}{{data.FirstName}},{{/each}}]")).Should().Be("[]");
    }

    [Fact]
    public async Task Html_in_a_referenced_value_is_escaped_in_an_email_body()
    {
        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var (order, supplier) = await TypesAsync(session);
        var user = await UserAsync(session, [Reads(order), Reads(supplier)]);

        var acme = await EntryAsync(session, supplier, new() { ["Name"] = "<b>Acme</b> & co" });
        var entry = await EntryAsync(session, order, new() { ["Supplier"] = acme.Id.ToString() }, user.Id);

        var resolved = await ActionParameters.ResolveAsync(Extractor(scope), "Email", new Dictionary<string, string>
        {
            ["Subject"] = "{{data.Supplier.Name}}",
            ["Body"] = "<p>{{data.Supplier.Name}}</p><ul>{{#each data.Supplier}}<li>{{data.Name}}</li>{{/each}}</ul>",
        }, entry, WorkflowEvents.Updated, 0, Ct);

        resolved.Should().HaveCount(2);
        resolved["Subject"].Should().Be("<b>Acme</b> & co");
        resolved["Body"].Should().Be(
            "<p>&lt;b&gt;Acme&lt;/b&gt; &amp; co</p><ul><li>&lt;b&gt;Acme&lt;/b&gt; &amp; co</li></ul>");
    }

    [Fact]
    public async Task Links_render_with_the_configured_bases_and_are_empty_when_nothing_is_configured()
    {
        var tenant = $"lnk-{Guid.NewGuid():N}"[..20];
        await using var session = _store.LightweightSession(tenant);
        session.Store(new Content
        {
            Id = Guid.NewGuid(),
            ContentType = "site",
            Status = ContentStatus.Published,
            Data = new() { ["Url"] = "https://site.example.com/" },
        });
        await session.SaveChangesAsync(Ct);

        var entry = new Content { Id = Guid.NewGuid(), ContentType = "absence" };
        const string template =
            "{{links.console}} {{links.edit}} {{links.entry}} {{links.site}} {{links.site \"/approvals/?view=mine\"}} {{links.transition \"Send back\"}}";

        ITemplateVariableExtractor configured = new TemplateVariableExtractor(session, null, null, Configuration(new()
        {
            ["App:BaseUrl"] = "https://api.example.com/",
            ["App:ConsoleUrl"] = "https://console.example.com",
        }));
        await configured.PrepareAsync(entry, WorkflowEvents.Created, 0, [template], Ct);

        configured.ResolveVariables(template, entry).Should().Be(
            $"https://console.example.com/content/{entry.Id} https://console.example.com/content/{entry.Id} "
          + $"https://api.example.com/api/contents/{entry.Id} https://site.example.com "
          + "https://site.example.com/approvals/?view=mine "
          + $"https://console.example.com/content/{entry.Id}?transition=Send%20back");

        await using var bare = _store.LightweightSession($"lnk-{Guid.NewGuid():N}"[..20]);
        ITemplateVariableExtractor unconfigured = new TemplateVariableExtractor(bare, null, null, Configuration(new()
        {
            ["App:ConsoleUrl"] = "console.example.com",
        }));
        await unconfigured.PrepareAsync(entry, WorkflowEvents.Created, 0, [template], Ct);

        unconfigured.ResolveVariables(template, entry).Should().Be("     ");
    }

    [Fact]
    public void A_template_with_loops_and_links_warns_only_about_what_will_not_be_filled()
    {
        var problems = TemplateExpression.Problems(
            "{{#each data.Runners}}{{data.FirstName}}{{/each}} {{data.Supplier.Email}} {{links.console}} "
          + "{{links.site \"/ok/\"}} {{links.site \"//elsewhere.example\"}} {{links.transition}} "
          + "{{#each data.A}}{{#each data.B}}{{data.C}}{{/each}}{{/each}}",
            onTransition: false).ToList();

        problems.Should().HaveCount(3);
        problems[0].Should().StartWith("'{{links.site \"//elsewhere.example\"}}' gives 'links.site' a path");
        problems[1].Should().StartWith("'{{links.transition}}' names 'links.transition' without the transition");
        problems[2].Should().Be("The loop over 'data.A' holds another loop, and loops do not nest, so it is sent as written.");
    }

    private static ITemplateVariableExtractor Extractor(IServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<ITemplateVariableExtractor>();

    private static async Task<string> ResolvedAsync(IServiceScope scope, Content entry, string template)
    {
        var extractor = Extractor(scope);
        await extractor.PrepareAsync(entry, WorkflowEvents.Updated, 0, [template], Ct);
        return extractor.ResolveVariables(template, entry);
    }

    private static IConfiguration Configuration(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private static string NewTypeName() => "tpr" + Guid.NewGuid().ToString("n")[..8];

    /// <summary>An order type whose <c>Supplier</c> points at a supplier type whose <c>Phone</c> is Sensitive.</summary>
    private static async Task<(string Order, string Supplier)> TypesAsync(IDocumentSession session)
    {
        var supplier = NewTypeName();
        var order = NewTypeName();

        await TypeAsync(session, supplier,
            new FieldDefinition { Name = "Name", DisplayName = "Name", Type = "string" },
            new FieldDefinition { Name = "Email", DisplayName = "Email", Type = "email" },
            new FieldDefinition { Name = "Phone", DisplayName = "Phone", Type = "string", Sensitivity = SensitivityLevel.Sensitive });
        await TypeAsync(session, order,
            new FieldDefinition { Name = "Number", DisplayName = "Number", Type = "string" },
            new FieldDefinition { Name = "Supplier", DisplayName = "Supplier", Type = "reference", ReferenceType = supplier });

        return (order, supplier);
    }

    private static async Task TypeAsync(IDocumentSession session, string name, params FieldDefinition[] fields)
    {
        session.Store(new ContentTypeDefinition { Id = Guid.NewGuid(), Name = name, DisplayName = name, Fields = [.. fields] });
        await session.SaveChangesAsync(Ct);
    }

    private static ContentTypePermission Reads(string type) => new()
    {
        ContentTypeSlug = type,
        Read = new PermissionRule { Enabled = true },
    };

    private static async Task<User> UserAsync(
        IDocumentSession session, List<ContentTypePermission> permissions, string? capability = null)
    {
        var role = new Role
        {
            Id = Guid.NewGuid(),
            Name = $"Role_{Guid.NewGuid():n}",
            Permissions = permissions,
            SystemCapabilities = capability is null ? [] : [capability],
        };
        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = $"tpr_{Guid.NewGuid():n}",
            Email = $"tpr_{Guid.NewGuid():n}@example.com",
            RoleIds = [role.Id],
        };
        session.Store(role);
        session.Store(user);
        await session.SaveChangesAsync(Ct);
        return user;
    }

    private static async Task<Content> EntryAsync(
        IDocumentSession session, string type, Dictionary<string, object> data, Guid? editor = null,
        SensitivityLevel sensitivity = SensitivityLevel.Public)
    {
        var entry = new Content
        {
            Id = Guid.NewGuid(),
            ContentType = type,
            Status = ContentStatus.Published,
            Sensitivity = sensitivity,
            Data = data,
            LastModifiedBy = editor ?? Guid.Empty,
        };
        session.Store(entry);
        await session.SaveChangesAsync(Ct);
        return entry;
    }
}
