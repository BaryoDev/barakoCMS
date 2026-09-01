using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using barakoCMS.Models;

namespace BarakoCMS.Tests;

/// <summary>
/// A content type may declare its own states, and the moves between them are enforced by the server.
/// </summary>
/// <remarks>
/// `ContentStatus` is Draft, Published, Archived, in the core, for every type. That is right for a
/// blog post and wrong for an invoice, which is Draft, Submitted, Approved, Sent, Paid.
///
/// Two properties matter more than the feature itself and both are asserted here. A type that
/// declares no lifecycle has to behave exactly as it did before this existed, because every type
/// that exists is one of those. And a custom lifecycle must not touch `ContentStatus`, which is what
/// public delivery reads: an invoice moving to Approved is not an invoice becoming publicly visible.
/// </remarks>
[Collection("Sequential")]
public class ContentLifecycleTests
{
    private readonly IntegrationTestFixture _factory;

    public ContentLifecycleTests(IntegrationTestFixture factory) => _factory = factory;

    private static LifecycleDefinition InvoiceLifecycle() => new()
    {
        States = ["Draft", "Submitted", "Approved", "Paid"],
        InitialState = "Draft",
        Transitions =
        [
            new StateTransition { Name = "Submit", From = "Draft", To = "Submitted" },
            new StateTransition { Name = "Approve", From = "Submitted", To = "Approved" },
            new StateTransition { Name = "Pay", From = "Approved", To = "Paid" },
        ],
    };

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

        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = $"life_{Guid.NewGuid():n}",
            Email = $"life_{Guid.NewGuid():n}@example.com",
            RoleIds = roleIds,
        };
        session.Store(user);
        await session.SaveChangesAsync();

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer", _factory.CreateToken(roles: ["SuperAdmin", "Admin"], userId: user.Id.ToString()));
        return client;
    }

    private async Task<string> TypeAsync(HttpClient client, LifecycleDefinition? lifecycle)
    {
        var name = "life" + Guid.NewGuid().ToString("n")[..8];
        var res = await client.PostAsJsonAsync("/api/content-types", new
        {
            name,
            displayName = "Lifecycle Test",
            fields = new[] { new { name = "Title", type = "string" } },
            lifecycle,
        });
        res.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}", res.StatusCode, await res.Content.ReadAsStringAsync());
        return name;
    }

    private static async Task<Guid> EntryAsync(HttpClient client, string type)
    {
        var res = await client.PostAsJsonAsync("/api/contents", new
        {
            contentType = type,
            data = new Dictionary<string, object> { ["Title"] = "an entry" },
        });
        res.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}", res.StatusCode, await res.Content.ReadAsStringAsync());
        using var doc = System.Text.Json.JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("id").GetGuid();
    }

    private async Task<Content> LoadAsync(Guid id)
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        return (await session.LoadAsync<Content>(id))!;
    }

    // ---- the type that declares nothing, which is every type that exists today ----------------

    /// <summary>
    /// A content type with no lifecycle behaves exactly as it did before lifecycles existed.
    /// </summary>
    /// <remarks>
    /// The control that matters most. Every content type in every deployment has no lifecycle, so a
    /// change that quietly altered their behaviour would break all of them, and every other test on
    /// this page would still pass.
    /// </remarks>
    [Fact]
    public async Task A_type_with_no_lifecycle_still_takes_a_status()
    {
        var client = await AdminAsync();
        var type = await TypeAsync(client, lifecycle: null);
        var id = await EntryAsync(client, type);

        var res = await client.PutAsJsonAsync($"/api/contents/{id}/status", new { id, newStatus = "Published" });

        res.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}", res.StatusCode, await res.Content.ReadAsStringAsync());
        var content = await LoadAsync(id);
        content.Status.Should().Be(ContentStatus.Published);
        content.LifecycleState.Should().BeNull("a type that declares no lifecycle never gets a state");
    }

    [Fact]
    public async Task A_type_with_no_lifecycle_refuses_a_transition()
    {
        var client = await AdminAsync();
        var type = await TypeAsync(client, lifecycle: null);
        var id = await EntryAsync(client, type);

        var res = await client.PutAsJsonAsync($"/api/contents/{id}/status", new { id, transition = "Approve" });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "a caller naming a transition on a type that has none has misunderstood something");
    }

    // ---- the type that declares one -----------------------------------------------------------

    [Fact]
    public async Task A_new_entry_starts_at_the_declared_initial_state()
    {
        var client = await AdminAsync();
        var type = await TypeAsync(client, InvoiceLifecycle());
        var id = await EntryAsync(client, type);

        (await LoadAsync(id)).LifecycleState.Should().Be("Draft");
    }

    [Fact]
    public async Task A_declared_transition_moves_the_entry()
    {
        var client = await AdminAsync();
        var type = await TypeAsync(client, InvoiceLifecycle());
        var id = await EntryAsync(client, type);

        var res = await client.PutAsJsonAsync($"/api/contents/{id}/status", new { id, transition = "Submit" });

        res.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}", res.StatusCode, await res.Content.ReadAsStringAsync());
        (await LoadAsync(id)).LifecycleState.Should().Be("Submitted");
    }

    /// <summary>
    /// A transition out of the wrong state is refused, and the paired case succeeds.
    /// </summary>
    /// <remarks>
    /// The pair is the point. Without the second half, a check that refused every transition would
    /// satisfy the first, and the feature would look enforced while being broken.
    /// </remarks>
    [Fact]
    public async Task A_transition_from_the_wrong_state_is_refused()
    {
        var client = await AdminAsync();
        var type = await TypeAsync(client, InvoiceLifecycle());
        var id = await EntryAsync(client, type);

        // Draft, and Approve moves Submitted to Approved.
        var refused = await client.PutAsJsonAsync($"/api/contents/{id}/status", new { id, transition = "Approve" });

        refused.StatusCode.Should().Be(HttpStatusCode.Conflict,
            "Paid does not go back to Draft, and Draft does not jump to Approved");
        (await LoadAsync(id)).LifecycleState.Should().Be("Draft", "a refused transition changes nothing");

        var allowed = await client.PutAsJsonAsync($"/api/contents/{id}/status", new { id, transition = "Submit" });
        allowed.IsSuccessStatusCode.Should().BeTrue("the declared move from Draft still works");
    }

    [Fact]
    public async Task An_undeclared_transition_is_refused_and_names_the_declared_ones()
    {
        var client = await AdminAsync();
        var type = await TypeAsync(client, InvoiceLifecycle());
        var id = await EntryAsync(client, type);

        var res = await client.PutAsJsonAsync($"/api/contents/{id}/status", new { id, transition = "Teleport" });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await res.Content.ReadAsStringAsync();
        body.Should().Contain("Submit", "the caller can only fix a mistake they are told about");
    }

    [Fact]
    public async Task A_type_with_a_lifecycle_refuses_a_status()
    {
        var client = await AdminAsync();
        var type = await TypeAsync(client, InvoiceLifecycle());
        var id = await EntryAsync(client, type);

        var res = await client.PutAsJsonAsync($"/api/contents/{id}/status", new { id, newStatus = "Published" });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "picking one of the two silently would hide the misunderstanding");
    }

    /// <summary>
    /// A transition does not touch ContentStatus, and therefore does not touch public delivery.
    /// </summary>
    /// <remarks>
    /// The separation this whole design rests on. `ContentStatus` decides whether the public sees an
    /// entry; a custom lifecycle decides where it sits in the type's own workflow. An invoice
    /// reaching Approved must not become publicly visible as a side effect, and the fact that both
    /// are called "status" in ordinary speech is exactly why it needs a test.
    /// </remarks>
    [Fact]
    public async Task A_transition_leaves_the_delivery_status_alone()
    {
        var client = await AdminAsync();
        var type = await TypeAsync(client, InvoiceLifecycle());
        var id = await EntryAsync(client, type);

        var before = (await LoadAsync(id)).Status;

        await client.PutAsJsonAsync($"/api/contents/{id}/status", new { id, transition = "Submit" });
        await client.PutAsJsonAsync($"/api/contents/{id}/status", new { id, transition = "Approve" });

        var after = await LoadAsync(id);
        after.LifecycleState.Should().Be("Approved");
        after.Status.Should().Be(before,
            "approving an invoice is not publishing it, and ContentStatus is what delivery reads");
    }

    [Fact]
    public async Task A_transition_appears_in_the_history()
    {
        var client = await AdminAsync();
        var type = await TypeAsync(client, InvoiceLifecycle());
        var id = await EntryAsync(client, type);

        await client.PutAsJsonAsync($"/api/contents/{id}/status", new { id, transition = "Submit" });

        var res = await client.GetAsync($"/api/contents/{id}/history");
        res.IsSuccessStatusCode.Should().BeTrue();
        var body = await res.Content.ReadAsStringAsync();

        body.Should().Contain("Transitioned", "a state change is a change, and history reports every event");
        body.Should().NotContain("ContentTransitioned",
            "the CLR type name is not the wire vocabulary, per #229");
    }

    // ---- declaration time ----------------------------------------------------------------------

    [Theory]
    [InlineData("initial state not in states")]
    [InlineData("transition from an undeclared state")]
    [InlineData("duplicate transition name")]
    public async Task An_incoherent_lifecycle_is_refused_at_declaration(string kind)
    {
        var client = await AdminAsync();

        var lifecycle = kind switch
        {
            "initial state not in states" => new LifecycleDefinition
            {
                States = ["Draft", "Approved"], InitialState = "Nowhere",
                Transitions = [new StateTransition { Name = "Approve", From = "Draft", To = "Approved" }],
            },
            "transition from an undeclared state" => new LifecycleDefinition
            {
                States = ["Draft", "Approved"], InitialState = "Draft",
                Transitions = [new StateTransition { Name = "Approve", From = "Elsewhere", To = "Approved" }],
            },
            _ => new LifecycleDefinition
            {
                States = ["Draft", "Approved"], InitialState = "Draft",
                Transitions =
                [
                    new StateTransition { Name = "Approve", From = "Draft", To = "Approved" },
                    new StateTransition { Name = "Approve", From = "Approved", To = "Draft" },
                ],
            },
        };

        var res = await client.PostAsJsonAsync("/api/content-types", new
        {
            name = "bad" + Guid.NewGuid().ToString("n")[..8],
            displayName = "Bad",
            fields = new[] { new { name = "Title", type = "string" } },
            lifecycle,
        });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "an incoherent lifecycle refused at declaration is one that cannot strand entries later");
    }

    /// <summary>The control for the theory above. A coherent lifecycle is accepted.</summary>
    [Fact]
    public async Task A_coherent_lifecycle_is_accepted()
    {
        var client = await AdminAsync();

        var name = await TypeAsync(client, InvoiceLifecycle());

        name.Should().NotBeEmpty();
    }
}
