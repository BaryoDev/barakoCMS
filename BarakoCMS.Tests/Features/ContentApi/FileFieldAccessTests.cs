using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using barakoCMS.Core.Interfaces;
using barakoCMS.Infrastructure.Multitenancy;
using barakoCMS.Models;
using BarakoCMS.Files;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BarakoCMS.Tests.Features.ContentApi;

/// <summary>
/// Who may put a stored file in a <c>file</c> field, and who is shown it on an authoring read,
/// over HTTP with the Files module behind the seam.
/// </summary>
/// <remarks>
/// The editors here hold create, read and update on the type and no administrator role, so the
/// only files they reach are their own and public ones. Every refusal has a control beside it: the
/// same id from the user the file belongs to.
/// </remarks>
[Collection("Sequential")]
public class FileFieldAccessTests
{
    private const string Refusal = "takes the id of a stored file you may use";

    private readonly IntegrationTestFixture _factory;

    public FileFieldAccessTests(IntegrationTestFixture factory) => _factory = factory;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Editor(Guid Id, HttpClient Client);

    private async Task<string> StoreTypeAsync()
    {
        var name = "filed-" + Guid.NewGuid().ToString("n")[..12];
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Store(new ContentTypeDefinition
        {
            Id = Guid.NewGuid(),
            Name = name,
            DisplayName = name,
            Fields =
            [
                new FieldDefinition { Name = "Title", DisplayName = "Title", Type = "string" },
                new FieldDefinition { Name = "Cover", DisplayName = "Cover", Type = "file" },
            ],
        });
        await session.SaveChangesAsync(Ct);
        return name;
    }

    /// <summary>Two users sharing one role that may create, read and update entries of the type.</summary>
    private async Task<(Editor First, Editor Second)> EditorsAsync(string type)
    {
        var role = new Role
        {
            Id = Guid.NewGuid(),
            Name = $"File editor {Guid.NewGuid():n}",
            Permissions =
            [
                new ContentTypePermission
                {
                    ContentTypeSlug = type,
                    Create = new PermissionRule { Enabled = true },
                    Read = new PermissionRule { Enabled = true },
                    Update = new PermissionRule { Enabled = true },
                },
            ],
        };

        var users = new[] { NewUser(role.Id), NewUser(role.Id) };

        using (var scope = _factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Store(role);
            session.Store(users);
            await session.SaveChangesAsync(Ct);
        }

        return (Signed(users[0], role.Name), Signed(users[1], role.Name));
    }

    private static User NewUser(Guid roleId)
    {
        var id = Guid.NewGuid();
        return new User { Id = id, Username = $"filed-{id:n}", Email = $"filed-{id:n}@example.com", RoleIds = [roleId] };
    }

