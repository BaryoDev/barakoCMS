using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Claims;
using barakoCMS.Core.Interfaces;
using barakoCMS.Infrastructure.Multitenancy;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using BarakoCMS.Files;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BarakoCMS.Tests;

/// <summary>
/// What a file store answers for the members it does not implement, with no host and no database.
/// </summary>
public class FileStoreDefaultTests
{
    /// <summary>A store written against the two members the seam first shipped with.</summary>
    private sealed class PublicReadsOnly : IFileStore
    {
        public Task<StoredFileInfo?> FindPublicAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult<StoredFileInfo?>(null);

        public Task<Stream?> OpenPublicAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult<Stream?>(null);
    }

    private static Func<Task>[] LaterMembers(IFileStore store)
    {
        var caller = new ClaimsPrincipal(new ClaimsIdentity());
        var ct = TestContext.Current.CancellationToken;

        return
        [
            () => store.PublicUrlAsync(Guid.NewGuid(), ct),
            () => store.FindAsync(Guid.NewGuid(), caller, ct),
            () => store.OpenAsync(Guid.NewGuid(), caller, ct),
            () => store.SaveAsync(
                new FileToStore { Content = Stream.Null, FileName = "receipt.pdf", ContentType = "application/pdf" }, ct),
            () => store.DeleteAsync(Guid.NewGuid(), caller, force: true, ct),
        ];
    }

    [Fact]
    public async Task Without_the_files_module_every_later_member_names_the_module_to_enable()
    {
        var calls = LaterMembers(new NoFileStore());
        calls.Should().HaveCount(5);

        foreach (var call in calls)
        {
            (await call.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("BarakoCMS.Files");
        }
    }

    [Fact]
    public async Task A_store_written_against_the_first_two_members_refuses_the_later_ones_by_name()
    {
        var calls = LaterMembers(new PublicReadsOnly());
        calls.Should().HaveCount(5);

        foreach (var call in calls)
        {
            (await call.Should().ThrowAsync<NotSupportedException>()).Which.Message.Should().Contain(nameof(PublicReadsOnly));
        }
    }
}

/// <summary>
/// The Files module behind the core's file seam: storing, reading for a caller, deleting, and the
/// public address.
/// </summary>
/// <remarks>
/// Every refusal of a private file is paired with a caller who does get it, so a store that handed
/// out nothing would fail here as surely as one that handed out everything. Callers are principals
/// built the way the token issuer and the API key handler build them.
/// </remarks>
[Collection("Sequential")]
public class FileStoreSeamTests
{
    private readonly IntegrationTestFixture _factory;
    private readonly HttpClient _client;

    public FileStoreSeamTests(IntegrationTestFixture factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // Distinct per file, so a read that returned some other file's bytes does not pass.
    private static byte[] PdfBytes() => FileSamples.Pdf().Concat(Guid.NewGuid().ToByteArray()).ToArray();

    private static string NewName(string extension = "pdf") => $"seam-{Guid.NewGuid():N}.{extension}";

    private static ClaimsPrincipal Caller(Guid userId, string role, params (string Type, string Value)[] extra)
    {
        var claims = new List<Claim>
        {
            new("UserId", userId.ToString()),
            new("Username", $"seam-{userId:N}"),
            new(ClaimTypes.Role, role),
        };
        claims.AddRange(extra.Select(e => new Claim(e.Type, e.Value)));

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Test", "Username", ClaimTypes.Role));
    }

    private static ClaimsPrincipal NotSignedIn() => new(new ClaimsIdentity());

    private async Task<Guid> StoreUserAsync(params Guid[] roleIds)
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();

        var userId = Guid.NewGuid();
        session.Store(new User
        {
            Id = userId,
            Username = $"seam-{userId:N}",
            Email = $"seam-{userId:N}@example.com",
            RoleIds = roleIds.ToList(),
        });
        await session.SaveChangesAsync(Ct);
        return userId;
    }

    /// <summary>A stored user holding the SuperAdmin role, which satisfies every capability.</summary>
    private async Task<Guid> SuperAdminAsync()
    {
        using (var scope = _factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            var role = await session.Query<Role>().FirstOrDefaultAsync(r => r.Name == "SuperAdmin", Ct)
                       ?? new Role { Id = barakoCMS.Data.DataSeeder.SuperAdminRoleId, Name = "SuperAdmin", Permissions = new() };
            session.Store(role);
            await session.SaveChangesAsync(Ct);
        }

        return await StoreUserAsync(barakoCMS.Data.DataSeeder.SuperAdminRoleId);
    }

    /// <summary>
    /// A stored user holding the seeded Admin role, which the Files module's seed gave
    /// <c>upload_files</c> and <c>manage_all_files</c>.
    /// </summary>
    private Task<Guid> AdminAsync() => StoreUserAsync(SystemRoles.AdminRoleId);

