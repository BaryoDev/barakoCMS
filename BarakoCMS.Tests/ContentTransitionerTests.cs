using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using barakoCMS.Core.Interfaces;
using barakoCMS.Events;
using barakoCMS.Infrastructure.Services;
using FluentAssertions;
using Marten;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using barakoCMS.Models;

namespace BarakoCMS.Tests;

/// <summary>
/// An entry can be moved through its lifecycle from code, by a stated actor, under the rules the
/// change-status endpoint applies.
/// </summary>
/// <remarks>
/// Every move here except the last test's is made with no HTTP request: a scope, the entry, and
/// <see cref="IContentTransitioner"/> resolved from the scope, which is all a module or a job has.
///
/// Each refusal is paired with the same move succeeding once the thing it lacked is supplied. A
/// transitioner that refused everything would pass the refusals on its own.
/// </remarks>
[Collection("Sequential")]
public class ContentTransitionerTests
{
    private readonly IntegrationTestFixture _factory;

    public ContentTransitionerTests(IntegrationTestFixture factory) => _factory = factory;

    private static readonly ContentTransitionOptions Trusted = new() { SkipPermissionChecks = true };

    private static LifecycleDefinition Review() => new()
    {
        States = ["Draft", "Submitted", "Approved", "Rejected"],
        InitialState = "Draft",
        Transitions =
        [
            new StateTransition { Name = "Submit", From = "Draft", To = "Submitted" },
            new StateTransition { Name = "Approve", From = "Submitted", To = "Approved" },
            new StateTransition
            {
                Name = "Reject",
                From = "Submitted",
                To = "Rejected",
                RequiredFields = ["RejectionReason"],
            },
        ],
    };

    private static List<FieldDefinition> Fields() =>
    [
        new() { Name = "Title", DisplayName = "Title", Type = "string" },
        new() { Name = "RejectionReason", DisplayName = "Rejection reason", Type = "string" },
    ];

