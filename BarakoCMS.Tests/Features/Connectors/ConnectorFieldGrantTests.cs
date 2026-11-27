using Marten;
using Microsoft.Extensions.DependencyInjection;
using barakoCMS.Infrastructure.Connectors;
using barakoCMS.Models;
using FluentAssertions;
using Xunit;
using static BarakoCMS.Tests.Features.Connectors.GrantKit;
using Subject = BarakoCMS.Tests.Features.Connectors.RequestKit.Subject;

namespace BarakoCMS.Tests.Features.Connectors;

/// <summary>
/// A connector sends a Sensitive or Members field only when its own list names that field of that
/// type, and every attempt checks the list as it stands then (#1423).
/// </summary>
[Collection("Integration1")]
public class ConnectorFieldGrantTests(IntegrationTestFixture fixture)
{
    private static string Body(string hole) => "{\"v\":\"" + hole + "\"}";

    private Task<Subject> SeedAsync(string[] granted, SensitivityLevel entryLevel = SensitivityLevel.Public) =>
        GrantKit.SeedAsync(fixture, granted, entryLevel);

    private Task<ComposedRequest> ComposeAsync(Subject subject, RequestDefinition definition) =>
        RequestKit.ComposeAsync(fixture, subject, definition);

    [Fact]
    public async Task A_granted_sensitive_field_is_sent_in_the_body()
    {
        var subject = await SeedAsync(["Ssn"]);

        var composed = await ComposeAsync(subject, Definition(subject, Body("{{Ssn}}")));

        composed.Ok.Should().BeTrue("got: {0}", composed.Refusal);
        composed.Body.Should().Be(Body(SsnValue));
    }

    [Fact]
    public async Task A_granted_members_field_is_sent()
    {
        var subject = await SeedAsync(["Perk"]);

        var composed = await ComposeAsync(subject, Definition(subject, Body("{{Perk}}")));

        composed.Ok.Should().BeTrue("got: {0}", composed.Refusal);
        composed.Body.Should().Be(Body(PerkValue));
    }

    [Fact]
    public async Task A_header_that_names_a_granted_field_is_marked_for_redaction_in_the_delivery_row()
    {
        var subject = await SeedAsync(["Ssn"]);
        var definition = Definition(subject, "{}", "/in", new() { ["X-Ref"] = "{{Ssn}}", ["X-Title"] = "{{Title}}" });

        var composed = await ComposeAsync(subject, definition);

        composed.Ok.Should().BeTrue("got: {0}", composed.Refusal);
        composed.RedactedHeaders.Should().BeEquivalentTo(["X-Ref"]);
    }

    [Fact]
    public async Task A_header_that_names_only_public_fields_is_not_marked_for_redaction()
    {
        var subject = await SeedAsync(["Ssn"]);

        var composed = await ComposeAsync(subject, Definition(subject, "{}", "/in", new() { ["X-Title"] = "{{Title}}" }));

        composed.RedactedHeaders.Should().BeEmpty();
    }

    [Fact]
    public async Task A_granted_field_is_sent_in_the_path_and_in_a_header()
    {
        var subject = await SeedAsync(["Ssn"]);
        var definition = Definition(subject, "{}", "/people/{{Ssn}}", new() { ["X-Ref"] = "{{Ssn}}" });

        var composed = await ComposeAsync(subject, definition);

        composed.Ok.Should().BeTrue("got: {0}", composed.Refusal);
        composed.Url.Should().EndWith("/people/" + SsnValue);
        composed.Headers["X-Ref"].Should().Be(SsnValue);
    }

    [Fact]
    public async Task A_connector_without_the_grant_is_refused_and_the_value_is_not_in_the_refusal()
    {
        var subject = await SeedAsync([]);

        var composed = await ComposeAsync(subject, Definition(subject, Body("{{Ssn}}")));

        composed.Ok.Should().BeFalse();
        composed.Refusal.Should().Contain("Ssn").And.Contain("allowedFields");
        composed.Refusal.Should().NotContain(SsnValue);
    }

    [Fact]
    public async Task A_grant_of_one_field_does_not_open_another()
    {
        var subject = await SeedAsync(["Ssn"]);

        var composed = await ComposeAsync(subject, Definition(subject, Body("{{Perk}}")));

        composed.Ok.Should().BeFalse();
        composed.Refusal.Should().Contain("Perk");
    }

    [Fact]
    public async Task A_grant_on_another_type_does_not_open_the_same_field_name()
    {
        var subject = await SeedAsync([]);
        await RequestKit.SetGrantsAsync(fixture, subject, ["some_other_type.Ssn"]);

        var composed = await ComposeAsync(subject, Definition(subject, Body("{{Ssn}}")));

        composed.Ok.Should().BeFalse("the grant names a field of a different type");
        composed.Refusal.Should().Contain("Ssn");
    }