    private Editor Signed(User user, string role)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            _factory.CreateToken([role], user.Id.ToString(), new Dictionary<string, string> { ["Username"] = user.Username }));
        return new Editor(user.Id, client);
    }

    private async Task<HttpClient> AdminAsync()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", await _factory.StoredUserTokenAsync("Admin", "SuperAdmin"));
        return client;
    }

    private async Task<Guid> FileAsync(bool isPublic, Guid owner, string name = "cover.png", IServiceScope? inScope = null)
    {
        var scope = inScope ?? _factory.Services.CreateScope();
        try
        {
            using var content = new MemoryStream(FileSamples.Png(Guid.NewGuid().ToByteArray()));
            var saved = await scope.ServiceProvider.GetRequiredService<IFileStore>().SaveAsync(
                new FileToStore { Content = content, FileName = name, ContentType = "image/png", IsPublic = isPublic, Owner = owner },
                Ct);

            saved.File.Should().NotBeNull(saved.Refused ?? string.Empty);
            return saved.File!.Id;
        }
        finally
        {
            if (inScope is null) scope.Dispose();
        }
    }

    private static Task<HttpResponseMessage> CreateAsync(HttpClient client, string type, object? cover, string title = "a")
    {
        var data = new Dictionary<string, object?> { ["Title"] = title };
        if (cover is not null) data["Cover"] = cover;

        return client.PostAsJsonAsync("/api/contents", new { contentType = type, data }, Ct);
    }

    private static async Task<Guid> CreatedIdAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("id").GetGuid();
    }

    private static async Task<JsonElement> ReadAsync(HttpClient client, Guid entry)
    {
        var response = await client.GetAsync($"/api/contents/{entry}", Ct);
        var body = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    /// <summary>The entry's row in the list of its type, found by id.</summary>
    private static async Task<JsonElement> ListedAsync(HttpClient client, string type, Guid entry)
    {
        var response = await client.GetAsync($"/api/contents?contentType={type}&pageSize=100", Ct);
        var body = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);

        var rows = JsonDocument.Parse(body).RootElement.GetProperty("items").EnumerateArray().ToList();
        rows.Should().NotBeEmpty();
        return rows.Single(row => row.GetProperty("id").GetGuid() == entry).Clone();
    }

    [Fact]
    public async Task An_editor_attaches_their_own_private_file_and_anyones_public_file()
    {
        var type = await StoreTypeAsync();
        var (first, second) = await EditorsAsync(type);

        var mine = await FileAsync(isPublic: false, owner: first.Id);
        var theirPublic = await FileAsync(isPublic: true, owner: second.Id);

        var own = await CreatedIdAsync(await CreateAsync(first.Client, type, mine.ToString()));
        await CreatedIdAsync(await CreateAsync(first.Client, type, theirPublic.ToString()));
        await CreatedIdAsync(await CreateAsync(first.Client, type, cover: null));

        using var scope = _factory.Services.CreateScope();
        var stored = await scope.ServiceProvider.GetRequiredService<IQuerySession>().LoadAsync<Content>(own, Ct);
        stored.Should().NotBeNull();
        stored!.Data["Cover"].ToString().Should().Be(mine.ToString(), "the entry stores the id and nothing else");
    }

    [Fact]
    public async Task A_file_the_editor_may_not_use_is_refused_in_the_same_words_whatever_the_reason()
    {
        var type = await StoreTypeAsync();
        var (owner, stranger) = await EditorsAsync(type);

        var theirs = await FileAsync(isPublic: false, owner: owner.Id, name: "payslip-march.png");
        var missing = Guid.NewGuid();

        var resize = new StoredFile
        {
            FileName = "resized.png",
            ContentType = "image/png",
            StorageKey = $"{FileKeys.Prefix(true)}{Guid.NewGuid():N}.png",
            IsPublic = true,
            ParentFileId = theirs,
            VariantWidth = 100,
            UploadedBy = stranger.Id,
        };
        using (var scope = _factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Store(resize);
            await session.SaveChangesAsync(Ct);
        }

        Guid elsewhere;
        using (var other = _factory.Services.CreateScopeForTenant($"filed-{Guid.NewGuid():N}"[..20]))
        {
            elsewhere = await FileAsync(isPublic: true, owner: stranger.Id, inScope: other);
        }

        var refused = new (string Reason, object Value)[]
        {
            ("somebody else's private file", theirs.ToString()),
            ("an id no file has", missing.ToString()),
            ("a cached resize, public and the caller's own", resize.Id.ToString()),
            ("a public file of another tenant", elsewhere.ToString()),
            ("an id written without hyphens", theirs.ToString("N")),
            ("a download path", $"/api/files/{theirs}"),
            ("text that is not an id", "not-an-id"),
        };
        refused.Should().HaveCount(7);

        foreach (var (reason, value) in refused)
        {
            var response = await CreateAsync(stranger.Client, type, value);
            var body = await response.Content.ReadAsStringAsync(Ct);

            response.StatusCode.Should().Be(HttpStatusCode.BadRequest, "{0}: {1}", reason, body);
            body.Should().Contain(Refusal, "{0} is answered as every other refusal is", reason);
            body.Should().NotContain("payslip", "the file's name is not the caller's to read");
            body.Should().NotContain(theirs.ToString(), "the id is not repeated back");
        }

        // The controls: the file is there for its owner, and for an administrator of the tenant.
        await CreatedIdAsync(await CreateAsync(owner.Client, type, theirs.ToString()));
        await CreatedIdAsync(await CreateAsync(await AdminAsync(), type, theirs.ToString()));
    }

    [Fact]
    public async Task A_colleague_saves_an_entry_keeping_its_file_and_cannot_swap_in_one_that_is_not_theirs()
    {
        var type = await StoreTypeAsync();
        var (owner, colleague) = await EditorsAsync(type);

        var attached = await FileAsync(isPublic: false, owner: owner.Id);
        var another = await FileAsync(isPublic: false, owner: owner.Id);
        var entry = await CreatedIdAsync(await CreateAsync(owner.Client, type, attached.ToString()));

        var kept = await colleague.Client.PutAsJsonAsync($"/api/contents/{entry}", new
        {
            data = new Dictionary<string, object> { ["Title"] = "retitled", ["Cover"] = attached.ToString() },
        }, Ct);
        kept.IsSuccessStatusCode.Should().BeTrue(
            "the file was checked for whoever attached it: got {0}: {1}", kept.StatusCode, await kept.Content.ReadAsStringAsync(Ct));

        var swapped = await colleague.Client.PutAsJsonAsync($"/api/contents/{entry}", new
        {
            data = new Dictionary<string, object> { ["Title"] = "retitled", ["Cover"] = another.ToString() },
        }, Ct);
        swapped.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await swapped.Content.ReadAsStringAsync(Ct)).Should().Contain(Refusal);

        using (var scope = _factory.Services.CreateScope())
        {
            var stored = await scope.ServiceProvider.GetRequiredService<IQuerySession>().LoadAsync<Content>(entry, Ct);
            stored!.Data["Title"].ToString().Should().Be("retitled");
            stored.Data["Cover"].ToString().Should().Be(attached.ToString());
        }

        // The control: the same swap by the owner, whose file it is.
        var swappedByOwner = await owner.Client.PutAsJsonAsync($"/api/contents/{entry}", new
        {
            data = new Dictionary<string, object> { ["Title"] = "retitled", ["Cover"] = another.ToString() },
        }, Ct);
        swappedByOwner.IsSuccessStatusCode.Should().BeTrue(
            "the file is checked for the user making the edit: got {0}: {1}",
            swappedByOwner.StatusCode, await swappedByOwner.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task A_file_field_sent_twice_in_different_case_is_refused()
    {
        var type = await StoreTypeAsync();
        var (owner, stranger) = await EditorsAsync(type);

        var open = await FileAsync(isPublic: true, owner: stranger.Id);
        var theirs = await FileAsync(isPublic: false, owner: owner.Id);

        var response = await stranger.Client.PostAsJsonAsync("/api/contents", new
        {
            contentType = type,
            data = new Dictionary<string, object> { ["Title"] = "a", ["Cover"] = open.ToString(), ["cover"] = theirs.ToString() },
        }, Ct);
        var body = await response.Content.ReadAsStringAsync(Ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, body);
        body.Should().Contain("more than once").And.NotContain(theirs.ToString());

        // A JSON null under the first spelling is refused the same way.
        var nullFirst = await stranger.Client.PostAsync("/api/contents", new StringContent(
            $$$"""{"contentType":"{{{type}}}","data":{"Title":"a","Cover":null,"cover":"{{{theirs}}}"}}""",
            System.Text.Encoding.UTF8,
            "application/json"), Ct);
        var nullFirstBody = await nullFirst.Content.ReadAsStringAsync(Ct);
        nullFirst.StatusCode.Should().Be(HttpStatusCode.BadRequest, nullFirstBody);
        nullFirstBody.Should().Contain("more than once").And.NotContain(theirs.ToString());

        // The control: the first spelling alone.
        await CreatedIdAsync(await CreateAsync(stranger.Client, type, open.ToString()));
    }

    [Fact]
    public async Task An_authoring_read_keeps_the_id_in_data_and_answers_the_file_only_to_a_caller_who_may_download_it()
    {
        var type = await StoreTypeAsync();
        var (owner, colleague) = await EditorsAsync(type);

        var locked = await FileAsync(isPublic: false, owner: owner.Id, name: "contract.png");
        var entry = await CreatedIdAsync(await CreateAsync(owner.Client, type, locked.ToString()));

        foreach (var read in new[] { await ReadAsync(owner.Client, entry), await ListedAsync(owner.Client, type, entry) })
        {
            read.GetProperty("data").GetProperty("Cover").GetString().Should().Be(locked.ToString());

            var file = read.GetProperty("files").GetProperty("Cover");
            file.GetProperty("id").GetGuid().Should().Be(locked);
            file.GetProperty("fileName").GetString().Should().Be("contract.png");
            file.GetProperty("contentType").GetString().Should().Be("image/png");
            file.GetProperty("size").GetInt64().Should().BeGreaterThan(0);
            file.GetProperty("url").ValueKind.Should().Be(JsonValueKind.Null, "a private file has no address anyone can fetch");
        }

        foreach (var read in new[] { await ReadAsync(colleague.Client, entry), await ListedAsync(colleague.Client, type, entry) })
        {
            read.GetProperty("data").GetProperty("Cover").GetString().Should().Be(
                locked.ToString(), "the id is the entry's data, and the colleague may read the entry");
            read.TryGetProperty("files", out _).Should().BeFalse("the file is not the colleague's to download");
            read.GetRawText().Should().NotContain("contract.png");
        }
    }

    [Fact]
    public async Task An_authoring_read_answers_a_public_file_with_its_address_to_any_reader_of_the_entry()
    {
        var type = await StoreTypeAsync();
        var (owner, colleague) = await EditorsAsync(type);

        var open = await FileAsync(isPublic: true, owner: owner.Id);
        var entry = await CreatedIdAsync(await CreateAsync(owner.Client, type, open.ToString()));

        var read = await ReadAsync(colleague.Client, entry);

        var file = read.GetProperty("files").GetProperty("Cover");
        file.GetProperty("id").GetGuid().Should().Be(open);
        file.GetProperty("url").GetString().Should().Be($"/api/public/files/{open}");

        var without = await CreatedIdAsync(await CreateAsync(owner.Client, type, cover: null));
        (await ReadAsync(owner.Client, without)).TryGetProperty("files", out _).Should().BeFalse(
            "an entry naming no file answers no files member");
    }
}
