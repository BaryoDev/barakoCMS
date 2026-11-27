using barakoCMS.Infrastructure.Connectors;
using barakoCMS.Models;
using Marten;
using Microsoft.Extensions.DependencyInjection;

namespace BarakoCMS.Tests.Features.Connectors;

/// <summary>A type, an entry of it, a connector and request definitions, in the default tenant, for the request tests that compose.</summary>
internal static class RequestKit
{
    public sealed record Subject(string Type, Guid EntryId, string ConnectorSlug);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static async Task<Subject> SeedAsync(
        IntegrationTestFixture fixture, IEnumerable<FieldDefinition> fields, Dictionary<string, object> data)
    {
        var type = "rk" + Guid.NewGuid().ToString("n")[..10];
        var connectorSlug = "rkc" + Guid.NewGuid().ToString("n")[..10];
        var entryId = Guid.NewGuid();

        using var scope = fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();

        session.Store(new ContentTypeDefinition { Id = Guid.NewGuid(), Name = type, DisplayName = type, Fields = [.. fields] });
        session.Store(new Connector
        {
            Id = Guid.NewGuid(),
            Name = "Request kit",
            Slug = connectorSlug,
            BaseUrl = "https://egress.example",
            Auth = ConnectorAuth.None,
        });
        session.Store(new Content { Id = entryId, ContentType = type, Status = ContentStatus.Published, Data = data });

        await session.SaveChangesAsync(Ct);
        return new Subject(type, entryId, connectorSlug);
    }

    public static RequestDefinition Definition(Subject subject, string? body, string path = "/in") => new()
    {
        Id = Guid.NewGuid(),
        Name = "Request kit",
        Slug = "rkq" + Guid.NewGuid().ToString("n")[..10],
        ConnectorSlug = subject.ConnectorSlug,
        Method = "POST",
        PathTemplate = path,
        BodyTemplate = body,
    };

    public static async Task<ComposedRequest> ComposeAsync(IntegrationTestFixture fixture, Subject subject, RequestDefinition definition)
    {
        using var scope = fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        var connector = await session.Query<Connector>().FirstAsync(c => c.Slug == subject.ConnectorSlug, Ct);
        var entry = await session.LoadAsync<Content>(subject.EntryId, Ct);

        return await scope.ServiceProvider.GetRequiredService<IRequestComposer>()
            .ComposeAsync(definition, connector, entry!, idempotencyKey: null, Ct);
    }
}
