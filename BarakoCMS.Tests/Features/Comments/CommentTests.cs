using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BarakoCMS.Comments;
using barakoCMS.Infrastructure.Audit;
using barakoCMS.Infrastructure.Auth;
using Microsoft.AspNetCore.Mvc.Testing;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using barakoCMS.Models;
using barakoCMS.Modules;

namespace BarakoCMS.Tests.Features.Comments;

/// <summary>
/// A thread on any record: post, reply, list by target, and the rule that a comment is only as
/// readable as the record it hangs on.
/// </summary>
[Collection("Sequential")]
public class CommentTests
{
    private readonly IntegrationTestFixture _factory;

    public CommentTests(IntegrationTestFixture factory) => _factory = factory;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<Guid> EntryAsync(string type)
    {
        using var scope = _factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IDocumentStore>();
        await using var session = store.LightweightSession();

        var id = Guid.NewGuid();
        session.Store(new Content
        {
            Id = id,
            ContentType = type,
            Status = ContentStatus.Published,
            Data = new Dictionary<string, object> { ["Title"] = "a record" },
        });
        await session.SaveChangesAsync(Ct);
        return id;
    }

    private static string NewType() => $"cm_{Guid.NewGuid():n}"[..12];

    private async Task<HttpClient> AdminAsync()
    {
        var (token, _) = await TestHelpers.CreateAdminUserAsync(_factory);
        return ClientFor(token);
    }

