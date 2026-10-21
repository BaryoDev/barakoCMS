using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using barakoCMS.Core.Interfaces;
using barakoCMS.Features.Workflows;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;

namespace BarakoCMS.Tests.Features.Workflows;

/// <summary>
/// CreateContent and Transition run as the system, so the person who saves the workflow is the one
/// held to the create and transition rights, and a Transition has to name the type it moves.
/// </summary>
[Collection("Sequential")]
public class WorkflowActionRightsTests(IntegrationTestFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Saving_a_transition_without_a_content_type_is_refused()
    {
        var type = await StoreTypeAsync();
        var client = fixture.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", await fixture.StoredUserTokenAsync("Admin", "SuperAdmin"));

        var response = await client.PostAsJsonAsync("/api/workflows",
            Workflow(type, Action("Transition", ("Transition", "Call"))), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(Ct)).Should().Contain("ContentType");
    }

    [Fact]
    public async Task A_user_without_create_rights_cannot_save_a_create_content_workflow()
    {
        var type = await StoreTypeAsync();
        var client = await CallerAsync();

        var response = await client.PostAsJsonAsync("/api/workflows",
            Workflow(type, Action("CreateContent", ("ContentType", type))), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_user_with_create_rights_can_save_a_create_content_workflow()
    {
        var type = await StoreTypeAsync();
        var client = await CallerAsync(new ContentTypePermission { ContentTypeSlug = type, Create = new PermissionRule { Enabled = true } });

        var response = await client.PostAsJsonAsync("/api/workflows",
            Workflow(type, Action("CreateContent", ("ContentType", type))), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task A_transition_workflow_needs_the_transition_right_on_that_type_and_name()
    {
        var type = await StoreTypeAsync();
        var body = Workflow(type, Action("Transition", ("ContentType", type), ("Transition", "Call")));

        var without = await CallerAsync(new ContentTypePermission { ContentTypeSlug = type, Update = new PermissionRule { Enabled = true } });
        var other = await CallerAsync(new ContentTypePermission
        {
            ContentTypeSlug = type,
            Transitions = new Dictionary<string, PermissionRule> { ["Other"] = new() { Enabled = true } },
        });
        var with = await CallerAsync(new ContentTypePermission
        {
            ContentTypeSlug = type,
            Transitions = new Dictionary<string, PermissionRule> { ["Call"] = new() { Enabled = true } },
        });

        (await without.PostAsJsonAsync("/api/workflows", body, Ct)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await other.PostAsJsonAsync("/api/workflows", body, Ct)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await with.PostAsJsonAsync("/api/workflows", body, Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Actions_inside_a_conditional_are_held_to_the_same_rights()
    {
        var type = await StoreTypeAsync();
        var client = await CallerAsync();
        var child = JsonSerializer.Serialize(new[]
        {
            new { Type = "CreateContent", Parameters = new Dictionary<string, string> { ["ContentType"] = type } },
        });

        var response = await client.PostAsJsonAsync("/api/workflows", Workflow(type,
            Action("Conditional", ("Condition", "{{Name}} == a"), ("ThenActions", child))), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task An_update_is_held_to_the_same_rights()
    {
        var type = await StoreTypeAsync();
        var admin = fixture.CreateClient();
        admin.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", await fixture.StoredUserTokenAsync("Admin", "SuperAdmin"));
        var saved = await admin.PostAsJsonAsync("/api/workflows", Workflow(type, Action("CreateTask", ("ContentType", type), ("Title", "t"))), Ct);
        saved.StatusCode.Should().Be(HttpStatusCode.OK, await saved.Content.ReadAsStringAsync(Ct));
        var id = (await saved.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();

        var client = await CallerAsync();
        var response = await client.PutAsJsonAsync($"/api/workflows/{id}",
            Workflow(type, Action("CreateContent", ("ContentType", type))), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_config_document_applied_by_a_user_is_held_to_their_rights_and_the_host_file_is_not()
    {
        var type = await StoreTypeAsync();
        var document = new Dictionary<string, JsonElement>
        {
            ["rights-" + type] = JsonSerializer.SerializeToElement(new
            {
                triggerContentTypes = new[] { type },
                triggerEvents = new[] { "Created" },
                actions = new[] { new { type = "CreateContent", parameters = new Dictionary<string, string> { ["ContentType"] = type } } },
                enabled = true,
            }),
        };

        using var scope = fixture.Services.CreateScope();
        var provider = ActivatorUtilities.CreateInstance<WorkflowConfigProvider>(scope.ServiceProvider);
        var userId = await StoreUserAsync();
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim("UserId", userId.ToString())], "test"));

        var asUser = await provider.PlanAsync(document, ConfigCaller.ForUser(principal), Ct);
        var asHost = await provider.PlanAsync(document, ConfigCaller.System, Ct);

        asUser.Errors.Should().ContainSingle().Which.Should().Contain("cannot create");
        asHost.Errors.Should().BeEmpty();
    }

    private static object Workflow(string type, object action) => new
    {
        name = "rights-" + Guid.NewGuid().ToString("n")[..10],
        triggerContentType = type,
        triggerEvent = "Created",
        actions = new[] { action },
    };

    private static object Action(string type, params (string Key, string Value)[] parameters) =>
        new { type, parameters = parameters.ToDictionary(p => p.Key, p => p.Value) };

    /// <summary>A type with a Call transition, so a workflow can trigger on it and name the move.</summary>
    private async Task<string> StoreTypeAsync()
    {
        var type = "rights-" + Guid.NewGuid().ToString("n")[..10];
        using var scope = fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Store(new ContentTypeDefinition
        {
            Id = Guid.NewGuid(),
            Name = type,
            DisplayName = type,
            Fields = [new FieldDefinition { Name = "Name", DisplayName = "Name", Type = "string" }],
            Lifecycle = new LifecycleDefinition
            {
                States = ["Waiting", "Done"],
                InitialState = "Waiting",
                Transitions = [new StateTransition { Name = "Call", From = "Waiting", To = "Done" }],
            },
        });
        await session.SaveChangesAsync(Ct);
        return type;
    }

    /// <summary>A user whose role holds the workflow capability and only the content permissions given.</summary>
    private async Task<Guid> StoreUserAsync(params ContentTypePermission[] permissions)
    {
        var (userId, _) = await StoreCallerAsync(permissions);
        return userId;
    }

    private async Task<(Guid UserId, string RoleName)> StoreCallerAsync(ContentTypePermission[] permissions)
    {
        var roleName = $"rights {Guid.NewGuid():N}";
        using var scope = fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var role = new Role
        {
            Id = Guid.NewGuid(),
            Name = roleName,
            SystemCapabilities = [SystemCapabilities.ManageWorkflows],
            Permissions = permissions.ToList(),
        };
        session.Store(role);
        var userId = Guid.NewGuid();
        session.Store(new User { Id = userId, Username = $"rights-{userId}", Email = $"rights-{userId}@example.com", RoleIds = [role.Id] });
        await session.SaveChangesAsync(Ct);
        return (userId, roleName);
    }

    private async Task<HttpClient> CallerAsync(params ContentTypePermission[] permissions)
    {
        var (userId, roleName) = await StoreCallerAsync(permissions);
        var client = fixture.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", fixture.CreateToken(roles: [roleName], userId: userId.ToString()));
        return client;
    }
}
