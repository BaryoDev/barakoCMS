using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using barakoCMS.Models;

namespace BarakoCMS.Tests;

/// <summary>
/// "Own records only", expressed as a permission condition on the document's owner.
/// </summary>
/// <remarks>
/// Permissions were per content type and per field, which is right for editorial content where a
/// role decides access and wrong for content created by end users, where identity does. There was
/// nowhere to put the answer: <c>Content</c> carried only <c>LastModifiedBy</c>, which moves to
/// whoever edited last, so ownership survived exactly until somebody else touched the record.
///
/// The events already carried it. <c>ContentCreated.CreatedBy</c> has always been there and
/// <c>Apply</c> wrote it into <c>LastModifiedBy</c>, where the next update overwrote it. So this is
/// a field the document was discarding rather than a fact the system never had, and a stream rebuild
/// recovers it for content written before 4.0.
///
/// The list path is the one worth the most care. Get and update gate a single record and fail
/// closed; a list that forgets the owner filter returns everyone's records at once and looks like it
/// is working, because the caller sees data.
/// </remarks>
[Collection("Sequential")]
public class ContentOwnershipTests
{
    private readonly IntegrationTestFixture _factory;

    public ContentOwnershipTests(IntegrationTestFixture factory) => _factory = factory;

    private const string Type = "ownedpost";

    /// <summary>A role that may read and update only what its holder created.</summary>
    private static PermissionRule OwnRecordsOnly() => new()
    {
        Enabled = true,
        Conditions = new Dictionary<string, object>
        {
            ["$createdBy"] = new Dictionary<string, object> { ["_eq"] = "$CURRENT_USER" },
        },
    };

    private async Task<(HttpClient Client, Guid UserId)> OwnerScopedUserAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();

