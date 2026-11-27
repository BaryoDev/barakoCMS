using System.Text.Json;
using barakoCMS.Models;
using FluentAssertions;
using Xunit;
using static BarakoCMS.Tests.Features.Connectors.GrantKit;

namespace BarakoCMS.Tests.Features.Connectors;

/// <summary>
/// The dry run shows a granted value only to a caller who may read it, and masks it in the URL, the
/// headers and the body where it lands, one hole at a time.
/// </summary>
[Collection("Integration1")]
public class ConnectorGrantDryRunTests(IntegrationTestFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Mask = "***";

    private async Task<(RequestKit.Subject Subject, string Slug)> StoreRequestAsync()
    {
        var subject = await GrantKit.SeedAsync(fixture, ["Ssn", "Perk"]);
        var definition = Definition(
            subject, "{\"ssn\":\"{{Ssn}}\",\"title\":\"{{Title}}\"}", "/people/{{Ssn}}", new() { ["X-Ref"] = "{{Ssn}}" });
        await StoreAsync(fixture, definition);
        return (subject, definition.Slug);
    }

    private static async Task<JsonElement> DryRunAsync(HttpClient client, string slug, Guid entry)
    {
        var res = await client.PostAsync($"/api/requests/{slug}/dry-run/{entry}", null, Ct);
        res.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}", res.StatusCode, await res.Content.ReadAsStringAsync(Ct));
        return JsonDocument.Parse(await res.Content.ReadAsStringAsync(Ct)).RootElement.Clone();
    }

    [Fact]
    public async Task A_caller_who_may_read_a_granted_field_sees_it_in_the_preview()
    {
        var (subject, slug) = await StoreRequestAsync();
        var reader = await CallerAsync(
            fixture, [SystemCapabilities.ManageRequests, SystemCapabilities.ViewSensitive], subject.Type);

        var dry = await DryRunAsync(reader, slug, subject.EntryId);

        dry.GetProperty("wouldSend").GetBoolean().Should().BeTrue();
        dry.GetProperty("url").GetString().Should().Contain(SsnValue);
        dry.GetProperty("headers").GetProperty("X-Ref").GetString().Should().Be(SsnValue);
        dry.GetProperty("body").GetString().Should().Contain(SsnValue);
    }

    [Fact]
    public async Task A_caller_who_may_not_read_a_granted_field_sees_it_masked_in_the_url_the_headers_and_the_body()
    {
        var (subject, slug) = await StoreRequestAsync();
        var blind = await CallerAsync(fixture, [SystemCapabilities.ManageRequests], subject.Type);

        var dry = await DryRunAsync(blind, slug, subject.EntryId);

        dry.GetProperty("wouldSend").GetBoolean().Should().BeTrue();
        // The dry run shows the URL as it would be sent, so the mask in the path is escaped.
        dry.GetProperty("url").GetString().Should().EndWith("/people/" + Uri.EscapeDataString(Mask)).And.NotContain(SsnValue);
        dry.GetProperty("headers").GetProperty("X-Ref").GetString().Should().Be(Mask);
        dry.GetProperty("body").GetString().Should().Contain(Mask).And.NotContain(SsnValue);
        dry.GetRawText().Should().NotContain(SsnValue);
    }

    [Fact]
    public async Task A_masked_field_does_not_mask_a_public_one_next_to_it()
    {
        var (subject, slug) = await StoreRequestAsync();
        var blind = await CallerAsync(fixture, [SystemCapabilities.ManageRequests], subject.Type);

        var dry = await DryRunAsync(blind, slug, subject.EntryId);

        dry.GetProperty("body").GetString().Should().Contain("plain title");
    }

    [Fact]
    public async Task A_super_admin_sees_the_granted_value_in_the_preview()
    {
        var (subject, slug) = await StoreRequestAsync();
        var admin = await fixture.CreateClientAsync("SuperAdmin", "Admin");

        var dry = await DryRunAsync(admin, slug, subject.EntryId);

        dry.GetProperty("body").GetString().Should().Contain(SsnValue);
    }

    [Fact]
    public async Task A_dry_run_of_a_field_that_is_not_granted_is_refused_for_everyone()
    {
        var subject = await GrantKit.SeedAsync(fixture, []);
        var definition = Definition(subject, "{{Ssn}}");
        await StoreAsync(fixture, definition);
        var admin = await fixture.CreateClientAsync("SuperAdmin", "Admin");

        var dry = await DryRunAsync(admin, definition.Slug, subject.EntryId);

        dry.GetProperty("wouldSend").GetBoolean().Should().BeFalse();
        dry.GetProperty("refusal").GetString().Should().Contain("Ssn");
        dry.GetRawText().Should().NotContain(SsnValue);
    }

    private async Task<(GrantKit.LineSeed Seed, string Slug)> StoreQueryRequestAsync()
    {
        var seed = await SeedLinesAsync(fixture, (lines, _) => [lines + ".Note"]);
        var query = new QueryDefinition
        {
            Id = Guid.NewGuid(), Slug = Name("dryq"), Name = "Dry run query", ContentType = seed.Lines, Fields = ["Qty", "Note"],
        };
        var definition = Definition(seed.Subject, "{{query.rows}}");
        definition.QuerySlug = query.Slug;
        await StoreAsync(fixture, query, definition);
        return (seed, definition.Slug);
    }

    [Fact]
    public async Task A_query_row_that_holds_a_granted_field_the_caller_may_not_read_is_masked_in_the_preview()
    {
        var (seed, slug) = await StoreQueryRequestAsync();
        var blind = await CallerAsync(fixture, [SystemCapabilities.ManageRequests], seed.Lines);

        var dry = await DryRunAsync(blind, slug, seed.Subject.EntryId);

        dry.GetProperty("wouldSend").GetBoolean().Should().BeTrue();
        var body = dry.GetProperty("body").GetString()!;
        body.Should().Contain(Mask);
        var parse = () => JsonDocument.Parse(body);
        parse.Should().NotThrow("a masked body keeps the shape of the send, so it is still JSON");
        dry.GetRawText().Should().NotContain(NoteValue);
    }

    [Fact]
    public async Task A_query_row_that_holds_a_granted_field_the_caller_may_read_is_shown_in_the_preview()
    {
        var (seed, slug) = await StoreQueryRequestAsync();
        var reader = await CallerAsync(
            fixture, [SystemCapabilities.ManageRequests, SystemCapabilities.ViewSensitive], seed.Lines);

        var dry = await DryRunAsync(reader, slug, seed.Subject.EntryId);

        dry.GetProperty("body").GetString().Should().Contain(NoteValue);
    }
}
