using barakoCMS.Features.Workflows;
using barakoCMS.Models;
using FluentAssertions;
using JasperFx.Events;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using Xunit;

namespace BarakoCMS.Tests.Features.Workflows;

/// <summary>
/// The workflow daemon runs outside any request, so nothing has resolved a tenant for it. It has to
/// take the tenant from the event it is processing.
/// </summary>
/// <remarks>
/// Both halves of cross-tenant isolation were already proven separately: Marten's conjoined
/// partitioning, and token issuance across tenants. Neither covers the one session opened outside a
/// request, which is where the daemon lives and where the two failure modes below come from.
///
/// The projection is driven directly rather than through the async daemon. The daemon proves the
/// same thing on a timer, and a test that waits for a projection to catch up fails on timing rather
/// than on tenancy.
/// </remarks>
[Collection("Sequential")]
public class WorkflowTenantIsolationTests
{
    private readonly IntegrationTestFixture _fixture;

    public WorkflowTenantIsolationTests(IntegrationTestFixture fixture) => _fixture = fixture;

    /// <summary>
    /// A workflow stored under a tenant fires for that tenant's content.
    /// </summary>
    /// <remarks>
    /// The definition lives in the tenant partition, so a daemon querying the default partition
    /// finds nothing, returns, and logs nothing. The workflow does not fail; as far as the engine is
    /// concerned it does not exist.
    ///
    /// The assertion is on the field the action writes rather than on a log line, because "no
    /// workflow matched" and "the workflow ran" are otherwise indistinguishable from outside.
    /// </remarks>
    [Fact]
    public async Task A_tenant_workflow_fires_for_that_tenants_content()
    {
        const string tenant = "wf-isolation-fires";
        const string contentType = "wf-isolation-fires-article";
        var store = _fixture.Services.GetRequiredService<IDocumentStore>();
        var contentId = Guid.NewGuid();

        await using (var tenantSession = store.LightweightSession(tenant))
        {
            tenantSession.Store(StampingWorkflow(contentType));
            tenantSession.Store(new Content
            {
                Id = contentId,
                ContentType = contentType,
                Status = ContentStatus.Published,
                Data = new Dictionary<string, object>(),
            });
            await tenantSession.SaveChangesAsync();
        }

        await ProjectPublishedAsync(store, tenant, contentId);

        var updated = await DrainUntilAsync(store, tenant, contentId, c => c.Data.ContainsKey("Stamp"));

        updated.Data.Should().ContainKey("Stamp");
        updated.Data["Stamp"]!.ToString().Should().Be("fired");
    }

    /// <summary>
    /// Runs the queued work to completion, in this thread.
    /// </summary>
    /// <remarks>
    /// The projection queues and a background runner executes (#329), so asserting straight after
    /// projecting races the runner. Driven explicitly rather than slept on: a sleep long enough to
    /// be reliable is long enough to hide a runner that never claimed anything, and the same lesson
    /// is already recorded on MultiInstanceSchedulingTests about taking the lock rather than racing.
    /// </remarks>
    /// <summary>
    /// Drives the runner until the content satisfies <paramref name="done"/>, or fails on a deadline.
    /// </summary>
    /// <remarks>
    /// Driving it rather than sleeping, for the reason above. But "one pass claimed nothing" is not
    /// the same as "the work is finished": the host also runs the real <c>WorkflowRunner</c>, which
    /// polls every five seconds, so it can claim the attempt first and still be executing it when
    /// this returns. The earlier version stopped on the first pass that claimed nothing and asserted
    /// immediately, which failed in CI at 201ms with the content untouched.
    ///
    /// So the exit condition is the outcome rather than the queue being empty, and the deadline is
    /// what turns a runner that never claims anything into a failure instead of a hang.
    /// </remarks>
    private async Task<Content> DrainUntilAsync(
        IDocumentStore store, string tenant, Guid contentId, Func<Content, bool> done)
    {
        var runner = _fixture.Services.GetServices<IHostedService>()
            .OfType<barakoCMS.Features.Workflows.WorkflowRunner>()
            .Single();

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);