    /// <summary>A stored user whose one role holds <c>upload_files</c> and nothing else, and that role's name.</summary>
    private Task<(Guid UserId, string Role)> MediaEditorAsync() => HolderAsync(FileCapabilities.UploadFiles);

    /// <summary>A stored user whose one role is new and holds exactly these capabilities, and that role's name.</summary>
    private async Task<(Guid UserId, string Role)> HolderAsync(params string[] capabilities)
    {
        var role = new Role
        {
            Id = Guid.NewGuid(),
            Name = $"Seam Editor {Guid.NewGuid():N}",
            SystemCapabilities = capabilities.ToList(),
        };

        using (var scope = _factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Store(role);
            await session.SaveChangesAsync(Ct);
        }

        return (await StoreUserAsync(role.Id), role.Name);
    }

    private static async Task<FileSaveResult> SaveAsync(
        IServiceProvider services,
        byte[] bytes,
        bool isPublic = false,
        Guid owner = default,
        string? name = null,
        string contentType = "application/pdf",
        Guid? suppliedBy = null)
    {
        using var scope = services.CreateScope();
        using var content = new MemoryStream(bytes);

        return await scope.ServiceProvider.GetRequiredService<IFileStore>().SaveAsync(
            new FileToStore
            {
                Content = content,
                FileName = name ?? NewName(),
                ContentType = contentType,
                IsPublic = isPublic,
                Owner = owner,
                SuppliedBy = suppliedBy,
            },
            Ct);
    }

    /// <summary>A stream that cannot say how long it is, as a request body cannot.</summary>
    private sealed class ForwardOnly(byte[] bytes) : Stream
    {
        private readonly MemoryStream _inner = new(bytes);

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>A record that is staged and never saved by the test, to see whether a store call commits it.</summary>
    private static StoredFile Bystander() => new()
    {
        FileName = NewName(),
        ContentType = "application/pdf",
        StorageKey = $"{FileKeys.Prefix(false)}{Guid.NewGuid():N}.pdf",
    };

    private async Task<Guid> StoredAsync(byte[] bytes, bool isPublic = false, Guid owner = default)
    {
        var saved = await SaveAsync(_factory.Services, bytes, isPublic, owner);
        saved.Refused.Should().BeNull();
        saved.File.Should().NotBeNull();
        return saved.File!.Id;
    }

    private async Task<T> WithStoreAsync<T>(Func<IFileStore, Task<T>> ask)
    {
        using var scope = _factory.Services.CreateScope();
        return await ask(scope.ServiceProvider.GetRequiredService<IFileStore>());
    }

    private Task<StoredFileInfo?> FindAsync(Guid id, ClaimsPrincipal caller) =>
        WithStoreAsync(store => store.FindAsync(id, caller, Ct));

    private async Task<byte[]?> ReadAsync(Guid id, ClaimsPrincipal caller)
    {
        using var scope = _factory.Services.CreateScope();
        var stream = await scope.ServiceProvider.GetRequiredService<IFileStore>().OpenAsync(id, caller, Ct);
        if (stream is null)
        {
            return null;
        }

        await using (stream)
        {
            using var held = new MemoryStream();
            await stream.CopyToAsync(held, Ct);
            return held.ToArray();
        }
    }

    private Task<FileDeleteResult> DeleteAsync(Guid id, ClaimsPrincipal caller, bool force = false) =>
        WithStoreAsync(store => store.DeleteAsync(id, caller, force, Ct));

    private async Task<StoredFile?> RecordAsync(Guid id)
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IQuerySession>().LoadAsync<StoredFile>(id, Ct);
    }

    private static async Task<int> StoredWithNameAsync(IServiceProvider services, string name)
    {
        using var scope = services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IQuerySession>()
            .Query<StoredFile>().Where(f => f.FileName == name).CountAsync(Ct);
    }

    private async Task<HttpResponseMessage> GetAsync(string path, string? token = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return await _client.SendAsync(request, Ct);
    }

    [Fact]
    public async Task A_class_that_names_only_the_seam_stores_a_file_and_reads_it_back()
    {
        var named = typeof(ReceiptKeeper)
            .GetConstructors().SelectMany(c => c.GetParameters()).Select(p => p.ParameterType)
            .Concat(typeof(ReceiptKeeper).GetFields(BindingFlags.Instance | BindingFlags.NonPublic).Select(f => f.FieldType))
            .ToList();
        named.Should().NotBeEmpty("the keeper is given a store, so an empty scan is a broken scan");
        named.Should().OnlyContain(t => t == typeof(IFileStore), "the keeper is handed the core's seam and nothing of the Files module");

        var payer = Guid.NewGuid();
        var pdf = PdfBytes();

        Guid receipt;
        using (var scope = _factory.Services.CreateScope())
        {
            var keeper = ActivatorUtilities.CreateInstance<ReceiptKeeper>(scope.ServiceProvider);
            receipt = await keeper.KeepAsync(pdf, NewName(), payer, Ct);
        }

        // A scope of its own, as the request that reads a receipt is not the one that stored it.
        using (var scope = _factory.Services.CreateScope())
        {
            var keeper = ActivatorUtilities.CreateInstance<ReceiptKeeper>(scope.ServiceProvider);

            (await keeper.ReadAsync(receipt, Caller(payer, "User"), Ct)).Should().Equal(pdf);
            (await keeper.ReadAsync(receipt, Caller(Guid.NewGuid(), "User"), Ct)).Should().BeNull();
        }
    }

