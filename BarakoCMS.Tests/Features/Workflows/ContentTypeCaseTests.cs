using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using barakoCMS.Models;

namespace BarakoCMS.Tests.Features.Workflows;

/// <summary>
/// A content type is found by name ignoring case. Config apply and POST /api/content-types store the
/// name lowercase, a module that seeds a type keeps the case it gave, and a client or a workflow
/// names the type however it likes.
/// </summary>
/// <remarks>
/// Each case is run both ways: a type stored lowercase and named in mixed case, and a type stored in
/// mixed case (stored straight through the session, as a module seeds it) and named lowercase.
/// A difference of more than case stays a different type, and is the control for each.
/// </remarks>
[Collection("Integration3")]
public class ContentTypeCaseTests
{
    private static readonly TimeSpan PollTimeout = TimeSpan.FromSeconds(30);

    private readonly IntegrationTestFixture _factory;
    private readonly HttpClient _client;
    private readonly HttpClient _approver;

    public ContentTypeCaseTests(IntegrationTestFixture factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
        _approver = factory.CreateClient();
    }

    // Validator

    [Fact]
    public async Task A_transition_trigger_naming_a_lowercase_stored_type_in_mixed_case_is_accepted()
    {
        await AuthenticateAsync();
        var type = await CreateLowercaseTypeAsync();

        var res = await PostTransitionWorkflowAsync(Mixed(type));

        res.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}", res.StatusCode, await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_transition_trigger_naming_a_mixed_case_stored_type_in_lowercase_is_accepted()
    {
        await AuthenticateAsync();
        var type = await StoreMixedTypeAsync();

        var res = await PostTransitionWorkflowAsync(type.ToLowerInvariant());

        res.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}", res.StatusCode, await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_transition_trigger_naming_a_type_that_differs_by_more_than_case_is_still_refused()
    {
        await AuthenticateAsync();
        var type = await CreateLowercaseTypeAsync();

        var res = await PostTransitionWorkflowAsync(Mixed(type) + "s");

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest, "'{0}s' is another type, not the same one in another case", type);
        (await res.Content.ReadAsStringAsync()).Should().Contain("does not exist");
    }

    // Trigger firing

    [Fact]
    public async Task A_workflow_naming_the_type_in_mixed_case_fires_for_an_entry_of_the_lowercase_type()
    {
        await AuthenticateAsync();
        var type = await CreateLowercaseTypeAsync();
        var probe = NewName("probe");
        await StoreWorkflowAsync(Mixed(type), WorkflowEvents.Created, probe);

        await CreateContentAsync(type);

        (await WaitForAsync(probe)).Should().Be(1);
    }

    [Fact]
    public async Task A_workflow_naming_the_type_in_lowercase_fires_for_an_entry_spelled_in_mixed_case()
    {
        await AuthenticateAsync();
        var type = await CreateLowercaseTypeAsync();
        var probe = NewName("probe");
        await StoreWorkflowAsync(type, WorkflowEvents.Created, probe);

        await CreateContentAsync(Mixed(type));

        (await WaitForAsync(probe)).Should().Be(1);
    }

    [Fact]
    public async Task A_workflow_does_not_fire_for_an_entry_of_a_type_that_differs_by_more_than_case()
    {
        await AuthenticateAsync();
        var type = await CreateLowercaseTypeAsync();
        var other = await CreateLowercaseTypeAsync();
        var probe = NewName("probe");
        await StoreWorkflowAsync(Mixed(type), WorkflowEvents.Created, probe);

        await CreateContentAsync(other);
        await Task.Delay(TimeSpan.FromSeconds(3));

        (await CountAsync(probe)).Should().Be(0);

        await CreateContentAsync(type);
        (await WaitForAsync(probe)).Should().Be(1, "the same workflow still fires for its own type");
    }

    // Transition

    [Fact]
    public async Task A_transition_on_an_entry_spelled_in_mixed_case_finds_the_lowercase_type()
    {
        await AuthenticateAsync();
        var type = await CreateLowercaseTypeAsync();
        var id = await CreateContentAsync(Mixed(type));

        var res = await TransitionAsync(id, "Submit");

        res.StatusCode.Should().Be(HttpStatusCode.OK, "got {0}: {1}", res.StatusCode, await res.Content.ReadAsStringAsync());
        (await StateOfAsync(id)).Should().Be("Submitted");
    }

    [Fact]
    public async Task A_transition_on_an_entry_spelled_in_lowercase_finds_the_mixed_case_type()
    {
        await AuthenticateAsync();
        var type = await StoreMixedTypeAsync();
        var id = await CreateContentAsync(type.ToLowerInvariant());

        var res = await TransitionAsync(id, "Submit");

        res.StatusCode.Should().Be(HttpStatusCode.OK, "got {0}: {1}", res.StatusCode, await res.Content.ReadAsStringAsync());
        (await StateOfAsync(id)).Should().Be("Submitted");
    }

    // Permissions: the case only finds the type, it never widens a grant or beats a deny

    [Fact]
    public async Task A_grant_on_a_lowercase_type_covers_the_same_type_named_in_mixed_case_and_no_other()
    {
        var type = NewName("perm");
        var token = await UserWithCreateAsync((type, true));
        var caller = _factory.CreateClient();
        caller.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        (await CreateAsync(caller, Mixed(type))).StatusCode.Should().Be(HttpStatusCode.OK);
        (await CreateAsync(caller, type + "x")).StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "a different name is a different type");
    }

    [Fact]
    public async Task A_deny_on_a_type_holds_when_the_caller_changes_the_case()
    {
        var type = NewName("deny");
        var token = await UserWithCreateAsync((type, false));
        var caller = _factory.CreateClient();
        caller.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        (await CreateAsync(caller, type)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await CreateAsync(caller, Mixed(type))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await CreateAsync(caller, type.ToUpperInvariant())).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task The_exact_spelling_decides_when_a_role_holds_two_that_differ_only_in_case()
    {
        var type = NewName("both");
        var token = await UserWithCreateAsync((Mixed(type), false), (type, true));
        var caller = _factory.CreateClient();
        caller.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        (await CreateAsync(caller, Mixed(type))).StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "the entry that spells the name exactly is the one that answers, as it did before");
        (await CreateAsync(caller, type)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // Listing

    [Fact]
    public async Task Listing_by_a_type_named_in_mixed_case_returns_the_entries_of_the_lowercase_type()
    {
        await AuthenticateAsync();
        var type = await CreateLowercaseTypeAsync();
        var id = await CreateContentAsync(type);

        var res = await _client.GetAsync($"/api/contents?contentType={Mixed(type)}&pageSize=50");
        var body = await res.Content.ReadAsStringAsync();

        res.StatusCode.Should().Be(HttpStatusCode.OK, body);
        body.Should().Contain(id.ToString());
    }

    // Helpers

    private async Task AuthenticateAsync()
    {
        var (token, _) = await TestHelpers.CreateAdminUserAsync(_factory);
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var (approverToken, _) = await TestHelpers.CreateAdminUserAsync(_factory);
        _approver.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", approverToken);
    }

    private static string NewName(string prefix) => $"{prefix}{Guid.NewGuid():N}"[..20];

    /// <summary>The same name with some letters upper case, so it is the same type in another spelling.</summary>
    private static string Mixed(string name) => char.ToUpperInvariant(name[0]) + name[1..^1] + char.ToUpperInvariant(name[^1]);

    private static LifecycleDefinition Lifecycle() => new()
    {
        States = ["Draft", "Submitted", "Approved"],
        InitialState = "Draft",
        Transitions =
        [
            new StateTransition { Name = "Submit", From = "Draft", To = "Submitted" },
            new StateTransition { Name = "Approve", From = "Submitted", To = "Approved" },
        ],
    };

    /// <summary>Through the endpoint, so the name is stored the way config apply stores it.</summary>
    private async Task<string> CreateLowercaseTypeAsync()
    {
        var name = NewName("inv");
        var res = await _client.PostAsJsonAsync("/api/content-types", new
        {
            name,
            displayName = "Invoice",
            fields = new[] { new { name = "Title", type = "string" } },
            lifecycle = Lifecycle(),
        });
        res.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}", res.StatusCode, await res.Content.ReadAsStringAsync());
        return name.ToLowerInvariant();
    }

    /// <summary>Stored straight through the session with its case kept, the way a module seeds a type.</summary>
    private async Task<string> StoreMixedTypeAsync()
    {
        var name = Mixed(NewName("inv"));
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Store(new ContentTypeDefinition
        {
            Id = Guid.NewGuid(),
            Name = name,
            DisplayName = "Invoice",
            Fields = [new FieldDefinition { Name = "Title", DisplayName = "Title", Type = "string" }],
            Lifecycle = Lifecycle(),
        });
        await session.SaveChangesAsync();
        return name;
    }

    private Task<HttpResponseMessage> PostTransitionWorkflowAsync(string type) =>
        _client.PostAsJsonAsync("/api/workflows", new
        {
            name = NewName("wf"),
            triggerContentTypes = new[] { type },
            triggerEvent = WorkflowEvents.ForTransition("Approve"),
            actions = new[] { Probe(NewName("probe")) },
        });

    private async Task<Guid> CreateContentAsync(string contentType)
    {
        var res = await _client.PostAsJsonAsync("/api/contents", new
        {
            ContentType = contentType,
            Data = new Dictionary<string, object> { ["Title"] = "an invoice" },
        });
        res.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}", res.StatusCode, await res.Content.ReadAsStringAsync());

        return (await res.Content.ReadFromJsonAsync<barakoCMS.Features.Content.Create.Response>(ApiJson.Options))!.Id;
    }

    private Task<HttpResponseMessage> TransitionAsync(Guid id, string transition) =>
        _approver.PutAsJsonAsync($"/api/contents/{id}/status", new { id, transition });

    private async Task<string?> StateOfAsync(Guid id)
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        return (await session.LoadAsync<barakoCMS.Models.Content>(id))?.LifecycleState;
    }

    private static WorkflowAction Probe(string probeContentType) => new()
    {
        Type = "CreateTask",
        Parameters = new Dictionary<string, string> { ["ContentType"] = probeContentType, ["Title"] = "probe" },
    };

    private async Task StoreWorkflowAsync(string triggerContentType, string triggerEvent, string probeContentType)
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Store(new WorkflowDefinition
        {
            Id = Guid.NewGuid(),
            Name = NewName("wf"),
            TriggerContentType = triggerContentType,
            TriggerEvent = triggerEvent,
            Actions = [Probe(probeContentType)],
        });
        await session.SaveChangesAsync();
    }

    private async Task<int> CountAsync(string probeContentType)
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        return await session.Query<barakoCMS.Models.Content>().CountAsync(c => c.ContentType == probeContentType);
    }

    private async Task<int> WaitForAsync(string probeContentType)
    {
        var deadline = DateTime.UtcNow + PollTimeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await CountAsync(probeContentType) >= 1)
            {
                await Task.Delay(TimeSpan.FromSeconds(2));
                return await CountAsync(probeContentType);
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250));
        }

        throw new Xunit.Sdk.XunitException($"Timed out: the workflow never created a '{probeContentType}' item");
    }

    private async Task<string> UserWithCreateAsync(params (string Type, bool Enabled)[] grants)
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var role = new Role
        {
            Id = Guid.NewGuid(),
            Name = $"Role_{Guid.NewGuid()}",
            Permissions = grants.Select(g => new ContentTypePermission
            {
                ContentTypeSlug = g.Type,
                Create = new PermissionRule { Enabled = g.Enabled },
                Read = new PermissionRule { Enabled = false },
                Update = new PermissionRule { Enabled = false },
                Delete = new PermissionRule { Enabled = false },
            }).ToList(),
        };
        session.Store(role);
        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = $"user_{Guid.NewGuid()}",
            Email = $"test_{Guid.NewGuid()}@example.com",
            RoleIds = [role.Id],
        };
        session.Store(user);
        await session.SaveChangesAsync();
        return _factory.CreateToken(roles: [role.Name], userId: user.Id.ToString());
    }

    private static Task<HttpResponseMessage> CreateAsync(HttpClient caller, string type) =>
        caller.PostAsJsonAsync("/api/contents", new barakoCMS.Features.Content.Create.Request
        {
            ContentType = type,
            Data = new Dictionary<string, object> { { "title", "t" } },
        });
}

