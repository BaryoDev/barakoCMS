using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using barakoCMS.Features.EmailTemplates;
using barakoCMS.Infrastructure.Security;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BarakoCMS.Tests.Features.EmailTemplates;

/// <summary>
/// <c>POST /api/email-templates/{id}/preview</c> renders a template against an entry the caller can
/// read, as that caller reads it, and sends nothing.
/// </summary>
[Collection("Sequential")]
public class EmailTemplatePreviewTests
{
    private const string Secret = "s3cret-value-7781";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IntegrationTestFixture _fixture;

    private static int _ip;

    public EmailTemplatePreviewTests(IntegrationTestFixture fixture) => _fixture = fixture;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IDocumentStore Store => _fixture.Services.GetRequiredService<IDocumentStore>();

    private sealed record Warning(string Field, string Message);

    private sealed record Preview(
        string Subject, string Html, string Status, bool Sendable, string? Problem, List<Warning> Warnings, List<string> Notes);

    private sealed record ValidationResult(bool IsValid, List<Warning> Errors, List<Warning> Warnings);

    private HttpClient Client(string token)
    {
        var client = _fixture.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        client.DefaultRequestHeaders.Add(TestRemoteIpFilter.Header, $"2001:db8:829::{Interlocked.Increment(ref _ip):x}");
        return client;
    }

    private async Task<HttpClient> SuperAdminAsync() => Client(await _fixture.StoredUserTokenAsync("SuperAdmin"));

    /// <summary>A user whose one role may write workflows and read the named types, and see Sensitive fields when asked.</summary>
    private async Task<HttpClient> AuthorAsync(IEnumerable<string> readable, bool seesSensitive = false)
    {
        var role = new Role
        {
            Id = Guid.NewGuid(),
            Name = $"previewer-{Guid.NewGuid():N}",
            SystemCapabilities = seesSensitive
                ? [SystemCapabilities.ManageWorkflows, SystemCapabilities.ViewSensitive]
                : [SystemCapabilities.ManageWorkflows],
            Permissions = readable
                .Select(type => new ContentTypePermission { ContentTypeSlug = type, Read = new PermissionRule { Enabled = true } })
                .ToList(),
        };
        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = $"previewer-{Guid.NewGuid():N}",
            Email = $"previewer-{Guid.NewGuid():N}@example.com",
            RoleIds = [role.Id],
        };

        await using (var session = Store.LightweightSession())
        {
            session.Store(role);
            session.Store(user);
            await session.SaveChangesAsync(Ct);
        }

