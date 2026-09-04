using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using barakoCMS.Models;
using BarakoCMS.Tests.Jobs;
using FastEndpoints;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// The question issue #106 asked first: does a job queued inside a request share the request's
/// transaction? The endpoints under <c>/api/_test/jobs</c> exist only in this test host.
/// </summary>
[Collection("Sequential")]
public class TransactionalEnqueueTests
{
    private readonly IntegrationTestFixture _factory;

    public TransactionalEnqueueTests(IntegrationTestFixture factory) => _factory = factory;

    private static readonly TimeSpan RunTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan DeadLetterTimeout = TimeSpan.FromSeconds(45);

    /// <summary>
    /// The control for the two rollback tests below. Without it, a query that finds nothing
    /// because it is looking in the wrong place would pass them.
    /// </summary>
    [Fact]
    public async Task A_job_queued_in_a_request_that_commits_is_stored_with_the_request()
    {
        var marker = Marker();

        var response = await _factory.CreateClient().PostAsync(
            $"/api/_test/jobs/enqueue-then-commit?message={marker}", null, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var id = await response.Content.ReadFromJsonAsync<Guid>(TestContext.Current.CancellationToken);

        var record = await FindByMarkerAsync(marker);
        record.Should().NotBeNull();
        record!.TrackingID.Should().Be(id);
        record.TenantId.Should().Be(JasperFx.StorageConstants.DefaultTenantId, "no tenant was named, so the request ran in the default partition");
        record.MaxAttempts.Should().Be(barakoCMS.Infrastructure.Jobs.JobOptions.DefaultMaxAttempts);
        record.CommandType.Should().Be(typeof(barakoCMS.Infrastructure.Jobs.LogMessageCommand).FullName);
        record.CreatedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task A_job_queued_in_a_request_that_throws_before_commit_leaves_no_record()
    {
        var control = Marker();
        var marker = Marker();
        var client = _factory.CreateClient();

        var committed = await client.PostAsync(
            $"/api/_test/jobs/enqueue-then-commit?message={control}", null, TestContext.Current.CancellationToken);
        committed.StatusCode.Should().Be(HttpStatusCode.OK);
        (await FindByMarkerAsync(control)).Should().NotBeNull("the control proves the lookup below can find a job");

        await PostExpectingFailureAsync(client, $"/api/_test/jobs/enqueue-then-throw?message={marker}");

        (await FindByMarkerAsync(marker)).Should().BeNull(
            "the job was staged in the request's session and the request never committed it");
    }

    /// <summary>
    /// The harder direction from the issue: StoreJobAsync returned successfully, then the write it
    /// belongs to failed at commit. The job must go with it.
    /// </summary>
    [Fact]
    public async Task A_job_whose_request_fails_at_commit_is_rolled_back_with_the_rest_of_the_write()
    {
        var marker = Marker();
        var client = _factory.CreateClient();

        await PostExpectingFailureAsync(client, $"/api/_test/jobs/enqueue-then-fail-commit?message={marker}");

        (await FindByMarkerAsync(marker)).Should().BeNull(
            "the duplicate role and the job were one transaction, and the unique index refused it");
    }

    [Fact]
    public async Task A_committed_job_runs_and_is_marked_complete()
    {
        var marker = Marker();

        var response = await _factory.CreateClient().PostAsync(
            $"/api/_test/jobs/enqueue-then-commit?message={marker}", null, TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var id = await response.Content.ReadFromJsonAsync<Guid>(TestContext.Current.CancellationToken);

        var done = await WaitForStateAsync(id, JobState.Completed, RunTimeout);

        done.IsComplete.Should().BeTrue();
        done.CompletedAt.Should().NotBeNull();
        done.AttemptCount.Should().Be(0, "it succeeded first time, and attempts count failures");
    }

    [Fact]
    public async Task A_job_that_keeps_failing_is_dead_lettered_after_max_attempts()
    {
        var marker = Marker();

        var response = await _factory.CreateClient().PostAsync(
            $"/api/_test/jobs/enqueue-failing?message={marker}", null, TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var id = await response.Content.ReadFromJsonAsync<Guid>(TestContext.Current.CancellationToken);

        var dead = await WaitForDeadLetterAsync(id);

        dead.AttemptCount.Should().Be(dead.MaxAttempts);
        dead.MaxAttempts.Should().Be(barakoCMS.Infrastructure.Jobs.JobOptions.DefaultMaxAttempts);
        dead.IsComplete.Should().BeFalse("dead-lettered is not completed; the purge must not treat it as done");
        dead.NextAttemptAt.Should().BeNull();
        dead.LastError.Should().Contain(AlwaysFailsCommandHandler.Reason);
        dead.LastError.Should().NotContain("   at ", "a stack trace has no place in a stored error");
    }

    /// <summary>
    /// A failure schedules the next attempt on the record itself, so the state is readable while
    /// the job is between attempts. The fixture sets the backoff base to zero, so the schedule is
    /// "now" and the arithmetic is JobBackoffTests; what this pins is that the fields move.
    /// </summary>
    [Fact]
    public async Task A_failed_attempt_is_counted_on_the_record()
    {
        var marker = Marker();

        var response = await _factory.CreateClient().PostAsync(
            $"/api/_test/jobs/enqueue-failing?message={marker}", null, TestContext.Current.CancellationToken);
        var id = await response.Content.ReadFromJsonAsync<Guid>(TestContext.Current.CancellationToken);

        var dead = await WaitForDeadLetterAsync(id);

        dead.AttemptCount.Should().BeGreaterThan(1, "every attempt before the last was counted and retried");
        dead.ExpireOn.Should().BeAfter(dead.CreatedAt.AddHours(4).AddMinutes(-1),
            "a retry the queue planned must not expire before it happens");
    }

    [Fact]
    public async Task A_job_belongs_to_the_tenant_that_queued_it_and_the_list_shows_only_that_tenants()
    {
        var alpha = await TenantAsync();
        var beta = await TenantAsync();
        var marker = Marker();

        var anonymousInAlpha = _factory.CreateClient();
        anonymousInAlpha.DefaultRequestHeaders.Add("X-Tenant", alpha);
        var response = await anonymousInAlpha.PostAsync(
            $"/api/_test/jobs/enqueue-then-commit?message={marker}", null, TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var id = await response.Content.ReadFromJsonAsync<Guid>(TestContext.Current.CancellationToken);

        var record = await FindByMarkerAsync(marker);
        record.Should().NotBeNull();
        record!.TenantId.Should().Be(alpha);

        var alphaMember = await MemberAsync(alpha, "10.106.0.1");
        var betaMember = await MemberAsync(beta, "10.106.0.2");

        var inAlpha = await alphaMember.GetFromJsonAsync<PaginatedResponse<JobRow>>(
            "/api/jobs", TestContext.Current.CancellationToken);
        var inBeta = await betaMember.GetFromJsonAsync<PaginatedResponse<JobRow>>(
            "/api/jobs", TestContext.Current.CancellationToken);

        inAlpha!.Items.Should().NotBeEmpty();
        inAlpha.Items.Should().Contain(j => j.Id == id);
        inBeta!.TotalItems.Should().Be(0, "nothing was queued in beta, and alpha's job must not show there");
    }

    [Fact]
    public async Task The_list_filters_by_state_and_refuses_a_state_it_does_not_know()
    {
        var marker = Marker();
        var client = await CallerHoldingAsync(SystemCapabilities.ViewJobs);

        var queued = await client.PostAsync(
            $"/api/_test/jobs/enqueue-then-commit?message={marker}", null, TestContext.Current.CancellationToken);
        var id = await queued.Content.ReadFromJsonAsync<Guid>(TestContext.Current.CancellationToken);
        await WaitForStateAsync(id, JobState.Completed, RunTimeout);

        var completed = await client.GetFromJsonAsync<PaginatedResponse<JobRow>>(
            "/api/jobs?state=completed", TestContext.Current.CancellationToken);
        var pending = await client.GetFromJsonAsync<PaginatedResponse<JobRow>>(
            "/api/jobs?state=Pending", TestContext.Current.CancellationToken);
        var unknown = await client.GetAsync("/api/jobs?state=Sleeping", TestContext.Current.CancellationToken);

        completed!.Items.Should().NotBeEmpty();
        completed.Items.Should().OnlyContain(j => j.State == nameof(JobState.Completed));
        completed.Items.Should().Contain(j => j.Id == id);
        pending!.Items.Should().NotContain(j => j.Id == id);
        unknown.StatusCode.Should().Be(HttpStatusCode.BadRequest, "a filter that is silently dropped returns more than was asked for");
    }

    [Fact]
    public async Task The_list_never_returns_a_commands_payload()
    {
        var marker = Marker();
        var client = await CallerHoldingAsync(SystemCapabilities.ViewJobs);

        var queued = await client.PostAsync(
            $"/api/_test/jobs/enqueue-then-commit?message={marker}", null, TestContext.Current.CancellationToken);
        queued.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await client.GetStringAsync("/api/jobs?pageSize=100", TestContext.Current.CancellationToken);

        body.Should().Contain(typeof(barakoCMS.Infrastructure.Jobs.LogMessageCommand).FullName!,
            "the control: the job is on the page");
        body.Should().NotContain(marker, "the payload is the one field a read of the queue must not hand out");
    }

    [Fact]
    public async Task View_jobs_opens_the_list_and_a_role_without_it_is_refused()
    {
        var holder = await CallerHoldingAsync(SystemCapabilities.ViewJobs);
        var other = await CallerHoldingAsync(SystemCapabilities.ViewWorkflowRuns);

        var allowed = await holder.GetAsync("/api/jobs", TestContext.Current.CancellationToken);
        var refused = await other.GetAsync("/api/jobs", TestContext.Current.CancellationToken);
        var elsewhere = await holder.GetAsync("/api/workflow-runs", TestContext.Current.CancellationToken);

        allowed.StatusCode.Should().Be(HttpStatusCode.OK);
        refused.StatusCode.Should().Be(HttpStatusCode.Forbidden, "watching workflow runs is not watching the queue");
        elsewhere.StatusCode.Should().Be(HttpStatusCode.Forbidden, "view_jobs opens the queue and nothing else");
    }

    [Fact]
    public void View_jobs_is_in_Admins_defaults()
    {
        SystemCapabilities.DefaultsFor("Admin").Should().Contain(SystemCapabilities.ViewJobs);
        SystemCapabilities.IsKnown(SystemCapabilities.ViewJobs).Should().BeTrue();
    }

    private sealed record JobRow(Guid Id, string State);

    private static string Marker() => $"job-{Guid.NewGuid():N}";

    /// <summary>
    /// A request whose handler throws. The in-memory test server surfaces that either as a 500 or
    /// as the exception itself, depending on what is in the pipeline; both mean the request failed.
    /// </summary>
    private static async Task PostExpectingFailureAsync(HttpClient client, string url)
    {
        try
        {
            var response = await client.PostAsync(url, null, TestContext.Current.CancellationToken);
            response.IsSuccessStatusCode.Should().BeFalse("the request is meant to fail after queueing");
        }
        catch (HttpRequestException)
        {
        }
        catch (InvalidOperationException)
        {
        }
        catch (Npgsql.PostgresException)
        {
        }
    }

    private async Task<JobRecord?> FindByMarkerAsync(string marker)
    {
        using var scope = _factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IDocumentStore>();
        await using var query = store.QuerySession();
        return await query.Query<JobRecord>()
            .Where(r => r.AnyTenant() && r.CommandJson.Contains(marker))
            .FirstOrDefaultAsync(TestContext.Current.CancellationToken);
    }

    private async Task<JobRecord?> LoadAsync(Guid id)
    {
        using var scope = _factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IDocumentStore>();
        await using var query = store.QuerySession();
        return await query.Query<JobRecord>()
            .Where(r => r.AnyTenant() && r.TrackingID == id)
            .FirstOrDefaultAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// A retry that is due is found on the worker's next storage probe, a minute apart by default,
    /// so this wait asks for a probe on every poll. The fixture says why the probe is not simply
    /// shortened: every host the suite keeps alive would pay for it, in database connections.
    /// </summary>
    private Task<JobRecord> WaitForDeadLetterAsync(Guid id) =>
        WaitForStateAsync(id, JobState.DeadLettered, DeadLetterTimeout,
            wake: () => new AlwaysFailsCommand().TriggerJobExecution());

    private async Task<JobRecord> WaitForStateAsync(Guid id, JobState state, TimeSpan timeout, Action? wake = null)
    {
        var deadline = DateTime.UtcNow + timeout;
        JobRecord? last = null;

        while (DateTime.UtcNow < deadline)
        {
            wake?.Invoke();
            last = await LoadAsync(id);
            if (last?.State == state) return last;
            await Task.Delay(250, TestContext.Current.CancellationToken);
        }

        throw new Xunit.Sdk.XunitException(
            $"Job {id} did not reach {state} within {timeout}. Last seen: {last?.State.ToString() ?? "no record"}, "
            + $"{last?.AttemptCount} attempt(s), error: {last?.LastError ?? "none"}");
    }

    private async Task<string> TenantAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var slug = $"jobs-{Guid.NewGuid():N}"[..14].ToLowerInvariant();
        session.Store(new Tenant { Id = Guid.NewGuid(), Slug = slug, Name = slug, IsActive = true });
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        return slug;
    }

    private async Task<HttpClient> MemberAsync(string tenantSlug, string ip)
    {
        var userId = Guid.NewGuid();

        using (var scope = _factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Store(new User
            {
                Id = userId,
                Username = $"jt-{Guid.NewGuid():n}"[..14],
                Email = $"jt-{Guid.NewGuid():n}@example.com",
                RoleIds = [SystemRoles.SuperAdminRoleId],
            });
            session.Store(new Membership
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                TenantSlug = tenantSlug,
                Status = MembershipStatus.Active,
                RoleIds = [SystemRoles.SuperAdminRoleId],
            });
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", _factory.CreateToken(
                roles: ["SuperAdmin"],
                userId: userId.ToString(),
                additionalClaims: new Dictionary<string, string> { ["tenant"] = tenantSlug }));
        client.DefaultRequestHeaders.Add("X-Tenant", tenantSlug);
        client.DefaultRequestHeaders.Add(TestRemoteIpFilter.Header, ip);
        return client;
    }

    /// <summary>A signed-in caller in the default tenant whose one role holds exactly these capabilities.</summary>
    private async Task<HttpClient> CallerHoldingAsync(params string[] capabilities)
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();

        var role = new Role
        {
            Id = Guid.NewGuid(),
            Name = $"Jobs {Guid.NewGuid():N}",
            Permissions = new List<ContentTypePermission>(),
            SystemCapabilities = [.. capabilities],
        };
        session.Store(role);

        var userId = Guid.NewGuid();
        session.Store(new User
        {
            Id = userId,
            Username = $"jobs-{userId}",
            Email = $"jobs-{userId}@example.com",
            RoleIds = [role.Id],
        });
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", _factory.CreateToken(roles: [role.Name], userId: userId.ToString()));
        return client;
    }
}
