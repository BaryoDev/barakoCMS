using System.Net;
using System.Text.RegularExpressions;
using BarakoCMS.Tests.Features.Connectors;
using barakoCMS.Core.Interfaces;
using barakoCMS.Features.EmailTemplates;
using barakoCMS.Features.Workflows;
using barakoCMS.Features.Workflows.Actions;
using barakoCMS.Infrastructure.Connectors;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using static BarakoCMS.Tests.Features.Connectors.QueryKit;

namespace BarakoCMS.Tests.Features.EmailTemplates;

/// <summary>
/// The invoice the templates issue builds by configuration: a customer, products, an invoice and its
/// lines in a tenant of their own, a saved query for the lines, a template that names it, and a
/// send of that template as the invoice's author.
/// </summary>
internal sealed class InvoiceKit(IntegrationTestFixture fixture, string? tenant = null)
{
    /// <summary>The template of the issue, character for character.</summary>
    public const string Template = """
        Invoice {{data.Number}} for {{data.Customer.Name}}
        {{#each lines}}{{row.Product.Name}}  {{row.Qty}} x {{row.Price | money}} = {{row.Total | money}}
        {{/each}}
        Subtotal {{= sum(lines.Total) | money}}
        Tax {{= round(sum(lines.Total) * data.TaxRate, 2) | money}}
        Total {{= round(sum(lines.Total) * (1 + data.TaxRate), 2) | money}}
        """;

    public const string LinesQuery =
        "from invoiceLine where Invoice = @id and status != Archived order by createdAt select Product.Name, Qty, Price, Total limit 200";

    public const string QuerySlug = "invoice-lines";

    public sealed record Line(string Product, int Qty, decimal Price, decimal Total, ContentStatus Status = ContentStatus.Published);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public string Tenant { get; } = tenant ?? QueryKit.Tenant();

    public IDocumentStore Store => fixture.Services.GetRequiredService<IDocumentStore>();

    public Guid Reader { get; private set; }

    public Guid InvoiceId { get; private set; }

    public string TemplateSlug { get; } = "invoice-" + Guid.NewGuid().ToString("n")[..10];

    private static FieldDefinition Reference(string name, string type) =>
        new() { Name = name, DisplayName = name, Type = "reference", ReferenceType = type };

    /// <summary>The types, the entries, the saved query, the template and a reader of all of it.</summary>
    public async Task SeedAsync(
        IReadOnlyList<Line> lines, string customer = "Acme Ltd", decimal taxRate = 0.12m, string template = Template,
        string querySlug = QuerySlug, string? datasets = null)
    {
        await using (var first = Store.LightweightSession(Tenant))
        {
            await EmailTemplateData.EnsureTypesAsync(fixture, first, Ct);
        }

        var customerId = Guid.NewGuid();
        InvoiceId = Guid.NewGuid();
        var start = DateTime.UtcNow.AddHours(-1);

        var entries = new List<Content>
        {
            Entry("customer", new() { ["Name"] = customer }, id: customerId),
            Entry(
                "invoice",
                new() { ["Number"] = "INV-0007", ["Date"] = "2026-11-27", ["TaxRate"] = taxRate, ["Customer"] = customerId.ToString() },
                id: InvoiceId),
        };

        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            var product = Entry("product", new() { ["Name"] = line.Product });
            entries.Add(product);
            entries.Add(Entry(
                "invoiceLine",
                new()
                {
                    ["Invoice"] = InvoiceId.ToString(), ["Product"] = product.Id.ToString(),
                    ["Qty"] = line.Qty, ["Price"] = line.Price, ["Total"] = line.Total,
                },
                createdAt: start.AddSeconds(i), status: line.Status));
        }

        var role = new Role
        {
            Id = Guid.NewGuid(),
            Name = "ir" + Guid.NewGuid().ToString("n")[..10],
            Permissions = [.. new[] { "customer", "product", "invoice", "invoiceLine" }.Select(t => new ContentTypePermission
            {
                ContentTypeSlug = t, Read = new PermissionRule { Enabled = true },
            })],
        };
        var user = new User { Id = Guid.NewGuid(), Username = role.Name, Email = role.Name + "@example.com", RoleIds = [role.Id] };
        Reader = user.Id;