        var role = new Role
        {
            Id = Guid.NewGuid(),
            Name = $"Owner_{Guid.NewGuid():n}",
            Permissions =
            [
                new ContentTypePermission
                {
                    ContentTypeSlug = Type,
                    Create = new PermissionRule { Enabled = true },
                    Read = OwnRecordsOnly(),
                    Update = OwnRecordsOnly(),
                    Delete = OwnRecordsOnly(),
                },
            ],
        };
        session.Store(role);

        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = $"owner_{Guid.NewGuid():n}",
            Email = $"owner_{Guid.NewGuid():n}@example.com",
            RoleIds = [role.Id],
        };
        session.Store(user);
        await session.SaveChangesAsync();

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer", _factory.CreateToken(roles: [role.Name], userId: user.Id.ToString()));

        return (client, user.Id);
    }

    /// <summary>
    /// A SuperAdmin with a real user document behind the token.
    /// </summary>
    /// <remarks>
    /// Minting a token that claims the SuperAdmin role is not enough. PermissionResolver resolves
    /// roles through MembershipRoles.EffectiveRoleIdsAsync, which reads the user document and its
    /// membership, so a token naming a role the stored user does not hold grants nothing. Worth
    /// knowing: it means a stolen or forged role claim cannot escalate on its own.
    /// </remarks>
    private async Task<HttpClient> SuperAdminAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();

        var role = await session.Query<Role>().FirstOrDefaultAsync(r => r.Name == "SuperAdmin");
        if (role is null)
        {
            role = new Role { Id = Guid.NewGuid(), Name = "SuperAdmin" };
            session.Store(role);
        }

        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = $"admin_{Guid.NewGuid():n}",
            Email = $"admin_{Guid.NewGuid():n}@example.com",
            RoleIds = [role.Id],
        };
        session.Store(user);
        await session.SaveChangesAsync();

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer", _factory.CreateToken(roles: ["SuperAdmin"], userId: user.Id.ToString()));
        return client;
    }

    private static async Task<Guid> CreateAsync(HttpClient client, string title)
    {
        var res = await client.PostAsJsonAsync("/api/contents", new
        {
            contentType = Type,
            data = new Dictionary<string, object> { ["Title"] = title },
        });
        res.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}",
            res.StatusCode, await res.Content.ReadAsStringAsync());

        using var doc = System.Text.Json.JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("id").GetGuid();
    }

    [Fact]
    public async Task The_creator_is_recorded_on_the_document()
    {
        var (client, userId) = await OwnerScopedUserAsync();
        var id = await CreateAsync(client, "mine");

        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        var content = await session.LoadAsync<Content>(id);

        content!.CreatedBy.Should().Be(userId, "the creator is who the event named");
    }

    /// <summary>
    /// Ownership survives somebody else editing the record.
    /// </summary>
    /// <remarks>
    /// The defect this issue is really about. While ownership lived in LastModifiedBy, one edit by
    /// an administrator transferred the record to them, and every ownership rule then denied the
    /// person who wrote it. A test that only checked ownership at creation time would pass against
    /// the broken behaviour.
    /// </remarks>
    [Fact]
    public async Task An_edit_by_someone_else_does_not_transfer_ownership()
    {
        var (owner, ownerId) = await OwnerScopedUserAsync();
        var id = await CreateAsync(owner, "mine");

        var admin = await SuperAdminAsync();

        var edit = await admin.PutAsJsonAsync($"/api/contents/{id}", new
        {
            data = new Dictionary<string, object> { ["Title"] = "edited by an admin" },
        });
        edit.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}",
            edit.StatusCode, await edit.Content.ReadAsStringAsync());

        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        var content = await session.LoadAsync<Content>(id);

        content!.CreatedBy.Should().Be(ownerId, "editing a record does not make it yours");
        content.LastModifiedBy.Should().NotBe(ownerId, "the admin is who touched it last");
    }

    [Fact]
    public async Task A_user_cannot_read_another_users_record_by_id()
    {
        var (a, _) = await OwnerScopedUserAsync();
        var (b, _) = await OwnerScopedUserAsync();

        var mine = await CreateAsync(a, "a's record");

        var asA = await a.GetAsync($"/api/contents/{mine}");
        var asB = await b.GetAsync($"/api/contents/{mine}");

        asA.StatusCode.Should().Be(HttpStatusCode.OK, "the owner can read their own record");
        asB.StatusCode.Should().BeOneOf([HttpStatusCode.Forbidden, HttpStatusCode.NotFound],
            "another user's record is not readable");
    }

    /// <summary>
    /// A list returns only the caller's own records.
    /// </summary>
    /// <remarks>
    /// The path this issue calls the risky one. Both users have records here so an implementation
    /// that skipped the per-item check would return the other user's and still look plausible: a
    /// non-empty page of the right shape.
    /// </remarks>
    [Fact]
    public async Task A_list_returns_only_the_callers_own_records()
    {
        var (a, _) = await OwnerScopedUserAsync();
        var (b, _) = await OwnerScopedUserAsync();

        var needle = $"a-only-{Guid.NewGuid():n}";
        await CreateAsync(a, needle);
        await CreateAsync(b, $"b-only-{Guid.NewGuid():n}");

        var body = await (await b.GetAsync($"/api/contents?contentType={Type}")).Content.ReadAsStringAsync();

        body.Should().NotContain(needle, "b is not the owner of a's record");
    }

    /// <summary>
    /// A user cannot update another user's record.
    /// </summary>
    /// <remarks>
    /// Only update, because there is no <c>DELETE /api/contents/{id}</c>. The only delete route is
    /// <c>/api/contents/{id}/erase</c>, which is SuperAdmin and audited. So the <c>Delete</c>
    /// permission rule on <c>ContentTypePermission</c> currently governs nothing, which is worth
    /// knowing and is not this issue's to fix.
    /// </remarks>
    [Fact]
    public async Task A_user_cannot_update_another_users_record()
    {
        var (a, _) = await OwnerScopedUserAsync();
        var (b, _) = await OwnerScopedUserAsync();

        var mine = await CreateAsync(a, "a's record");

        var update = await b.PutAsJsonAsync($"/api/contents/{mine}", new
        {
            data = new Dictionary<string, object> { ["Title"] = "taken over" },
        });

        update.IsSuccessStatusCode.Should().BeFalse("b does not own this record");
    }

    /// <summary>
    /// The control. Without it, a resolver that denied everything would satisfy every test above.
    /// </summary>
    [Fact]
    public async Task The_owner_can_update_their_own_record()
    {
        var (a, _) = await OwnerScopedUserAsync();
        var id = await CreateAsync(a, "a's record");

        var update = await a.PutAsJsonAsync($"/api/contents/{id}", new
        {
            data = new Dictionary<string, object> { ["Title"] = "still mine" },
        });

        update.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}",
            update.StatusCode, await update.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// A SuperAdmin still sees everything.
    /// </summary>
    /// <remarks>
    /// The rule is "own records only unless a role says otherwise", and the bypass has to keep
    /// working or an ownership rule locks administrators out of their own instance.
    /// </remarks>
    [Fact]
    public async Task An_administrator_still_sees_records_they_do_not_own()
    {
        var (a, _) = await OwnerScopedUserAsync();
        var id = await CreateAsync(a, "a's record");

        var admin = await SuperAdminAsync();

        var res = await admin.GetAsync($"/api/contents/{id}");

        res.StatusCode.Should().Be(HttpStatusCode.OK, "a SuperAdmin bypass is not conditional on ownership");
    }

    /// <summary>
    /// A record with no owner is denied, not granted.
    /// </summary>
    /// <remarks>
    /// Content written before 4.0 has <c>Guid.Empty</c> on the document until a stream rebuild runs.
    /// An ownership condition compares that against the caller and denies, which is the direction
    /// that cannot leak. Asserted rather than assumed, because the opposite reading of "no owner" is
    /// "anyone", and that would hand every legacy record to every user on the first request.
    /// </remarks>
    [Fact]
    public async Task A_record_with_no_owner_is_not_readable_under_an_ownership_rule()
    {
        var (client, _) = await OwnerScopedUserAsync();

        var orphan = Guid.NewGuid();
        using (var scope = _factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Store(new Content
            {
                Id = orphan,
                ContentType = Type,
                Data = new Dictionary<string, object> { ["Title"] = "written before 4.0" },
                // CreatedBy left at Guid.Empty, which is what a pre-4.0 document reads as.
            });
            await session.SaveChangesAsync();
        }

        var res = await client.GetAsync($"/api/contents/{orphan}");

        res.IsSuccessStatusCode.Should().BeFalse(
            "an unowned record is nobody's, and reading that as everybody's would hand every legacy "
          + "record to every user");
    }
}