    [Fact]
    public async Task A_file_stored_through_the_seam_is_the_record_an_upload_writes()
    {
        using var roles = _factory.Services.CreateScope();
        var userRole = await roles.ServiceProvider.GetRequiredService<IQuerySession>()
            .Query<Role>().FirstOrDefaultAsync(r => r.Name == "User", Ct);
        userRole.Should().NotBeNull("the fixture seeds the User role");

        var owner = await StoreUserAsync(userRole!.Id);
        var stranger = await StoreUserAsync(userRole.Id);
        var bytes = PdfBytes();

        var saved = await SaveAsync(_factory.Services, bytes, owner: owner, name: "folder/invoice.pdf");

        saved.Refused.Should().BeNull();
        saved.CanRetry.Should().BeFalse();
        saved.File.Should().NotBeNull();
        saved.File!.ContentType.Should().Be("application/pdf");
        saved.File.Size.Should().Be(bytes.Length);
        saved.File.FileName.Should().Be("invoice.pdf", "only the last segment of a name is kept, as on upload");

        var record = await RecordAsync(saved.File.Id);
        record.Should().NotBeNull();
        record!.UploadedBy.Should().Be(owner);
        record.IsPublic.Should().BeFalse("a file is private unless the caller says otherwise");
        record.Size.Should().Be(bytes.Length);
        record.ContentType.Should().Be("application/pdf");
        record.ParentFileId.Should().BeNull();
        record.StorageKey.Should().StartWith(FileKeys.PrivatePrefix).And.EndWith(".pdf");
        record.FileName.Should().Be("invoice.pdf");

        // The module's own routes treat it as an upload by its owner.
        var asOwner = await GetAsync($"/api/files/{saved.File.Id}", _factory.CreateToken(new[] { "User" }, owner.ToString()));
        asOwner.StatusCode.Should().Be(HttpStatusCode.OK);
        (await asOwner.Content.ReadAsByteArrayAsync(Ct)).Should().Equal(bytes);

        var asStranger = await GetAsync($"/api/files/{saved.File.Id}", _factory.CreateToken(new[] { "User" }, stranger.ToString()));
        asStranger.StatusCode.Should().Be(HttpStatusCode.NotFound);

        (await GetAsync($"/api/public/files/{saved.File.Id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_private_file_is_read_by_its_owner_and_an_administrator_and_by_nobody_else()
    {
        var owner = Guid.NewGuid();
        var bytes = PdfBytes();
        var id = await StoredAsync(bytes, owner: owner);

        (await FindAsync(id, Caller(owner, "User")))!.Id.Should().Be(id);
        (await ReadAsync(id, Caller(owner, "User"))).Should().Equal(bytes);
        (await ReadAsync(id, Caller(await AdminAsync(), "Admin"))).Should().Equal(bytes);
        (await ReadAsync(id, Caller(await SuperAdminAsync(), "SuperAdmin"))).Should().Equal(bytes);

        // An administrator's role name on an account the store has never seen opens nothing.
        (await ReadAsync(id, Caller(Guid.NewGuid(), "Admin"))).Should().BeNull();
        (await ReadAsync(id, Caller(Guid.NewGuid(), "SuperAdmin"))).Should().BeNull();

        var stranger = Caller(Guid.NewGuid(), "User");
        (await FindAsync(id, stranger)).Should().BeNull();
        (await ReadAsync(id, stranger)).Should().BeNull();
        (await FindAsync(id, NotSignedIn())).Should().BeNull();
        (await ReadAsync(id, NotSignedIn())).Should().BeNull();

        // The members that take no caller hand out public files only.
        (await WithStoreAsync(store => store.FindPublicAsync(id, Ct))).Should().BeNull();
        (await WithStoreAsync(store => store.OpenPublicAsync(id, Ct))).Should().BeNull();
        (await WithStoreAsync(store => store.PublicUrlAsync(id, Ct))).Should().BeNull();

        (await FindAsync(Guid.NewGuid(), Caller(Guid.NewGuid(), "SuperAdmin"))).Should().BeNull("no such file");
    }

    [Fact]
    public async Task A_file_stored_for_no_user_is_read_by_an_administrator_only()
    {
        var bytes = PdfBytes();
        var id = await StoredAsync(bytes);

        (await RecordAsync(id))!.UploadedBy.Should().Be(Guid.Empty);
        (await ReadAsync(id, Caller(await AdminAsync(), "Admin"))).Should().Equal(bytes);

        // A principal whose UserId is the empty id is not the owner of a file nobody owns.
        (await ReadAsync(id, Caller(Guid.Empty, "User"))).Should().BeNull();
        (await ReadAsync(id, Caller(Guid.NewGuid(), "User"))).Should().BeNull();
    }

    /// <summary>
    /// Each of these principals carries the owner's id and an administrator's role name, and is
    /// still not a caller the file routes would have let in.
    /// </summary>
    [Fact]
    public async Task A_caller_the_file_routes_would_not_let_in_reads_no_private_file()
    {
        var owner = Guid.NewGuid();
        var bytes = PdfBytes();
        var id = await StoredAsync(bytes, owner: owner);

        var apiKey = Caller(owner, "SuperAdmin", ("auth_method", "apikey"));
        var otherTenant = Caller(owner, "Admin", ("tenant", "seam-some-other-tenant"));

        var unauthenticated = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim("UserId", owner.ToString()),
            new Claim(ClaimTypes.Role, "SuperAdmin"),
        }));

        foreach (var caller in new[] { apiKey, otherTenant, unauthenticated })
        {
            (await FindAsync(id, caller)).Should().BeNull();
            (await ReadAsync(id, caller)).Should().BeNull();
        }

        // The controls: the same claims without the one that refuses, and a token for this tenant.
        (await ReadAsync(id, Caller(owner, "SuperAdmin"))).Should().Equal(bytes);
        (await ReadAsync(id, Caller(owner, "Admin", ("tenant", Tenant.DefaultSlug)))).Should().Equal(bytes);
        (await ReadAsync(id, Caller(owner, "Admin", ("tenant", Tenant.DefaultSlug.ToUpperInvariant())))).Should().Equal(bytes);
        (await ReadAsync(id, Caller(owner, "User", ("auth_method", "jwt")))).Should().Equal(bytes);

        // None of them is refused a public file, which needs no caller at all.
        var open = await StoredAsync(PdfBytes(), isPublic: true);
        foreach (var caller in new[] { apiKey, otherTenant, unauthenticated })
        {
            (await FindAsync(open, caller)).Should().NotBeNull();
        }
    }