        var parsed = QueryText.Parse(LinesQuery);
        parsed.Ok.Should().BeTrue(parsed.Error);
        var query = parsed.Definition!;
        query.Id = Guid.NewGuid();
        query.Name = "Invoice lines";
        query.Slug = querySlug;

        var emailTemplate = EmailTemplateData.Template(
            "Invoice {{data.Number}}", template, slug: TemplateSlug,
            datasets: datasets ?? $"{{\"lines\":\"{querySlug}\"}}");

        await using (var session = Store.LightweightSession(Tenant))
        {
            session.Store(
                Type("customer", Field("Name")),
                Type("product", Field("Name")),
                Type(
                    "invoice", Reference("Customer", "customer"), Field("Number"), Field("Date", "date"), Field("TaxRate", "decimal")),
                Type(
                    "invoiceLine", Reference("Invoice", "invoice"), Reference("Product", "product"),
                    Field("Qty", "int"), Field("Price", "decimal"), Field("Total", "decimal")));
            session.Store(query);
            session.Store(emailTemplate);
            session.Store(entries.ToArray());
            await session.SaveChangesAsync(Ct);
        }

        foreach (var session in new[] { Store.LightweightSession(Tenant), Store.LightweightSession() })
        {
            await using var _ = session;
            session.Store(role);
            session.Store(user);
            await session.SaveChangesAsync(Ct);
        }

        // The entry the template is sent for is the invoice, made by the reader, so the run reads as them.
        await using var author = Store.LightweightSession(Tenant);
        var invoice = (await author.LoadAsync<Content>(InvoiceId, Ct))!;
        invoice.CreatedBy = user.Id;
        invoice.LastModifiedBy = user.Id;
        author.Store(invoice);
        await author.SaveChangesAsync(Ct);
    }

    /// <summary>The email the Email action sends for the template, or why it did not.</summary>
    public async Task<(WorkflowActionResult Result, RecordingEmailService.Sent? Sent)> SendAsync(
        ILogger? log = null, Guid? asActor = null, string? template = null)
    {
        await using var session = Store.LightweightSession(Tenant);
        using var scope = fixture.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var permissions = sp.GetRequiredService<IPermissionResolver>();
        var sensitivity = sp.GetRequiredService<ISensitivityService>();

        var entry = (await session.LoadAsync<Content>(InvoiceId, Ct))!;
        if (asActor is { } actor)
        {
            entry.CreatedBy = actor;
            entry.LastModifiedBy = actor;
        }

        var extractor = new TemplateVariableExtractor(session, permissions, sensitivity, null)
        {
            Queries = new QueryRunner(session, permissions, sensitivity),
            Log = log,
        };
        await extractor.PrepareAsync(entry, WorkflowEvents.Created, 0, [], Ct);

        var recorder = new RecordingEmailService();
        var action = new EmailAction(recorder, NullLogger<EmailAction>.Instance, session: session, extractor: extractor);
        var result = await action.RunAsync(
            new() { ["To"] = "customer@example.com", ["Template"] = template ?? TemplateSlug }, entry, Ct);

        return (result, recorder.Messages.FirstOrDefault());
    }

    /// <summary>
    /// The text of the lines from the one starting with <paramref name="first"/> to the one starting with <paramref name="last"/>.
    /// The layout's head (its style rule) is dropped first, since it shares a line with the body's first line.
    /// </summary>
    public static IReadOnlyList<string> TextOf(string html, string first = "Invoice ", string last = "Total ")
    {
        var body = Regex.Replace(html, "<head>.*?</head>", string.Empty, RegexOptions.Singleline);
        var text = WebUtility.HtmlDecode(Regex.Replace(body, "<[^>]*>", string.Empty));
        var lines = text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        var from = lines.FindIndex(l => l.StartsWith(first, StringComparison.Ordinal));
        var to = lines.FindLastIndex(l => l.StartsWith(last, StringComparison.Ordinal));
        return from < 0 || to < from ? lines : lines.GetRange(from, to - from + 1);
    }
}
