using System.Globalization;
using System.Text.Json;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Moq;
using Xunit;

namespace BarakoCMS.Tests.Features.Workflows;

/// <summary>
/// What a placeholder with a format, a duration, an author or a transition resolves to, with no
/// database: the entry and the context are built here.
/// </summary>
/// <remarks>
/// Every expected string is exact, in a fixed zone and the invariant culture. The entry was created
/// at 00:30 UTC, which is 8:30 in the morning in Manila, and the transition happened at 09:00 UTC,
/// which is 5:00 in the afternoon there.
/// </remarks>
public class TemplateExpressionTests
{
    private const string Hostile = "<script>alert(\"x\")</script>\r\nBcc: x@example.com {{data.Secret}}";

    private static readonly DateTime CreatedAt = new(2026, 9, 14, 0, 30, 0, DateTimeKind.Utc);
    private static readonly DateTime ClockedOutAt = new(2026, 9, 14, 9, 0, 0, DateTimeKind.Utc);

    private static readonly TemplateContext Manila = new(
        TimeZoneInfo.FindSystemTimeZoneById("Asia/Manila"),
        "PHP",
        new TemplatePerson("maria", "maria@example.com"),
        new TemplateTransition("ClockOut", ClockedOutAt, new TemplatePerson("ramon", "ramon@example.com")));

    private static Content Entry(Dictionary<string, object>? data = null) => new()
    {
        Id = Guid.Parse("11111111-1111-1111-1111-111111111111"),
        ContentType = "timeEntry",
        Status = ContentStatus.Published,
        CreatedAt = CreatedAt,
        UpdatedAt = ClockedOutAt,
        Data = data ?? new Dictionary<string, object>
        {
            ["Name"] = "Maria Santos",
            ["Amount"] = 1234.5m,
            ["Secret"] = "TOP-SECRET",
        },
    };

    private static string Resolve(string template, Content? entry = null, TemplateContext? context = null) =>
        TemplateVariableExtractor.Resolve(template, entry ?? Entry(), TemplateValueEncoding.None, context ?? Manila);

    /// <summary>Red without the change: the engine left a hole with a format in it as written.</summary>
    [Fact]
    public void A_date_is_formatted_in_the_zone_the_format_names()
    {
        TemplateVariableExtractor.Resolve(
                "{{createdAt | date \"MMM d, h:mm tt\" \"Asia/Manila\"}}", Entry(), TemplateValueEncoding.None)
            .Should().Be("Sep 14, 8:30 AM");
    }

    /// <summary>Red without the change. The second line is the control: with nothing prepared, the zone is UTC.</summary>
    [Fact]
    public void A_date_with_no_zone_named_is_formatted_in_the_tenants_zone()
    {
        Resolve("{{createdAt | date \"h:mm tt\"}} to {{transition.at | date \"h:mm tt\"}}")
            .Should().Be("8:30 AM to 5:00 PM");

        TemplateVariableExtractor.Resolve("{{createdAt | date \"h:mm tt\"}}", Entry(), TemplateValueEncoding.None)
            .Should().Be("12:30 AM");
    }

    /// <summary>Red without the change.</summary>
    [Fact]
    public void A_date_with_no_format_uses_year_month_day_hours_and_minutes()
    {
        Resolve("{{createdAt | date}}").Should().Be("2026-09-14 08:30");
    }

    /// <summary>
    /// Red without the change. Under a French server culture a culture-sensitive format would give
    /// "septembre" and "1 234,50".
    /// </summary>
    [Fact]
    public void A_format_reads_the_same_whatever_the_servers_culture_is()
    {
        var before = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");

        try
        {
            Resolve("{{createdAt | date \"dddd, MMMM d, tt\" \"UTC\"}} / {{data.Amount | money}} / {{hours createdAt transition.at}}")
                .Should().Be("Monday, September 14, AM / PHP 1,234.50 / 8.5");
        }
        finally
        {
            CultureInfo.CurrentCulture = before;
        }
    }

    /// <summary>Red without the change. A field holding a date in any of the ISO 8601 shapes is a date.</summary>
    [Theory]
    [InlineData("2026-09-14", "2026-09-14 00:00")]
    [InlineData("2026-09-14T08:30", "2026-09-14 08:30")]
    [InlineData("2026-09-14T08:30Z", "2026-09-14 08:30")]
    [InlineData("2026-09-14T08:30:15Z", "2026-09-14 08:30")]
    [InlineData("2026-09-14T08:30:15.1234567Z", "2026-09-14 08:30")]
    [InlineData("2026-09-14T08:30:00+08:00", "2026-09-14 00:30")]
    public void A_field_holding_an_iso_date_is_formatted_as_one(string stored, string expected)
    {
        Resolve("{{data.When | date \"yyyy-MM-dd HH:mm\" \"UTC\"}}", Entry(new() { ["When"] = stored }))
            .Should().Be(expected);
    }

