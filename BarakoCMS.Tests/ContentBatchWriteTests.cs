using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using barakoCMS.Models;
using BarakoCMS.Tests.Rules;
using FluentAssertions;
using Marten;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using static BarakoCMS.Tests.Rules.RuleKit;

namespace BarakoCMS.Tests;

/// <summary>
/// POST /api/contents/batch: creates and transitions in one transaction, each step held to the
/// checks of the single create and the status change.
/// </summary>
/// <remarks>
/// The host allows a creator to make the Issue move on their own entry, because a batch that
/// creates an entry and issues it is the creator moving it on, and the default policy refuses that.
/// </remarks>
[Collection("Integration3")]
public class ContentBatchWriteTests(IntegrationTestFixture host)
{
    private const string Refusal = "A order is required.";

    private static readonly Lock HostGate = new();
    private static WebApplicationFactory<Program>? _relaxed;

    private WebApplicationFactory<Program> Relaxed()
    {
        lock (HostGate)
        {
            return _relaxed ??= host.WithSettings(new Dictionary<string, string?>
            {
                ["Lifecycle:AllowSelfTransition:Issue"] = "true",
            });
        }
    }

    private async Task<string> TypeAsync(bool withRule = false)
    {
        var name = $"cbw{Guid.NewGuid():n}"[..20];
        var rules = new List<RuleDefinition>();
        if (withRule)
        {
            var rule = Rule("order", Cond("Order", "notEmpty"));
            rule.When = RuleWhen.Transition;
            rule.Transition = "Issue";
            rule.Message = Refusal;
            rules.Add(rule);
        }

        using var scope = host.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Store(new ContentTypeDefinition
        {
            Id = Guid.NewGuid(),
            Name = name,
            DisplayName = "Order",
            Fields =
            [
                new FieldDefinition { Name = "Title", Type = "string" },
                new FieldDefinition { Name = "Order", Type = "string" },
            ],
            Lifecycle = new LifecycleDefinition
            {
                States = ["Draft", "Issued"],
                InitialState = "Draft",
                Transitions = [new StateTransition { Name = "Issue", From = "Draft", To = "Issued" }],
            },
            Rules = rules,
        });
        await session.SaveChangesAsync();
        return name;
    }

    private async Task<HttpClient> AdminAsync()
    {
        var client = Relaxed().CreateClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer", await host.StoredUserTokenAsync("Admin", "SuperAdmin"));
        return client;
    }

    /// <summary>A user whose role grants exactly the permission given.</summary>
    private async Task<HttpClient> UserAsync(ContentTypePermission permission)
    {
        using var scope = host.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var role = new Role { Id = Guid.NewGuid(), Name = $"Role_{Guid.NewGuid():n}", Permissions = [permission] };
        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = $"cbw_{Guid.NewGuid():n}",
            Email = $"cbw_{Guid.NewGuid():n}@example.com",
            RoleIds = [role.Id],
        };
        session.Store(role);
        session.Store(user);
        await session.SaveChangesAsync();

