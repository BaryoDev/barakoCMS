using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Threading;
using barakoCMS.Core.Interfaces;
using FluentAssertions;
using Marten;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// A content lifecycle hook is told the id of the entry being written: null on create, the entry's
/// own id on update and on a rollback (issue #715).
/// </summary>
/// <remarks>
/// The contrast is the whole test. A hook guarding a parent reference has to refuse an entry that
/// names itself as its parent, and it cannot do that from the data alone. Every case is asserted
/// against the same content type and the same field values, so the only difference between the
/// recordings is the id, which is what a caller passing null would erase.
///
/// Not to be confused with ContentLifecycleTests, which is about a content type's declared state
/// lifecycle (Draft, Submitted, Approved) and never touches IContentLifecycleHook.
/// </remarks>
[Collection("Sequential")]
public class ContentHookEntryIdTests
{
    private const string TypeName = "hookentryidprobe";

    private readonly IntegrationTestFixture _factory;

    public ContentHookEntryIdTests(IntegrationTestFixture factory) => _factory = factory;

    private sealed record Seen(Guid? EntryId, bool IsCreate);

    /// <summary>Every context the probe hook was handed, keyed by the title the test wrote.</summary>
    private sealed class Recorder
    {
        private readonly Dictionary<string, List<Seen>> _byMarker = new(StringComparer.Ordinal);
        private readonly Lock _gate = new();

        public void Add(string marker, Seen seen)
        {
            lock (_gate)
            {
                if (!_byMarker.TryGetValue(marker, out var list))
                {
                    _byMarker[marker] = list = new List<Seen>();
                }

                list.Add(seen);
            }
        }

        public IReadOnlyList<Seen> For(string marker)
        {
            lock (_gate)
            {
                return _byMarker.TryGetValue(marker, out var list) ? list.ToList() : [];
            }
        }
    }

    private sealed class ProbeHook : IContentLifecycleHook
    {
        private readonly Recorder _recorder;

        public ProbeHook(Recorder recorder) => _recorder = recorder;

        public string ContentType => TypeName;

        public Task<IReadOnlyList<string>> OnBeforeSaveAsync(ContentLifecycleContext context, CancellationToken ct)
        {
            var marker = context.Data.TryGetValue("Title", out var title) ? title?.ToString() ?? "" : "";
            _recorder.Add(marker, new Seen(context.EntryId, context.IsCreate));
            return Task.FromResult<IReadOnlyList<string>>([]);
        }
    }

    private static readonly Recorder Recorded = new();
    private static readonly Lock HostGate = new();
    private static WebApplicationFactory<Program>? _host;