/// <summary>
/// Two stored types that differ only by case (legacy data: a module-seeded "PurchaseOrder" beside a
/// config "purchaseorder"). A name that is neither exact is a guess, and a guess is refused.
/// </summary>
[Collection("Integration1")]
public class ContentTypeAmbiguousCaseTests : IAsyncLifetime
{
    private readonly IntegrationTestFixture _factory;
    private readonly List<string> _stored = [];
    private LegacyNameIndex? _legacy;

    public ContentTypeAmbiguousCaseTests(IntegrationTestFixture factory) => _factory = factory;

    // The name index ignores case, so the two spellings these tests need only fit with it dropped.
    public async ValueTask InitializeAsync()
    {
        var store = _factory.Services.GetRequiredService<IDocumentStore>();
        _legacy = await LegacyNameIndex.DropAsync(store, async () =>
        {
            await using var cleanup = store.LightweightSession();
            var names = _stored.Select(n => n.ToLowerInvariant()).Distinct().ToList();
            cleanup.DeleteWhere<ContentTypeDefinition>(d => names.Contains(d.Name.ToLower()));
            cleanup.DeleteWhere<Content>(c => names.Contains(c.ContentType.ToLower()));
            await cleanup.SaveChangesAsync(CancellationToken.None);
        }, TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_legacy is not null)
        {
            await _legacy.DisposeAsync();
        }
    }

