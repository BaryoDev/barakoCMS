using barakoCMS.Features.EmailTemplates;
using barakoCMS.Features.Workflows;
using barakoCMS.Features.Workflows.Actions;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BarakoCMS.Tests.Features.EmailTemplates;

/// <summary>
/// An Email action that names a template sends the published template of its own tenant, resolved
/// against the entry the way an inline subject and body are, and fails permanently when it cannot.
/// </summary>
[Collection("Sequential")]
public class EmailTemplateActionTests
{
    private readonly IntegrationTestFixture _fixture;

    public EmailTemplateActionTests(IntegrationTestFixture fixture) => _fixture = fixture;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IDocumentStore Store => _fixture.Services.GetRequiredService<IDocumentStore>();

    private static EmailAction ActionFor(RecordingEmailService recorder, IDocumentSession session) =>
        new(recorder, NullLogger<EmailAction>.Instance, session: session, extractor: new TemplateVariableExtractor(session));

    private static Content Entry(Dictionary<string, object>? data = null) =>
        new() { Id = Guid.NewGuid(), ContentType = "signup", Status = ContentStatus.Draft, Data = data ?? new() { ["Name"] = "Ana" } };

    /// <summary>
    /// Red if the lookup read past the tenant: the same id and slug that send in the template's own
    /// tenant find nothing from another one.
    /// </summary>
    [Fact]
    public async Task A_template_of_another_tenant_cannot_be_named_by_id_or_slug()
    {
        var owner = $"tpl-owner-{Guid.NewGuid():N}"[..20];
        var other = $"tpl-other-{Guid.NewGuid():N}"[..20];
        var slug = $"welcome-{Guid.NewGuid():N}"[..20];
        var template = EmailTemplateData.Template("Hi {{data.Name}}", "Welcome, {{data.Name}}.", slug: slug);

        await using (var session = Store.LightweightSession(owner))
        {
            await EmailTemplateData.EnsureTypesAsync(_fixture, session, Ct);
            session.Store(template);
            await session.SaveChangesAsync(Ct);
        }

        await using (var session = Store.LightweightSession(other))
        {
            await EmailTemplateData.EnsureTypesAsync(_fixture, session, Ct);
        }

        foreach (var name in new[] { template.Id.ToString(), slug })
        {
            var recorder = new RecordingEmailService();
            await using var session = Store.LightweightSession(other);

            var result = await ActionFor(recorder, session).RunAsync(
                new() { ["To"] = "a@example.com", ["Template"] = name }, Entry(), Ct);

            result.Succeeded.Should().BeFalse(name);
            result.Retryable.Should().BeFalse("the template is not going to appear in this tenant on a retry");
            result.Error.Should().Contain("No email template");
            recorder.Messages.Should().BeEmpty();
        }

        foreach (var name in new[] { template.Id.ToString(), slug })
        {
            var recorder = new RecordingEmailService();
            await using var session = Store.LightweightSession(owner);

            var result = await ActionFor(recorder, session).RunAsync(
                new() { ["To"] = "a@example.com", ["Template"] = name }, Entry(), Ct);

            result.Succeeded.Should().BeTrue(result.Error);
            recorder.Messages.Should().ContainSingle().Which.Subject.Should().Be("Hi Ana");
        }
    }

    /// <summary>Only a published template is sent. A draft, an archived one and an erased one each fail for good, saying which.</summary>
    [Theory]
    [InlineData(ContentStatus.Draft, "is Draft")]
    [InlineData(ContentStatus.Archived, "is Archived")]
    [InlineData(ContentStatus.Scheduled, "is Scheduled")]
    public async Task A_template_that_is_not_published_fails_permanently_naming_its_status(ContentStatus status, string expected)
    {
        var template = EmailTemplateData.Template("s", "b", status);
        await using var session = Store.LightweightSession();
        session.Store(template);
        await session.SaveChangesAsync(Ct);

        var recorder = new RecordingEmailService();
        var result = await ActionFor(recorder, session).RunAsync(
            new() { ["To"] = "a@example.com", ["Template"] = template.Id.ToString() }, Entry(), Ct);

        result.Succeeded.Should().BeFalse();
        result.Retryable.Should().BeFalse();
        result.Error.Should().Contain(expected).And.Contain("Only a published template is sent");
        recorder.Messages.Should().BeEmpty();
    }

