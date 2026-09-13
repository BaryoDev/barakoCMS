using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using barakoCMS.Core.Hooks;
using barakoCMS.Core.Interfaces;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// A reference declared to be a parent may not point at its own entry or close a loop (issue #716).
/// </summary>
/// <remarks>
/// The type carries two self-typed references on purpose. Only ParentPage is declared a parent, so
/// RelatedPage pointing both ways between two entries has to keep working: that is the reason the
/// rule is a hook on the declaring type and not a check every reference gets.
/// </remarks>
[Collection("Sequential")]
public class ParentReferenceHookTests
{
    private const string TypeName = "parentrefprobe";
    private const int MaxDepth = 3;

    private readonly IntegrationTestFixture _factory;

    public ParentReferenceHookTests(IntegrationTestFixture factory) => _factory = factory;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// Holds two racing writes at a point after the parent check and before the commit, so both have
    /// read the tree before either has written to it. Runs after <see cref="ParentReferenceHook"/>
    /// because hooks run in registration order.
    /// </summary>
    private sealed class Barrier
    {
        private readonly Lock _gate = new();
        private readonly Dictionary<string, (int Arrived, TaskCompletionSource Both)> _byMarker = new();

        public Task ArriveAsync(string marker)
        {
            lock (_gate)
            {
                var entry = _byMarker.TryGetValue(marker, out var e)
                    ? e
                    : (Arrived: 0, Both: new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
                entry.Arrived++;
                _byMarker[marker] = entry;
                if (entry.Arrived >= 2)
                {
                    entry.Both.TrySetResult();
                }

                // A writer blocked on the lock never arrives, so the first one gives up waiting and
                // commits. That is the fixed behaviour; without the lock both arrive and both commit.
                return Task.WhenAny(entry.Both.Task, Task.Delay(TimeSpan.FromSeconds(4)));
            }
        }
    }

    private sealed class BarrierHook : IContentLifecycleHook
    {
        private readonly Barrier _barrier;

        public BarrierHook(Barrier barrier) => _barrier = barrier;

        public string ContentType => TypeName;

        public async Task<IReadOnlyList<string>> OnBeforeSaveAsync(ContentLifecycleContext context, CancellationToken ct)
        {
            if (context.Data.TryGetValue("Title", out var title) && title?.ToString() is { } t && t.StartsWith("race-"))
            {
                await _barrier.ArriveAsync(t);
            }

            return [];
        }
    }

    private static readonly Barrier SharedBarrier = new();
    private static readonly Lock HostGate = new();
    private static WebApplicationFactory<Program>? _host;