    /// <summary>Red without the change. A field can hold a date as a value and not as text.</summary>
    [Fact]
    public void A_field_holding_a_date_value_is_formatted_as_one()
    {
        var entry = Entry(new()
        {
            ["Stored"] = JsonDocument.Parse("\"2026-09-14T00:30:00Z\"").RootElement.Clone(),
            ["Boxed"] = CreatedAt,
        });

        Resolve("{{data.Stored | date \"h:mm tt\"}} {{data.Boxed | date \"h:mm tt\"}}", entry)
            .Should().Be("8:30 AM 8:30 AM");
    }

    /// <summary>Red without the change.</summary>
    [Fact]
    public void Money_is_two_decimals_with_separators_after_the_currency_code()
    {
        var entry = Entry(new()
        {
            ["Decimal"] = 1234.5m,
            ["Double"] = 1234.567d,
            ["Whole"] = 1250,
            ["Text"] = "-99.1",
            ["Json"] = JsonDocument.Parse("1000000.25").RootElement.Clone(),
        });

        Resolve("{{data.Decimal | money}}", entry).Should().Be("PHP 1,234.50");
        Resolve("{{data.Double | money}}", entry).Should().Be("PHP 1,234.57");
        Resolve("{{data.Whole | money}}", entry).Should().Be("PHP 1,250.00");
        Resolve("{{data.Text | money}}", entry).Should().Be("PHP -99.10");
        Resolve("{{data.Json | money}}", entry).Should().Be("PHP 1,000,000.25");
        Resolve("{{data.Decimal | money \"usd\"}}", entry).Should().Be("USD 1,234.50");

        TemplateVariableExtractor.Resolve("{{data.Decimal | money}}", entry, TemplateValueEncoding.None)
            .Should().Be("1,234.50", "with no site currency read, the amount carries no code");
    }

    /// <summary>Red without the change.</summary>
    [Fact]
    public void Upper_and_lower_change_the_case_of_a_value()
    {
        Resolve("{{data.Name | upper}} {{data.Name | lower}} {{status|upper}}")
            .Should().Be("MARIA SANTOS maria santos PUBLISHED");
    }

    /// <summary>Red without the change. The first row is the sentence the issue asks for.</summary>
    [Theory]
    [InlineData("2026-09-14T00:30Z", "2026-09-14T09:00Z", "8 hours 30 minutes", "8.5")]
    [InlineData("2026-09-14T00:00Z", "2026-09-14T08:00Z", "8 hours", "8.0")]
    [InlineData("2026-09-14T00:00Z", "2026-09-14T00:45:59Z", "45 minutes", "0.8")]
    [InlineData("2026-09-14T00:00Z", "2026-09-14T01:01Z", "1 hour 1 minute", "1.0")]
    [InlineData("2026-09-14T00:00Z", "2026-09-14T08:03Z", "8 hours 3 minutes", "8.1")]
    [InlineData("2026-09-14T00:00Z", "2026-09-14T08:02Z", "8 hours 2 minutes", "8.0")]
    [InlineData("2026-09-14T00:00Z", "2026-09-14T00:00:30Z", "0 minutes", "0.0")]
    [InlineData("2026-09-14T02:30Z", "2026-09-14T00:00Z", "-2 hours 30 minutes", "-2.5")]
    [InlineData("2026-09-13T00:00Z", "2026-09-14T06:00Z", "30 hours", "30.0")]
    public void A_duration_is_hours_and_minutes_and_hours_is_one_decimal(string from, string to, string duration, string hours)
    {
        var entry = Entry(new() { ["In"] = from, ["Out"] = to });

        Resolve("{{duration data.In data.Out}}", entry).Should().Be(duration);
        Resolve("{{hours data.In data.Out}}", entry).Should().Be(hours);
    }

    /// <summary>Red without the change: the issue's sentence, against the entry and its transition.</summary>
    [Fact]
    public void The_clock_out_sentence_reads_as_the_issue_writes_it()
    {
        Resolve("You worked {{duration createdAt transition.at}} today, from {{createdAt | date \"h:mm tt\"}} to {{transition.at | date \"h:mm tt\"}}.")
            .Should().Be("You worked 8 hours 30 minutes today, from 8:30 AM to 5:00 PM.");
    }

