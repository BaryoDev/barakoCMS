using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using NSubstitute;
using Marten;
using barakoCMS.Features.Workflows;
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

    public WorkflowProjectionTests()
    {
        _serviceProvider = Substitute.For<IServiceProvider>();
        _scopeFactory = Substitute.For<IServiceScopeFactory>();
        _scope = Substitute.For<IServiceScope>();

        _serviceProvider.GetService(typeof(IServiceScopeFactory)).Returns(_scopeFactory);
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
        var updatedEvent = new barakoCMS.Events.ContentUpdated(contentId, new Dictionary<string, object>(), Guid.NewGuid());

        // Mock Event Envelope
        var eventEnvelope = Substitute.For<IEvent<barakoCMS.Events.ContentUpdated>>();
        eventEnvelope.Data.Returns(updatedEvent);
        eventEnvelope.StreamId.Returns(streamId);

        // Mock IWorkflowEngine
        var engine = Substitute.For<IWorkflowEngine>();
        _serviceProvider.GetService(typeof(IWorkflowEngine)).Returns(engine);

        // Mock Document Loading
        var content = new Content { Id = contentId, ContentType = "Article", Data = new Dictionary<string, object>() };
        _ops.LoadAsync<Content>(contentId, Arg.Any<CancellationToken>()).Returns(content);

        // Act
        await _sut.Project(eventEnvelope, _ops, CancellationToken.None);

        // Assert
        // Verify LoadAsync called on ops
        await _ops.Received(1).LoadAsync<Content>(contentId, Arg.Any<CancellationToken>());

        // Verify Engine called
        await engine.Received(1).ProcessEventAsync("Article", "Updated", content, Arg.Any<CancellationToken>());
    }
}