    [Fact]
    public async Task An_erased_template_fails_permanently()
    {
        var template = EmailTemplateData.Template("s", "b");
        await using var session = Store.LightweightSession();
        session.Store(template);
        await session.SaveChangesAsync(Ct);
        session.Delete<Content>(template.Id);
        await session.SaveChangesAsync(Ct);

        var recorder = new RecordingEmailService();
        var result = await ActionFor(recorder, session).RunAsync(
            new() { ["To"] = "a@example.com", ["Template"] = template.Id.ToString() }, Entry(), Ct);

        result.Succeeded.Should().BeFalse();
        result.Retryable.Should().BeFalse();
        result.Error.Should().Contain("No email template");
        recorder.Messages.Should().BeEmpty();
    }

    /// <summary>
    /// The issue's first check: two workflows naming one template, reworded once, both send the new
    /// text. The workflows run through the engine, as a live change does.
    /// </summary>
    [Fact]
    public async Task A_template_used_by_two_workflows_is_reworded_once_and_both_send_the_new_text()
    {
        var type = $"tplwf_{Guid.NewGuid():n}"[..14];
        var slug = $"shared-{Guid.NewGuid():N}"[..20];
        var template = EmailTemplateData.Template("Old subject", "Old body", slug: slug);

        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        await EmailTemplateData.EnsureTypesAsync(_fixture, session, Ct);
        session.Store(template);

        foreach (var to in new[] { "first@example.com", "second@example.com" })
        {
            session.Store(new WorkflowDefinition
            {
                Id = Guid.NewGuid(),
                Name = $"{type} {to}",
                TriggerContentType = type,
                TriggerEvent = "Created",
                Actions = [new WorkflowAction { Type = "Email", Parameters = new() { ["To"] = to, ["Template"] = slug } }],
            });
        }

        await session.SaveChangesAsync(Ct);

        var recorder = new RecordingEmailService();
        var extractor = scope.ServiceProvider.GetRequiredService<ITemplateVariableExtractor>();
        var email = new EmailAction(recorder, NullLogger<EmailAction>.Instance, session: session, extractor: extractor);
        var engine = new WorkflowEngine(
            session,
            [email],
            extractor,
            scope.ServiceProvider.GetRequiredService<IWorkflowDebugger>(),
            scope.ServiceProvider.GetRequiredService<barakoCMS.Infrastructure.Security.ISecretProtector>(),
            NullLogger<WorkflowEngine>.Instance);

        await engine.ProcessEventAsync(type, "Created", new Content { Id = Guid.NewGuid(), ContentType = type }, Ct);

        await using (var editor = Store.LightweightSession())
        {
            var stored = await editor.LoadAsync<Content>(template.Id, Ct);
            stored!.Data["Subject"] = "New subject";
            stored.Data["Body"] = "New body";
            editor.Store(stored);
            await editor.SaveChangesAsync(Ct);
        }

        await engine.ProcessEventAsync(type, "Created", new Content { Id = Guid.NewGuid(), ContentType = type }, Ct);

        var sent = recorder.Messages.ToList();
        sent.Should().HaveCount(4);
        sent.Take(2).Should().OnlyContain(m => m.Subject == "Old subject" && m.Body.Contains("<p>Old body</p>"));
        sent.Skip(2).Should().OnlyContain(m => m.Subject == "New subject" && m.Body.Contains("<p>New body</p>"));
        sent.Skip(2).Select(m => m.To).Should().BeEquivalentTo(["first@example.com", "second@example.com"]);
    }