        while (true)
        {
            // Each pass claims at most one attempt, so a run with several actions needs several.
            await runner.RunOnceAsync(TestContext.Current.CancellationToken);

            await using var session = store.QuerySession(tenant);
            var content = await session.LoadAsync<Content>(contentId, TestContext.Current.CancellationToken);

            if (content is not null && done(content))
            {
                return content;
            }

            if (DateTime.UtcNow > deadline)
            {
                content.Should().NotBeNull("the content the workflow should have stamped is gone");
                throw new Xunit.Sdk.XunitException(
                    "Timed out after 30s waiting for the workflow to stamp the content. "
                  + $"Data holds: {string.Join(", ", content!.Data.Keys)}");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(200), TestContext.Current.CancellationToken);
        }
    }

    /// <summary>
    /// A default-tenant workflow does not reach into a tenant's event, and its action does not write
    /// the tenant's document into the default partition.
    /// </summary>
    /// <remarks>
    /// This is why the issue is tagged isolation rather than correctness, and it needs the workflow
    /// on the *other* side of the boundary from the content. With the scope on the default tenant
    /// the engine finds this default-partition definition, runs it against the tenant's content, and
    /// <c>UpdateFieldAction</c> stores that document through the default-partition session: one
    /// tenant's row written outside its boundary, over whatever default-partition document shares
    /// the id.
    ///
    /// The decoy in the default partition carries the same id and a different marker, so the clobber
    /// shows up as a changed value rather than as an extra row that could be explained away by test
    /// ordering. Asserting only "the tenant's workflow did not run" would pass on the broken code,
    /// because a tenant definition is invisible from the default partition either way.
    /// </remarks>
    [Fact]
    public async Task A_default_tenant_workflow_does_not_write_into_the_default_partition()
    {
        const string tenant = "wf-isolation-partition";
        const string contentType = "wf-isolation-partition-article";
        var store = _fixture.Services.GetRequiredService<IDocumentStore>();
        var contentId = Guid.NewGuid();

        await using (var platform = store.LightweightSession())
        {
            platform.Store(StampingWorkflow(contentType));
            platform.Store(new Content
            {
                Id = contentId,
                ContentType = contentType,
                Status = ContentStatus.Draft,
                Data = new Dictionary<string, object> { ["Stamp"] = "untouched" },
            });
            await platform.SaveChangesAsync();
        }

        await using (var tenantSession = store.LightweightSession(tenant))
        {
            tenantSession.Store(new Content
            {
                Id = contentId,
                ContentType = contentType,
                Status = ContentStatus.Published,
                Data = new Dictionary<string, object>(),
            });
            await tenantSession.SaveChangesAsync();
        }

        await ProjectPublishedAsync(store, tenant, contentId);

        await using var platformCheck = store.QuerySession();
        var platformCopy = await platformCheck.LoadAsync<Content>(contentId);
        platformCopy!.Data["Stamp"]!.ToString().Should().Be("untouched");
    }

    private static WorkflowDefinition StampingWorkflow(string contentType) => new()
    {
        Id = Guid.NewGuid(),
        Name = "stamp on publish",
        TriggerContentType = contentType,
        TriggerEvent = WorkflowEvents.Published,
        Actions =
        [
            new WorkflowAction
            {
                Type = "UpdateField",
                Parameters = new Dictionary<string, string>
                {
                    ["Field"] = "data.Stamp",
                    ["Value"] = "fired",
                },
            },
        ],
    };

    /// <summary>
    /// Drives the projection for a Published transition exactly as the daemon would, for one tenant.
    /// </summary>
    private async Task ProjectPublishedAsync(IDocumentStore store, string tenant, Guid contentId)
    {
        var projection = new WorkflowProjection(_fixture.Services);

        var envelope = Substitute.For<IEvent<barakoCMS.Events.ContentStatusChanged>>();
        envelope.Data.Returns(new barakoCMS.Events.ContentStatusChanged(
            contentId, ContentStatus.Published, Guid.NewGuid()));
        envelope.TenantId.Returns(tenant);

        await using var ops = store.LightweightSession(tenant);
        await projection.Project(envelope, ops, CancellationToken.None);
    }
}
