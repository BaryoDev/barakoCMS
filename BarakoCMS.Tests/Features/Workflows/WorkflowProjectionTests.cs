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
        await queue.Received(1).EnqueueAsync(content, "Updated", Arg.Any<long>(), Arg.Any<CancellationToken>());
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