    [Fact]
    public async Task A_public_file_is_read_by_any_caller_and_has_an_address_anyone_can_fetch()
    {
        var bytes = PdfBytes();
        var id = await StoredAsync(bytes, isPublic: true, owner: Guid.NewGuid());

        (await RecordAsync(id))!.StorageKey.Should().StartWith(FileKeys.PublicPrefix);

        (await ReadAsync(id, NotSignedIn())).Should().Equal(bytes);
        (await ReadAsync(id, Caller(Guid.NewGuid(), "User"))).Should().Equal(bytes);
        (await WithStoreAsync(store => store.FindPublicAsync(id, Ct)))!.Id.Should().Be(id);

        var address = await WithStoreAsync(store => store.PublicUrlAsync(id, Ct));
        address.Should().Be($"/api/public/files/{id}", "the Postgres store serves nothing itself, so the address is the API's route");

        var fetched = await GetAsync(address!);
        fetched.StatusCode.Should().Be(HttpStatusCode.OK);
        (await fetched.Content.ReadAsByteArrayAsync(Ct)).Should().Equal(bytes);

        (await WithStoreAsync(store => store.PublicUrlAsync(Guid.NewGuid(), Ct))).Should().BeNull();
    }

    [Fact]
    public async Task A_file_is_stored_into_the_scopes_tenant_and_reached_from_no_other()
    {
        var tenant = $"seam-{Guid.NewGuid():N}"[..20];
        var owner = Guid.NewGuid();
        var superAdmin = await SuperAdminAsync();
        var bytes = PdfBytes();

        Guid id;
        using (var inTenant = _factory.Services.CreateScopeForTenant(tenant))
        {
            using var content = new MemoryStream(bytes);
            var saved = await inTenant.ServiceProvider.GetRequiredService<IFileStore>().SaveAsync(
                new FileToStore { Content = content, FileName = NewName(), ContentType = "application/pdf", IsPublic = true, Owner = owner },
                Ct);
            saved.File.Should().NotBeNull(saved.Refused ?? string.Empty);
            id = saved.File!.Id;
        }

        // The control: it is there, in the tenant it was stored into.
        using (var inTenant = _factory.Services.CreateScopeForTenant(tenant))
        {
            var store = inTenant.ServiceProvider.GetRequiredService<IFileStore>();
            (await store.FindAsync(id, Caller(owner, "User"), Ct)).Should().NotBeNull();
            (await store.PublicUrlAsync(id, Ct)).Should().NotBeNull();
        }

        // From the default tenant it is not, for its owner, an administrator or nobody, public as it is.
        (await RecordAsync(id)).Should().BeNull();
        (await FindAsync(id, Caller(owner, "User"))).Should().BeNull();
        (await ReadAsync(id, Caller(superAdmin, "SuperAdmin"))).Should().BeNull();
        (await WithStoreAsync(store => store.FindPublicAsync(id, Ct))).Should().BeNull();
        (await WithStoreAsync(store => store.PublicUrlAsync(id, Ct))).Should().BeNull();
        (await DeleteAsync(id, Caller(superAdmin, "SuperAdmin"), force: true)).Should().Be(FileDeleteResult.NotFound);

        using (var inTenant = _factory.Services.CreateScopeForTenant(tenant))
        {
            (await inTenant.ServiceProvider.GetRequiredService<IFileStore>().FindAsync(id, Caller(owner, "User"), Ct))
                .Should().NotBeNull("a delete from another tenant removed nothing");
        }
    }

