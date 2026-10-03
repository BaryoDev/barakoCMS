using System.Net;
using System.Net.Http.Json;
using barakoCMS.Events;
using FluentAssertions;
using Marten;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using barakoCMS.Models;

namespace BarakoCMS.Tests;

/// <summary>
/// What the change-status endpoint answers and records for a transition, where no other test said.
/// </summary>
/// <remarks>
/// The audit row, the actor on the event, the two Lifecycle settings and the wording of the
/// answers were all behaviour with no test on them. Every test here passes before the transition
/// rules moved behind IContentTransitioner and after: they are what says the move changed nothing
/// a client or an auditor can see.
/// </remarks>
[Collection("Sequential")]
public class ChangeStatusTransitionTests
{
    private readonly IntegrationTestFixture _factory;

    public ChangeStatusTransitionTests(IntegrationTestFixture factory) => _factory = factory;

    private static readonly Lock HostGate = new();
    private static WebApplicationFactory<Program>? _relaxed;

    /// <summary>
    /// One host for the class with enforcement off and Submit allowed to its creator. Never
    /// disposed, per the note on IntegrationTestFixture.WithSetting.
    /// </summary>
    private WebApplicationFactory<Program> RelaxedHost()
    {
        lock (HostGate)
        {
            return _relaxed ??= _factory.WithSettings(new Dictionary<string, string?>
            {
                ["Lifecycle:EnforceTransitions"] = "false",
                ["Lifecycle:AllowSelfTransition:Submit"] = "true",
            });
        }
    }

    private static LifecycleDefinition Invoice() => new()
    {
        States = ["Draft", "Submitted", "Approved"],
        InitialState = "Draft",
        Transitions =
        [
            new StateTransition { Name = "Submit", From = "Draft", To = "Submitted" },
            new StateTransition { Name = "Approve", From = "Submitted", To = "Approved" },
        ],
    };

    private async Task<(HttpClient Client, User User)> AdminAsync(WebApplicationFactory<Program>? host = null)
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();