    /// <summary>Red without the change: these names were unknown and left as written.</summary>
    [Fact]
    public void The_author_and_the_transition_resolve_from_the_context()
    {
        Resolve("{{createdBy.name}} <{{createdBy.email}}> {{transition.name}} {{transition.at}} {{transition.by.name}} <{{transition.by.email}}>")
            .Should().Be($"maria <maria@example.com> ClockOut {ClockedOutAt:o} ramon <ramon@example.com>");
    }

    /// <summary>
    /// A guard that passes with or without the change: each of these was left as written before,
    /// and still is. It is what keeps a mistyped format visible in the message sent.
    /// </summary>
    [Theory]
    [InlineData("{{createdAt | nope}}")]
    [InlineData("{{createdAt | Date \"h:mm\"}}")]
    [InlineData("{{createdAt | date \"h:mm\" \"Mars/Phobos\"}}")]
    [InlineData("{{createdAt | date \"h:mm\" \"../../etc/passwd\"}}")]
    [InlineData("{{createdAt | date \"h:mm\" \"UTC\" \"extra\"}}")]
    [InlineData("{{createdAt | date \"\"}}")]
    [InlineData("{{createdAt | date \"h\"}}")]
    [InlineData("{{createdAt | date h:mm}}")]
    [InlineData("{{createdAt | date \"yyyy-MM-dd yyyy-MM-dd yyyy-MM-dd yyyy-MM-dd yyyy-MM-dd yyyy-MM-dd\"}}")]
    [InlineData("{{createdAt | upper \"x\"}}")]
    [InlineData("{{createdAt | date \"h:mm\" | upper}}")]
    [InlineData("{{data.Name | date \"h:mm\"}}")]
    [InlineData("{{data.Name | money}}")]
    [InlineData("{{data.Amount | money \"PESO\"}}")]
    [InlineData("{{data.Missing | upper}}")]
    [InlineData("{{nope | upper}}")]
    [InlineData("{{duration createdAt}}")]
    [InlineData("{{duration createdAt status}}")]
    [InlineData("{{duration createdAt data.Missing}}")]
    [InlineData("{{minutes createdAt updatedAt}}")]
    [InlineData("{{createdBy.phone}}")]
    [InlineData("{{transition.to}}")]
    [InlineData("{{#each data.Items}}")]
    public void What_the_engine_cannot_fill_is_left_as_written(string template)
    {
        Resolve($"before {template} after").Should().Be($"before {template} after");
    }

    /// <summary>
    /// A guard that passes with or without the change. With nothing prepared there is no author and
    /// no transition to name, so those holes stay, as they did before the names existed.
    /// </summary>
    [Theory]
    [InlineData("{{createdBy.name}}")]
    [InlineData("{{createdBy.email}}")]
    [InlineData("{{transition.name}}")]
    [InlineData("{{transition.by.email}}")]
    [InlineData("{{duration createdAt transition.at}}")]
    public void An_author_or_a_transition_nobody_read_is_left_as_written(string template)
    {
        TemplateVariableExtractor.Resolve(template, Entry(), TemplateValueEncoding.None).Should().Be(template);
    }

    /// <summary>
    /// A guard that passes with or without the change, and the one that matters most: a template
    /// that resolved before formats existed resolves to the same text, character for character,
    /// with a full context in hand.
    /// </summary>
    [Theory]
    [InlineData("{{id}}", "11111111-1111-1111-1111-111111111111")]
    [InlineData("{{ id }}", "11111111-1111-1111-1111-111111111111")]
    [InlineData("{{\n\tstatus \r\n}}", "Published")]
    [InlineData("{{contentType}}/{{status}}", "timeEntry/Published")]
    [InlineData("{{createdAt}}", "2026-09-14T00:30:00.0000000Z")]
    [InlineData("{{updatedAt}}", "2026-09-14T09:00:00.0000000Z")]
    [InlineData("{{data.Name}}", "Maria Santos")]
    [InlineData("{{data.Missing}}", "{{data.Missing}}")]
    [InlineData("{{Data.Name}}", "{{Data.Name}}")]
    [InlineData("{{ID}}", "{{ID}}")]
    [InlineData("{{data.}}", "{{data.}}")]
    [InlineData("{{}}", "{{}}")]
    [InlineData("{{ }}", "{{ }}")]
    [InlineData("{{id}", "{{id}")]
    [InlineData("{id}}", "{id}}")]
    [InlineData("{{{status}}}", "{Published}")]
    [InlineData("{{ {{status}} }}", "{{ Published }}")]
    [InlineData("{{status}}{{status}}", "PublishedPublished")]
    [InlineData("{{data.Name}} | upper", "Maria Santos | upper")]
    [InlineData("a | b {{status}} \"c\"", "a | b Published \"c\"")]
    public void A_template_that_resolved_before_resolves_to_the_same_text(string template, string expected)
    {
        Resolve(template).Should().Be(expected);
    }

