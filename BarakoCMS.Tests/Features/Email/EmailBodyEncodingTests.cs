using System.Net;
using System.Net.Http.Json;
using barakoCMS.Features.Workflows;
using barakoCMS.Features.Workflows.Actions;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BarakoCMS.Tests.Features.Email;

/// <summary>
/// Text that came from a request or an entry reaches an email as text, never as markup.
/// </summary>
/// <remarks>
/// Every provider sends the body as HTML, so a value interpolated into it unescaped is rendered: a
/// user-agent or a form field holding a link shows up as a real link in a real email from this
/// server. The expected strings below are the exact escaped form, so a body that leaves the markup
/// alone cannot pass by containing the visible words.
/// </remarks>
[Collection("Sequential")]
public class EmailBodyEncodingTests
{
    private const string Link = "<a href=\"https://x.example\">click</a>";
    private const string EncodedLink = "&lt;a href=&quot;https://x.example&quot;&gt;click&lt;/a&gt;";

    private readonly IntegrationTestFixture _factory;

    private static int _ipCounter;
    private readonly string _ip = $"2001:db8:456::{Interlocked.Increment(ref _ipCounter):x}";

    public EmailBodyEncodingTests(IntegrationTestFixture factory) => _factory = factory;

    [Fact]
    public async Task A_user_agent_in_the_sign_in_code_email_is_shown_as_text()
    {
        var email = await SignedUpEmailAsync();

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestRemoteIpFilter.Header, _ip);
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", Link);

        var response = await client.PostAsJsonAsync("/api/auth/otp/request",
            new { email }, TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var sent = _factory.Email.Messages.Where(m => m.To == email.Trim().ToLowerInvariant()).ToList();
        sent.Should().ContainSingle("one code was requested for this address");

        sent[0].Body.Should().Contain(EncodedLink);
        sent[0].Body.Should().NotContain("<a href");
    }

    [Fact]
    public async Task A_form_value_in_an_html_workflow_email_is_escaped()
    {
        var sent = await RunEmailWorkflowAsync(
            new() { ["To"] = "staff@example.com", ["Subject"] = "New message", ["Body"] = "<p>From {{data.Name}}</p>" },
            new() { ["Name"] = Link });

        sent.Should().ContainSingle();
        sent[0].Body.Should().Be($"<p>From {EncodedLink}</p>",
            "the template's own markup stays, the submitted value is escaped");
    }

    /// <summary>
    /// Nothing is encoded that does not need it: the author's own text, an ampersand included, and
    /// a value with no markup characters arrive exactly as written.
    /// </summary>
    [Fact]
    public async Task A_plain_template_with_a_plain_value_is_sent_unchanged()
    {
        var sent = await RunEmailWorkflowAsync(
            new() { ["To"] = "staff@example.com", ["Subject"] = "Q & A", ["Body"] = "Q & A from {{data.Name}}." },
            new() { ["Name"] = "Maria Santos" });

        sent.Should().ContainSingle();
        sent[0].Body.Should().Be("Q & A from Maria Santos.");
        sent[0].Subject.Should().Be("Q & A");
    }

    /// <summary>
    /// A subject is a header, not HTML: a line break in a value is removed, and an ampersand is
    /// left as an ampersand rather than turned into an entity nobody's mail client decodes there.
    /// </summary>
    [Fact]
    public async Task A_line_break_in_a_subject_value_is_removed_and_nothing_else_changes()
    {
        var sent = await RunEmailWorkflowAsync(
            new() { ["To"] = "staff@example.com", ["Subject"] = "From {{data.Name}}", ["Body"] = "b" },
            new() { ["Name"] = "Tom & Jerry\r\nBcc: someone@example.com" });

        sent.Should().ContainSingle();
        sent[0].Subject.Should().Be("From Tom & Jerry Bcc: someone@example.com");
    }

    /// <summary>
    /// An email inside a conditional gets the same treatment, and a quote in a value cannot change
    /// the action list the branch was configured with.
    /// </summary>
    /// <remarks>
    /// The branch runs on the else side because the engine resolves the condition before the
    /// conditional sees it, which is a separate problem this test does not depend on.
    /// </remarks>
    [Fact]
    public async Task A_form_value_in_a_conditional_email_is_escaped_and_cannot_change_the_branch()
    {
        var children = System.Text.Json.JsonSerializer.Serialize(new[]
        {
            new { Type = "Email", Parameters = new Dictionary<string, string>
            {
                ["To"] = "staff@example.com", ["Subject"] = "s", ["Body"] = "<p>{{data.Name}}</p>",
            } },
        });

        var sent = await RunEmailWorkflowAsync(
            new() { ["Condition"] = "{{status}} == Nope", ["ElseActions"] = children },
            new() { ["Name"] = "\"" + Link },
            actionType: "Conditional");

        sent.Should().ContainSingle("the one configured child ran, with the value as data");
        sent[0].To.Should().Be("staff@example.com");
        sent[0].Body.Should().Be($"<p>&quot;{EncodedLink}</p>");
    }

    private async Task<List<RecordingEmailService.Sent>> RunEmailWorkflowAsync(
        Dictionary<string, string> parameters, Dictionary<string, object> data, string actionType = "Email")
    {
        var type = $"wfe_{Guid.NewGuid():n}"[..12];
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();

        var recorder = new RecordingEmailService();
        var email = new EmailAction(recorder, NullLogger<EmailAction>.Instance);

        var children = new ServiceCollection();
        children.AddSingleton<IWorkflowAction>(email);
        var conditional = new ConditionalAction(children.BuildServiceProvider(), NullLogger<ConditionalAction>.Instance);

        var engine = new WorkflowEngine(
            session,
            [email, conditional],
            scope.ServiceProvider.GetRequiredService<barakoCMS.Infrastructure.Services.ITemplateVariableExtractor>(),
            scope.ServiceProvider.GetRequiredService<barakoCMS.Infrastructure.Services.IWorkflowDebugger>(),
            scope.ServiceProvider.GetRequiredService<barakoCMS.Infrastructure.Security.ISecretProtector>(),
            NullLogger<WorkflowEngine>.Instance);

        session.Store(new WorkflowDefinition
        {
            Id = Guid.NewGuid(),
            Name = type,
            TriggerContentType = type,
            TriggerEvent = "Created",
            Actions = [new WorkflowAction { Type = actionType, Parameters = parameters }],
        });
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);

        await engine.ProcessEventAsync(type, "Created", new Content
        {
            Id = Guid.NewGuid(),
            ContentType = type,
            Status = ContentStatus.Draft,
            Data = data,
        }, TestContext.Current.CancellationToken);

        return recorder.Messages.ToList();
    }

    private async Task<string> SignedUpEmailAsync()
    {
        var (_, userId) = await TestHelpers.CreateAdminUserAsync(_factory);

        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        var user = await session.LoadAsync<User>(userId);

        user.Should().NotBeNull();
        return user!.Email;
    }
}
