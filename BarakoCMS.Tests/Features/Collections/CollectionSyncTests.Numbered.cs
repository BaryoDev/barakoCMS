using System.Net;
using System.Net.Http.Json;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BarakoCMS.Tests.Features.Collections;

public partial class CollectionSyncTests
{
    /// <summary>
    /// The entry's number is written once, on create. A later source row that sends a different
    /// one is skipped, and the stored number stays.
    /// </summary>
    [Fact]
    public async Task A_source_row_that_changes_the_entry_number_is_skipped()
    {
        var ct = TestContext.Current.CancellationToken;
        var number = "N-1";
        var setup = await ArrangeAsync(
            () => (HttpStatusCode.OK, $$"""{ "data": [ { "id": "a", "no": "{{number}}" } ] }"""),
            save: false,
            fields:
            [
                new FieldDefinition { Name = "packageId", Type = "string" },
                new FieldDefinition { Name = "invoiceNo", Type = "string" },
            ]);

        using (var scope = _factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            var type = await session.Query<ContentTypeDefinition>().FirstAsync(t => t.Name == setup.Type, ct);
            type.Numbering = new NumberingRule { Field = "invoiceNo", Prefix = "INV-", Padding = 4 };
            session.Store(type);
            await session.SaveChangesAsync(ct);
        }

        var body = SyncBody(setup);
        body["fieldMap"] = new Dictionary<string, string> { ["packageId"] = "id", ["invoiceNo"] = "no" };
        var saved = await (await AdminAsync()).PostAsJsonAsync("/api/collection-syncs", body, ct);
        saved.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}", saved.StatusCode, await saved.Content.ReadAsStringAsync(ct));

        (await RunAsync(setup)).GetProperty("created").GetInt32().Should().Be(1);
        var stored = await StoredInvoiceNoAsync(setup.Type);

        number = "N-2";
        var second = await RunAsync(setup);

        second.GetProperty("succeeded").GetBoolean().Should().BeFalse("the only row was skipped");
        second.GetProperty("error").GetString().Should().Contain("numbered by the server");
        (await StoredInvoiceNoAsync(setup.Type)).Should().Be(stored);
    }

    private async Task<string?> StoredInvoiceNoAsync(string type)
    {
        using var scope = _factory.Services.CreateScope();
        var query = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        var entry = await query.Query<Content>().SingleAsync(c => c.ContentType == type, TestContext.Current.CancellationToken);
        return entry.Data.FirstOrDefault(p => p.Key == "invoiceNo").Value?.ToString();
    }
}