    /// <summary>
    /// Red without the change, since none of these holes was filled before. A name, a formatted
    /// value and a date format's own literal text all go through the encoding of the sink, and a
    /// placeholder inside a value is not resolved a second time.
    /// </summary>
    [Theory]
    [InlineData(TemplateValueEncoding.None,
        "<script>alert(\"x\")</script>\r\nBcc: x@example.com {{data.Secret}}",
        "<SCRIPT>ALERT(\"X\")</SCRIPT>\r\nBCC: X@EXAMPLE.COM {{DATA.SECRET}}",
        "<b>8:30")]
    [InlineData(TemplateValueEncoding.Html,
        "&lt;script&gt;alert(&quot;x&quot;)&lt;/script&gt;\r\nBcc: x@example.com {{data.Secret}}",
        "&lt;SCRIPT&gt;ALERT(&quot;X&quot;)&lt;/SCRIPT&gt;\r\nBCC: X@EXAMPLE.COM {{DATA.SECRET}}",
        "&lt;b&gt;8:30")]
    [InlineData(TemplateValueEncoding.SingleLine,
        "<script>alert(\"x\")</script> Bcc: x@example.com {{data.Secret}}",
        "<SCRIPT>ALERT(\"X\")</SCRIPT> BCC: X@EXAMPLE.COM {{DATA.SECRET}}",
        "<b>8:30")]
    public void A_hostile_name_is_encoded_for_its_sink_like_any_other_value(
        TemplateValueEncoding encoding, string plain, string upper, string formatLiteral)
    {
        var context = Manila with
        {
            Author = new TemplatePerson(Hostile, Hostile),
            Transition = new TemplateTransition(Hostile, ClockedOutAt, new TemplatePerson(Hostile, Hostile)),
        };
        var entry = Entry(new() { ["Hostile"] = Hostile, ["Secret"] = "TOP-SECRET" });

        string In(string template) => TemplateVariableExtractor.Resolve(template, entry, encoding, context);

        var asAField = In("{{data.Hostile}}");
        asAField.Should().Be(plain);

        In("{{createdBy.name}}").Should().Be(asAField);
        In("{{createdBy.email}}").Should().Be(asAField);
        In("{{transition.name}}").Should().Be(asAField);
        In("{{transition.by.name}}").Should().Be(asAField);
        In("{{transition.by.email}}").Should().Be(asAField);
        In("{{createdBy.name | upper}}").Should().Be(upper);
        In("{{createdAt | date \"'<b>'h:mm\"}}").Should().Be(formatLiteral);

        In("<p>{{createdBy.name}}</p>").Should().NotContain("TOP-SECRET").And.StartWith("<p>").And.EndWith("</p>");
    }

    /// <summary>Red without the change: an erased entry has no dates, so a format on one gives nothing.</summary>
    [Fact]
    public void A_format_on_an_erased_entrys_date_resolves_to_nothing()
    {
        var erased = new barakoCMS.Features.Workflows.ErasedContent(Guid.NewGuid(), "timeEntry");

        Resolve("[{{createdAt | date \"h:mm tt\"}}][{{duration createdAt updatedAt}}][{{createdAt}}]", erased)
            .Should().Be("[][][]");
    }

    /// <summary>Red without the change. A null field is a known field with nothing in it.</summary>
    [Fact]
    public void A_format_on_an_empty_field_resolves_to_nothing()
    {
        var entry = Entry(new() { ["Empty"] = null!, ["Blank"] = "" });

        Resolve("[{{data.Empty | money}}][{{data.Blank | date}}][{{data.Empty | upper}}]", entry)
            .Should().Be("[][][]");
    }

    /// <summary>
    /// Red without the change. A site naming a zone the server does not know gives a null zone, and
    /// a date with no zone of its own is then left as written, where one naming its own still works.
    /// </summary>
    [Fact]
    public void A_date_is_left_as_written_when_the_sites_zone_is_not_known()
    {
        var context = Manila with { TimeZone = null };

        Resolve("{{createdAt | date \"h:mm tt\"}} {{createdAt | date \"h:mm tt\" \"UTC\"}}", context: context)
            .Should().Be("{{createdAt | date \"h:mm tt\"}} 12:30 AM");
    }