        var client = Relaxed().CreateClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer", host.CreateToken(roles: [role.Name], userId: user.Id.ToString()));
        return client;
    }

    private static object Create(string type, Guid id, string title = "a order", string? order = null)
    {
        var data = new Dictionary<string, object> { ["Title"] = title };
        if (order is not null) data["Order"] = order;
        return new { op = "create", contentType = type, id, data };
    }

    private static object Move(Guid id, string transition = "Issue") => new { op = "transition", id, transition };

    private static Task<HttpResponseMessage> BatchAsync(HttpClient client, params object[] steps) =>
        client.PostAsJsonAsync("/api/contents/batch", new { steps });

    private static async Task<JsonElement> ErrorAsync(HttpResponseMessage res)
    {
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("errors")[0].Clone();
    }

    private async Task<int> RowsAsync(string type)
    {
        using var scope = host.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IQuerySession>()
            .Query<Content>().CountAsync(c => c.ContentType == type, TestContext.Current.CancellationToken);
    }

    private async Task<Content?> LoadAsync(Guid id)
    {
        using var scope = host.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IQuerySession>()
            .LoadAsync<Content>(id, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Create_and_transition_in_one_request()
    {
        var admin = await AdminAsync();
        var type = await TypeAsync();
        var id = Guid.CreateVersion7();

        var res = await BatchAsync(admin, Create(type, id), Move(id));

        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        var steps = doc.RootElement.GetProperty("steps");
        steps.GetArrayLength().Should().Be(2);
        steps[0].GetProperty("state").GetString().Should().Be("Draft");
        steps[1].GetProperty("index").GetInt32().Should().Be(1);
        steps[1].GetProperty("state").GetString().Should().Be("Issued");
        (await LoadAsync(id))!.LifecycleState.Should().Be("Issued");
    }

    [Fact]
    public async Task Several_creates_commit_together()
    {
        var admin = await AdminAsync();
        var type = await TypeAsync();

        var res = await BatchAsync(admin,
            Create(type, Guid.CreateVersion7()), Create(type, Guid.CreateVersion7()), Create(type, Guid.CreateVersion7()));

        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        (await RowsAsync(type)).Should().Be(3);
    }

    [Fact]
    public async Task Unknown_op_is_400()
    {
        var admin = await AdminAsync();

        var res = await BatchAsync(admin, new { op = "delete", id = Guid.CreateVersion7() });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Empty_steps_is_400()
    {
        var admin = await AdminAsync();

        (await BatchAsync(admin)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task More_than_100_steps_is_400()
    {
        var admin = await AdminAsync();
        var type = await TypeAsync();

        var res = await BatchAsync(admin, Enumerable.Range(0, 101).Select(_ => Create(type, Guid.CreateVersion7())).ToArray());

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await RowsAsync(type)).Should().Be(0);
    }

    [Fact]
    public async Task A_transition_of_a_missing_entry_is_404_and_names_the_step()
    {
        var admin = await AdminAsync();
        var type = await TypeAsync();

        var res = await BatchAsync(admin, Create(type, Guid.CreateVersion7()), Move(Guid.CreateVersion7()));

        res.StatusCode.Should().Be(HttpStatusCode.NotFound, await res.Content.ReadAsStringAsync());
        (await ErrorAsync(res)).GetProperty("name").GetString().Should().Be("steps[1]");
    }

    private static async Task<HttpResponseMessage> KeyedAsync(HttpClient client, string key, params object[] steps)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/contents/batch") { Content = JsonContent.Create(new { steps }) };
        req.Headers.Add("Idempotency-Key", key);
        return await client.SendAsync(req, TestContext.Current.CancellationToken);
    }

    private static bool Replayed(HttpResponseMessage res) =>
        res.Headers.TryGetValues(barakoCMS.Infrastructure.Filters.IdempotencyFilter.ReplayedHeader, out var values)
        && values.Contains("true");

    /// <summary>True when nothing of the entry is left: no document, no stream, no audit row, no workflow run.</summary>
    private async Task<bool> LeftNothingAsync(params Guid[] ids)
    {
        using var scope = host.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        var token = TestContext.Current.CancellationToken;

        foreach (var id in ids)
        {
            var text = id.ToString();
            if (await session.LoadAsync<Content>(id, token) is not null
                || (await session.Events.FetchStreamAsync(id, token: token)).Count > 0
                || await session.Query<AuditEvent>().AnyAsync(a => a.TargetId == text, token)
                || await session.Query<WorkflowRun>().AnyAsync(r => r.ContentId == id, token))
            {
                return false;
            }
        }

        return true;
    }

    [Fact]
    public async Task A_failing_third_step_saves_nothing()
    {
        var admin = await AdminAsync();
        var type = await TypeAsync();
        Guid a = Guid.CreateVersion7(), b = Guid.CreateVersion7();

        var res = await BatchAsync(admin, Create(type, a), Create(type, b), Move(Guid.CreateVersion7()));

        res.StatusCode.Should().Be(HttpStatusCode.NotFound, await res.Content.ReadAsStringAsync());
        (await ErrorAsync(res)).GetProperty("name").GetString().Should().Be("steps[2]");
        (await RowsAsync(type)).Should().Be(0);
        (await LeftNothingAsync(a, b)).Should().BeTrue();
    }

    [Fact]
    public async Task A_failing_rule_on_step_two_saves_nothing()
    {
        var admin = await AdminAsync();
        var type = await TypeAsync(withRule: true);
        var id = Guid.CreateVersion7();

        var res = await BatchAsync(admin, Create(type, id), Move(id));

        res.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity, await res.Content.ReadAsStringAsync());
        var error = await ErrorAsync(res);
        error.GetProperty("name").GetString().Should().Be("steps[1]");
        error.GetProperty("reason").GetString().Should().Be(Refusal);
        (await LeftNothingAsync(id)).Should().BeTrue();
    }

    [Fact]
    public async Task A_passing_rule_lets_the_batch_commit()
    {
        var admin = await AdminAsync();
        var type = await TypeAsync(withRule: true);
        var id = Guid.CreateVersion7();

        var res = await BatchAsync(admin, Create(type, id, order: "V1"), Move(id));

        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        (await LoadAsync(id))!.LifecycleState.Should().Be("Issued");
    }

    [Fact]
    public async Task Each_step_checks_permissions()
    {
        var type = await TypeAsync();

        // May create and read, holds no transition permission.
        var clerk = await UserAsync(new ContentTypePermission
        {
            ContentTypeSlug = type,
            Create = new PermissionRule { Enabled = true },
            Read = new PermissionRule { Enabled = true },
        });
        var id = Guid.CreateVersion7();

        var moved = await BatchAsync(clerk, Create(type, id), Move(id));

        moved.StatusCode.Should().Be(HttpStatusCode.Forbidden, await moved.Content.ReadAsStringAsync());
        (await ErrorAsync(moved)).GetProperty("name").GetString().Should().Be("steps[1]");
        (await LeftNothingAsync(id)).Should().BeTrue();

        // Holds nothing on the type.
        var outsider = await UserAsync(new ContentTypePermission { ContentTypeSlug = type });
        var other = Guid.CreateVersion7();

        var created = await BatchAsync(outsider, Create(type, other));

        created.StatusCode.Should().Be(HttpStatusCode.Forbidden, await created.Content.ReadAsStringAsync());
        (await ErrorAsync(created)).GetProperty("name").GetString().Should().Be("steps[0]");
        (await LeftNothingAsync(other)).Should().BeTrue();
    }

    [Fact]
    public async Task A_retry_with_the_same_Idempotency_Key_replays_the_stored_answer()
    {
        var admin = await AdminAsync();
        var type = await TypeAsync();
        var id = Guid.CreateVersion7();
        var key = $"batch-{Guid.NewGuid():n}";

        var first = await KeyedAsync(admin, key, Create(type, id), Move(id));
        var second = await KeyedAsync(admin, key, Create(type, id), Move(id));

        first.StatusCode.Should().Be(HttpStatusCode.OK, await first.Content.ReadAsStringAsync());
        Replayed(first).Should().BeFalse();
        second.StatusCode.Should().Be(HttpStatusCode.OK);
        Replayed(second).Should().BeTrue();
        (await second.Content.ReadAsStringAsync()).Should().Be(await first.Content.ReadAsStringAsync());
        (await RowsAsync(type)).Should().Be(1);
    }

    /// <summary>
    /// The filter keeps a key only for an answer in the 2xx range, so a failed batch is run again by
    /// its retry, fails the same way, and still saves nothing.
    /// </summary>
    [Fact]
    public async Task A_failing_batch_retried_with_the_same_key_runs_again_and_saves_nothing()
    {
        var admin = await AdminAsync();
        var type = await TypeAsync();
        var id = Guid.CreateVersion7();
        var key = $"batch-{Guid.NewGuid():n}";
        var steps = new[] { Create(type, id), Move(Guid.CreateVersion7()) };

        var first = await KeyedAsync(admin, key, steps);
        var second = await KeyedAsync(admin, key, steps);

        first.StatusCode.Should().Be(HttpStatusCode.NotFound);
        second.StatusCode.Should().Be(HttpStatusCode.NotFound);
        Replayed(second).Should().BeFalse();
        (await LeftNothingAsync(id)).Should().BeTrue();
    }

    [Fact]
    public async Task The_same_key_with_a_different_body_is_refused()
    {
        var admin = await AdminAsync();
        var type = await TypeAsync();
        var key = $"batch-{Guid.NewGuid():n}";

        (await KeyedAsync(admin, key, Create(type, Guid.CreateVersion7()))).StatusCode.Should().Be(HttpStatusCode.OK);
        var other = await KeyedAsync(admin, key, Create(type, Guid.CreateVersion7()));

        other.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await RowsAsync(type)).Should().Be(1);
    }

    [Fact]
    public async Task A_duplicate_client_id_in_two_steps_is_refused_and_saves_nothing()
    {
        var admin = await AdminAsync();
        var type = await TypeAsync();
        var id = Guid.CreateVersion7();

        var res = await BatchAsync(admin, Create(type, id), Create(type, id, title: "again"));

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest, await res.Content.ReadAsStringAsync());
        var error = await ErrorAsync(res);
        error.GetProperty("name").GetString().Should().Be("steps[1]");
        error.GetProperty("reason").GetString().Should().Be("Id was already used in this batch.");
        (await LeftNothingAsync(id)).Should().BeTrue();
    }
}
