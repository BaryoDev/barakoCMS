using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Json;
using System.Text.Json;
using barakoCMS.Models;
using BarakoCMS.Tests.SampleProbe;
using Marten;

namespace BarakoCMS.Tests.Realtime;

/// <summary>Types, readers and entries for the tests of content topics.</summary>
internal static class ContentTopicKit
{
    /// <summary>A caller: their client, the bearer token, and the user behind it.</summary>
    internal sealed record Caller(HttpClient Client, string Token, Guid UserId);

    /// <summary>Stores a type with a Title field and a Waiting to Called lifecycle, and returns its name.</summary>
    internal static async Task<string> TypeAsync(
        SampleProbeHost host, string? topic, bool branchScoped = true, string? name = null, bool lifecycle = true)
    {
        name ??= "tkt" + Guid.NewGuid().ToString("n")[..10];
        await using var session = host.OpenSession();
        session.Store(new ContentTypeDefinition
        {
            Id = Guid.NewGuid(),
            Name = name,
            DisplayName = name,
            BranchScoped = branchScoped,
            RealtimeTopic = topic,
            Fields =
            [
                new() { Name = "Title", DisplayName = "Title", Type = "string" },
                new() { Name = "Counter", DisplayName = "Counter", Type = "string" },
            ],
            Lifecycle = lifecycle
                ? new LifecycleDefinition
                {
                    States = ["Waiting", "Called"],
                    InitialState = "Waiting",
                    Transitions = [new StateTransition { Name = "Call", From = "Waiting", To = "Called" }],
                }
                : null,
        });
        await session.SaveChangesAsync();
        return name;
    }

    /// <summary>A topic name no other test uses.</summary>
    internal static string NewTopic(string prefix = "queue") => $"{prefix}.t{Guid.NewGuid():n}"[..24];

    /// <summary>
    /// A user who reads <paramref name="contentType"/> through a role and holds <paramref name="branches"/>
    /// in their membership. <paramref name="conditions"/> narrow the read rule.
    /// </summary>
    internal static async Task<Caller> ReaderAsync(
        SampleProbeHost host, string contentType, Dictionary<string, object>? conditions = null, params Guid[] branches)
    {
        var roleName = $"rd-{Guid.NewGuid():N}"[..20];
        await using (var session = host.OpenSession())
        {
            session.Store(new Role
            {
                Id = Guid.NewGuid(),
                Name = roleName,
                Permissions =
                [
                    new ContentTypePermission
                    {
                        ContentTypeSlug = contentType,
                        Read = new PermissionRule { Enabled = true, Conditions = conditions },
                    },
                ],
            });
            await session.SaveChangesAsync();
        }

        return await WithBranchesAsync(host, roleName, branches);
    }

    /// <summary>A user with the seeded admin roles, holding <paramref name="branches"/>.</summary>
    internal static Task<Caller> WriterAsync(SampleProbeHost host, params Guid[] branches) =>
        WithBranchesAsync(host, null, branches);

    private static async Task<Caller> WithBranchesAsync(SampleProbeHost host, string? roleName, Guid[] branches)
    {
        var client = await host.CreateClientAsync(roleName is null ? ["SuperAdmin", "Admin"] : [roleName]);
        var token = client.DefaultRequestHeaders.Authorization!.Parameter!;
        var userId = Guid.Parse(new JwtSecurityTokenHandler().ReadJwtToken(token).Claims.First(c => c.Type == "UserId").Value);

        await using var session = host.OpenSession();
        var user = await session.LoadAsync<User>(userId);
        session.Store(new Membership
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            TenantSlug = Tenant.DefaultSlug,
            Status = MembershipStatus.Active,
            RoleIds = user!.RoleIds,
            Profile = branches.Length == 0
                ? new Dictionary<string, string>()
                : new Dictionary<string, string> { ["branches"] = string.Join(",", branches) },
        });
        await session.SaveChangesAsync();
        return new Caller(client, token, userId);
    }

    /// <summary>Creates an entry through the API and returns its id.</summary>
    internal static async Task<Guid> CreateAsync(
        HttpClient writer, string type, Guid? branch, string title = "A-1", string counter = "1")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/contents")
        {
            Content = JsonContent.Create(new
            {
                contentType = type,
                data = new Dictionary<string, object> { ["Title"] = title, ["Counter"] = counter },
            }),
        };
        if (branch is { } b) request.Headers.Add("X-Branch-Id", b.ToString());

        var response = await writer.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"{(int)response.StatusCode}: {body}");
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("id").GetGuid();
    }

    /// <summary>Moves an entry along a named lifecycle transition.</summary>
    internal static async Task TransitionAsync(HttpClient writer, Guid id, string transition, Guid? branch)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, $"/api/contents/{id}/status")
        {
            Content = JsonContent.Create(new { transition }),
        };
        if (branch is { } b) request.Headers.Add("X-Branch-Id", b.ToString());

        var response = await writer.SendAsync(request);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
    }
}