    [Fact]
    public async Task A_cached_resize_is_not_a_file_a_caller_reads_or_deletes()
    {
        var superAdmin = await SuperAdminAsync();
        var parent = await StoredAsync(PdfBytes(), owner: superAdmin);

        var variant = new StoredFile
        {
            FileName = "receipt.pdf",
            ContentType = "application/pdf",
            StorageKey = $"{FileKeys.Prefix(false)}{Guid.NewGuid():N}.pdf",
            UploadedBy = superAdmin,
            ParentFileId = parent,
            VariantWidth = 400,
        };

        using (var scope = _factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Store(variant);
            await session.SaveChangesAsync(Ct);
        }

        var caller = Caller(superAdmin, "SuperAdmin");

        (await FindAsync(parent, caller)).Should().NotBeNull("the control: its original is found");
        (await FindAsync(variant.Id, caller)).Should().BeNull();
        (await ReadAsync(variant.Id, caller)).Should().BeNull();
        (await DeleteAsync(variant.Id, caller, force: true)).Should().Be(FileDeleteResult.NotFound);
        (await RecordAsync(variant.Id)).Should().NotBeNull();

        // Deleting the original takes the resize with it, as the delete route does.
        (await DeleteAsync(parent, caller)).Should().Be(FileDeleteResult.Deleted);
        (await RecordAsync(variant.Id)).Should().BeNull();
    }

    [Fact]
    public async Task A_file_that_fails_an_upload_check_is_refused_and_nothing_is_stored()
    {
        var pdf = FileSamples.Pdf();
        var tooLarge = new byte[10 * 1024 * 1024 + 1];
        pdf.CopyTo(tooLarge, 0);

        var refused = new (string Name, byte[] Bytes, string ContentType, string Reason)[]
        {
            (NewName("txt"), "plain text"u8.ToArray(), "text/plain", "Only these types are allowed"),
            (NewName("svg"), "<svg xmlns=\"http://www.w3.org/2000/svg\"/>"u8.ToArray(), "image/svg+xml", "Only these types are allowed"),
            (NewName(), FileSamples.Png(1, 2, 3), "application/pdf", "content is not application/pdf"),
            (NewName(), Array.Empty<byte>(), "application/pdf", "A file is required"),
            (NewName(), tooLarge, "application/pdf", "too large"),
        };

        foreach (var (name, bytes, contentType, reason) in refused)
        {
            var result = await SaveAsync(_factory.Services, bytes, name: name, contentType: contentType);

            result.File.Should().BeNull(reason);
            result.Refused.Should().Contain(reason);
            result.CanRetry.Should().BeFalse("the same bytes are refused again");
            (await StoredWithNameAsync(_factory.Services, name)).Should().Be(0, reason);
        }

        // The control: the same call with a file that passes every check.
        var kept = NewName();
        var stored = await SaveAsync(_factory.Services, PdfBytes(), name: kept);
        stored.File.Should().NotBeNull(stored.Refused ?? string.Empty);
        (await StoredWithNameAsync(_factory.Services, kept)).Should().Be(1);
    }