    /// <summary>
    /// Red when the site is read for the words alone. Only a bar followed by <c>date</c> or
    /// <c>money</c> reads it, also as JSON writes that bar inside a Conditional's branch.
    /// </summary>
    [Theory]
    [InlineData("The candidate was updated on a date to validate, money aside. {{status}}", false)]
    [InlineData("{{data.Mandate}} {{updatedAt}} | candidate | dated | moneyed", false)]
    [InlineData("{{createdAt | date \"h:mm\"}}", true)]
    [InlineData("{{data.Amount|money}}", true)]
    [InlineData("{{createdAt |\n\t date}}", true)]
    [InlineData("[{\"Body\":\"{{createdAt \\u007c date}}\"}]", true)]
    [InlineData("[{\"Body\":\"{{createdAt \\u007C\\n money}}\"}]", true)]
    public void The_site_is_read_only_for_a_date_or_money_format(string template, bool site)
    {
        var needs = TemplateExpression.Needs([template]);

        needs.Site.Should().Be(site);
        needs.Author.Should().BeFalse();
        needs.Transition.Should().BeFalse();
    }

    /// <summary>
    /// Red when the site is read for the words alone. The branch is serialised by the same
    /// serialiser a caller would use, so the test does not assume how it writes a bar or a quote.
    /// </summary>
    [Fact]
    public void A_format_inside_a_conditionals_branch_is_seen_however_the_json_wrote_it()
    {
        var branch = JsonSerializer.Serialize(new[]
        {
            new { Type = "Email", Parameters = new Dictionary<string, string> { ["Body"] = "{{createdAt | date \"h:mm tt\"}} {{createdBy.name}}" } },
        });

        var nested = JsonSerializer.Serialize(new[]
        {
            new { Type = "Conditional", Parameters = new Dictionary<string, string> { ["ThenActions"] = branch } },
        });

        TemplateExpression.Needs([branch]).Should().Be((true, true, false));
        TemplateExpression.Needs([nested]).Should().Be((true, true, false));
    }

    /// <summary>
    /// Red when the site is read for the words alone: the session refuses every call, so a prepare
    /// that reads anything throws. A workflow using none of the new placeholders makes no query.
    /// </summary>
    [Fact]
    public async Task Preparing_a_template_that_names_nothing_new_reads_nothing()
    {
        var session = new Mock<IDocumentSession>(MockBehavior.Strict);
        ITemplateVariableExtractor extractor = new TemplateVariableExtractor(session.Object);
        var entry = Entry();

        await extractor.PrepareAsync(
            entry, "Updated", 12,
            ["The candidate was updated on a date to validate, money aside. {{status}} {{data.Name}}", "https://example.com/update"],
            TestContext.Current.CancellationToken);

        session.Invocations.Should().BeEmpty();
        extractor.ResolveVariables("{{status}} {{data.Name}}", entry).Should().Be("Published Maria Santos");
    }

    /// <summary>What a template alone says the engine will leave as written. New with the change.</summary>
    [Fact]
    public void The_problems_of_a_template_name_each_hole_that_will_not_be_filled()
    {
        var problems = TemplateExpression.Problems(
            "{{id}} {{data.Anything}} {{createdAt | date \"h:mm tt\"}} {{createdBy.email}} "
          + "{{createdAt | dat \"h:mm\"}} {{creatdAt}} {{status | date}} {{createdAt | date \"h\" \"Mars/Phobos\"}} {{transition.name}}",
            onTransition: false).ToList();

        problems.Should().HaveCount(5);
        problems[0].Should().Be("'{{createdAt | dat \"h:mm\"}}' uses 'dat', which is not a format (the formats are date, money, upper, lower), so it is sent as written.");
        problems[1].Should().Be("'{{creatdAt}}' names 'creatdAt', which is not a placeholder, so it is sent as written.");
        problems[2].Should().Be("'{{status | date}}' formats 'status' as a date, which it is not, so it is sent as written.");
        problems[3].Should().Be("'{{createdAt | date \"h\" \"Mars/Phobos\"}}' names the time zone 'Mars/Phobos', which this server does not know, so it is sent as written.");
        problems[4].Should().Be("'{{transition.name}}' names 'transition.name', which is only filled when the trigger is a transition, so it is sent as written.");

        var onTransition = TemplateExpression.Problems("{{transition.name}} {{nope}}", onTransition: true).ToList();
        onTransition.Should().HaveCount(1);
        onTransition[0].Should().StartWith("'{{nope}}' names 'nope'");
    }
}