    private WebApplicationFactory<Program> Host()
    {
        lock (HostGate)
        {
            return _host ??= _factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            {
                services.AddScoped<IContentLifecycleHook>(_ => new ParentReferenceHook(TypeName, "ParentPage", MaxDepth));
                services.AddSingleton(SharedBarrier);
                services.AddScoped<IContentLifecycleHook, BarrierHook>();
            }));
        }
    }

    private static readonly SemaphoreSlim TypeGate = new(1, 1);
    private static bool _typeDeclared;

    private async Task DeclareTypeAsync()
    {
        await TypeGate.WaitAsync(Ct);
        try
        {
            if (_typeDeclared)
            {
                return;
            }

            using var scope = _factory.Services.CreateScope();
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            if (!await session.Query<ContentTypeDefinition>().AnyAsync(d => d.Name == TypeName, Ct))
            {
                session.Store(new ContentTypeDefinition
                {
                    Id = Guid.NewGuid(),
                    Name = TypeName,
                    DisplayName = "Parent reference probe",
                    Fields =
                    [
                        new FieldDefinition { Name = "Title", DisplayName = "Title", Type = "string" },
                        new FieldDefinition { Name = "ParentPage", DisplayName = "Parent page", Type = "reference", ReferenceType = TypeName },
                        new FieldDefinition { Name = "RelatedPage", DisplayName = "Related page", Type = "reference", ReferenceType = TypeName },
                    ],
                });
                await session.SaveChangesAsync(Ct);
            }

            _typeDeclared = true;
        }
        finally
        {
            TypeGate.Release();
        }
    }

    private async Task<HttpClient> AdminAsync()
    {
        await DeclareTypeAsync();

        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();

        var roleNames = new[] { "SuperAdmin", "Admin" };
        var roleIds = new List<Guid>();
        foreach (var name in roleNames)
        {
            var role = await session.Query<Role>().FirstOrDefaultAsync(r => r.Name == name, Ct);
            if (role is null)
            {
                role = new Role { Id = Guid.NewGuid(), Name = name };
                session.Store(role);
            }

            roleIds.Add(role.Id);
        }

        var userId = Guid.NewGuid();
        session.Store(new User
        {
            Id = userId,
            Username = $"parentref-{userId:n}",
            Email = $"parentref-{userId:n}@example.com",
            RoleIds = roleIds,
        });
        await session.SaveChangesAsync(Ct);

        var client = Host().CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", _factory.CreateToken(roles: roleNames, userId: userId.ToString()));
        return client;
    }

    private static async Task<Guid> CreateAsync(HttpClient client, string title, Guid? parent = null)
    {
        var data = new Dictionary<string, object> { ["Title"] = title };
        if (parent is { } p)
        {
            data["ParentPage"] = p.ToString();
        }

        var res = await client.PostAsJsonAsync("/api/contents", new { contentType = TypeName, data }, Ct);
        res.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}", res.StatusCode, await res.Content.ReadAsStringAsync(Ct));
        using var doc = System.Text.Json.JsonDocument.Parse(await res.Content.ReadAsStringAsync(Ct));
        return doc.RootElement.GetProperty("id").GetGuid();
    }

    private static Task<HttpResponseMessage> UpdateAsync(
        HttpClient client, Guid id, string title, Guid? parent = null, Guid? related = null)
    {
        var data = new Dictionary<string, object> { ["Title"] = title };
        if (parent is { } p)
        {
            data["ParentPage"] = p.ToString();
        }

        if (related is { } r)
        {
            data["RelatedPage"] = r.ToString();
        }

        return client.PutAsJsonAsync($"/api/contents/{id}", new { id, data }, Ct);
    }

    /// <summary>Stores entries straight into the database, for shapes the API would now refuse.</summary>
    private async Task<Guid[]> SeedAsync(params Guid?[] parents)
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var ids = parents.Select(_ => Guid.NewGuid()).ToArray();
        for (var i = 0; i < ids.Length; i++)
        {
            var data = new Dictionary<string, object> { ["Title"] = $"seed-{i}" };
            if (parents[i] is { } p)
            {
                data["ParentPage"] = p.ToString();
            }

            session.Store(new Content { Id = ids[i], ContentType = TypeName, Data = data });
        }

        await session.SaveChangesAsync(Ct);
        return ids;
    }

    private async Task<string?> StoredParentAsync(Guid id)
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        var content = await session.LoadAsync<Content>(id, Ct);
        content.Should().NotBeNull();
        return content!.Data.TryGetValue("ParentPage", out var v) ? v?.ToString() : null;
    }

    private static async Task ShouldBeRefusedAsync(HttpResponseMessage res, string because)
    {
        var body = await res.Content.ReadAsStringAsync(Ct);
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest, "{0}, got: {1}", because, body);
        body.Should().Contain("Parent page", "the error names the field it is about");
    }

    [Fact]
    public async Task A_parent_reference_to_the_entry_itself_is_refused()
    {
        var client = await AdminAsync();
        var a = await CreateAsync(client, "self");

        var res = await UpdateAsync(client, a, "self", parent: a);

        await ShouldBeRefusedAsync(res, "a page cannot be its own parent");
        (await res.Content.ReadAsStringAsync(Ct)).Should().Contain("itself");
        (await StoredParentAsync(a)).Should().BeNull("the refused write stored nothing");
    }

    [Fact]
    public async Task A_two_entry_cycle_is_refused_on_the_write_that_closes_it()
    {
        var client = await AdminAsync();
        var a = await CreateAsync(client, "two-a");
        var b = await CreateAsync(client, "two-b", parent: a);

        var res = await UpdateAsync(client, a, "two-a", parent: b);

        await ShouldBeRefusedAsync(res, "A under B under A is a loop");
        (await res.Content.ReadAsStringAsync(Ct)).Should().Contain("cycle");
        (await StoredParentAsync(a)).Should().BeNull();
    }

    [Fact]
    public async Task A_three_entry_cycle_is_refused()
    {
        var client = await AdminAsync();
        var a = await CreateAsync(client, "three-a");
        var b = await CreateAsync(client, "three-b", parent: a);
        var c = await CreateAsync(client, "three-c", parent: b);

        var res = await UpdateAsync(client, a, "three-a", parent: c);

        await ShouldBeRefusedAsync(res, "A under C under B under A is a loop");
        (await res.Content.ReadAsStringAsync(Ct)).Should().Contain("cycle");
    }

    [Fact]
    public async Task A_reference_that_is_not_declared_a_parent_may_point_back()
    {
        var client = await AdminAsync();
        var a = await CreateAsync(client, "related-a");
        var b = await CreateAsync(client, "related-b");

        var forward = await UpdateAsync(client, a, "related-a", related: b);
        forward.StatusCode.Should().Be(HttpStatusCode.OK, await forward.Content.ReadAsStringAsync(Ct));

        var back = await UpdateAsync(client, b, "related-b", related: a);
        back.StatusCode.Should().Be(HttpStatusCode.OK, await back.Content.ReadAsStringAsync(Ct));

        var self = await UpdateAsync(client, a, "related-a", related: a);
        self.StatusCode.Should().Be(HttpStatusCode.OK, await self.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task A_valid_parent_chain_is_accepted()
    {
        var client = await AdminAsync();
        var root = await CreateAsync(client, "chain-root");
        var child = await CreateAsync(client, "chain-child", parent: root);
        var leaf = await CreateAsync(client, "chain-leaf");

        var res = await UpdateAsync(client, leaf, "chain-leaf", parent: child);

        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync(Ct));
        (await StoredParentAsync(leaf)).Should().Be(child.ToString());

        var moved = await UpdateAsync(client, child, "chain-child", parent: null);
        moved.StatusCode.Should().Be(HttpStatusCode.OK, "clearing a parent is always allowed");
    }

    /// <summary>
    /// The walk reads at most MaxDepth ancestors. Exactly MaxDepth is accepted and one more is
    /// refused, so the bound is the number the hook was given and not something larger.
    /// </summary>
    [Fact]
    public async Task The_chain_walk_stops_at_the_configured_depth()
    {
        var client = await AdminAsync();
        var chain = await SeedChainAsync(MaxDepth);

        var atLimit = await CreateAsync(client, "depth-at-limit");
        var accepted = await UpdateAsync(client, atLimit, "depth-at-limit", parent: chain[^1]);
        accepted.StatusCode.Should().Be(HttpStatusCode.OK,
            "{0} ancestors is the limit, got: {1}", MaxDepth, await accepted.Content.ReadAsStringAsync(Ct));

        var overLimit = await CreateAsync(client, "depth-over-limit");
        var refused = await UpdateAsync(client, overLimit, "depth-over-limit", parent: atLimit);
        await ShouldBeRefusedAsync(refused, "one ancestor past the limit");
        (await refused.Content.ReadAsStringAsync(Ct)).Should().Contain($"{MaxDepth} levels");
    }

    /// <summary>
    /// A loop already in the database that does not pass through the entry being written. The walk
    /// has to end, and it is refused, since hanging a page under a loop gives it no root.
    /// </summary>
    [Fact]
    public async Task A_loop_already_stored_above_the_new_parent_ends_the_walk()
    {
        var client = await AdminAsync();
        var p = Guid.NewGuid();
        var q = Guid.NewGuid();
        using (var scope = _factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Store(new Content { Id = p, ContentType = TypeName, Data = new() { ["Title"] = "loop-p", ["ParentPage"] = q.ToString() } });
            session.Store(new Content { Id = q, ContentType = TypeName, Data = new() { ["Title"] = "loop-q", ["ParentPage"] = p.ToString() } });
            await session.SaveChangesAsync(Ct);
        }

        var x = await CreateAsync(client, "loop-x");
        var res = await UpdateAsync(client, x, "loop-x", parent: p);

        await ShouldBeRefusedAsync(res, "the chain above p never reaches a root");
    }

    /// <summary>
    /// Two writes that each add one edge of a loop, both past the check before either commits. The
    /// hook takes a transaction advisory lock before it reads the tree, so the second write waits
    /// for the first to commit and then sees its parent.
    /// </summary>
    [Fact]
    public async Task Two_concurrent_writes_cannot_close_a_cycle_between_them()
    {
        var client = await AdminAsync();
        var a = await CreateAsync(client, "race-a-create");
        var b = await CreateAsync(client, "race-b-create");
        var marker = "race-" + Guid.NewGuid().ToString("n");

        var results = await Task.WhenAll(
            UpdateAsync(client, a, marker, parent: b),
            UpdateAsync(client, b, marker, parent: a));

        var codes = results.Select(r => r.StatusCode).ToList();
        codes.Should().HaveCount(2);
        codes.Should().ContainSingle(c => c == HttpStatusCode.OK, "one of the two edges can land");
        codes.Should().ContainSingle(c => c == HttpStatusCode.BadRequest, "the other would close the loop");

        var parents = new[] { await StoredParentAsync(a), await StoredParentAsync(b) };
        parents.Should().ContainSingle(p => p != null, "only one edge is stored");
    }

    private async Task<Guid[]> SeedChainAsync(int length)
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var ids = new Guid[length];
        for (var i = 0; i < length; i++)
        {
            ids[i] = Guid.NewGuid();
            var data = new Dictionary<string, object> { ["Title"] = $"depth-{i}" };
            if (i > 0)
            {
                data["ParentPage"] = ids[i - 1].ToString();
            }

            session.Store(new Content { Id = ids[i], ContentType = TypeName, Data = data });
        }

        await session.SaveChangesAsync(Ct);
        ids.Should().HaveCount(length);
        return ids;
    }
}