    [Fact]
    public async Task The_size_limit_is_ten_megabytes_whether_or_not_the_stream_says_its_length()
    {
        const int limit = 10 * 1024 * 1024;
        var atLimit = new byte[limit];
        var over = new byte[limit + 1];
        FileSamples.Pdf().CopyTo(atLimit, 0);
        FileSamples.Pdf().CopyTo(over, 0);
        atLimit[^1] = 7;

        async Task<FileSaveResult> SaveFromAsync(Stream content, string name)
        {
            using var scope = _factory.Services.CreateScope();
            await using (content)
            {
                return await scope.ServiceProvider.GetRequiredService<IFileStore>().SaveAsync(
                    new FileToStore { Content = content, FileName = name, ContentType = "application/pdf" }, Ct);
            }
        }

        var overSeekable = NewName();
        var overForwardOnly = NewName();
        (await SaveFromAsync(new MemoryStream(over), overSeekable)).Refused.Should().Contain("too large");
        (await SaveFromAsync(new ForwardOnly(over), overForwardOnly)).Refused.Should().Contain("too large");
        (await StoredWithNameAsync(_factory.Services, overSeekable)).Should().Be(0);
        (await StoredWithNameAsync(_factory.Services, overForwardOnly)).Should().Be(0);

        // Exactly at the limit is stored whole, read from a stream that does not say its length.
        var kept = await SaveFromAsync(new ForwardOnly(atLimit), NewName());
        kept.File.Should().NotBeNull(kept.Refused ?? string.Empty);
        kept.File!.Size.Should().Be(limit);

        var read = await ReadAsync(kept.File.Id, Caller(await AdminAsync(), "Admin"));
        read.Should().NotBeNull();
        read!.Length.Should().Be(limit);
        read[^1].Should().Be(7, "the last byte is the file's and not a spare byte of the buffer it was read into");
    }

    /// <summary>
    /// The store commits through the scope's session. A caller that staged a row of its own first
    /// would have it committed by a save that then refuses the file, or by a delete it may not make.
    /// </summary>
    [Fact]
    public async Task A_save_or_a_delete_with_work_already_staged_throws_and_commits_nothing()
    {
        var superAdmin = await SuperAdminAsync();
        var existing = await StoredAsync(PdfBytes(), owner: superAdmin);

        var beforeSave = Bystander();
        var name = NewName();
        using (var scope = _factory.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<IDocumentSession>().Store(beforeSave);
            var store = scope.ServiceProvider.GetRequiredService<IFileStore>();

            using var content = new MemoryStream(PdfBytes());
            var save = () => store.SaveAsync(
                new FileToStore { Content = content, FileName = name, ContentType = "application/pdf" }, Ct);

            (await save.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("SaveAsync");
        }

        (await RecordAsync(beforeSave.Id)).Should().BeNull("the caller never saved it, and the store must not");
        (await StoredWithNameAsync(_factory.Services, name)).Should().Be(0);

        var beforeDelete = Bystander();
        using (var scope = _factory.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<IDocumentSession>().Store(beforeDelete);
            var store = scope.ServiceProvider.GetRequiredService<IFileStore>();

            var delete = () => store.DeleteAsync(existing, Caller(superAdmin, "SuperAdmin"), force: true, Ct);

            (await delete.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("DeleteAsync");
        }

        (await RecordAsync(beforeDelete.Id)).Should().BeNull();
        (await RecordAsync(existing)).Should().NotBeNull("the delete did not start");

        // The control: the same save and delete from a scope with nothing staged.
        var stored = await SaveAsync(_factory.Services, PdfBytes(), name: name);
        stored.File.Should().NotBeNull(stored.Refused ?? string.Empty);
        (await DeleteAsync(existing, Caller(superAdmin, "SuperAdmin"))).Should().Be(FileDeleteResult.Deleted);
    }

    private sealed class SwitchableScanner : IFileScanner
    {
        public ScanResult Answer { get; set; } = ScanResult.Clean;

        public bool Configured => true;

        public int Scans { get; private set; }

        public long BytesRead { get; private set; }

        public async Task<ScanResult> ScanAsync(Stream content, CancellationToken ct = default)
        {
            Scans++;

            var buffer = new byte[8192];
            int read;
            while ((read = await content.ReadAsync(buffer, ct)) > 0)
            {
                BytesRead += read;
            }

            return Answer;
        }
    }

    [Fact]
    public async Task A_file_the_scanner_does_not_pass_is_refused_recorded_and_not_stored()
    {
        var scanner = new SwitchableScanner();

        // A host of its own because the shared one has no scanner and no way to swap one in
        // (UploadScanningTests derives a host for the same reason). One host, three answers.
        // Never disposed: FastEndpoints keeps a pointer to the last host that mapped endpoints.
        var host = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IFileScanner>();
                services.AddSingleton<IFileScanner>(scanner);
            }));

        var owner = Guid.NewGuid();
        var sender = Guid.NewGuid();

        scanner.Answer = ScanResult.Infected("Eicar-Test-Signature");

