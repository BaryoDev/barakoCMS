using System.Net.Http.Json;
using System.Security.Claims;
using barakoCMS.Core.Interfaces;
using barakoCMS.Infrastructure.Multitenancy;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// A transition carrying a <c>file</c> field, made by a module for a user who is not the one whose
/// request is running. The file is checked for the actor, never for the request's user.
/// </summary>
/// <remarks>
/// The request here is an administrator's, who may download every file in the tenant. The actor is
/// a reviewer, who may download only their own private files. Before the check named the actor,
/// the administrator's reach decided what the reviewer could attach.
/// </remarks>
[Collection("Sequential")]
public class TransitionFileFieldTests
{
    private readonly IntegrationTestFixture _factory;

    public TransitionFileFieldTests(IntegrationTestFixture factory) => _factory = factory;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<string> TypeAsync()
    {
        var name = "ctrf" + Guid.NewGuid().ToString("n")[..8];
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Store(new ContentTypeDefinition
        {
            Id = Guid.NewGuid(),
            Name = name,
            DisplayName = "Claim",
            Fields =
            [
                new FieldDefinition { Name = "Title", DisplayName = "Title", Type = "string" },
                new FieldDefinition { Name = "Evidence", DisplayName = "Evidence", Type = "file" },
            ],
            Lifecycle = new LifecycleDefinition
            {
                States = ["Draft", "Approved"],
                InitialState = "Draft",
                Transitions = [new StateTransition { Name = "Attach", From = "Draft", To = "Approved", RequiredFields = ["Evidence"] }],
            },
        });
        await session.SaveChangesAsync(Ct);
        return name;
    }

    private async Task<User> StoreUserAsync(params Guid[] roleIds)
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = $"ctrf_{Guid.NewGuid():n}",
            Email = $"ctrf_{Guid.NewGuid():n}@example.com",
            RoleIds = roleIds.ToList(),
        };
        session.Store(user);
        await session.SaveChangesAsync(Ct);
        return user;
    }

    private async Task<User> ReviewerAsync(string type)
    {
        var role = new Role
        {
            Id = Guid.NewGuid(),
            Name = $"Role_{Guid.NewGuid():n}",
            Permissions =
            [
                new ContentTypePermission
                {
                    ContentTypeSlug = type,
                    Read = new PermissionRule { Enabled = true },
                    Transitions = new(StringComparer.OrdinalIgnoreCase) { ["Attach"] = new PermissionRule { Enabled = true } },
                },
            ],
        };

        using (var scope = _factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Store(role);
            await session.SaveChangesAsync(Ct);
        }

        return await StoreUserAsync(role.Id);
    }

    private async Task<Guid> PrivateFileAsync(Guid owner)
    {
        using var scope = _factory.Services.CreateScope();
        using var content = new MemoryStream(FileSamples.Png(Guid.NewGuid().ToByteArray()));
        var saved = await scope.ServiceProvider.GetRequiredService<IFileStore>().SaveAsync(
            new FileToStore { Content = content, FileName = "evidence.png", ContentType = "image/png", Owner = owner },
            Ct);
        saved.File.Should().NotBeNull(saved.Refused ?? string.Empty);
        return saved.File!.Id;
    }

    /// <summary>An entry in Draft, raised through the API by an administrator.</summary>
    private async Task<Guid> EntryAsync(string type)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer", await _factory.StoredUserTokenAsync("Admin", "SuperAdmin"));

        var res = await client.PostAsJsonAsync("/api/contents", new
        {
            contentType = type,
            data = new Dictionary<string, object> { ["Title"] = "a claim" },
        }, Ct);
        var body = await res.Content.ReadAsStringAsync(Ct);
        res.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}", res.StatusCode, body);
        using var doc = System.Text.Json.JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("id").GetGuid();
    }

    /// <summary>The move a module makes for <paramref name="actor"/> while an administrator's request is running.</summary>
    private async Task<ContentTransitionResult> AttachInsideAdminRequestAsync(Guid id, User actor, User admin, Guid evidence)
    {
        using var requestScope = _factory.Services.CreateScope();
        requestScope.ServiceProvider.GetRequiredService<TenantContext>().Slug = Tenant.DefaultSlug;

        var claims = new List<Claim>
        {
            new("UserId", admin.Id.ToString()),
            new("Username", admin.Username),
            new(ClaimTypes.Role, "SuperAdmin"),
            new(ClaimTypes.Role, "Admin"),
        };
        var request = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test", "Username", ClaimTypes.Role)),
            RequestServices = requestScope.ServiceProvider,
        };

        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var transitioner = scope.ServiceProvider.GetRequiredService<IContentTransitioner>();
        var content = await session.LoadAsync<Content>(id, Ct);
        content.Should().NotBeNull();

        var accessor = _factory.Services.GetRequiredService<IHttpContextAccessor>();
        accessor.HttpContext = request;
        try
        {
            return await transitioner.TransitionAsync(
                content!,
                "Attach",
                ContentTransitionActor.ForUser(actor.Id),
                new ContentTransitionOptions { Data = new Dictionary<string, object> { ["Evidence"] = evidence.ToString() } },
                Ct);
        }
        finally
        {
            accessor.HttpContext = null;
        }
    }

    [Fact]
    public async Task A_file_carried_by_a_transition_is_checked_for_the_actor_and_not_for_the_request_running()
    {
        var type = await TypeAsync();
        var reviewer = await ReviewerAsync(type);
        var admin = await StoreUserAsync();

        var adminsFile = await PrivateFileAsync(admin.Id);
        var reviewersFile = await PrivateFileAsync(reviewer.Id);
        var id = await EntryAsync(type);

        var refused = await AttachInsideAdminRequestAsync(id, reviewer, admin, adminsFile);
        refused.Outcome.Should().Be(ContentTransitionOutcome.Invalid, string.Join(" ", refused.Errors));
        refused.Errors.Should().ContainSingle().Which.Should().Contain("takes the id of a stored file you may use");

        using (var scope = _factory.Services.CreateScope())
        {
            var stored = await scope.ServiceProvider.GetRequiredService<IQuerySession>().LoadAsync<Content>(id, Ct);
            stored!.LifecycleState.Should().Be("Draft");
            stored.Data.Should().NotContainKey("Evidence");
        }

        // The control: the reviewer's own file, in the same administrator's request.
        var moved = await AttachInsideAdminRequestAsync(id, reviewer, admin, reviewersFile);
        moved.Outcome.Should().Be(ContentTransitionOutcome.Transitioned, string.Join(" ", moved.Errors));
    }
}
