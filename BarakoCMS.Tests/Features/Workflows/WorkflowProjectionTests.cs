using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Xunit;
using NSubstitute;
using Marten;
using barakoCMS.Features.Workflows;
using barakoCMS.Infrastructure.Multitenancy;
using barakoCMS.Models;
using barakoCMS.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using JasperFx.Events;

namespace BarakoCMS.Tests.Features.Workflows;

public class WorkflowProjectionTests
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IServiceScope _scope;
    private readonly IDocumentOperations _ops;
    private readonly WorkflowProjection _sut;
    private readonly IDocumentSession _session;
    private readonly TenantContext _tenantContext = new();

    public WorkflowProjectionTests()
    {
        _serviceProvider = Substitute.For<IServiceProvider>();
        _scopeFactory = Substitute.For<IServiceScopeFactory>();
        _scope = Substitute.For<IServiceScope>();

        _serviceProvider.GetService(typeof(IServiceScopeFactory)).Returns(_scopeFactory);
        _serviceProvider.GetService(typeof(TenantContext)).Returns(_tenantContext);
        _scopeFactory.CreateScope().Returns(_scope);
        _scope.ServiceProvider.Returns(_serviceProvider);

        _ops = Substitute.For<IDocumentOperations>();
        _session = Substitute.For<IDocumentSession>();

        _sut = new WorkflowProjection(_serviceProvider);
    }

    [Fact]
    public async Task Project_ContentUpdated_ShouldTriggerWorkflow()
    {
        // Arrange
        var contentId = Guid.NewGuid();
        var streamId = Guid.NewGuid();
        var updatedEvent = new barakoCMS.Events.ContentUpdated(contentId, new Dictionary<string, object>(), Guid.NewGuid(), String.Empty);

        // Mock Event Envelope
        var eventEnvelope = Substitute.For<IEvent<barakoCMS.Events.ContentUpdated>>();
        eventEnvelope.Data.Returns(updatedEvent);
        eventEnvelope.StreamId.Returns(streamId);
        eventEnvelope.TenantId.Returns(JasperFx.StorageConstants.DefaultTenantId);

        // The projection queues rather than executes (#329), so the collaborator it reaches for is
        // the queue. Asserting on the engine here would keep passing against a projection that had
        // stopped doing anything at all, since nothing would call the mock either way.
        var queue = Substitute.For<IWorkflowRunQueue>();
        _serviceProvider.GetService(typeof(IWorkflowRunQueue)).Returns(queue);

        // Mock Document Loading
        var content = new Content { Id = contentId, ContentType = "Article", Data = new Dictionary<string, object>() };
        _ops.LoadAsync<Content>(contentId, Arg.Any<CancellationToken>()).Returns(content);

        // Act
        await _sut.Project(eventEnvelope, _ops, CancellationToken.None);

        // Assert
        // Verify LoadAsync called on ops
        await _ops.Received(1).LoadAsync<Content>(contentId, Arg.Any<CancellationToken>());

        // Verify Engine called
        await queue.Received(1).EnqueueAsync(Arg.Is<Content>(c => c.Id == contentId), "Updated", Arg.Any<long>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// One update starts one run of the workflow it matches, even when the daemon reads an earlier
    /// event of the same entry after the update has landed.
    /// </summary>
    /// <remarks>
    /// The case found in a shop: a workflow's UpdateField set StockEvent to Done, and before the
    /// daemon got to that event a person saved StockEvent Waste. The daemon then read both events
    /// against the document as it stood, which said Waste both times, so a workflow conditioned on
    /// Waste was queued twice for one save and took the stock twice. Each event is matched against
    /// the data it wrote.
    /// </remarks>
    [Fact]
    public async Task An_earlier_update_read_late_does_not_match_against_the_data_of_a_later_one()
    {
        var contentId = Guid.NewGuid();
        var queue = new MatchingQueue(new WorkflowDefinition
        {
            Id = Guid.NewGuid(),
            Name = "waste-stock",
            TriggerContentType = "Shipment",
            TriggerEvent = WorkflowEvents.Updated,
            Conditions = new Dictionary<string, string> { ["StockEvent"] = "Waste" },
        });
        _serviceProvider.GetService(typeof(IWorkflowRunQueue)).Returns(queue);
        _ops.LoadAsync<Content>(contentId, Arg.Any<CancellationToken>()).Returns(new Content
        {
            Id = contentId, ContentType = "Shipment", Data = new Dictionary<string, object> { ["StockEvent"] = "Waste" },
        });

        await _sut.Project(UpdatedEvent(contentId, 10, new() { ["StockEvent"] = "Done" }), _ops, CancellationToken.None);
        await _sut.Project(UpdatedEvent(contentId, 11, new() { ["StockEvent"] = "Waste" }), _ops, CancellationToken.None);

        queue.Runs.Should().Equal([11L], "only the save that wrote Waste matches");
    }

    [Fact]
    public async Task A_create_read_after_a_later_update_matches_against_the_data_it_was_created_with()
    {
        var contentId = Guid.NewGuid();
        var queue = new MatchingQueue(new WorkflowDefinition
        {
            Id = Guid.NewGuid(),
            Name = "on-create",
            TriggerContentType = "Shipment",
            TriggerEvent = WorkflowEvents.Created,
            Conditions = new Dictionary<string, string> { ["Kind"] = "Urgent" },
        });
        _serviceProvider.GetService(typeof(IWorkflowRunQueue)).Returns(queue);
        _ops.LoadAsync<Content>(contentId, Arg.Any<CancellationToken>()).Returns(new Content
        {
            Id = contentId, ContentType = "Shipment", Data = new Dictionary<string, object> { ["Kind"] = "Urgent" },
        });

        var created = Substitute.For<IEvent<barakoCMS.Events.ContentCreated>>();
        created.Data.Returns(new barakoCMS.Events.ContentCreated(
            contentId, "Shipment", new Dictionary<string, object> { ["Kind"] = "Routine" }, ContentStatus.Draft,
            Guid.NewGuid(), null, SensitivityLevel.Public, DateTime.UtcNow));
        created.Sequence.Returns(5L);
        created.TenantId.Returns(JasperFx.StorageConstants.DefaultTenantId);

        await _sut.Project(created, _ops, CancellationToken.None);

        queue.Runs.Should().BeEmpty("the entry was created Routine and only later edited to Urgent");
    }

    private static IEvent<barakoCMS.Events.ContentUpdated> UpdatedEvent(Guid id, long sequence, Dictionary<string, object> data)
    {
        var e = Substitute.For<IEvent<barakoCMS.Events.ContentUpdated>>();
        e.Data.Returns(new barakoCMS.Events.ContentUpdated(id, data, Guid.NewGuid(), null, DateTime.UtcNow));
        e.Sequence.Returns(sequence);
        e.TenantId.Returns(JasperFx.StorageConstants.DefaultTenantId);
        return e;
    }

    /// <summary>Queues a run of one workflow per event its conditions match, as the real queue decides.</summary>
    private sealed class MatchingQueue(WorkflowDefinition workflow) : IWorkflowRunQueue
    {
        public List<long> Runs { get; } = [];

        public Task<int> EnqueueAsync(Content content, string eventType, long eventSequence, CancellationToken ct)
        {
            if (!WorkflowTriggers.Events(workflow).Contains(eventType)
                || !WorkflowConditions.Matches(workflow, content)
                || Runs.Contains(eventSequence))
            {
                return Task.FromResult(0);
            }

            Runs.Add(eventSequence);
            return Task.FromResult(1);
        }

        public Task<int> QueueDeletedAsync(Guid contentId, string contentType, CancellationToken ct) => Task.FromResult(0);

        public Task<int> FireAsync(Content content, string trigger, string? triggerKey, CancellationToken ct) => Task.FromResult(0);

        public Task<bool> EnqueueScheduledAsync(WorkflowDefinition workflow, Content content, string triggerKey, CancellationToken ct) =>
            Task.FromResult(false);

        public WorkflowRun StageManual(WorkflowDefinition workflow, Content content, Guid? runId, Guid? startedBy) =>
            throw new NotSupportedException();

        public Task<bool> ListensAsync(string contentType, string eventType, CancellationToken ct) => Task.FromResult(true);
    }

    /// <summary>
    /// The scope the engine is resolved from carries the event's tenant.
    /// </summary>
    /// <remarks>
    /// Everything downstream reads the tenant off <see cref="TenantContext"/>: the session factory
    /// picks the partition from it, and the workflow actions write through that session. If it is
    /// still on the default slug here, a tenant's workflows are invisible and a workflow's writes
    /// leave the tenant. Asserting on the context is the narrowest place that break is visible.
    /// </remarks>
    [Theory]
    [InlineData("acme", "acme")]
    [InlineData(JasperFx.StorageConstants.DefaultTenantId, barakoCMS.Models.Tenant.DefaultSlug)]
    public async Task Project_carries_the_events_tenant_into_the_scope(string eventTenantId, string expectedSlug)
    {
        var contentId = Guid.NewGuid();
        var eventEnvelope = Substitute.For<IEvent<barakoCMS.Events.ContentUpdated>>();
        eventEnvelope.Data.Returns(new barakoCMS.Events.ContentUpdated(
            contentId, new Dictionary<string, object>(), Guid.NewGuid(), String.Empty));
        eventEnvelope.TenantId.Returns(eventTenantId);

        var queue = Substitute.For<IWorkflowRunQueue>();
        _serviceProvider.GetService(typeof(IWorkflowRunQueue)).Returns(queue);
        _ops.LoadAsync<Content>(contentId, Arg.Any<CancellationToken>())
            .Returns(new Content { Id = contentId, ContentType = "Article" });

        await _sut.Project(eventEnvelope, _ops, CancellationToken.None);

        _tenantContext.Slug.Should().Be(expectedSlug);
    }
}