        // A row the caller staged before the save. A refusal that commits its audit entry through
        // the same session would commit this with it.
        var bystander = Bystander();
        using (var scope = host.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<IDocumentSession>().Store(bystander);

            using var content = new MemoryStream(PdfBytes());
            var save = () => scope.ServiceProvider.GetRequiredService<IFileStore>().SaveAsync(
                new FileToStore { Content = content, FileName = NewName(), ContentType = "application/pdf" }, Ct);

            await save.Should().ThrowAsync<InvalidOperationException>();
        }

        scanner.Scans.Should().Be(0, "the save stopped before anything was read");
        (await RecordAsync(bystander.Id)).Should().BeNull("a save that stores no file commits nothing of the caller's");

        var infectedName = NewName();
        var bytes = PdfBytes();
        var infected = await SaveAsync(host.Services, bytes, owner: owner, name: infectedName, suppliedBy: sender);

        scanner.Scans.Should().Be(1, "a save that never reached the scanner proves nothing below");
        scanner.BytesRead.Should().Be(bytes.Length, "the scanner is handed the whole file");
        infected.File.Should().BeNull();
        infected.Refused.Should().Contain("Eicar-Test-Signature");
        infected.CanRetry.Should().BeFalse();
        (await StoredWithNameAsync(host.Services, infectedName)).Should().Be(0);

        scanner.Answer = ScanResult.Unavailable("clamd is not answering.");
        var unscannedName = NewName();
        var unscanned = await SaveAsync(host.Services, PdfBytes(), owner: owner, name: unscannedName);

        scanner.Scans.Should().Be(2);
        unscanned.File.Should().BeNull();
        unscanned.Refused.Should().Contain("could not be scanned").And.NotContain("clamd");
        unscanned.CanRetry.Should().BeTrue("the scanner being down says nothing about the file");
        (await StoredWithNameAsync(host.Services, unscannedName)).Should().Be(0);

        using (var scope = _factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();

            var recorded = await session.Query<AuditEvent>().Where(e => e.TargetId == infectedName).ToListAsync(Ct);
            recorded.Should().HaveCount(1);
            recorded[0].Action.Should().Be("file.refused.infected");
            recorded[0].ActorUserId.Should().Be(sender, "the actor is who sent the file, not who it would have belonged to");
            recorded[0].Metadata.Should().ContainKey("reason");
            recorded[0].Metadata!["reason"].ToString().Should().Contain("Eicar-Test-Signature");
            recorded[0].Metadata.Should().ContainKey("owner");
            recorded[0].Metadata["owner"].ToString().Should().Be(owner.ToString());

            var missed = await session.Query<AuditEvent>().Where(e => e.TargetId == unscannedName).ToListAsync(Ct);
            missed.Should().HaveCount(1);
            missed[0].Action.Should().Be("file.refused.unscanned");
            missed[0].ActorUserId.Should().BeNull("nobody was named as having sent it");
        }

        // The control: the same host stores a file its scanner passes.
        scanner.Answer = ScanResult.Clean;
        var cleanName = NewName();
        var clean = await SaveAsync(host.Services, PdfBytes(), owner: owner, name: cleanName);

        scanner.Scans.Should().Be(3);
        clean.File.Should().NotBeNull(clean.Refused ?? string.Empty);
        (await StoredWithNameAsync(host.Services, cleanName)).Should().Be(1);
    }

    /// <summary>
    /// The delete route asks for <c>upload_files</c> and then for the uploader or an administrator.
    /// Each refusal here holds one of the two and not the other.
    /// </summary>
    [Fact]
    public async Task A_delete_asks_for_what_the_delete_route_asks_for()
    {
        var owner = await StoreUserAsync();
        var (editor, editorRole) = await MediaEditorAsync();
        var superAdmin = await SuperAdminAsync();

        var owned = await StoredAsync(PdfBytes(), owner: owner);
        var editors = await StoredAsync(PdfBytes(), owner: editor);
        var key = (await RecordAsync(editors))!.StorageKey;

        // The owner, with no capability.
        (await DeleteAsync(owned, Caller(owner, "User"), force: true)).Should().Be(FileDeleteResult.NotFound);

        // The capability, on a file that is someone else's.
        (await DeleteAsync(owned, Caller(editor, editorRole), force: true)).Should().Be(FileDeleteResult.NotFound);

        // An administrator's role name on an account that holds no capability, and one through an API key.
        (await DeleteAsync(owned, Caller(owner, "Admin"), force: true)).Should().Be(FileDeleteResult.NotFound);
        (await DeleteAsync(owned, Caller(superAdmin, "SuperAdmin", ("auth_method", "apikey")), force: true))
            .Should().Be(FileDeleteResult.NotFound);
        (await DeleteAsync(owned, NotSignedIn(), force: true)).Should().Be(FileDeleteResult.NotFound);

        // A SuperAdmin whose token was issued for another tenant.
        (await DeleteAsync(owned, Caller(superAdmin, "SuperAdmin", ("tenant", "seam-some-other-tenant")), force: true))
            .Should().Be(FileDeleteResult.NotFound);

        (await RecordAsync(owned)).Should().NotBeNull("none of those callers may delete it");

        // Both: the capability and the file is the caller's own.
        (await DeleteAsync(editors, Caller(editor, editorRole))).Should().Be(FileDeleteResult.Deleted);
        (await RecordAsync(editors)).Should().BeNull();
        (await FindAsync(editors, Caller(editor, editorRole))).Should().BeNull();

        using (var scope = _factory.Services.CreateScope())
        {
            (await scope.ServiceProvider.GetRequiredService<IFileStorage>().GetAsync(key, Ct))
                .Should().BeNull("the bytes go with the record");
        }

        // Both: the capability and an administrator.
        (await DeleteAsync(owned, Caller(superAdmin, "SuperAdmin"))).Should().Be(FileDeleteResult.Deleted);
        (await RecordAsync(owned)).Should().BeNull();
        (await DeleteAsync(owned, Caller(superAdmin, "SuperAdmin"))).Should().Be(FileDeleteResult.NotFound, "it is gone");
    }

