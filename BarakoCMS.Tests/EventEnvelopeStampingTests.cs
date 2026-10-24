using barakoCMS.Core.Interfaces;
using barakoCMS.Events;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// Content events carry the branch they were written for, the node that wrote them and that node's
/// gapless origin sequence, and the document keeps the branch through a rebuild.
/// </summary>
[Collection("Integration1")]
public class EventEnvelopeStampingTests
{
    private const string Type = "envelope-probe";

    private readonly IntegrationTestFixture _factory;

    public EventEnvelopeStampingTests(IntegrationTestFixture factory) => _factory = factory;

    private WebApplicationFactory<Program> Node(Guid node) =>
        _factory.WithSettings(new Dictionary<string, string?> { ["Barako:NodeId"] = node.ToString() });

    private static ContentCreated Created(Guid id) => new(
        id, Type, new Dictionary<string, object> { ["Title"] = "probe" }, ContentStatus.Draft,
        Guid.NewGuid(), null, SensitivityLevel.Public, DateTime.UtcNow);

    private static async Task CreateAsync(IServiceProvider services, Guid id, Guid? branch = null)
    {
        using var scope = services.CreateScope();
        using var _ = scope.ServiceProvider.GetRequiredService<IEventEnvelopeContext>().Use(branch);
        await scope.ServiceProvider.GetRequiredService<IContentWriter>().CreateAsync(Created(id), CancellationToken.None);
        await scope.ServiceProvider.GetRequiredService<IDocumentSession>().SaveChangesAsync();
    }

    private static async Task<IReadOnlyList<JasperFx.Events.IEvent>> StreamAsync(IServiceProvider services, Guid id)
    {
        using var scope = services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IQuerySession>().Events.FetchStreamAsync(id);
    }

    private static async Task<long?> SequenceOfAsync(IServiceProvider services, Guid id) =>
        Sequence((await StreamAsync(services, id)).Single());

    private static long? Sequence(JasperFx.Events.IEvent e) =>
        e.GetHeader(EnvelopeHeaders.OriginSequence)?.ToString() is { } text ? long.Parse(text) : null;

    [Fact]
    public async Task A_created_entry_carries_branch_node_and_sequence_headers()
    {
        var node = Guid.NewGuid();
        var branch = Guid.NewGuid();
        var host = Node(node);
        var id = Guid.NewGuid();

        await CreateAsync(host.Services, id, branch);

        var e = (await StreamAsync(host.Services, id)).Single();
        e.GetHeader(EnvelopeHeaders.BranchId)?.ToString().Should().Be(branch.ToString());
        e.GetHeader(EnvelopeHeaders.OriginNodeId)?.ToString().Should().Be(node.ToString());
        Sequence(e).Should().Be(1, "a new node's series starts at one");

        using var scope = host.Services.CreateScope();
        var stored = await scope.ServiceProvider.GetRequiredService<IQuerySession>().LoadAsync<Content>(id);
        stored!.BranchId.Should().Be(branch);
    }

    [Fact]
    public async Task Two_events_in_one_save_get_consecutive_numbers()
    {
        var host = Node(Guid.NewGuid());
        var first = Guid.NewGuid();
        await CreateAsync(host.Services, first);

        var second = Guid.NewGuid();
        using (var scope = host.Services.CreateScope())
        {
            var writer = scope.ServiceProvider.GetRequiredService<IContentWriter>();
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            var content = await session.LoadAsync<Content>(first);
            await writer.AppendOptimisticAsync(content!, [new ContentUpdated(
                first, new Dictionary<string, object> { ["Title"] = "again" }, Guid.NewGuid(), null, DateTime.UtcNow)],
                CancellationToken.None);
            await writer.CreateAsync(Created(second), CancellationToken.None);
            await session.SaveChangesAsync();
        }

        var firstStream = await StreamAsync(host.Services, first);
        var numbers = new[] { Sequence(firstStream[0]), Sequence(firstStream[1]), await SequenceOfAsync(host.Services, second) };
        numbers.Should().Equal(1L, 2L, 3L);
    }

    [Fact]
    public async Task A_save_that_fails_leaves_no_gap()
    {
        var host = Node(Guid.NewGuid());
        var a = Guid.NewGuid();
        await CreateAsync(host.Services, a);

        var act = async () =>
        {
            using var scope = host.Services.CreateScope();
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            await scope.ServiceProvider.GetRequiredService<IContentWriter>().CreateAsync(Created(Guid.NewGuid()), CancellationToken.None);
            session.Events.StartStream<Content>(a, Created(a));
            await session.SaveChangesAsync();
        };
        await act.Should().ThrowAsync<Exception>("the second stream reuses an id that exists");

        var c = Guid.NewGuid();
        await CreateAsync(host.Services, c);
        (await SequenceOfAsync(host.Services, c)).Should().Be(await SequenceOfAsync(host.Services, a) + 1,
            "the rolled back save must not burn a number");
    }

    [Fact]
    public async Task Concurrent_saves_get_distinct_numbers()
    {
        var host = Node(Guid.NewGuid());
        var ids = Enumerable.Range(0, 8).Select(_ => Guid.NewGuid()).ToList();

        await Task.WhenAll(ids.Select(id => CreateAsync(host.Services, id)));

        var numbers = new List<long>();
        foreach (var id in ids) numbers.Add((await SequenceOfAsync(host.Services, id))!.Value);
        numbers.Order().Should().Equal(Enumerable.Range(1, 8).Select(n => (long)n));
    }

    [Fact]
    public async Task Rebuilding_the_projection_keeps_BranchId()
    {
        var branch = Guid.NewGuid();
        var id = Guid.NewGuid();
        await CreateAsync(_factory.Services, id, branch);

        ContentProjection.Fold(await StreamAsync(_factory.Services, id))!.BranchId.Should().Be(branch);
    }

    [Fact]
    public async Task Without_a_node_id_nothing_is_stamped()
    {
        var id = Guid.NewGuid();
        await CreateAsync(_factory.Services, id);

        var e = (await StreamAsync(_factory.Services, id)).Single();
        e.GetHeader(EnvelopeHeaders.OriginSequence).Should().BeNull();
        e.GetHeader(EnvelopeHeaders.BranchId).Should().BeNull();

        using var scope = _factory.Services.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<IQuerySession>().LoadAsync<Content>(id))!.BranchId.Should().BeNull();
    }
}