    private static string NewName() => $"dup{Guid.NewGuid():N}"[..14];

    private async Task StoreTypesAsync(params string[] names)
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        foreach (var name in names)
        {
            _stored.Add(name);
            session.Store(new ContentTypeDefinition
            {
                Id = Guid.NewGuid(), Name = name, DisplayName = name,
                Fields = [new FieldDefinition { Name = "Title", DisplayName = "Title", Type = "string" }],
            });
        }

        await session.SaveChangesAsync();
    }

    private async Task<HttpClient> AdminAsync()
    {
        var (token, _) = await TestHelpers.CreateAdminUserAsync(_factory);
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static Task<HttpResponseMessage> CreateAsync(HttpClient client, string type) =>
        client.PostAsJsonAsync("/api/contents", new { ContentType = type, Data = new Dictionary<string, object> { ["Title"] = "t" } });

    [Fact]
    public async Task A_list_by_a_name_that_is_neither_exact_is_refused()
    {
        var lower = NewName();
        var upper = lower.ToUpperInvariant();
        await StoreTypesAsync(lower, upper);
        var client = await AdminAsync();
        (await CreateAsync(client, lower)).IsSuccessStatusCode.Should().BeTrue();

        var mixed = char.ToUpperInvariant(lower[0]) + lower[1..];
        var res = await client.GetAsync($"/api/contents?contentType={mixed}&pageSize=50");

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest, await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_list_by_the_exact_name_does_not_list_the_rows_of_the_sibling_spelling()
    {
        var lower = NewName();
        var upper = lower.ToUpperInvariant();
        await StoreTypesAsync(lower, upper);
        var client = await AdminAsync();
        var a = await (await CreateAsync(client, lower)).Content.ReadFromJsonAsync<barakoCMS.Features.Content.Create.Response>(ApiJson.Options);
        var b = await (await CreateAsync(client, upper)).Content.ReadFromJsonAsync<barakoCMS.Features.Content.Create.Response>(ApiJson.Options);

        var body = await client.GetStringAsync($"/api/contents?contentType={lower}&pageSize=50");

        body.Should().Contain(a!.Id.ToString()).And.NotContain(b!.Id.ToString());
    }

    [Fact]
    public async Task A_create_naming_neither_exact_spelling_is_refused()
    {
        var lower = NewName();
        await StoreTypesAsync(lower, lower.ToUpperInvariant());
        var client = await AdminAsync();

        var res = await CreateAsync(client, char.ToUpperInvariant(lower[0]) + lower[1..]);

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest, await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_role_holding_two_spellings_grants_nothing_to_a_third()
    {
        var lower = NewName();
        var token = await UserHoldingAsync(lower, lower.ToUpperInvariant());
        var caller = _factory.CreateClient();
        caller.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var third = char.ToUpperInvariant(lower[0]) + lower[1..];
        (await CreateAsync(caller, third)).StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "which of the two grants is meant is a guess, so none applies");
    }

    private async Task<string> UserHoldingAsync(params string[] spellings)
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var role = new Role
        {
            Id = Guid.NewGuid(), Name = $"Role_{Guid.NewGuid()}",
            Permissions = spellings.Select(s => new ContentTypePermission
            {
                ContentTypeSlug = s,
                Create = new PermissionRule { Enabled = true },
                Read = new PermissionRule { Enabled = false },
                Update = new PermissionRule { Enabled = false },
                Delete = new PermissionRule { Enabled = false },
            }).ToList(),
        };
        session.Store(role);
        var user = new User
        {
            Id = Guid.NewGuid(), Username = $"user_{Guid.NewGuid()}", Email = $"t_{Guid.NewGuid()}@example.com", RoleIds = [role.Id],
        };
        session.Store(user);
        await session.SaveChangesAsync();
        return _factory.CreateToken(roles: [role.Name], userId: user.Id.ToString());
    }
}