    /// <summary>
    /// The override on another user's file is the <c>manage_all_files</c> capability, on a role of
    /// any name, as on the routes. Reading needs it alone and deleting needs <c>upload_files</c> too.
    /// </summary>
    [Fact]
    public async Task A_role_of_any_name_holding_manage_all_files_reads_and_deletes_another_users_file()
    {
        var owner = Guid.NewGuid();
        var bytes = PdfBytes();
        var id = await StoredAsync(bytes, owner: owner);

        var (reader, readerRole) = await HolderAsync(FileCapabilities.ManageAllFiles);
        var (manager, managerRole) = await HolderAsync(FileCapabilities.UploadFiles, FileCapabilities.ManageAllFiles);

        (await FindAsync(id, Caller(reader, readerRole)))!.Id.Should().Be(id);
        (await ReadAsync(id, Caller(reader, readerRole))).Should().Equal(bytes);

        // Held through an API key or a token for another tenant, it opens nothing.
        (await ReadAsync(id, Caller(reader, readerRole, ("auth_method", "apikey")))).Should().BeNull();
        (await ReadAsync(id, Caller(reader, readerRole, ("tenant", "seam-some-other-tenant")))).Should().BeNull();

        // Without upload_files the delete is refused, as the route's gate refuses it.
        (await DeleteAsync(id, Caller(reader, readerRole), force: true)).Should().Be(FileDeleteResult.NotFound);
        (await RecordAsync(id)).Should().NotBeNull();

        (await DeleteAsync(id, Caller(manager, managerRole))).Should().Be(FileDeleteResult.Deleted);
        (await RecordAsync(id)).Should().BeNull();
    }

    [Fact]
    public async Task A_file_an_entry_names_is_not_deleted_until_forced_and_the_delete_is_recorded()
    {
        var superAdmin = await SuperAdminAsync();
        var caller = Caller(superAdmin, "SuperAdmin");
        var used = await StoredAsync(PdfBytes(), owner: superAdmin);
        var unused = await StoredAsync(PdfBytes(), owner: superAdmin);

        using (var scope = _factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            var type = $"seam_receipt_{Guid.NewGuid():N}"[..24];
            session.Store(new ContentTypeDefinition
            {
                Id = Guid.NewGuid(),
                Name = type,
                DisplayName = type,
                Fields = [new FieldDefinition { Name = "Receipt", DisplayName = "Receipt", Type = "string" }],
            });
            session.Store(new Content
            {
                Id = Guid.NewGuid(),
                ContentType = type,
                Status = ContentStatus.Published,
                Data = new Dictionary<string, object> { ["Receipt"] = $"/api/files/{used}" },
            });
            await session.SaveChangesAsync(Ct);
        }

        (await DeleteAsync(used, caller)).Should().Be(FileDeleteResult.InUse);
        (await RecordAsync(used)).Should().NotBeNull();

        // The control: a file no entry names needs no force.
        (await DeleteAsync(unused, caller)).Should().Be(FileDeleteResult.Deleted);

        (await DeleteAsync(used, caller, force: true)).Should().Be(FileDeleteResult.Deleted);
        (await RecordAsync(used)).Should().BeNull();

        using (var scope = _factory.Services.CreateScope())
        {
            var recorded = await scope.ServiceProvider.GetRequiredService<IQuerySession>()
                .Query<AuditEvent>().Where(e => e.TargetId == used.ToString()).ToListAsync(Ct);

            recorded.Should().HaveCount(1);
            recorded[0].Action.Should().Be("file.deleted");
            recorded[0].ActorUserId.Should().Be(superAdmin);
            recorded[0].ActorUsername.Should().Be($"seam-{superAdmin:N}");
        }
    }
}