    private HttpClient ClientFor(string token)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        client.DefaultRequestHeaders.Add(TestRemoteIpFilter.Header, $"198.51.100.{Random.Shared.Next(1, 250)}");
        return client;
    }

    /// <summary>
    /// A signed-in user holding exactly the capabilities given and read access on the content types
    /// given, so a refusal is the rule under test and not a missing wildcard.
    /// </summary>
    private async Task<(HttpClient Client, Guid UserId)> UserAsync(string[] capabilities, params string[] readableTypes)
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();

        var role = new Role
        {
            Id = Guid.NewGuid(),
            Name = $"Role_{Guid.NewGuid():n}",
            SystemCapabilities = [.. capabilities],
            Permissions = [.. readableTypes.Select(t => new ContentTypePermission
            {
                ContentTypeSlug = t,
                Read = new PermissionRule { Enabled = true },
            })],
        };
        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = $"cm-{Guid.NewGuid():n}"[..14],
            Email = $"cm-{Guid.NewGuid():n}@example.com",
            RoleIds = [role.Id],
        };
        session.Store(role);
        session.Store(user);
        await session.SaveChangesAsync(Ct);

        return (ClientFor(_factory.CreateToken(roles: [role.Name], userId: user.Id.ToString())), user.Id);
    }

    private static readonly string[] Both = [CommentsCapabilities.ReadComments, CommentsCapabilities.PostComments];

    private static async Task<JsonElement> PostAsync(
        HttpClient client, string type, Guid id, string body, Guid? parent = null, HttpStatusCode expect = HttpStatusCode.Created)
    {
        var res = await client.PostAsJsonAsync(
            "/api/comments", new { targetType = type, targetId = id, body, parentId = parent }, Ct);
        res.StatusCode.Should().Be(expect, await res.Content.ReadAsStringAsync(Ct));
        return expect == HttpStatusCode.Created
            ? await res.Content.ReadFromJsonAsync<JsonElement>(Ct)
            : default;
    }

    private static async Task<JsonElement> ListAsync(
        HttpClient client, string type, Guid id, string query = "", HttpStatusCode expect = HttpStatusCode.OK)
    {
        var res = await client.GetAsync($"/api/comments?targetType={type}&targetId={id}{query}", Ct);
        res.StatusCode.Should().Be(expect, await res.Content.ReadAsStringAsync(Ct));
        return expect == HttpStatusCode.OK ? await res.Content.ReadFromJsonAsync<JsonElement>(Ct) : default;
    }

    private static List<string> Bodies(JsonElement page) =>
        page.GetProperty("items").EnumerateArray().Select(c => c.GetProperty("body").GetString()!).ToList();

    [Fact]
    public async Task A_comment_posted_on_an_entry_is_listed_with_its_author_and_body()
    {
        var type = NewType();
        var entry = await EntryAsync(type);
        var (client, userId) = await UserAsync(Both, type);

        var posted = await PostAsync(client, "content", entry, "first note");

        posted.GetProperty("id").GetGuid().Should().NotBeEmpty();
        posted.GetProperty("authorId").GetGuid().Should().Be(userId);
        var page = await ListAsync(client, "content", entry);
        page.GetProperty("totalItems").GetInt32().Should().Be(1);
        var item = page.GetProperty("items")[0];
        item.GetProperty("body").GetString().Should().Be("first note");
        item.GetProperty("authorId").GetGuid().Should().Be(userId);
        item.GetProperty("targetType").GetString().Should().Be("content");
        item.GetProperty("targetId").GetGuid().Should().Be(entry);
    }

    [Fact]
    public async Task A_reply_names_its_parent_and_the_thread_lists_oldest_first()
    {
        var entry = await EntryAsync(NewType());
        var admin = await AdminAsync();

        var root = await PostAsync(admin, "content", entry, "question");
        var reply = await PostAsync(admin, "content", entry, "answer", root.GetProperty("id").GetGuid());
        await PostAsync(admin, "content", entry, "follow up");

        reply.GetProperty("parentId").GetGuid().Should().Be(root.GetProperty("id").GetGuid());
        var page = await ListAsync(admin, "content", entry);
        Bodies(page).Should().Equal("question", "answer", "follow up");
        page.GetProperty("items")[0].GetProperty("parentId").ValueKind.Should().Be(JsonValueKind.Null);
        page.GetProperty("items")[1].GetProperty("parentId").GetGuid().Should().Be(root.GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task Listing_one_record_does_not_return_the_comments_of_another()
    {
        var type = NewType();
        var first = await EntryAsync(type);
        var second = await EntryAsync(type);
        var admin = await AdminAsync();

        await PostAsync(admin, "content", first, "on the first");
        await PostAsync(admin, "content", second, "on the second");

        Bodies(await ListAsync(admin, "content", first)).Should().Equal("on the first");
        Bodies(await ListAsync(admin, "content", second)).Should().Equal("on the second");
    }

    [Fact]
    public async Task A_reply_cannot_name_a_parent_on_another_record()
    {
        var type = NewType();
        var first = await EntryAsync(type);
        var second = await EntryAsync(type);
        var admin = await AdminAsync();
        var elsewhere = await PostAsync(admin, "content", first, "on the first");

        await PostAsync(admin, "content", second, "stray reply", elsewhere.GetProperty("id").GetGuid(),
            HttpStatusCode.BadRequest);
        await PostAsync(admin, "content", second, "orphan reply", Guid.NewGuid(), HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task The_list_is_paged_and_the_page_size_is_capped()
    {
        var entry = await EntryAsync(NewType());
        var admin = await AdminAsync();
        for (var i = 1; i <= 3; i++) await PostAsync(admin, "content", entry, $"note {i}");

        var first = await ListAsync(admin, "content", entry, "&page=1&pageSize=2");
        var second = await ListAsync(admin, "content", entry, "&page=2&pageSize=2");
        var huge = await ListAsync(admin, "content", entry, "&pageSize=100000");

        Bodies(first).Should().Equal("note 1", "note 2");
        Bodies(second).Should().Equal("note 3");
        first.GetProperty("totalItems").GetInt32().Should().Be(3);
        first.GetProperty("pageSize").GetInt32().Should().Be(2);
        huge.GetProperty("pageSize").GetInt32().Should().Be(PaginatedRequest.MaxPageSize);
    }

    [Fact]
    public async Task Markup_in_a_comment_is_stored_and_returned_as_text_and_never_as_html()
    {
        var entry = await EntryAsync(NewType());
        var admin = await AdminAsync();
        const string markup = "<script>alert(1)</script><img src=x onerror=alert(2)> & \"quoted\"";

        await PostAsync(admin, "content", entry, markup);

        using (var scope = _factory.Services.CreateScope())
        {
            var stored = await scope.ServiceProvider.GetRequiredService<IQuerySession>()
                .Query<Comment>().Where(c => c.TargetId == entry).ToListAsync(Ct);
            stored.Should().ContainSingle().Which.Body.Should().Be(markup, "the API keeps what was typed and encodes nothing");
        }

        var res = await admin.GetAsync($"/api/comments?targetType=content&targetId={entry}", Ct);
        res.Content.Headers.ContentType!.MediaType.Should().Be("application/json",
            "a browser given this response as a document would show JSON, never run it");
        Bodies(await res.Content.ReadFromJsonAsync<JsonElement>(Ct)).Should().Equal(markup);
    }

    [Fact]
    public async Task An_empty_or_oversized_body_is_refused()
    {
        var entry = await EntryAsync(NewType());
        var admin = await AdminAsync();

        await PostAsync(admin, "content", entry, "   ", expect: HttpStatusCode.BadRequest);
        await PostAsync(admin, "content", entry, new string('x', CommentLimits.MaxBodyLength + 1), expect: HttpStatusCode.BadRequest);
        (await ListAsync(admin, "content", entry)).GetProperty("totalItems").GetInt32().Should().Be(0);

        await PostAsync(admin, "content", entry, new string('x', CommentLimits.MaxBodyLength));
    }

    [Fact]
    public async Task A_target_type_nobody_registered_is_refused()
    {
        var admin = await AdminAsync();

        await PostAsync(admin, "nonsense", Guid.NewGuid(), "hello", expect: HttpStatusCode.BadRequest);
        await ListAsync(admin, "nonsense", Guid.NewGuid(), expect: HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_record_that_does_not_exist_has_no_thread()
    {
        var admin = await AdminAsync();

        await PostAsync(admin, "content", Guid.NewGuid(), "hello", expect: HttpStatusCode.NotFound);
        await ListAsync(admin, "content", Guid.NewGuid(), expect: HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task An_anonymous_caller_can_neither_read_nor_post()
    {
        var entry = await EntryAsync(NewType());
        var anonymous = _factory.CreateClient();
        anonymous.DefaultRequestHeaders.Add(TestRemoteIpFilter.Header, "198.51.100.251");

        (await anonymous.GetAsync($"/api/comments?targetType=content&targetId={entry}", Ct))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await anonymous.PostAsJsonAsync("/api/comments", new { targetType = "content", targetId = entry, body = "hi" }, Ct))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_caller_without_the_comment_capabilities_is_refused_even_when_they_can_read_the_entry()
    {
        var type = NewType();
        var entry = await EntryAsync(type);
        var (readOnly, _) = await UserAsync([CommentsCapabilities.ReadComments], type);
        var (none, _) = await UserAsync([], type);

        await PostAsync(readOnly, "content", entry, "hi", expect: HttpStatusCode.Forbidden);
        await ListAsync(none, "content", entry, expect: HttpStatusCode.Forbidden);
        (await ListAsync(readOnly, "content", entry)).GetProperty("totalItems").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task Nobody_reads_or_adds_comments_on_an_entry_they_cannot_read()
    {
        var readable = NewType();
        var secret = NewType();
        var secretEntry = await EntryAsync(secret);
        var admin = await AdminAsync();
        await PostAsync(admin, "content", secretEntry, "for those who can read this");
        var (client, _) = await UserAsync(Both, readable);

        await ListAsync(client, "content", secretEntry, expect: HttpStatusCode.Forbidden);
        await PostAsync(client, "content", secretEntry, "let me in", expect: HttpStatusCode.Forbidden);

        (await ListAsync(admin, "content", secretEntry)).GetProperty("totalItems").GetInt32().Should().Be(1,
            "the refused post left nothing behind");
    }

    [Fact]
    public async Task A_second_kind_of_record_shares_the_same_surface_and_its_own_read_rule()
    {
        var open = Guid.NewGuid();
        var closed = Guid.NewGuid();
        TicketCommentTarget.Instance.Add(open, readable: true);
        TicketCommentTarget.Instance.Add(closed, readable: false);
        var entry = await EntryAsync(NewType());
        var admin = await AdminAsync();

        await PostAsync(admin, "ticket", open, "ticket reply");
        await PostAsync(admin, "content", entry, "editorial note");

        Bodies(await ListAsync(admin, "ticket", open)).Should().Equal("ticket reply");
        Bodies(await ListAsync(admin, "content", entry)).Should().Equal("editorial note");
        await PostAsync(admin, "ticket", closed, "no", expect: HttpStatusCode.Forbidden);
        await ListAsync(admin, "ticket", closed, expect: HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task The_author_deletes_their_own_comment_and_its_replies_go_with_it()
    {
        var type = NewType();
        var entry = await EntryAsync(type);
        var (author, _) = await UserAsync(Both, type);
        var root = await PostAsync(author, "content", entry, "mine");
        var reply = await PostAsync(author, "content", entry, "reply", root.GetProperty("id").GetGuid());
        await PostAsync(author, "content", entry, "nested", reply.GetProperty("id").GetGuid());
        await PostAsync(author, "content", entry, "unrelated");

        var res = await author.DeleteAsync($"/api/comments/{root.GetProperty("id").GetGuid()}", Ct);

        res.StatusCode.Should().Be(HttpStatusCode.NoContent);
        Bodies(await ListAsync(author, "content", entry)).Should().Equal("unrelated");
    }

    [Fact]
    public async Task Only_the_author_or_a_moderator_can_delete_a_comment()
    {
        var type = NewType();
        var entry = await EntryAsync(type);
        var (author, _) = await UserAsync(Both, type);
        var (other, _) = await UserAsync(Both, type);
        var (moderator, _) = await UserAsync([.. Both, CommentsCapabilities.ManageComments], type);
        var first = await PostAsync(author, "content", entry, "keep");
        var second = await PostAsync(author, "content", entry, "remove");

        (await other.DeleteAsync($"/api/comments/{first.GetProperty("id").GetGuid()}", Ct))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await moderator.DeleteAsync($"/api/comments/{second.GetProperty("id").GetGuid()}", Ct))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);

        Bodies(await ListAsync(author, "content", entry)).Should().Equal("keep");
    }

    [Fact]
    public async Task An_author_cannot_delete_a_root_that_holds_another_authors_reply()
    {
        var type = NewType();
        var entry = await EntryAsync(type);
        var (a, _) = await UserAsync(Both, type);
        var (b, _) = await UserAsync(Both, type);
        var root = await PostAsync(a, "content", entry, "a's root");
        await PostAsync(b, "content", entry, "b's reply", root.GetProperty("id").GetGuid());

        var res = await a.DeleteAsync($"/api/comments/{root.GetProperty("id").GetGuid()}", Ct);

        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        Bodies(await ListAsync(a, "content", entry)).Should().Equal("a's root", "b's reply");
    }

    [Fact]
    public async Task A_moderator_holding_only_manage_comments_can_delete_a_comment_and_its_replies()
    {
        var type = NewType();
        var entry = await EntryAsync(type);
        var (author, _) = await UserAsync(Both, type);
        var (moderator, _) = await UserAsync([CommentsCapabilities.ManageComments], type);
        var root = await PostAsync(author, "content", entry, "remove me");
        await PostAsync(author, "content", entry, "and me", root.GetProperty("id").GetGuid());
        await PostAsync(author, "content", entry, "stay");

        var res = await moderator.DeleteAsync($"/api/comments/{root.GetProperty("id").GetGuid()}", Ct);

        res.StatusCode.Should().Be(HttpStatusCode.NoContent);
        Bodies(await ListAsync(author, "content", entry)).Should().Equal("stay");
    }

    [Fact]
    public async Task A_caller_with_neither_post_nor_manage_comments_cannot_delete()
    {
        var type = NewType();
        var entry = await EntryAsync(type);
        var admin = await AdminAsync();
        var (reader, _) = await UserAsync([CommentsCapabilities.ReadComments], type);
        var comment = await PostAsync(admin, "content", entry, "kept");

        (await reader.DeleteAsync($"/api/comments/{comment.GetProperty("id").GetGuid()}", Ct))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        Bodies(await ListAsync(admin, "content", entry)).Should().Equal("kept");
    }

    [Fact]
    public async Task Delete_is_refused_when_the_caller_can_no_longer_read_the_record()
    {
        var id = Guid.NewGuid();
        TicketCommentTarget.Instance.Add(id, readable: true);
        var admin = await AdminAsync();
        var comment = await PostAsync(admin, "ticket", id, "mine");
        TicketCommentTarget.Instance.Add(id, readable: false);

        var res = await admin.DeleteAsync($"/api/comments/{comment.GetProperty("id").GetGuid()}", Ct);

        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        TicketCommentTarget.Instance.Add(id, readable: true);
        Bodies(await ListAsync(admin, "ticket", id)).Should().Equal("mine");
    }

    private async Task RemoveContentAsync(Guid id)
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.HardDelete<Content>(id);
        await session.SaveChangesAsync(Ct);
    }

    private async Task<int> CommentCountAsync(Guid targetId)
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IQuerySession>()
            .Query<Comment>().Where(c => c.TargetId == targetId).CountAsync(Ct);
    }

    [Fact]
    public async Task A_moderator_can_delete_comments_on_an_erased_record_and_anyone_else_gets_not_found()
    {
        var type = NewType();
        var entry = await EntryAsync(type);
        var (author, _) = await UserAsync(Both, type);
        var (moderator, _) = await UserAsync([CommentsCapabilities.ManageComments], type);
        var root = await PostAsync(author, "content", entry, "orphan to be");
        await PostAsync(author, "content", entry, "its reply", root.GetProperty("id").GetGuid());
        await RemoveContentAsync(entry);
        var path = $"/api/comments/{root.GetProperty("id").GetGuid()}";

        (await author.DeleteAsync(path, Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await CommentCountAsync(entry)).Should().Be(2);
        (await moderator.DeleteAsync(path, Ct)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await CommentCountAsync(entry)).Should().Be(0);
    }

    [Fact]
    public async Task A_moderator_can_delete_a_comment_whose_target_type_is_no_longer_registered()
    {
        var targetId = Guid.NewGuid();
        var comment = new Comment
        {
            Id = Guid.NewGuid(),
            TargetType = "retired_kind",
            TargetId = targetId,
            AuthorId = Guid.NewGuid(),
            Body = "left behind",
        };
        using (var scope = _factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Store(comment);
            await session.SaveChangesAsync(Ct);
        }

        var (other, _) = await UserAsync(Both);
        var (moderator, _) = await UserAsync([CommentsCapabilities.ManageComments]);

        (await other.DeleteAsync($"/api/comments/{comment.Id}", Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await CommentCountAsync(targetId)).Should().Be(1);
        (await moderator.DeleteAsync($"/api/comments/{comment.Id}", Ct)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await CommentCountAsync(targetId)).Should().Be(0);
    }

    [Fact]
    public async Task Deleting_comments_that_are_not_all_the_callers_own_is_audited_and_deleting_ones_own_is_not()
    {
        var type = NewType();
        var entry = await EntryAsync(type);
        var (author, _) = await UserAsync(Both, type);
        var (moderator, moderatorId) = await UserAsync([CommentsCapabilities.ManageComments], type);
        var own = await PostAsync(author, "content", entry, "own");
        var root = await PostAsync(author, "content", entry, "root");
        await PostAsync(author, "content", entry, "reply", root.GetProperty("id").GetGuid());

        (await author.DeleteAsync($"/api/comments/{own.GetProperty("id").GetGuid()}", Ct))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await moderator.DeleteAsync($"/api/comments/{root.GetProperty("id").GetGuid()}", Ct))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);

        using var scope = _factory.Services.CreateScope();
        var entries = await scope.ServiceProvider.GetRequiredService<IQuerySession>()
            .Query<AuditEvent>().Where(e => e.TargetId == entry.ToString()).ToListAsync(Ct);
        var logged = entries.Should().ContainSingle().Subject;
        logged.Action.Should().Be("comment.moderated");
        logged.ActorUserId.Should().Be(moderatorId);
        logged.TargetType.Should().Be("content");
        logged.Metadata!["count"].ToString().Should().Be("2");
        logged.Metadata["commentId"].ToString().Should().Be(root.GetProperty("id").GetGuid().ToString());
    }

    [Fact]
    public async Task A_legacy_admin_role_can_moderate_when_the_legacy_fallback_is_on_and_not_otherwise()
    {
        var id = Guid.NewGuid();
        TicketCommentTarget.Instance.Add(id, readable: true);
        var admin = await AdminAsync();
        var comment = await PostAsync(admin, "ticket", id, "someone else's");
        var path = $"/api/comments/{comment.GetProperty("id").GetGuid()}";
        var token = _factory.CreateToken(roles: ["Admin"], userId: Guid.NewGuid().ToString());

        var defaultHost = ClientFor(token);
        (await defaultHost.DeleteAsync(path, Ct)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await CommentCountAsync(id)).Should().Be(1);

        var legacyClient = LegacyHost().CreateClient();
        legacyClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        legacyClient.DefaultRequestHeaders.Add(TestRemoteIpFilter.Header, "198.51.100.77");
        (await legacyClient.DeleteAsync(path, Ct)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await CommentCountAsync(id)).Should().Be(0);
    }

    private static readonly Lock LegacyHostGate = new();
    private static WebApplicationFactory<Program>? _legacyHost;

    private WebApplicationFactory<Program> LegacyHost()
    {
        lock (LegacyHostGate)
        {
            return _legacyHost ??= _factory.WithSetting(CapabilityGateProcessor.LegacyRoleFallbackKey, "true");
        }
    }

    [Fact]
    public async Task The_same_id_under_two_target_types_keeps_the_threads_apart()
    {
        var type = NewType();
        var shared = await EntryAsync(type);
        TicketCommentTarget.Instance.Add(shared, readable: true);
        var admin = await AdminAsync();

        var onContent = await PostAsync(admin, "content", shared, "on the entry");
        var onTicket = await PostAsync(admin, "ticket", shared, "on the ticket");

        Bodies(await ListAsync(admin, "content", shared)).Should().Equal("on the entry");
        Bodies(await ListAsync(admin, "ticket", shared)).Should().Equal("on the ticket");
        await PostAsync(admin, "ticket", shared, "crossed", onContent.GetProperty("id").GetGuid(), HttpStatusCode.BadRequest);
        await PostAsync(admin, "content", shared, "crossed", onTicket.GetProperty("id").GetGuid(), HttpStatusCode.BadRequest);
        Bodies(await ListAsync(admin, "content", shared)).Should().Equal("on the entry");
        Bodies(await ListAsync(admin, "ticket", shared)).Should().Equal("on the ticket");
    }

    [Fact]
    public async Task A_comment_that_does_not_exist_cannot_be_deleted()
    {
        var admin = await AdminAsync();

        (await admin.DeleteAsync($"/api/comments/{Guid.NewGuid()}", Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public void The_module_declares_an_http_contract_version_and_its_capabilities()
    {
        IBarakoModule module = new CommentsModule();

        module.Name.Should().Be("Comments");
        module.HttpContractVersion.Should().Be(1);
        CommentsCapabilities.All.Should().BeEquivalentTo(
            [CommentsCapabilities.ReadComments, CommentsCapabilities.PostComments, CommentsCapabilities.ManageComments]);
    }
}
