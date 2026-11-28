using System.Net;
using System.Net.Http.Headers;
using BarakoCMS.Tests.Features.Tenants;
using barakoCMS.Features.EmailTemplates;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Xunit;

namespace BarakoCMS.Tests.Features.EmailTemplates;

/// <summary>
/// What a dataset must never reach: another tenant's rows, a log line, or an export.
/// </summary>
[Collection("Integration1")]
public class TemplateIsolationTests(IntegrationTestFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Two_tenants_with_the_same_template_and_query_name_each_render_only_their_own_rows()
    {
        var one = new InvoiceKit(fixture);
        var two = new InvoiceKit(fixture);
        await one.SeedAsync([new("Widget", 2, 10m, 20m)], customer: "Acme Ltd");
        await two.SeedAsync(
            [new("Gadget", 3, 4m, 12m), new("Gizmo", 1, 7m, 7m), new("Sprocket", 5, 1m, 5m)], customer: "Globex Ltd");

        var (first, firstSent) = await one.SendAsync();
        var (second, secondSent) = await two.SendAsync();

        first.Succeeded.Should().BeTrue(first.Error);
        second.Succeeded.Should().BeTrue(second.Error);
        InvoiceKit.TextOf(firstSent!.Body).Should().Contain("Subtotal 20.00").And.NotContain(l => l.Contains("Gadget"));
        firstSent.Body.Should().NotContain("Gadget").And.NotContain("Gizmo").And.NotContain("Globex");
        InvoiceKit.TextOf(secondSent!.Body).Should().Contain("Subtotal 24.00");
        secondSent.Body.Should().NotContain("Widget").And.NotContain("Acme").And.Contain("Sprocket");
    }

    [Fact]
    public async Task A_template_in_one_tenant_cannot_read_the_query_of_another()
    {
        var one = new InvoiceKit(fixture);
        var two = new InvoiceKit(fixture);
        await one.SeedAsync([new("Widget", 2, 10m, 20m)], querySlug: "only-in-one");

        // The second tenant names a saved query that only the first holds.
        await two.SeedAsync([new("Gadget", 3, 4m, 12m)], datasets: "{\"lines\":\"only-in-one\"}");
        var (result, sent) = await two.SendAsync();

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Contain("only-in-one").And.Contain("does not exist");
        sent.Should().BeNull();
    }

    private sealed class Capture : ILogger
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Lines.Add(formatter(state, exception) + (exception is null ? string.Empty : " " + exception));
    }

    private static readonly string[] Values = ["Zebra-9931", "Quillfeather", "INV-0007", "7771", "313.37", "customer@example.com"];

    [Fact]
    public async Task A_render_logs_no_field_value_when_it_succeeds_or_fails()
    {
        var log = new Capture();
        var good = new InvoiceKit(fixture);
        await good.SeedAsync([new("Zebra-9931", 7771, 313.37m, 313.37m)], customer: "Quillfeather Ltd");
        var (ok, _) = await good.SendAsync(log);
        ok.Succeeded.Should().BeTrue(ok.Error);

        var tooMany = new InvoiceKit(fixture);
        await tooMany.SeedAsync([.. Enumerable.Range(0, 201).Select(_ => new InvoiceKit.Line("Zebra-9931", 7771, 313.37m, 313.37m))], customer: "Quillfeather Ltd");
        var (failed, _) = await tooMany.SendAsync(log);
        failed.Succeeded.Should().BeFalse();

        var missing = new InvoiceKit(fixture);
        await missing.SeedAsync([new("Zebra-9931", 7771, 313.37m, 313.37m)], customer: "Quillfeather Ltd", datasets: "{\"lines\":\"gone\"}");
        var (none, _) = await missing.SendAsync(log);
        none.Succeeded.Should().BeFalse();

        log.Lines.Should().NotBeEmpty("the capture sees what the render logs");
        log.Lines.Should().Contain(l => l.Contains("read 1 rows"));
        foreach (var value in Values)
        {
            log.Lines.Should().NotContain(l => l.Contains(value), "no value of an entry goes to a log");
        }
    }

    [Fact]
    public async Task No_export_carries_a_dataset_row_or_a_rendered_template()
    {
        var slug = await TenantProfileSeed.TenantAsync(fixture, Ct);
        var kit = new InvoiceKit(fixture, slug);
        await kit.SeedAsync([new("Widget", 2, 10m, 20m), new("Gadget", 3, 4.125m, 12.375m)]);
        var (sent, _) = await kit.SendAsync();
        sent.Succeeded.Should().BeTrue(sent.Error);

        var platform = await fixture.CreateClientAsync("SuperAdmin");
        var admin = await TenantProfileSeed.AdminInAsync(fixture, slug, Ct);

        var bodies = new List<string>();
        foreach (var (client, url) in new[]
        {
            (platform, $"/api/tenants/{slug}/export"),
            (admin, "/api/config/export"),
            (admin, "/api/config/export?sections=contentTypes,contents"),
        })
        {
            var response = await client.GetAsync(url, Ct);
            response.StatusCode.Should().Be(HttpStatusCode.OK, url);
            bodies.Add(await response.Content.ReadAsStringAsync(Ct));
        }

        bodies[0].Should().Contain("sum(lines.Total)", "the tenant bundle holds the template as it was written");
        foreach (var body in bodies)
        {
            body.Should().NotContain("Subtotal 32.38").And.NotContain("Total 36.27").And.NotContain("Widget  2 x 10.00")
                .And.NotContain("INV-0007 for Acme", "a template is exported as written and never rendered");
            body.Should().NotContain("\"rows\"", "no dataset is read for an export");
        }
    }
}
