using FluentAssertions;
using Marten;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using barakoCMS.Core.Interfaces;
using barakoCMS.Events;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using Xunit;

namespace BarakoCMS.Tests.Features.ContentApi;

/// <summary>
/// <c>EventSourcing:DocumentTypesAppend</c> decides whether a type that is not event sourced still
/// writes its changes to a stream.
/// </summary>
/// <remarks>
/// True by omission, which is what every deployment does today. Issue #331 asked for the other
/// behaviour and it is a flag rather than the new default, because a document type that stops
/// appending loses more than storage: <c>GET /api/contents/{id}/history</c> returns nothing for it,
/// the rollback endpoint has nothing to roll back to, and <c>WorkflowProjection</c> never sees a
/// ContentCreated or ContentUpdated for it, so its workflows stop firing.
///
/// The writer is built here from a real <see cref="IConfiguration"/> rather than from a bool, so the
/// key string itself is under test. A flag whose name is misspelt in the reader is a setting that
/// silently does nothing, and reading it back through configuration is what catches that.
/// </remarks>
[Collection("Sequential")]
public class DocumentTypeAppendFlagTests
{
    private readonly IntegrationTestFixture _fixture;

    public DocumentTypeAppendFlagTests(IntegrationTestFixture fixture) => _fixture = fixture;

    private static IConfiguration Config(string? value) => new ConfigurationBuilder()
        .AddInMemoryCollection(value is null
            ? new Dictionary<string, string?>()
            : new Dictionary<string, string?> { [ContentWriter.DocumentTypesAppendKey] = value })
        .Build();

    private static string NewTypeName() => "df" + Guid.NewGuid().ToString("N")[..10];

    /// <summary>Creates and then updates one entry, and reports how long its stream is.</summary>
    private async Task<long> StreamLengthAfterCreateAndUpdateAsync(string? flag, bool eventSourced)
    {
        var type = NewTypeName();
        var id = Guid.NewGuid();

        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var policy = scope.ServiceProvider.GetRequiredService<IContentSourcingPolicy>();

        await policy.DecideAsync(type, eventSourced, default);
        await session.SaveChangesAsync();

        var writer = new ContentWriter(session, policy, Config(flag));

        var content = await writer.CreateAsync(new ContentCreated(
            id, type,
            new Dictionary<string, object> { ["Title"] = "first" },
            ContentStatus.Draft, Guid.NewGuid(), "first", SensitivityLevel.Public), default);

        await session.SaveChangesAsync();

        await writer.AppendAsync(content, new ContentUpdated(
            id, new Dictionary<string, object> { ["Title"] = "second" }, Guid.NewGuid(), "second"), default);

        await session.SaveChangesAsync();

        // The document is the record either way, so it has to be right in both cases. Asserting only
        // the stream would pass against a writer that stopped writing anything at all.
        var stored = await session.LoadAsync<Content>(id);
        stored.Should().NotBeNull("the document is stored in both modes");
        stored!.Data["Title"].ToString().Should().Be("second");

        var state = await session.Events.FetchStreamStateAsync(id);
        return state?.Version ?? 0;
    }

    [Fact]
    public async Task A_document_type_appends_when_the_flag_is_absent()
    {
        var version = await StreamLengthAfterCreateAndUpdateAsync(flag: null, eventSourced: false);

        version.Should().Be(2,
            "omitting the setting has to leave every existing deployment appending exactly as it does today");
    }

    [Fact]
    public async Task A_document_type_appends_when_the_flag_is_on()
    {
        var version = await StreamLengthAfterCreateAndUpdateAsync(flag: "true", eventSourced: false);

        version.Should().Be(2);
    }

    [Fact]
    public async Task A_document_type_writes_no_stream_when_the_flag_is_off()
    {
        var version = await StreamLengthAfterCreateAndUpdateAsync(flag: "false", eventSourced: false);

        version.Should().Be(0, "the create and the update both went to the document only");
    }

    [Fact]
    public async Task An_event_sourced_type_appends_whatever_the_flag_says()
    {
        // The flag is about types whose document is the record. Letting it reach an event-sourced
        // type would leave that type with no record at all, which is the failure this pairing exists
        // to catch.
        var version = await StreamLengthAfterCreateAndUpdateAsync(flag: "false", eventSourced: true);

        version.Should().Be(2, "the stream is the record for this type and no setting may take it away");
    }
}