        return Client(_fixture.CreateToken([role.Name], user.Id.ToString(),
            new Dictionary<string, string> { ["Username"] = user.Username }));
    }

    /// <summary>A fresh entry type with a Sensitive field, one entry of it, and a template.</summary>
    private async Task<(string Type, Content Entry, Content Template)> ArrangeAsync(
        string subject, string body, ContentStatus status = ContentStatus.Draft)
    {
        var type = $"pv{Guid.NewGuid():N}"[..14];
        var entry = new Content
        {
            Id = Guid.NewGuid(),
            ContentType = type,
            Status = ContentStatus.Published,
            Data = new Dictionary<string, object> { ["Name"] = "Ana", ["Secret"] = Secret },
        };
        var template = EmailTemplateData.Template(subject, body, status);

        await using var session = Store.LightweightSession();
        await EmailTemplateData.EnsureTypesAsync(_fixture, session, Ct);
        session.Store(new ContentTypeDefinition
        {
            Id = Guid.NewGuid(),
            Name = type,
            DisplayName = type,
            Fields =
            [
                new FieldDefinition { Name = "Name", Type = "string" },
                new FieldDefinition { Name = "Secret", Type = "string", Sensitivity = SensitivityLevel.Sensitive },
            ],
        });
        session.Store(entry);
        session.Store(template);
        await session.SaveChangesAsync(Ct);

        return (type, entry, template);
    }

    private static async Task<HttpResponseMessage> PreviewAsync(HttpClient client, string template, Guid entry) =>
        await client.PostAsJsonAsync($"/api/email-templates/{template}/preview", new { entryId = entry }, Ct);

    private static async Task<Preview> ReadAsync(HttpResponseMessage response)
    {
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<Preview>(Json, Ct))!;
    }

    [Fact]
    public async Task A_preview_renders_a_draft_against_the_entry_by_id_or_slug_and_says_it_would_not_be_sent()
    {
        var (_, entry, template) = await ArrangeAsync("Hi {{data.Name}}", "Hello **{{data.Name}}**");
        var client = await SuperAdminAsync();

        var preview = await ReadAsync(await PreviewAsync(client, template.Id.ToString(), entry.Id));

        preview.Subject.Should().Be("Hi Ana");
        preview.Html.Should().StartWith("<!DOCTYPE html>").And.Contain("<p>Hello <strong>Ana</strong></p>");
        preview.Status.Should().Be("Draft");
        preview.Sendable.Should().BeFalse();
        preview.Problem.Should().Contain("Only a published template is sent");

        var bySlug = await ReadAsync(await PreviewAsync(client, (string)template.Data["Slug"], entry.Id));
        bySlug.Subject.Should().Be("Hi Ana");
    }

    /// <summary>
    /// Red if the preview resolved against the stored entry: a caller who may not see the Sensitive
    /// field would read it in the rendered body.
    /// </summary>
    [Fact]
    public async Task A_sensitive_field_renders_only_for_a_caller_who_may_see_it()
    {
        var (type, entry, template) = await ArrangeAsync("Code", "Code [{{data.Secret}}] for {{data.Name}}", ContentStatus.Published);
        var readable = new[] { type, EmailTemplateRenderer.TemplateType };

        var withheld = await ReadAsync(await PreviewAsync(await AuthorAsync(readable), template.Id.ToString(), entry.Id));
        var shown = await ReadAsync(await PreviewAsync(await AuthorAsync(readable, seesSensitive: true), template.Id.ToString(), entry.Id));

        withheld.Html.Should().NotContain(Secret);
        withheld.Html.Should().Contain("<p>Code [] for Ana</p>");
        withheld.Warnings.Should().ContainSingle(w => w.Field == "entryId").Which.Message.Should().StartWith("1 field(s)");

        shown.Html.Should().Contain($"<p>Code [{Secret}] for Ana</p>");
        shown.Warnings.Should().NotContain(w => w.Field == "entryId");
        shown.Sendable.Should().BeTrue();
    }

    [Fact]
    public async Task An_entry_the_caller_cannot_read_is_refused_with_403_and_a_template_they_cannot_read_with_404()
    {
        var (type, entry, template) = await ArrangeAsync("s", "b");

        var noEntry = await PreviewAsync(await AuthorAsync([EmailTemplateRenderer.TemplateType]), template.Id.ToString(), entry.Id);
        var noTemplate = await PreviewAsync(await AuthorAsync([type]), template.Id.ToString(), entry.Id);
        var both = await PreviewAsync(await AuthorAsync([type, EmailTemplateRenderer.TemplateType]), template.Id.ToString(), entry.Id);

        noEntry.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        noTemplate.StatusCode.Should().Be(HttpStatusCode.NotFound);
        both.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    /// <summary>
    /// A placeholder the engine cannot fill is warned about in the preview and on the workflow save
    /// with the very message an inline body gets.
    /// </summary>
    [Fact]
    public async Task A_template_placeholder_the_engine_cannot_fill_gets_the_inline_warning_in_the_preview_and_on_save()
    {
        const string body = "Hello {{nope.thing}}";
        var (type, entry, template) = await ArrangeAsync("s", body, ContentStatus.Published);
        var client = await SuperAdminAsync();
        var expected = TemplateExpression.Problems(body, onTransition: true).ToList();
        expected.Should().HaveCount(1);

        var preview = await ReadAsync(await PreviewAsync(client, template.Id.ToString(), entry.Id));

        preview.Warnings.Should().ContainSingle(w => w.Field == "Body").Which.Message.Should().Be(expected[0]);

        var inline = await ValidateAsync(client, type, new() { ["To"] = "a@example.com", ["Subject"] = "s", ["Body"] = body });
        var named = await ValidateAsync(client, type, new() { ["To"] = "a@example.com", ["Template"] = template.Id.ToString() });

        inline.IsValid.Should().BeTrue();
        inline.Warnings.Should().ContainSingle().Which.Message.Should().Be(expected[0]);
        named.IsValid.Should().BeTrue(string.Join("; ", named.Errors.Select(e => e.Message)));
        named.Warnings.Should().ContainSingle().Which.Should().Be(
            new Warning("actions[0].parameters.Template", $"In the template's Body: {expected[0]}"));
    }

    [Fact]
    public async Task Saving_a_workflow_naming_a_template_that_does_not_exist_warns_and_does_not_refuse()
    {
        var client = await SuperAdminAsync();

        var result = await ValidateAsync(client, "pvmissing", new() { ["To"] = "a@example.com", ["Template"] = "no-such-template" });

        result.IsValid.Should().BeTrue();
        result.Warnings.Should().ContainSingle().Which.Message.Should().StartWith("No email template 'no-such-template' exists in this tenant.");
    }

    private static async Task<ValidationResult> ValidateAsync(HttpClient client, string type, Dictionary<string, string> parameters)
    {
        var response = await client.PostAsJsonAsync("/api/workflows/validate", new
        {
            name = "preview check",
            triggerContentType = type,
            triggerEvent = "Created",
            actions = new[] { new { type = "Email", parameters } },
        }, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<ValidationResult>(Json, Ct))!;
    }

    [Fact]
    public void The_preview_route_carries_its_own_rate_limit()
    {
        var preview = _fixture.Services.GetServices<EndpointDataSource>()
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText?.TrimStart('/') == "api/email-templates/{id}/preview")
            .ToList();

        preview.Should().ContainSingle();
        preview[0].Metadata.GetMetadata<EnableRateLimitingAttribute>()?.PolicyName.Should().Be(RateLimitSetup.EmailPreviewPolicy);
        preview[0].Metadata.GetMetadata<EnableRateLimitingAttribute>().Should().NotBeNull();
        RateLimitSetup.EmailPreview.Should().Be(new RateLimitWindow(30, 60, 0));
    }
}