        var roleIds = new List<Guid>();
        foreach (var name in new[] { "SuperAdmin", "Admin" })
        {
            var role = await session.Query<Role>().FirstOrDefaultAsync(r => r.Name == name);
            if (role is null) { role = new Role { Id = Guid.NewGuid(), Name = name }; session.Store(role); }
            roleIds.Add(role.Id);
        }

        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = $"cst_{Guid.NewGuid():n}",
            Email = $"cst_{Guid.NewGuid():n}@example.com",
            RoleIds = roleIds,
        };
        session.Store(user);
        await session.SaveChangesAsync();

        var client = (host ?? _factory).CreateClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer", _factory.CreateToken(roles: ["SuperAdmin", "Admin"], userId: user.Id.ToString()));
        return (client, user);
    }

    private static async Task<string> TypeAsync(HttpClient admin)
    {
        var name = "cst" + Guid.NewGuid().ToString("n")[..8];
        var res = await admin.PostAsJsonAsync("/api/content-types", new
        {
            name,
            displayName = "Invoice",
            fields = new[] { new { name = "Title", type = "string" } },
            lifecycle = Invoice(),
        });
        res.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}", res.StatusCode, await res.Content.ReadAsStringAsync());
        return name;
    }

    private static async Task<Guid> EntryAsync(HttpClient client, string type)
    {
        var res = await client.PostAsJsonAsync("/api/contents", new
        {
            contentType = type,
            data = new Dictionary<string, object> { ["Title"] = "an invoice" },
        });
        res.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}", res.StatusCode, await res.Content.ReadAsStringAsync());
        using var doc = System.Text.Json.JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("id").GetGuid();
    }

    private static Task<HttpResponseMessage> MoveAsync(HttpClient client, Guid id, string transition) =>
        client.PutAsJsonAsync($"/api/contents/{id}/status", new { id, transition });

    /// <summary>The body with its JSON escapes undone, so an apostrophe reads as one.</summary>
    private static async Task<string> BodyAsync(HttpResponseMessage response) =>
        (await response.Content.ReadAsStringAsync()).Replace("\\u0027", "'");

    private async Task<Content> LoadAsync(Guid id)
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        return (await session.LoadAsync<Content>(id))!;
    }

    private async Task<List<AuditEvent>> AuditAsync(Guid id)
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        var target = id.ToString();
        var rows = await session.Query<AuditEvent>()
            .Where(e => e.Action == "content.transitioned" && e.TargetId == target)
            .ToListAsync();
        return rows.OrderBy(e => e.CreatedAt).ToList();
    }

    private async Task<List<ContentTransitioned>> TransitionsAsync(Guid id)
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        var stream = await session.Events.FetchStreamAsync(id);
        return stream.Select(e => e.Data).OfType<ContentTransitioned>().ToList();
    }

    [Fact]
    public async Task A_transition_is_audited_with_the_user_who_made_it_and_a_refused_one_is_not()
    {
        var (creator, _) = await AdminAsync();
        var (approver, approverUser) = await AdminAsync();
        var type = await TypeAsync(creator);
        var id = await EntryAsync(creator, type);

        // Draft, so Approve does not apply.
        var refused = await MoveAsync(approver, id, "Approve");
        refused.StatusCode.Should().Be(HttpStatusCode.Conflict, await refused.Content.ReadAsStringAsync());
        (await AuditAsync(id)).Should().BeEmpty("a refused transition did not happen");

        var moved = await MoveAsync(approver, id, "Submit");
        moved.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}", moved.StatusCode, await moved.Content.ReadAsStringAsync());

        var rows = await AuditAsync(id);
        rows.Should().HaveCount(1);
        var row = rows[0];
        row.ActorUserId.Should().Be(approverUser.Id);
        row.ActorUsername.Should().Be(approverUser.Username);
        row.TargetType.Should().Be(type);
        row.TenantSlug.Should().Be(Tenant.DefaultSlug);
        row.Metadata.Should().NotBeNull();
        row.Metadata!.Keys.Should().BeEquivalentTo(new[] { "transition", "from", "to" });
        row.Metadata["transition"].ToString().Should().Be("Submit");
        row.Metadata["from"].ToString().Should().Be("Draft");
        row.Metadata["to"].ToString().Should().Be("Submitted");
    }

    [Fact]
    public async Task The_transition_event_names_the_user_and_the_states()
    {
        var (creator, _) = await AdminAsync();
        var (approver, approverUser) = await AdminAsync();
        var type = await TypeAsync(creator);
        var id = await EntryAsync(creator, type);

        var moved = await MoveAsync(approver, id, "Submit");
        moved.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}", moved.StatusCode, await moved.Content.ReadAsStringAsync());

        var transitions = await TransitionsAsync(id);
        transitions.Should().HaveCount(1);
        transitions[0].Transition.Should().Be("Submit");
        transitions[0].FromState.Should().Be("Draft");
        transitions[0].ToState.Should().Be("Submitted");
        transitions[0].UpdatedBy.Should().Be(approverUser.Id);
    }

    [Fact]
    public async Task The_answers_keep_their_wording()
    {
        var (creator, _) = await AdminAsync();
        var (approver, _) = await AdminAsync();
        var type = await TypeAsync(creator);
        var id = await EntryAsync(creator, type);

        var unknown = await MoveAsync(approver, id, "Pay");
        unknown.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await BodyAsync(unknown)).Should().Contain(
            $"'Pay' is not a transition on '{type}'. Declared transitions: Approve, Submit.");

        var early = await MoveAsync(approver, id, "approve");
        early.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await BodyAsync(early)).Should().Contain("'Approve' moves Submitted to Approved, and this entry is Draft.");

        var both = await approver.PutAsJsonAsync($"/api/contents/{id}/status", new { id, newStatus = "Published" });
        both.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await BodyAsync(both)).Should().Contain(
            $"Content type '{type}' declares a lifecycle, so it takes Transition rather than NewStatus.");

        // Sent in another casing, answered with the name the type declares.
        var moved = await MoveAsync(approver, id, "submit");
        moved.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = System.Text.Json.JsonDocument.Parse(await moved.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("message").GetString().Should().Be("Submit moved this entry to Submitted");
    }

    /// <summary>
    /// Lifecycle:EnforceTransitions off lets an out-of-order move through and records the state the
    /// entry was really in. The same request on the default host is the control.
    /// </summary>
    [Fact]
    public async Task With_enforcement_off_an_out_of_order_transition_goes_through()
    {
        var (creator, _) = await AdminAsync();
        var type = await TypeAsync(creator);

        var enforcedId = await EntryAsync(creator, type);
        var (enforcedApprover, _) = await AdminAsync();
        var refused = await MoveAsync(enforcedApprover, enforcedId, "Approve");
        refused.StatusCode.Should().Be(HttpStatusCode.Conflict, "enforcement is on unless it is switched off");

        var id = await EntryAsync(creator, type);
        var (approver, _) = await AdminAsync(RelaxedHost());
        var moved = await MoveAsync(approver, id, "Approve");

        moved.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}", moved.StatusCode, await moved.Content.ReadAsStringAsync());
        (await LoadAsync(id)).LifecycleState.Should().Be("Approved");
        var transitions = await TransitionsAsync(id);
        transitions.Should().HaveCount(1);
        transitions[0].FromState.Should().Be("Draft", "the event records where the entry was, not where the transition starts");
        transitions[0].ToState.Should().Be("Approved");
    }

    /// <summary>
    /// Lifecycle:AllowSelfTransition is per transition: Submit is allowed to the creator on this
    /// host, and Approve is not.
    /// </summary>
    [Fact]
    public async Task With_self_transition_allowed_for_one_transition_the_creator_may_make_that_one_only()
    {
        var (creator, _) = await AdminAsync(RelaxedHost());
        var type = await TypeAsync(creator);
        var id = await EntryAsync(creator, type);

        var submitted = await MoveAsync(creator, id, "Submit");
        submitted.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}", submitted.StatusCode, await submitted.Content.ReadAsStringAsync());
        (await LoadAsync(id)).LifecycleState.Should().Be("Submitted");

        var approved = await MoveAsync(creator, id, "Approve");
        approved.StatusCode.Should().Be(HttpStatusCode.Forbidden, "only Submit was allowed to its creator");
        (await LoadAsync(id)).LifecycleState.Should().Be("Submitted");
    }
}