    [Fact]
    public async Task A_grant_is_compared_ignoring_case_on_both_parts()
    {
        var subject = await SeedAsync([]);
        await RequestKit.SetGrantsAsync(fixture, subject, [subject.Type.ToUpperInvariant() + ".SSN"]);

        var composed = await ComposeAsync(subject, Definition(subject, Body("{{ssn}}")));

        composed.Ok.Should().BeTrue("got: {0}", composed.Refusal);
        composed.Body.Should().Be(Body(SsnValue));
    }

    [Fact]
    public async Task A_hidden_field_is_refused_even_when_a_stored_grant_names_it()
    {
        var subject = await SeedAsync(["Vault"]);

        var composed = await ComposeAsync(subject, Definition(subject, Body("{{Vault}}")));

        composed.Ok.Should().BeFalse();
        composed.Refusal.Should().Contain("Vault").And.NotContain("allowedFields", "a Hidden field cannot be granted");
        composed.Refusal.Should().NotContain("vault value");
    }

    [Fact]
    public async Task A_field_the_owner_can_hide_is_refused_even_when_a_stored_grant_names_it()
    {
        var subject = await SeedAsync(["Memo"]);

        var composed = await ComposeAsync(subject, Definition(subject, Body("{{Memo}}")));

        composed.Ok.Should().BeFalse();
        composed.Refusal.Should().Contain("Memo").And.NotContain("owner memo");
    }

    [Fact]
    public async Task A_nested_hole_is_judged_by_its_first_segment()
    {
        var granted = await SeedAsync(["Ssn"]);
        var ungranted = await SeedAsync([]);

        var allowed = await ComposeAsync(granted, Definition(granted, Body("{{data.Ssn.Part}}")));
        var refused = await ComposeAsync(ungranted, Definition(ungranted, Body("{{data.Ssn.Part}}")));

        allowed.Ok.Should().BeTrue("got: {0}", allowed.Refusal);
        refused.Ok.Should().BeFalse();
        refused.Refusal.Should().Contain("Ssn");
    }

    [Fact]
    public async Task A_grant_revoked_between_two_attempts_refuses_the_second()
    {
        var subject = await SeedAsync(["Ssn"]);
        var definition = Definition(subject, Body("{{Ssn}}"));

        var first = await ComposeAsync(subject, definition);
        await RequestKit.SetGrantsAsync(fixture, subject, null);
        var second = await ComposeAsync(subject, definition);

        first.Ok.Should().BeTrue("got: {0}", first.Refusal);
        second.Ok.Should().BeFalse("the list is read again on every attempt");
        second.Refusal.Should().Contain("Ssn");
    }

    [Fact]
    public async Task A_granted_field_raised_to_hidden_between_two_attempts_refuses_the_second()
    {
        var subject = await SeedAsync(["Ssn"]);
        var definition = Definition(subject, Body("{{Ssn}}"));

        var first = await ComposeAsync(subject, definition);
        await SetLevelAsync(fixture, subject.Type, "Ssn", SensitivityLevel.Hidden);
        var second = await ComposeAsync(subject, definition);

        first.Ok.Should().BeTrue("got: {0}", first.Refusal);
        second.Ok.Should().BeFalse("the field's level is read again on every attempt");
        second.Refusal.Should().NotContain(SsnValue);
    }

    [Fact]
    public async Task A_granted_field_lowered_to_public_between_two_attempts_is_still_sent()
    {
        var subject = await SeedAsync(["Ssn"]);
        await SetLevelAsync(fixture, subject.Type, "Ssn", SensitivityLevel.Public);

        var composed = await ComposeAsync(subject, Definition(subject, Body("{{Ssn}}")));

        composed.Ok.Should().BeTrue("a Public field needs no grant: {0}", composed.Refusal);
    }

    [Theory]
    [InlineData("{{= data.Ssn}}")]
    [InlineData("{{= upper(data.Ssn)}}")]
    [InlineData("{{#each Ssn}}x{{/each}}")]
    [InlineData("{{row.Ssn}}")]
    [InlineData("{{= sum(lines.Ssn)}}")]
    public async Task A_form_a_request_does_not_render_stays_refused_with_a_granted_field(string form)
    {
        var subject = await SeedAsync(["Ssn"]);
        var sender = new GrantCountingSender();

        var composed = await ComposeAsync(subject, Definition(subject, Body(form)));
        var step = await RunStepAsync(fixture, subject, Definition(subject, Body(form)), sender);

        composed.Ok.Should().BeFalse("'{0}' is not a plain hole, granted or not", form);
        composed.Body.Should().BeNull();
        step.Succeeded.Should().BeFalse();
        sender.Sent.Should().Be(0);
    }

    [Fact]
    public async Task An_entry_that_is_hidden_is_not_sent_through_a_grant()
    {
        var subject = await SeedAsync(["Ssn"], SensitivityLevel.Hidden);

        var composed = await ComposeAsync(subject, Definition(subject, Body("{{Ssn}}")));

        composed.Ok.Should().BeFalse();
        composed.Refusal.Should().Contain("Hidden").And.NotContain(SsnValue);
    }