    private static ContentTypePermission Clerk(string type) => new()
    {
        ContentTypeSlug = type,
        Read = new PermissionRule { Enabled = true },
        Update = new PermissionRule { Enabled = true },
        Transitions = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Submit"] = new PermissionRule { Enabled = true },
        },
    };

    private static ContentTypePermission Reviewer(string type) => new()
    {
        ContentTypeSlug = type,
        Read = new PermissionRule { Enabled = true },
        Transitions = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Approve"] = new PermissionRule { Enabled = true },
            ["Reject"] = new PermissionRule { Enabled = true },
        },
    };

    private async Task<string> TypeAsync(LifecycleDefinition? lifecycle, List<FieldDefinition>? fields = null)
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();

        var name = "ctr" + Guid.NewGuid().ToString("n")[..8];
        session.Store(new ContentTypeDefinition
        {
            Id = Guid.NewGuid(),
            Name = name,
            DisplayName = "Claim",
            Fields = fields ?? Fields(),
            Lifecycle = lifecycle,
        });
        await session.SaveChangesAsync();
        return name;
    }

    /// <summary>A stored user holding one role with the permission given, or no role at all.</summary>
    private async Task<User> UserAsync(ContentTypePermission? permission, string? roleName = null)
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();

        var roleIds = new List<Guid>();
        if (permission is not null)
        {
            var role = new Role
            {
                Id = Guid.NewGuid(),
                Name = roleName ?? $"Role_{Guid.NewGuid():n}",
                Permissions = [permission],
            };
            session.Store(role);
            roleIds.Add(role.Id);
        }

        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = $"ctr_{Guid.NewGuid():n}",
            Email = $"ctr_{Guid.NewGuid():n}@example.com",
            RoleIds = roleIds,
        };
        session.Store(user);
        await session.SaveChangesAsync();
        return user;
    }

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
            Username = $"ctradmin_{Guid.NewGuid():n}",
            Email = $"ctradmin_{Guid.NewGuid():n}@example.com",
            RoleIds = roleIds,
        };
        session.Store(user);
        await session.SaveChangesAsync();

        var client = (host ?? _factory).CreateClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer", _factory.CreateToken(roles: ["SuperAdmin", "Admin"], userId: user.Id.ToString()));
        return (client, user);
    }

    /// <summary>An entry in Draft, raised through the API by an administrator.</summary>
    private async Task<(Guid Id, User Creator)> EntryAsync(string type)
    {
        var (client, creator) = await AdminAsync();
        var res = await client.PostAsJsonAsync("/api/contents", new
        {
            contentType = type,
            data = new Dictionary<string, object> { ["Title"] = "a claim" },
        });
        res.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}", res.StatusCode, await res.Content.ReadAsStringAsync());
        using var doc = System.Text.Json.JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        return (doc.RootElement.GetProperty("id").GetGuid(), creator);
    }

    /// <summary>The same entry, moved to Submitted by a system actor.</summary>
    private async Task<(Guid Id, User Creator)> SubmittedAsync(string type)
    {
        var (id, creator) = await EntryAsync(type);
        var submitted = await MoveAsync(id, "Submit", ContentTransitionActor.ForSystem("test-setup"), Trusted);
        submitted.Outcome.Should().Be(ContentTransitionOutcome.Transitioned, string.Join(" ", submitted.Errors));
        return (id, creator);
    }

    /// <summary>What a module does: a scope, the entry from its session, the interface from its provider.</summary>
    private async Task<ContentTransitionResult> MoveAsync(
        Guid id, string transition, ContentTransitionActor actor, ContentTransitionOptions? options = null)
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var transitioner = scope.ServiceProvider.GetRequiredService<IContentTransitioner>();

        var content = await session.LoadAsync<Content>(id);
        content.Should().NotBeNull();
        return await transitioner.TransitionAsync(content!, transition, actor, options);
    }

    private async Task<Content> LoadAsync(Guid id)
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        return (await session.LoadAsync<Content>(id))!;
    }

    private static string? Value(Content content, string field) =>
        content.Data.FirstOrDefault(kv => kv.Key.Equals(field, StringComparison.OrdinalIgnoreCase)).Value?.ToString();

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

    // ---- a system actor ------------------------------------------------------------------------

    [Fact]
    public async Task A_system_actor_that_states_the_skip_moves_the_entry_and_the_audit_row_names_it()
    {
        var type = await TypeAsync(Review());
        var (id, _) = await EntryAsync(type);

        var result = await MoveAsync(id, "submit", ContentTransitionActor.ForSystem("nightly-close"), Trusted);

        result.Outcome.Should().Be(ContentTransitionOutcome.Transitioned, string.Join(" ", result.Errors));
        result.Succeeded.Should().BeTrue();
        result.Errors.Should().BeEmpty();
        result.Transition.Should().Be("Submit", "the result carries the name the type declares");
        result.FromState.Should().Be("Draft");
        result.ToState.Should().Be("Submitted");
        (await LoadAsync(id)).LifecycleState.Should().Be("Submitted");

        var transitions = await TransitionsAsync(id);
        transitions.Should().HaveCount(1);
        transitions[0].Transition.Should().Be("Submit");
        transitions[0].UpdatedBy.Should().Be(Guid.Empty, "no user made the move, so the event names none");

        var rows = await AuditAsync(id);
        rows.Should().HaveCount(1);
        rows[0].ActorUserId.Should().BeNull("a system actor is not a user");
        rows[0].ActorUsername.Should().BeNull();
        rows[0].TargetType.Should().Be(type);
        rows[0].Metadata.Should().NotBeNull();
        rows[0].Metadata!.Keys.Should().BeEquivalentTo(new[] { "transition", "from", "to", "actor", "permissionChecks" });
        rows[0].Metadata!["actor"].ToString().Should().Be("system:nightly-close");
        rows[0].Metadata!["permissionChecks"].ToString().Should().Be("skipped");
        rows[0].Metadata!["transition"].ToString().Should().Be("Submit");
        rows[0].Metadata!["from"].ToString().Should().Be("Draft");
        rows[0].Metadata!["to"].ToString().Should().Be("Submitted");
    }

    [Fact]
    public async Task A_system_actor_is_refused_a_move_the_lifecycle_does_not_allow()
    {
        var type = await TypeAsync(Review());
        var (id, _) = await EntryAsync(type);
        var system = ContentTransitionActor.ForSystem("payments-webhook");

        // Draft, and Approve moves Submitted to Approved. Skipping the permission checks does not
        // skip the lifecycle.
        var refused = await MoveAsync(id, "Approve", system, Trusted);

        refused.Outcome.Should().Be(ContentTransitionOutcome.Conflict);
        refused.Succeeded.Should().BeFalse();
        refused.Errors.Should().HaveCount(1);
        refused.Errors[0].Should().Be("'Approve' moves Submitted to Approved, and this entry is Draft.");
        (await LoadAsync(id)).LifecycleState.Should().Be("Draft", "a refused transition changes nothing");
        (await TransitionsAsync(id)).Should().BeEmpty();
        (await AuditAsync(id)).Should().BeEmpty();

        (await MoveAsync(id, "Submit", system, Trusted)).Outcome.Should().Be(ContentTransitionOutcome.Transitioned);
        (await MoveAsync(id, "Approve", system, Trusted)).Outcome.Should().Be(ContentTransitionOutcome.Transitioned);
        (await LoadAsync(id)).LifecycleState.Should().Be("Approved");
        (await AuditAsync(id)).Should().HaveCount(2);
    }

    [Fact]
    public async Task A_system_actor_that_does_not_state_the_skip_is_refused()
    {
        var type = await TypeAsync(Review());
        var (id, _) = await EntryAsync(type);
        var system = ContentTransitionActor.ForSystem("nightly-close");

        var byDefault = await MoveAsync(id, "Submit", system);
        var saidNo = await MoveAsync(id, "Submit", system, new ContentTransitionOptions { SkipPermissionChecks = false });

        byDefault.Outcome.Should().Be(ContentTransitionOutcome.Forbidden, "a system actor holds no permissions");
        saidNo.Outcome.Should().Be(ContentTransitionOutcome.Forbidden);
        (await LoadAsync(id)).LifecycleState.Should().Be("Draft");
        (await AuditAsync(id)).Should().BeEmpty();

        var allowed = await MoveAsync(id, "Submit", system, Trusted);
        allowed.Outcome.Should().Be(ContentTransitionOutcome.Transitioned, string.Join(" ", allowed.Errors));
    }

    [Fact]
    public async Task A_system_actor_has_to_send_the_fields_the_transition_requires()
    {
        var type = await TypeAsync(Review());
        var (id, _) = await SubmittedAsync(type);
        var system = ContentTransitionActor.ForSystem("review-bot");

        var refused = await MoveAsync(id, "Reject", system, Trusted);

        refused.Outcome.Should().Be(ContentTransitionOutcome.Invalid);
        refused.Errors.Should().HaveCount(1);
        refused.Errors[0].Should().Contain("RejectionReason");
        (await LoadAsync(id)).LifecycleState.Should().Be("Submitted");

        var allowed = await MoveAsync(id, "Reject", system, new ContentTransitionOptions
        {
            SkipPermissionChecks = true,
            Data = new Dictionary<string, object> { ["RejectionReason"] = "No receipt attached" },
        });

        allowed.Outcome.Should().Be(ContentTransitionOutcome.Transitioned, string.Join(" ", allowed.Errors));
        var content = await LoadAsync(id);
        content.LifecycleState.Should().Be("Rejected");
        Value(content, "RejectionReason").Should().Be("No receipt attached");
        Value(content, "Title").Should().Be("a claim");
    }

    [Theory]
    [InlineData("")]
    [InlineData("two words")]
    [InlineData("line\nbreak")]
    [InlineData("system:nested")]
    public void A_system_actor_name_is_a_short_fixed_name(string name)
    {
        var act = () => ContentTransitionActor.ForSystem(name);

        act.Should().Throw<ArgumentException>();
        ContentTransitionActor.ForSystem("payments-webhook_v2.1").SystemName.Should().Be("payments-webhook_v2.1");
    }

    [Fact]
    public void An_actor_is_a_user_with_an_id_or_a_system_with_a_bounded_name()
    {
        var act = () => ContentTransitionActor.ForUser(Guid.Empty);

        act.Should().Throw<ArgumentException>("Guid.Empty is what a system actor records");

        var tooLong = () => ContentTransitionActor.ForSystem(new string('a', ContentTransitionActor.MaxSystemNameLength + 1));
        tooLong.Should().Throw<ArgumentException>();
        var longest = new string('a', ContentTransitionActor.MaxSystemNameLength);
        ContentTransitionActor.ForSystem(longest).SystemName.Should().Be(longest);

        var id = Guid.NewGuid();
        ContentTransitionActor.ForUser(id).UserId.Should().Be(id);
        ContentTransitionActor.ForUser(id).IsSystem.Should().BeFalse();
        ContentTransitionActor.ForSystem("job").IsSystem.Should().BeTrue();
        ContentTransitionActor.ForSystem("job").RecordedId.Should().Be(Guid.Empty);
    }

    // ---- a user actor --------------------------------------------------------------------------

    /// <summary>
    /// A user actor outside a request is held to the permissions a request by that user is. The
    /// clerk may edit and may not approve, and the reviewer may approve.
    /// </summary>
    [Fact]
    public async Task A_user_actor_is_checked_through_the_permission_resolver()
    {
        var type = await TypeAsync(Review());
        var (id, _) = await SubmittedAsync(type);
        var clerk = await UserAsync(Clerk(type));
        var reviewer = await UserAsync(Reviewer(type));

        var refused = await MoveAsync(id, "Approve", ContentTransitionActor.ForUser(clerk.Id));

        refused.Outcome.Should().Be(ContentTransitionOutcome.Forbidden, "the clerk role has Update and no Approve");
        (await LoadAsync(id)).LifecycleState.Should().Be("Submitted");

        var allowed = await MoveAsync(id, "Approve", ContentTransitionActor.ForUser(reviewer.Id));

        allowed.Outcome.Should().Be(ContentTransitionOutcome.Transitioned, string.Join(" ", allowed.Errors));
        (await LoadAsync(id)).LifecycleState.Should().Be("Approved");

        var approvals = (await TransitionsAsync(id)).Where(t => t.Transition == "Approve").ToList();
        approvals.Should().HaveCount(1);
        approvals[0].UpdatedBy.Should().Be(reviewer.Id);

        var rows = (await AuditAsync(id)).Where(r => r.Metadata!["transition"].ToString() == "Approve").ToList();
        rows.Should().HaveCount(1);
        rows[0].ActorUserId.Should().Be(reviewer.Id);
        rows[0].ActorUsername.Should().Be(reviewer.Username);
        rows[0].Metadata!.Keys.Should().BeEquivalentTo(new[] { "transition", "from", "to" },
            "a user who passed the checks is recorded exactly as the endpoint records one");
    }

    [Fact]
    public async Task A_user_actor_with_no_rights_on_the_type_is_refused_before_anything_is_named()
    {
        var type = await TypeAsync(Review());
        var (id, _) = await EntryAsync(type);
        var outsider = await UserAsync(new ContentTypePermission { ContentTypeSlug = type });
        var clerk = await UserAsync(Clerk(type));

        var refused = await MoveAsync(id, "NoSuchThing", ContentTransitionActor.ForUser(outsider.Id));

        refused.Outcome.Should().Be(ContentTransitionOutcome.Forbidden);
        refused.Errors.Should().HaveCount(1);
        refused.Errors[0].Should().NotContain("Submit", "the declared transitions are not for an actor who may not read the type");

        var unknown = await MoveAsync(id, "NoSuchThing", ContentTransitionActor.ForUser(clerk.Id));

        unknown.Outcome.Should().Be(ContentTransitionOutcome.Invalid);
        unknown.Errors.Should().HaveCount(1);
        unknown.Errors[0].Should().Be(
            $"'NoSuchThing' is not a transition on '{type}'. Declared transitions: Approve, Reject, Submit.");
    }

    /// <summary>
    /// The events and the audit row name the actor, so an id nobody holds is refused even by a
    /// caller that skipped the permission checks.
    /// </summary>
    [Fact]
    public async Task A_user_actor_that_is_not_a_user_is_refused_even_with_the_checks_skipped()
    {
        var type = await TypeAsync(Review());
        var (id, _) = await EntryAsync(type);
        var nobody = await UserAsync(permission: null);

        var refused = await MoveAsync(id, "Submit", ContentTransitionActor.ForUser(Guid.NewGuid()), Trusted);

        refused.Outcome.Should().Be(ContentTransitionOutcome.Forbidden);
        (await LoadAsync(id)).LifecycleState.Should().Be("Draft");
        (await AuditAsync(id)).Should().BeEmpty();

        // A stored user with no role at all: refused on their own rights, allowed when the caller
        // takes the decision on itself, and recorded as that user with the skip on the row.
        var onTheirOwn = await MoveAsync(id, "Submit", ContentTransitionActor.ForUser(nobody.Id));
        onTheirOwn.Outcome.Should().Be(ContentTransitionOutcome.Forbidden);

        var allowed = await MoveAsync(id, "Submit", ContentTransitionActor.ForUser(nobody.Id), Trusted);
        allowed.Outcome.Should().Be(ContentTransitionOutcome.Transitioned, string.Join(" ", allowed.Errors));

        var rows = await AuditAsync(id);
        rows.Should().HaveCount(1);
        rows[0].ActorUserId.Should().Be(nobody.Id);
        rows[0].ActorUsername.Should().Be(nobody.Username);
        rows[0].Metadata!.Keys.Should().BeEquivalentTo(new[] { "transition", "from", "to", "permissionChecks" });
        rows[0].Metadata!["permissionChecks"].ToString().Should().Be("skipped");
    }

    /// <summary>
    /// The self transition rule is about who acted, not about a permission, so skipping the
    /// permission checks does not skip it.
    /// </summary>
    [Fact]
    public async Task The_creator_cannot_move_their_entry_on_even_with_the_checks_skipped()
    {
        var type = await TypeAsync(Review());
        var (id, creator) = await EntryAsync(type);
        var other = await UserAsync(permission: null);

        var refused = await MoveAsync(id, "Submit", ContentTransitionActor.ForUser(creator.Id), Trusted);

        refused.Outcome.Should().Be(ContentTransitionOutcome.Forbidden);
        (await LoadAsync(id)).LifecycleState.Should().Be("Draft");

        var allowed = await MoveAsync(id, "Submit", ContentTransitionActor.ForUser(other.Id), Trusted);
        allowed.Outcome.Should().Be(ContentTransitionOutcome.Transitioned, string.Join(" ", allowed.Errors));
    }

    // ---- field sensitivity outside a request -------------------------------------------------

    /// <summary>
    /// With no request to read a principal from, field sensitivity is answered from the roles the
    /// user holds: a reviewer whose role may see the field sets it, and one whose role may not is
    /// refused, as the endpoint refuses them.
    /// </summary>
    [Fact]
    public async Task Outside_a_request_a_user_actor_may_set_only_a_field_their_roles_may_see()
    {
        var payroll = $"Payroll_{Guid.NewGuid():n}";
        var fields = Fields();
        var reason = fields.Single(f => f.Name == "RejectionReason");
        reason.Sensitivity = SensitivityLevel.Sensitive;
        reason.VisibleToRoles = [payroll];
        var type = await TypeAsync(Review(), fields);
        var (id, _) = await SubmittedAsync(type);

        var reviewer = await UserAsync(Reviewer(type));
        var payrollReviewer = await UserAsync(Reviewer(type), roleName: payroll);
        var sent = new ContentTransitionOptions
        {
            Data = new Dictionary<string, object> { ["RejectionReason"] = "No receipt attached" },
        };

        var refused = await MoveAsync(id, "Reject", ContentTransitionActor.ForUser(reviewer.Id), sent);

        refused.Outcome.Should().Be(ContentTransitionOutcome.Invalid, "the value was put back, so the required field was not sent");
        refused.Errors.Should().HaveCount(1);
        refused.Errors[0].Should().Contain("RejectionReason");
        var after = await LoadAsync(id);
        after.LifecycleState.Should().Be("Submitted");
        Value(after, "RejectionReason").Should().BeNull();

        var allowed = await MoveAsync(id, "Reject", ContentTransitionActor.ForUser(payrollReviewer.Id), sent);

        allowed.Outcome.Should().Be(ContentTransitionOutcome.Transitioned, string.Join(" ", allowed.Errors));
        var content = await LoadAsync(id);
        content.LifecycleState.Should().Be("Rejected");
        Value(content, "RejectionReason").Should().Be("No receipt attached");
    }

    /// <summary>
    /// Skipping the permission checks covers field sensitivity on the sent values too, which is
    /// what lets a system actor, who holds no role, write a field no role but Payroll may see.
    /// </summary>
    [Fact]
    public async Task A_caller_that_skips_the_checks_may_set_a_sensitive_field()
    {
        var fields = Fields();
        var reason = fields.Single(f => f.Name == "RejectionReason");
        reason.Sensitivity = SensitivityLevel.Sensitive;
        reason.VisibleToRoles = [$"Payroll_{Guid.NewGuid():n}"];
        var type = await TypeAsync(Review(), fields);
        var (id, _) = await SubmittedAsync(type);

        var result = await MoveAsync(id, "Reject", ContentTransitionActor.ForSystem("review-bot"), new ContentTransitionOptions
        {
            SkipPermissionChecks = true,
            Data = new Dictionary<string, object> { ["RejectionReason"] = "Duplicate claim" },
        });

        result.Outcome.Should().Be(ContentTransitionOutcome.Transitioned, string.Join(" ", result.Errors));
        Value(await LoadAsync(id), "RejectionReason").Should().Be("Duplicate claim");
    }

    // ---- a type with nothing to move through ------------------------------------------------

    [Fact]
    public async Task A_type_with_no_lifecycle_has_no_transitions()
    {
        var plain = await TypeAsync(lifecycle: null);
        var (plainId, _) = await EntryAsync(plain);

        var refused = await MoveAsync(plainId, "Submit", ContentTransitionActor.ForSystem("nightly-close"), Trusted);

        refused.Outcome.Should().Be(ContentTransitionOutcome.Invalid);
        refused.Errors.Should().HaveCount(1);
        refused.Errors[0].Should().Contain("declares no lifecycle");
        (await AuditAsync(plainId)).Should().BeEmpty();

        var type = await TypeAsync(Review());
        var (id, _) = await EntryAsync(type);
        var allowed = await MoveAsync(id, "Submit", ContentTransitionActor.ForSystem("nightly-close"), Trusted);
        allowed.Outcome.Should().Be(ContentTransitionOutcome.Transitioned, string.Join(" ", allowed.Errors));
    }

    // ---- the endpoint is one caller ---------------------------------------------------------

    private sealed record Call(string Transition, ContentTransitionActor Actor, ContentTransitionOptions? Options);

    private static readonly ConcurrentDictionary<Guid, Call> Calls = new();

    private sealed class RecordingTransitioner(IContentTransitioner inner) : IContentTransitioner
    {
        public Task<ContentTransitionResult> TransitionAsync(
            Content content,
            string transition,
            ContentTransitionActor actor,
            ContentTransitionOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Calls[content.Id] = new Call(transition, actor, options);
            return inner.TransitionAsync(content, transition, actor, options, cancellationToken);
        }
    }

    private static readonly Lock HostGate = new();
    private static WebApplicationFactory<Program>? _host;

    /// <summary>
    /// One host for the class, with the transitioner wrapped so a call through the interface is
    /// seen. Never disposed, per the note on IntegrationTestFixture.WithSetting.
    /// </summary>
    private WebApplicationFactory<Program> RecordingHost()
    {
        lock (HostGate)
        {
            return _host ??= _factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
                services.AddScoped<IContentTransitioner>(sp =>
                    new RecordingTransitioner(ActivatorUtilities.CreateInstance<ContentTransitioner>(sp)))));
        }
    }

    [Fact]
    public async Task The_change_status_endpoint_makes_the_move_through_the_transitioner_as_the_caller()
    {
        var type = await TypeAsync(Review());
        var (id, _) = await SubmittedAsync(type);
        var (reviewer, reviewerUser) = await AdminAsync(RecordingHost());

        var res = await reviewer.PutAsJsonAsync($"/api/contents/{id}/status", new
        {
            id,
            transition = "Reject",
            data = new Dictionary<string, object> { ["RejectionReason"] = "No receipt attached" },
        });

        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        Calls.TryGetValue(id, out var call).Should().BeTrue("the endpoint is a caller of IContentTransitioner");
        call!.Transition.Should().Be("Reject");
        call.Actor.UserId.Should().Be(reviewerUser.Id);
        call.Actor.IsSystem.Should().BeFalse();
        call.Options.Should().NotBeNull();
        call.Options!.SkipPermissionChecks.Should().BeFalse("a request never skips the checks");
        call.Options.Data.Should().NotBeNull();
        call.Options.Data!.Keys.Should().BeEquivalentTo(new[] { "RejectionReason" });
        (await LoadAsync(id)).LifecycleState.Should().Be("Rejected");
    }
}
