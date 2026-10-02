using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using barakoCMS.Models;

namespace BarakoCMS.Tests;

/// <summary>
/// A transition can require fields, and a reviewer who may not edit an entry can still fill them in
/// with the move.
/// </summary>
/// <remarks>
/// A transition moved an entry between states behind a permission check and nothing else, so a
/// Reject with no reason went through and the rejection email had nothing to say.
///
/// Every refusal here is paired with the same request succeeding once the field is sent. A check
/// that refused every Reject would pass the refusals on its own.
/// </remarks>
[Collection("Sequential")]
public class TransitionRequiredFieldsTests
{
    private static readonly TimeSpan PollTimeout = TimeSpan.FromSeconds(30);

    private readonly IntegrationTestFixture _factory;

    public TransitionRequiredFieldsTests(IntegrationTestFixture factory) => _factory = factory;

    private static LifecycleDefinition Review(params string[] rejectRequires) => new()
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
                RequiredFields = [.. rejectRequires],
                OptionalFields = ["RejectionNote"],
            },
        ],
    };

    private static FieldDefinition Text(string name) =>
        new() { Name = name, DisplayName = name, Type = "string" };

    private static List<FieldDefinition> Fields() =>
        [Text("Title"), Text("RejectionReason"), Text("RejectionNote")];

    private static ContentTypePermission Clerk(string type) => new()
    {
        ContentTypeSlug = type,
        Create = new PermissionRule { Enabled = true },
        Read = new PermissionRule { Enabled = true },
        Update = new PermissionRule { Enabled = true },
        Transitions = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Submit"] = new PermissionRule { Enabled = true },
        },
    };

    /// <summary>May read, approve and reject. May not edit, which is the point.</summary>
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

    private async Task<HttpClient> UserAsync(ContentTypePermission permission)
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();

        var role = new Role
        {
            Id = Guid.NewGuid(),
            Name = $"Role_{Guid.NewGuid():n}",
            Permissions = [permission],
        };
        session.Store(role);

        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = $"trf_{Guid.NewGuid():n}",
            Email = $"trf_{Guid.NewGuid():n}@example.com",
            RoleIds = [role.Id],
        };
        session.Store(user);
        await session.SaveChangesAsync();

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer", _factory.CreateToken(roles: [role.Name], userId: user.Id.ToString()));
        return client;
    }

    private async Task<HttpClient> AdminAsync()
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

        var admin = new User
        {
            Id = Guid.NewGuid(),
            Username = $"trfadmin_{Guid.NewGuid():n}",
            Email = $"trfadmin_{Guid.NewGuid():n}@example.com",
            RoleIds = roleIds,
        };
        session.Store(admin);
        await session.SaveChangesAsync();

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer", _factory.CreateToken(roles: ["SuperAdmin", "Admin"], userId: admin.Id.ToString()));
        return client;
    }

    private static string NewName(string prefix) => prefix + Guid.NewGuid().ToString("n")[..8];

    private static Task<HttpResponseMessage> PostTypeAsync(
        HttpClient admin, string name, LifecycleDefinition lifecycle, object[]? fields = null) =>
        admin.PostAsJsonAsync("/api/content-types", new
        {
            name,
            displayName = "Claim",
            fields = fields ?? new object[]
            {
                new { name = "Title", displayName = "Title", type = "string" },
                new { name = "RejectionReason", displayName = "Rejection reason", type = "string" },
                new { name = "RejectionNote", displayName = "Rejection note", type = "string" },
            },
            lifecycle,
        });

    /// <summary>A type whose Reject requires RejectionReason and takes RejectionNote, saved through the API.</summary>
    private async Task<string> TypeAsync(object[]? fields = null)
    {
        var name = NewName("claim");
        var res = await PostTypeAsync(await AdminAsync(), name, Review("RejectionReason"), fields);
        res.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}", res.StatusCode, await res.Content.ReadAsStringAsync());
        return name;
    }

    /// <summary>Stored straight through the session, the way a type from before a check was added is.</summary>
    private async Task<string> StoredTypeAsync(LifecycleDefinition lifecycle, List<FieldDefinition> fields)
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();

        var name = NewName("claim");
        session.Store(new ContentTypeDefinition
        {
            Id = Guid.NewGuid(),
            Name = name,
            DisplayName = "Claim",
            Fields = fields,
            Lifecycle = lifecycle,
        });
        await session.SaveChangesAsync();
        return name;
    }

    /// <summary>An entry raised by one clerk and submitted by another, so it sits in Submitted.</summary>
    private async Task<Guid> SubmittedAsync(string type, Dictionary<string, object>? data = null)
    {
        var clerk = await UserAsync(Clerk(type));
        var submitter = await UserAsync(Clerk(type));

        var created = await clerk.PostAsJsonAsync("/api/contents", new
        {
            contentType = type,
            data = data ?? new Dictionary<string, object> { ["Title"] = "a claim" },
        });
        created.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}", created.StatusCode, await created.Content.ReadAsStringAsync());
        using var doc = System.Text.Json.JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var id = doc.RootElement.GetProperty("id").GetGuid();

        var submitted = await submitter.PutAsJsonAsync($"/api/contents/{id}/status", new { id, transition = "Submit" });
        submitted.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}", submitted.StatusCode, await submitted.Content.ReadAsStringAsync());
        return id;
    }

    private static Task<HttpResponseMessage> MoveAsync(
        HttpClient client, Guid id, string transition, Dictionary<string, object?>? data = null) =>
        client.PutAsJsonAsync($"/api/contents/{id}/status", new { id, transition, data });

    private async Task<Content> LoadAsync(Guid id)
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        return (await session.LoadAsync<Content>(id))!;
    }

    private static string? Value(Content content, string field) =>
        content.Data.FirstOrDefault(kv => kv.Key.Equals(field, StringComparison.OrdinalIgnoreCase)).Value?.ToString();

    // ---- the requirement ----------------------------------------------------------------------

    [Fact]
    public async Task A_reject_without_the_reason_is_refused_and_names_the_field()
    {
        var type = await TypeAsync();
        var id = await SubmittedAsync(type);
        var reviewer = await UserAsync(Reviewer(type));

        var refused = await MoveAsync(reviewer, id, "Reject");
        var body = await refused.Content.ReadAsStringAsync();

        refused.StatusCode.Should().Be(HttpStatusCode.BadRequest, body);
        body.Should().Contain("RejectionReason", "the caller has to be told which field to send");
        body.Should().NotContain("RejectionNote", "an optional field is not asked for");
        (await LoadAsync(id)).LifecycleState.Should().Be("Submitted", "a refused transition changes nothing");

        var allowed = await MoveAsync(reviewer, id, "Reject", new() { ["RejectionReason"] = "No receipt attached" });
        allowed.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}", allowed.StatusCode, await allowed.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task A_blank_reason_is_no_reason(string? reason)
    {
        var type = await TypeAsync();
        var id = await SubmittedAsync(type);
        var reviewer = await UserAsync(Reviewer(type));

        var refused = await MoveAsync(reviewer, id, "Reject", new() { ["RejectionReason"] = reason });
        var body = await refused.Content.ReadAsStringAsync();

        refused.StatusCode.Should().Be(HttpStatusCode.BadRequest, body);
        body.Should().Contain("RejectionReason");
        (await LoadAsync(id)).LifecycleState.Should().Be("Submitted");
    }

    [Fact]
    public async Task A_reject_with_the_reason_moves_the_entry_and_stores_what_was_sent()
    {
        var type = await TypeAsync();
        var id = await SubmittedAsync(type);
        var reviewer = await UserAsync(Reviewer(type));

        var res = await MoveAsync(reviewer, id, "Reject", new()
        {
            ["RejectionReason"] = "No receipt attached",
            ["RejectionNote"] = "Resubmit with the scan",
        });

        res.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}", res.StatusCode, await res.Content.ReadAsStringAsync());
        var content = await LoadAsync(id);
        content.LifecycleState.Should().Be("Rejected");
        Value(content, "RejectionReason").Should().Be("No receipt attached");
        Value(content, "RejectionNote").Should().Be("Resubmit with the scan");
        Value(content, "Title").Should().Be("a claim", "the fields the transition does not take are carried over");

        var history = await (await AdminAsync()).GetAsync($"/api/contents/{id}/history");
        history.IsSuccessStatusCode.Should().BeTrue();
        (await history.Content.ReadAsStringAsync()).Should().Contain("\"Updated\"",
            "the values are recorded as an update beside the transition");
    }

    [Fact]
    public async Task A_field_sent_in_another_casing_is_stored_under_the_fields_own_name()
    {
        var type = await TypeAsync();
        var id = await SubmittedAsync(type);
        var reviewer = await UserAsync(Reviewer(type));

        var res = await MoveAsync(reviewer, id, "Reject", new() { ["rejectionreason"] = "No receipt attached" });

        res.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}", res.StatusCode, await res.Content.ReadAsStringAsync());
        var content = await LoadAsync(id);
        content.Data.Keys.Where(k => k.Equals("RejectionReason", StringComparison.OrdinalIgnoreCase))
            .Should().Equal("RejectionReason");
        Value(content, "RejectionReason").Should().Be("No receipt attached");
    }

    /// <summary>
    /// The requirement is that the field holds a value once the move is made, so one already on the
    /// entry counts. Passes without the change too: it pins the reading, it does not prove the check.
    /// </summary>
    [Fact]
    public async Task A_reason_already_on_the_entry_meets_the_requirement()
    {
        var type = await TypeAsync();
        var id = await SubmittedAsync(type, new()
        {
            ["Title"] = "a claim",
            ["RejectionReason"] = "Flagged by the clerk",
        });
        var reviewer = await UserAsync(Reviewer(type));

        var res = await MoveAsync(reviewer, id, "Reject");

        res.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}", res.StatusCode, await res.Content.ReadAsStringAsync());
        var content = await LoadAsync(id);
        content.LifecycleState.Should().Be("Rejected");
        Value(content, "RejectionReason").Should().Be("Flagged by the clerk");
    }

    // ---- what a transition may write ----------------------------------------------------------

    [Fact]
    public async Task Data_may_carry_only_the_fields_the_transition_declares()
    {
        var type = await TypeAsync();
        var id = await SubmittedAsync(type);
        var reviewer = await UserAsync(Reviewer(type));

        var res = await MoveAsync(reviewer, id, "Reject", new()
        {
            ["RejectionReason"] = "No receipt attached",
            ["Title"] = "rewritten by the reviewer",
            ["Zq7Undeclared"] = "x",
        });
        var body = await res.Content.ReadAsStringAsync();

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "a transition permission is not an update permission, so it writes the declared fields and no others. {0}", body);
        body.Should().Contain("RejectionReason", "the answer names what the transition does take");
        body.Should().NotContain("Zq7Undeclared", "a key the caller typed is not echoed back");

        var content = await LoadAsync(id);
        content.LifecycleState.Should().Be("Submitted");
        Value(content, "Title").Should().Be("a claim");
        Value(content, "RejectionReason").Should().BeNull("nothing of a refused request is stored");
    }

    [Fact]
    public async Task A_value_sent_with_a_transition_is_validated_like_an_update()
    {
        var type = await TypeAsync(
        [
            new { name = "Title", displayName = "Title", type = "string" },
            new
            {
                name = "RejectionReason",
                displayName = "Rejection reason",
                type = "string",
                validationRules = new Dictionary<string, object> { ["maxLength"] = 10 },
            },
            new { name = "RejectionNote", displayName = "Rejection note", type = "string" },
        ]);
        var id = await SubmittedAsync(type);
        var reviewer = await UserAsync(Reviewer(type));

        var refused = await MoveAsync(reviewer, id, "Reject", new() { ["RejectionReason"] = "far longer than ten characters" });
        var body = await refused.Content.ReadAsStringAsync();

        refused.StatusCode.Should().Be(HttpStatusCode.BadRequest, body);
        body.Should().Contain("maxLength");
        var after = await LoadAsync(id);
        after.LifecycleState.Should().Be("Submitted");
        Value(after, "RejectionReason").Should().BeNull();

        var allowed = await MoveAsync(reviewer, id, "Reject", new() { ["RejectionReason"] = "too late" });
        allowed.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}", allowed.StatusCode, await allowed.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// A caller who may not see a field may not set it, by a transition either.
    /// </summary>
    /// <remarks>
    /// The value sent is dropped by the same write-path sensitivity an update runs, which leaves the
    /// required field blank, so the move is refused instead of going through with nothing stored.
    /// The control is an administrator, who may see the field, making the same request.
    /// </remarks>
    [Fact]
    public async Task A_field_the_caller_may_not_see_cannot_be_set_by_a_transition()
    {
        var fields = Fields();
        var reason = fields.Single(f => f.Name == "RejectionReason");
        reason.Sensitivity = SensitivityLevel.Sensitive;
        reason.VisibleToRoles = ["Payroll"];
        var type = await StoredTypeAsync(Review("RejectionReason"), fields);

        var id = await SubmittedAsync(type);
        var reviewer = await UserAsync(Reviewer(type));

        var refused = await MoveAsync(reviewer, id, "Reject", new() { ["RejectionReason"] = "No receipt attached" });
        var body = await refused.Content.ReadAsStringAsync();

        refused.StatusCode.Should().Be(HttpStatusCode.BadRequest, body);
        var after = await LoadAsync(id);
        after.LifecycleState.Should().Be("Submitted");
        Value(after, "RejectionReason").Should().BeNull();

        var allowed = await MoveAsync(await AdminAsync(), id, "Reject", new() { ["RejectionReason"] = "No receipt attached" });
        allowed.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}", allowed.StatusCode, await allowed.Content.ReadAsStringAsync());
        Value(await LoadAsync(id), "RejectionReason").Should().Be("No receipt attached");
    }

    // ---- what does not change -----------------------------------------------------------------

    /// <summary>Passes without the change too. It is the control for every refusal above.</summary>
    [Fact]
    public async Task A_transition_that_requires_nothing_behaves_as_before()
    {
        var type = await TypeAsync();
        var id = await SubmittedAsync(type);
        var reviewer = await UserAsync(Reviewer(type));

        var res = await MoveAsync(reviewer, id, "Approve");

        res.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}", res.StatusCode, await res.Content.ReadAsStringAsync());
        (await LoadAsync(id)).LifecycleState.Should().Be("Approved");
    }

    /// <summary>
    /// Passes without the change too. Data was an unknown property and was ignored, and a request
    /// that was accepted must not start being refused, or start writing.
    /// </summary>
    [Fact]
    public async Task Data_sent_to_a_transition_that_declares_no_fields_is_ignored()
    {
        var type = await TypeAsync();
        var id = await SubmittedAsync(type);
        var reviewer = await UserAsync(Reviewer(type));

        var res = await MoveAsync(reviewer, id, "Approve", new() { ["Title"] = "rewritten by the reviewer" });

        res.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}", res.StatusCode, await res.Content.ReadAsStringAsync());
        var content = await LoadAsync(id);
        content.LifecycleState.Should().Be("Approved");
        Value(content, "Title").Should().Be("a claim");
    }

    /// <summary>
    /// The permission check runs first, so a caller who may not reject is not told what a reject
    /// needs.
    /// </summary>
    /// <remarks>
    /// Passes without the change too, where every such request was a bare 403. It guards the order:
    /// a field check moved ahead of the permission check answers 400 naming the field here.
    /// </remarks>
    [Fact]
    public async Task A_caller_who_may_not_take_the_transition_learns_nothing_about_its_fields()
    {
        var type = await TypeAsync();
        var id = await SubmittedAsync(type);
        var clerk = await UserAsync(Clerk(type));

        var withoutReason = await MoveAsync(clerk, id, "Reject");
        var withReason = await MoveAsync(clerk, id, "Reject", new() { ["RejectionReason"] = "No receipt attached" });

        withoutReason.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await withoutReason.Content.ReadAsStringAsync()).Should().NotContain("RejectionReason");
        withReason.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var content = await LoadAsync(id);
        content.LifecycleState.Should().Be("Submitted");
        Value(content, "RejectionReason").Should().BeNull("a refused caller writes nothing");
    }

    // ---- saving the type ----------------------------------------------------------------------

    [Theory]
    [InlineData("required")]
    [InlineData("optional")]
    public async Task A_type_saved_with_a_transition_naming_an_unknown_field_is_refused(string list)
    {
        var lifecycle = Review("RejectionReason");
        var reject = lifecycle.Transitions.Single(t => t.Name == "Reject");
        if (list == "required")
            reject.RequiredFields = ["RejectionReason", "Verdict"];
        else
            reject.OptionalFields = ["Verdict"];

        var res = await PostTypeAsync(await AdminAsync(), NewName("bad"), lifecycle);
        var body = await res.Content.ReadAsStringAsync();

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest, body);
        body.Should().Contain("Verdict");
        body.Should().Contain("Reject");
    }

    [Fact]
    public async Task A_type_saved_with_a_field_both_required_and_optional_is_refused()
    {
        var lifecycle = Review("RejectionReason");
        lifecycle.Transitions.Single(t => t.Name == "Reject").OptionalFields = ["rejectionreason"];

        var res = await PostTypeAsync(await AdminAsync(), NewName("bad"), lifecycle);
        var body = await res.Content.ReadAsStringAsync();

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest, body);
        body.Should().Contain("more than once");
    }

    /// <summary>The control for the two refusals above, and the lists come back as they were sent.</summary>
    [Fact]
    public async Task A_type_whose_transition_names_declared_fields_is_saved_with_them()
    {
        var type = await TypeAsync();

        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        var stored = await session.Query<ContentTypeDefinition>().FirstAsync(d => d.Name == type);

        var reject = stored.Lifecycle!.Transitions.Single(t => t.Name == "Reject");
        reject.RequiredFields.Should().Equal("RejectionReason");
        reject.OptionalFields.Should().Equal("RejectionNote");
        var approve = stored.Lifecycle.Transitions.Single(t => t.Name == "Approve");
        approve.RequiredFields.Should().BeEmpty();
        approve.OptionalFields.Should().BeEmpty();
    }

    /// <summary>
    /// A stored transition naming a field the type no longer has is skipped, and the rest of what it
    /// requires still applies.
    /// </summary>
    /// <remarks>
    /// The type is stored through the session because the API refuses to save it. Requiring a field
    /// that does not exist can never be met, so applying it would leave every entry stuck.
    /// </remarks>
    [Fact]
    public async Task A_stored_transition_naming_a_missing_field_skips_that_name_and_keeps_the_rest()
    {
        var type = await StoredTypeAsync(Review("Verdict", "RejectionReason"), Fields());
        var id = await SubmittedAsync(type);
        var reviewer = await UserAsync(Reviewer(type));

        var refused = await MoveAsync(reviewer, id, "Reject");
        var body = await refused.Content.ReadAsStringAsync();

        refused.StatusCode.Should().Be(HttpStatusCode.BadRequest, body);
        body.Should().Contain("RejectionReason");
        body.Should().NotContain("Verdict", "a field the type does not have is not asked for");

        var allowed = await MoveAsync(reviewer, id, "Reject", new() { ["RejectionReason"] = "No receipt attached" });
        allowed.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}", allowed.StatusCode, await allowed.Content.ReadAsStringAsync());
        (await LoadAsync(id)).LifecycleState.Should().Be("Rejected");
    }

    // ---- the workflow on the transition -------------------------------------------------------

    /// <summary>
    /// The workflow on Reject reads the reason, and a workflow on Updated fires as well, because
    /// the data did change.
    /// </summary>
    [Fact]
    public async Task A_workflow_on_the_transition_reads_the_value_sent_with_it()
    {
        var type = await TypeAsync();
        var onReject = NewName("probe");
        var onUpdated = NewName("probe");
        var id = await SubmittedAsync(type);

        // Stored after the entry exists, so the Updated probe counts only what the reject wrote.
        await StoreWorkflowAsync(type, WorkflowEvents.ForTransition("Reject"), onReject);
        await StoreWorkflowAsync(type, WorkflowEvents.Updated, onUpdated);
        var reviewer = await UserAsync(Reviewer(type));

        var res = await MoveAsync(reviewer, id, "Reject", new() { ["RejectionReason"] = "No receipt attached" });
        res.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}", res.StatusCode, await res.Content.ReadAsStringAsync());

        var rejected = await ProbesAsync(onReject);
        rejected.Should().HaveCount(1);
        Value(rejected[0], "Title").Should().Be("Reason: No receipt attached");

        var updated = await ProbesAsync(onUpdated);
        updated.Should().HaveCount(1);
        Value(updated[0], "Title").Should().Be("Reason: No receipt attached");
    }

    /// <summary>Stored straight through the session, so a firing test does not depend on validation.</summary>
    private async Task StoreWorkflowAsync(string type, string triggerEvent, string probeContentType)
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Store(new WorkflowDefinition
        {
            Id = Guid.NewGuid(),
            Name = NewName("wf"),
            TriggerContentType = type,
            TriggerEvent = triggerEvent,
            Actions =
            [
                new WorkflowAction
                {
                    Type = "CreateTask",
                    Parameters = new Dictionary<string, string>
                    {
                        ["ContentType"] = probeContentType,
                        ["Title"] = "Reason: {{data.RejectionReason}}",
                    },
                },
            ],
        });
        await session.SaveChangesAsync();
    }

    private async Task<List<Content>> ProbesAsync(string probeContentType)
    {
        var deadline = DateTime.UtcNow + PollTimeout;

        while (DateTime.UtcNow < deadline)
        {
            using var scope = _factory.Services.CreateScope();
            var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
            var found = await session.Query<Content>().Where(c => c.ContentType == probeContentType).ToListAsync();
            if (found.Count > 0)
                return found.ToList();

            await Task.Delay(TimeSpan.FromMilliseconds(250));
        }

        throw new Xunit.Sdk.XunitException(
            $"Timed out after {PollTimeout.TotalSeconds:0}s: the workflow on Reject never created a '{probeContentType}' item.");
    }
}