    [Fact]
    public async Task A_hidden_entry_is_still_sent_when_it_names_only_public_fields()
    {
        var subject = await SeedAsync(["Ssn"], SensitivityLevel.Hidden);

        var composed = await ComposeAsync(subject, Definition(subject, Body("{{Title}}")));

        composed.Ok.Should().BeTrue("the Hidden rule is about the granted fields: {0}", composed.Refusal);
    }

    [Theory]
    [InlineData("\r\nX-Injected: evil")]
    [InlineData("\nX-Injected: evil")]
    [InlineData("\rX-Injected: evil")]
    public async Task A_granted_value_with_a_line_break_is_refused_in_a_header(string tail)
    {
        var subject = await SeedAsync(["Ssn"]);
        await StoreEntryValueAsync(subject, "Ssn", "safe" + tail);

        var composed = await ComposeAsync(subject, Definition(subject, "{}", "/in", new() { ["X-Ref"] = "{{Ssn}}" }));

        composed.Ok.Should().BeFalse();
        composed.Refusal.Should().Contain("control character").And.Contain("X-Ref");
        composed.Refusal.Should().NotContain("X-Injected");
    }

    [Theory]
    [InlineData("run-1\r\nX-Injected: evil")]
    [InlineData("run-1\nX-Injected: evil")]
    public async Task An_idempotency_key_with_a_line_break_is_refused_after_substitution(string key)
    {
        var subject = await SeedAsync(["Ssn"]);

        var composed = await ComposeWithKeyAsync(fixture, subject, Definition(subject, Body("{{Ssn}}")), key);

        composed.Ok.Should().BeFalse();
        composed.Refusal.Should().Contain("idempotency key").And.Contain("control character");
    }

    [Fact]
    public async Task An_ordinary_idempotency_key_is_sent_as_a_header_with_a_granted_body()
    {
        var subject = await SeedAsync(["Ssn"]);

        var composed = await ComposeWithKeyAsync(fixture, subject, Definition(subject, Body("{{Ssn}}")), "run-1");

        composed.Ok.Should().BeTrue("got: {0}", composed.Refusal);
        composed.Headers["Idempotency-Key"].Should().Be("run-1");
    }

    [Fact]
    public async Task The_request_step_sends_a_granted_field()
    {
        var subject = await SeedAsync(["Ssn"]);
        var sender = new GrantCountingSender();

        var result = await RunStepAsync(fixture, subject, Definition(subject, Body("{{Ssn}}")), sender);

        result.Succeeded.Should().BeTrue("got: {0}", result.Error);
        sender.Sent.Should().Be(1);
        sender.Last!.Body.Should().Be(Body(SsnValue));
    }

    [Fact]
    public async Task The_request_step_sends_nothing_for_a_field_its_connector_is_not_granted()
    {
        var subject = await SeedAsync(["Perk"]);
        var sender = new GrantCountingSender();

        var result = await RunStepAsync(fixture, subject, Definition(subject, Body("{{Ssn}}")), sender);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Contain("Ssn");
        sender.Sent.Should().Be(0);
    }

    [Fact]
    public async Task The_request_step_sends_nothing_once_the_grant_is_gone()
    {
        var subject = await SeedAsync(["Ssn"]);
        var definition = Definition(subject, Body("{{Ssn}}"));
        var sender = new GrantCountingSender();

        var first = await RunStepAsync(fixture, subject, definition, sender);
        await RequestKit.SetGrantsAsync(fixture, subject, null);
        var retry = await RunStepAsync(fixture, subject, definition, sender);

        first.Succeeded.Should().BeTrue("got: {0}", first.Error);
        retry.Succeeded.Should().BeFalse("a retry judges the grant as it is then");
        sender.Sent.Should().Be(1);
    }

    [Fact]
    public async Task A_collection_sync_passes_a_granted_hole_and_fetches()
    {
        var subject = await SeedAsync(["Ssn"]);
        var fetcher = new GrantCountingFetcher();

        var outcome = await SyncAsync(fixture, subject, Definition(subject, null, "/feed?ref={{Ssn}}"), fetcher);

        fetcher.Fetched.Should().Be(1, "the hole is granted, so the check lets the sync through: {0}", outcome.Error);
        fetcher.Last!.Url.Should().NotContain(SsnValue, "a sync composes against an empty entry of the type, so no value is read");
    }

    [Fact]
    public async Task A_collection_sync_fetches_nothing_for_a_hole_its_connector_is_not_granted()
    {
        var subject = await SeedAsync([]);
        var fetcher = new GrantCountingFetcher();

        var outcome = await SyncAsync(fixture, subject, Definition(subject, null, "/feed?ref={{Ssn}}"), fetcher);

        outcome.Succeeded.Should().BeFalse();
        outcome.Error.Should().Contain("Ssn");
        fetcher.Fetched.Should().Be(0);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task StoreEntryValueAsync(Subject subject, string field, string value)
    {
        // Content uses optimistic concurrency, so the entry is loaded and saved in one session.
        using var scope = fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var entry = (await session.LoadAsync<Content>(subject.EntryId, Ct))!;
        entry.Data[field] = value;
        session.Store(entry);
        await session.SaveChangesAsync(Ct);
    }
}