    /// <summary>
    /// Red without the extractor reading what the template names: the runner prepares for the
    /// action's own parameters, which name no author, so the author was left as written.
    /// </summary>
    [Fact]
    public async Task A_template_placeholder_the_parameters_do_not_name_is_still_filled()
    {
        var (_, authorId) = await TestHelpers.CreateAdminUserAsync(_fixture);
        var template = EmailTemplateData.Template("From {{createdBy.name}}", "By {{createdBy.name}}");

        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Store(template);
        await session.SaveChangesAsync(Ct);

        var parameters = new Dictionary<string, string> { ["To"] = "a@example.com", ["Template"] = template.Id.ToString() };
        var entry = Entry();
        entry.CreatedBy = authorId;

        var extractor = scope.ServiceProvider.GetRequiredService<ITemplateVariableExtractor>();
        var resolved = await ActionParameters.ResolveAsync(extractor, "Email", parameters, entry, "Created", 0, Ct);

        var recorder = new RecordingEmailService();
        var result = await new EmailAction(recorder, NullLogger<EmailAction>.Instance, session: session, extractor: extractor)
            .RunAsync(resolved, entry, Ct);

        result.Succeeded.Should().BeTrue(result.Error);
        recorder.Messages.Should().ContainSingle().Which.Subject.Should().Be($"From admin-{authorId}");
        recorder.Messages.Single().Body.Should().Contain($"<p>By admin-{authorId}</p>");
    }

    [Fact]
    public async Task A_layout_wraps_the_body_and_one_that_is_not_published_fails_permanently()
    {
        var layout = EmailTemplateData.Layout("**Acme news**", "#123456");
        var draftLayout = EmailTemplateData.Layout("Draft", "#654321", ContentStatus.Draft);
        var wrapped = EmailTemplateData.Template("s", "Body text", layout: layout.Id);
        var broken = EmailTemplateData.Template("s", "Body text", layout: draftLayout.Id);

        await using var session = Store.LightweightSession();
        session.Store(layout, draftLayout, wrapped, broken);
        await session.SaveChangesAsync(Ct);

        var recorder = new RecordingEmailService();
        var sent = await ActionFor(recorder, session).RunAsync(
            new() { ["To"] = "a@example.com", ["Template"] = wrapped.Id.ToString() }, Entry(), Ct);
        var refused = await ActionFor(recorder, session).RunAsync(
            new() { ["To"] = "a@example.com", ["Template"] = broken.Id.ToString() }, Entry(), Ct);

        sent.Succeeded.Should().BeTrue(sent.Error);
        var body = recorder.Messages.Should().ContainSingle().Which.Body;
        body.Should().StartWith("<!DOCTYPE html>");
        body.Should().Contain("a{color:#123456;}");
        body.IndexOf("<strong>Acme news</strong>", StringComparison.Ordinal).Should().BePositive()
            .And.BeLessThan(body.IndexOf("<p>Body text</p>", StringComparison.Ordinal));
        body.Should().Contain("<p>Sent by Acme</p>");

        refused.Succeeded.Should().BeFalse();
        refused.Retryable.Should().BeFalse();
        refused.Error.Should().Contain("layout");
    }

    /// <summary>The cap a workflow parameter has, on a template stored past it some other way than the blueprint's rule.</summary>
    [Fact]
    public async Task A_template_body_past_the_parameter_cap_fails_permanently()
    {
        var template = EmailTemplateData.Template("s", new string('a', TemplateExpression.MaxTemplateLength + 1));
        await using var session = Store.LightweightSession();
        session.Store(template);
        await session.SaveChangesAsync(Ct);

        var recorder = new RecordingEmailService();
        var result = await ActionFor(recorder, session).RunAsync(
            new() { ["To"] = "a@example.com", ["Template"] = template.Id.ToString() }, Entry(), Ct);

        result.Succeeded.Should().BeFalse();
        result.Retryable.Should().BeFalse();
        result.Error.Should().Contain($"at most {TemplateExpression.MaxTemplateLength}");
        recorder.Messages.Should().BeEmpty();
    }

    /// <summary>The issue's third check: given the store and the extractor, an inline email is sent exactly as written.</summary>
    [Fact]
    public async Task An_email_with_an_inline_subject_and_body_sends_exactly_as_before()
    {
        await using var session = Store.LightweightSession();
        var recorder = new RecordingEmailService();

        var result = await ActionFor(recorder, session).RunAsync(
            new() { ["To"] = "a@example.com", ["Subject"] = "Q & A", ["Body"] = "**not markdown** {{data.Name}}" }, Entry(), Ct);

        result.Succeeded.Should().BeTrue(result.Error);
        var message = recorder.Messages.Should().ContainSingle().Which;
        message.Subject.Should().Be("Q & A");
        message.Body.Should().Be("**not markdown** {{data.Name}}", "the runner resolves inline parameters, and the action sends them as handed over");
    }
}
