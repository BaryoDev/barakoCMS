using barakoCMS.Infrastructure.Erasure;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace BarakoCMS.Tests.Features.Erasure;

public class ContentEraserSchemaTests
{
    [Fact]
    public async Task The_erase_statement_targets_the_configured_schema()
    {
        var id = Guid.NewGuid();
        var options = new StoreOptions { DatabaseSchemaName = "doc_schema" };
        options.Events.DatabaseSchemaName = "tenant_schema";
        var store = Substitute.For<IDocumentStore>();
        store.Options.Returns(options);

        var session = Substitute.For<IDocumentSession>();
        session.DocumentStore.Returns(store);
        session.LoadAsync<barakoCMS.Models.Content>(id, Arg.Any<CancellationToken>())
            .Returns(new barakoCMS.Models.Content { Id = id });

        var eraser = new ContentEraser(session, new ErasureOptions(), NullLogger<ContentEraser>.Instance);

        (await eraser.QueueEraseAsync(id, CancellationToken.None)).Should().BeTrue();

        var sql = session.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(IDocumentSession.QueueSqlCommand))
            .Select(c => (string)c.GetArguments()[0]!)
            .ToList();

        sql.Should().HaveCount(1);
        sql[0].Should().Contain("tenant_schema.mt_events");
        sql[0].Should().Contain("tenant_schema.mt_streams");
        sql[0].Should().NotContain("public.");
        sql[0].Should().NotContain("doc_schema");
    }
}