    /// <summary>
    /// One host for the whole class, carrying the probe hook. Never disposed, per the note on
    /// IntegrationTestFixture.WithSetting.
    /// </summary>
    private WebApplicationFactory<Program> HookHost()
    {
        lock (HostGate)
        {
            return _host ??= _factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            {
                services.AddSingleton(Recorded);
                services.AddScoped<IContentLifecycleHook, ProbeHook>();
            }));
        }
    }

    private static readonly SemaphoreSlim TypeGate = new(1, 1);
    private static bool _typeDeclared;

    /// <summary>
    /// The probe type, declared once. The hook matches one fixed type name, so the type cannot carry
    /// a per-test suffix the way the rest of the suite's throwaway types do.
    /// </summary>
    private static async Task DeclareTypeAsync(HttpClient client)
    {
        await TypeGate.WaitAsync(TestContext.Current.CancellationToken);
        try
        {
            if (_typeDeclared)
            {
                return;
            }

            var res = await client.PostAsJsonAsync("/api/content-types", new
            {
                name = TypeName,
                displayName = "Hook Entry Id Probe",
                fields = new[] { new { name = "Title", type = "string" } },
            }, TestContext.Current.CancellationToken);

            res.IsSuccessStatusCode.Should().BeTrue(
                "got {0}: {1}", res.StatusCode, await res.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            _typeDeclared = true;
        }
        finally
        {
            TypeGate.Release();
        }
    }

    private static async Task<Guid> CreateAsync(HttpClient client, string marker)
    {
        var res = await client.PostAsJsonAsync("/api/contents", new
        {
            contentType = TypeName,
            data = new Dictionary<string, object> { ["Title"] = marker },
        }, TestContext.Current.CancellationToken);

        res.IsSuccessStatusCode.Should().BeTrue(
            "got {0}: {1}", res.StatusCode, await res.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        using var doc = System.Text.Json.JsonDocument.Parse(
            await res.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return doc.RootElement.GetProperty("id").GetGuid();
    }

    private async Task<HttpClient> AdminAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();

        var roleNames = new[] { "SuperAdmin", "Admin" };
        var roleIds = new List<Guid>();
        foreach (var name in roleNames)
        {
            var role = await session.Query<barakoCMS.Models.Role>().FirstOrDefaultAsync(r => r.Name == name);
            if (role is null)
            {
                role = new barakoCMS.Models.Role { Id = Guid.NewGuid(), Name = name };
                session.Store(role);
            }

            roleIds.Add(role.Id);
        }

        var userId = Guid.NewGuid();
        session.Store(new barakoCMS.Models.User
        {
            Id = userId,
            Username = $"hookid-{userId:n}",
            Email = $"hookid-{userId:n}@example.com",
            RoleIds = roleIds,
        });
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);

        var client = HookHost().CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", _factory.CreateToken(roles: roleNames, userId: userId.ToString()));
        return client;
    }

    [Fact]
    public async Task A_create_tells_the_hook_there_is_no_id_yet()
    {
        var client = await AdminAsync();
        await DeclareTypeAsync(client);
        var marker = "create-" + Guid.NewGuid().ToString("n");

        var id = await CreateAsync(client, marker);

        var seen = Recorded.For(marker);
        seen.Should().HaveCount(1, "the hook has to have run, or there is nothing for the assertions below to read");
        seen[0].IsCreate.Should().BeTrue();
        seen[0].EntryId.Should().BeNull("the entry has no id until the write is accepted");
        id.Should().NotBeEmpty("and the write did go through");
    }

    [Fact]
    public async Task An_update_tells_the_hook_which_entry_is_being_written()
    {
        var client = await AdminAsync();
        await DeclareTypeAsync(client);
        var marker = "update-" + Guid.NewGuid().ToString("n");
        var id = await CreateAsync(client, marker);

        var res = await client.PutAsJsonAsync($"/api/contents/{id}", new
        {
            id,
            data = new Dictionary<string, object> { ["Title"] = marker },
        }, TestContext.Current.CancellationToken);

        res.IsSuccessStatusCode.Should().BeTrue(
            "got {0}: {1}", res.StatusCode, await res.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        var seen = Recorded.For(marker);
        seen.Should().HaveCount(2, "the create and then the update, and an empty list would prove nothing");
        seen[1].IsCreate.Should().BeFalse();
        seen[1].EntryId.Should().Be(id, "an update knows the id of the entry it is writing");
        seen[0].EntryId.Should().BeNull("the same data on create carries no id, so the id is the only difference");
    }

    /// <summary>
    /// A rollback is a write like any other, and it is the third caller of the runner. Its id comes
    /// from a different place than the update endpoint's, the loaded document rather than the
    /// request, so it is worth its own assertion.
    /// </summary>
    [Fact]
    public async Task A_rollback_tells_the_hook_which_entry_is_being_restored()
    {
        var client = await AdminAsync();
        await DeclareTypeAsync(client);
        var original = "rollback-v1-" + Guid.NewGuid().ToString("n");
        var replacement = "rollback-v2-" + Guid.NewGuid().ToString("n");

        var id = await CreateAsync(client, original);

        var update = await client.PutAsJsonAsync($"/api/contents/{id}", new
        {
            id,
            data = new Dictionary<string, object> { ["Title"] = replacement },
        }, TestContext.Current.CancellationToken);
        update.IsSuccessStatusCode.Should().BeTrue(
            "got {0}: {1}", update.StatusCode, await update.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        var versionId = await VersionOfAsync(client, id, original);

        var rollback = await client.PostAsJsonAsync(
            $"/api/contents/{id}/rollback/{versionId}", new { }, TestContext.Current.CancellationToken);
        rollback.IsSuccessStatusCode.Should().BeTrue(
            "got {0}: {1}", rollback.StatusCode, await rollback.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        // Restoring the first version writes the first version's data, so the hook sees this marker
        // twice: once at create, once at the rollback. Same data both times, and the id is the only
        // thing that differs.
        var seen = Recorded.For(original);
        seen.Should().HaveCount(2, "the create and then the rollback that restored it, and an empty list would prove nothing");
        seen[0].EntryId.Should().BeNull("the create still carries no id");
        seen[1].IsCreate.Should().BeFalse();
        seen[1].EntryId.Should().Be(id, "a rollback knows the id of the entry it is restoring");
    }

    /// <summary>The id of the version of <paramref name="id"/> whose Title is <paramref name="marker"/>.</summary>
    private static async Task<Guid> VersionOfAsync(HttpClient client, Guid id, string marker)
    {
        var res = await client.GetAsync($"/api/contents/{id}/history", TestContext.Current.CancellationToken);
        res.IsSuccessStatusCode.Should().BeTrue(
            "got {0}: {1}", res.StatusCode, await res.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        using var doc = System.Text.Json.JsonDocument.Parse(
            await res.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var versions = doc.RootElement.GetProperty("items").EnumerateArray().ToList();
        versions.Should().NotBeEmpty("the history has to list something for a version to be picked out of it");

        var match = versions
            .Where(v => v.GetProperty("data").GetProperty("Title").GetString() == marker)
            .Select(v => v.GetProperty("versionId").GetGuid())
            .ToList();
        match.Should().HaveCount(1, "exactly one recorded version wrote {0}", marker);
        return match[0];
    }
}
