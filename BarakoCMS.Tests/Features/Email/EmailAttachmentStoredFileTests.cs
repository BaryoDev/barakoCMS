// Aliased: Microsoft.Extensions.DependencyInjection also ships a ServiceCollectionExtensions.
using Host = barakoCMS.Extensions.ServiceCollectionExtensions;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using barakoCMS.Core.Interfaces;
using barakoCMS.Features.Workflows;
using barakoCMS.Infrastructure.Auth;
using barakoCMS.Infrastructure.Multitenancy;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using barakoCMS.Modules;
using BarakoCMS.Files;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BarakoCMS.Tests.Features.Email;

/// <summary>
/// Which <see cref="IFileStore"/> a host ends up with, read from what <c>AddBarakoCMS</c> registers.
/// </summary>
/// <remarks>
/// No host is started. A scope resolves the last registration of a service, so the last descriptor
/// is the store every workflow action is handed.
/// </remarks>
public class FileStoreRegistrationTests
{
    private static List<ServiceDescriptor> FileStores(params IBarakoModule[] modules)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:DefaultConnection"] = "Host=localhost;Database=none",
            ["JWT:Key"] = "test-super-secret-key-that-is-at-least-32-chars-long",
        }).Build();

        var services = new ServiceCollection();
        Host.AddBarakoCMS(services, config, m =>
        {
            m.Discover = false;
            foreach (var module in modules) m.Add(module);
        });

        return services.Where(d => d.ServiceType == typeof(IFileStore)).ToList();
    }

    [Fact]
    public void Without_the_files_module_the_file_store_is_the_one_that_refuses()
    {
        var stores = FileStores();

        stores.Should().HaveCount(1);
        stores[0].ImplementationType.Should().Be(typeof(NoFileStore));
    }

    [Fact]
    public void With_the_files_module_its_store_is_the_one_a_scope_resolves()
    {
        var stores = FileStores(new FilesModule());

        stores.Should().NotBeEmpty();
        stores[^1].ImplementationType.Should().NotBeNull();
        stores[^1].ImplementationType!.Assembly.Should().BeSameAs(typeof(FilesModule).Assembly);
        stores[^1].ImplementationType.Should().NotBe(typeof(NoFileStore));
    }
}

/// <summary>
/// The Files module behind the core's file seam, and the registered Email action on top of it: a
/// stored file goes out only when it is public, the entry names it and it is in the run's tenant.
/// </summary>
/// <remarks>
/// Nobody's rights are consulted, so every refusal here is set up with a user who could download
/// the private file through the API: its uploader, a SuperAdmin, an administrator through a
/// membership. A rule that asked about any of them would send the file and fail the test.
/// </remarks>
[Collection("Sequential")]
public class EmailAttachmentStoredFileTests
{
    private const string Refused = "cannot be attached";

    private static readonly TimeSpan PollTimeout = TimeSpan.FromSeconds(30);

    private readonly IntegrationTestFixture _factory;
    private readonly HttpClient _client;

    public EmailAttachmentStoredFileTests(IntegrationTestFixture factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private sealed record UploadResponse(Guid Id, string FileName, string ContentType, long Size, bool IsPublic, string? PublicUrl);

    private sealed record Uploaded(Guid Id, Guid Uploader, byte[] Bytes);

    // Distinct per file, so a test that attaches two can tell them apart.
    private static byte[] PdfBytes() => FileSamples.Pdf().Concat(Guid.NewGuid().ToByteArray()).ToArray();

    private static string Address() => $"attach-{Guid.NewGuid():N}@example.com";

    private static string NewName(string prefix) => $"{prefix}{Guid.NewGuid():N}"[..20];

    /// <summary>Stores a user holding the global SuperAdmin role and answers its id.</summary>
    private async Task<Guid> SuperAdminAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var role = await session.Query<Role>().FirstOrDefaultAsync(r => r.Name == "SuperAdmin", TestContext.Current.CancellationToken)
                   ?? new Role { Id = barakoCMS.Data.DataSeeder.SuperAdminRoleId, Name = "SuperAdmin", Permissions = new() };
        session.Store(role);

        var userId = Guid.NewGuid();
        session.Store(new User { Id = userId, Username = $"attach-{userId}", Email = $"attach-{userId}@example.com", RoleIds = new() { role.Id } });
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        return userId;
    }

    /// <summary>Uploads a PDF into the default tenant as a new SuperAdmin, through the API.</summary>
    private async Task<Uploaded> UploadPdfAsync(bool isPublic, string name = "receipt.pdf")
    {
        var bytes = PdfBytes();
        var uploader = await SuperAdminAsync();
        var token = _factory.CreateToken(new[] { "SuperAdmin" }, uploader.ToString());

        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        form.Add(file, "file", name);
        form.Add(new StringContent(isPublic ? "true" : "false"), "isPublic");

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/files") { Content = form };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var response = await _client.SendAsync(request, TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        var body = await response.Content.ReadFromJsonAsync<UploadResponse>(TestContext.Current.CancellationToken);
        return new Uploaded(body!.Id, uploader, bytes);
    }

    /// <summary>Stores a file in a named tenant the way the upload does: bytes through the storage, then the record.</summary>
    private async Task<Uploaded> StoreInTenantAsync(string tenant, Guid uploader, bool isPublic, string name)
    {
        var bytes = PdfBytes();

        using var scope = _factory.Services.CreateScopeForTenant(tenant);
        var storage = scope.ServiceProvider.GetRequiredService<IFileStorage>();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();

        using var content = new MemoryStream(bytes);
        var stored = await storage.PutAsync(
            content, $"{FileKeys.Prefix(isPublic)}{Guid.NewGuid():N}.pdf", "application/pdf", isPublic, TestContext.Current.CancellationToken);

        var record = new StoredFile
        {
            FileName = name,
            ContentType = "application/pdf",
            Size = bytes.Length,
            Provider = storage.Provider,
            StorageKey = stored.Key,
            IsPublic = isPublic,
            PublicUrl = stored.PublicUrl,
            UploadedBy = uploader,
        };
        session.Store(record);
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);

        return new Uploaded(record.Id, uploader, bytes);
    }

    private static async Task<byte[]> ReadAllAsync(Stream stream)
    {
        await using (stream)
        {
            using var held = new MemoryStream();
            await stream.CopyToAsync(held, TestContext.Current.CancellationToken);
            return held.ToArray();
        }
    }

    private static Dictionary<string, string> Mail(string to, Content entry, string attachments) =>
        // The runner's own step. It fills To, Subject and Body from the entry and hands Attachments
        // over as written, for the action to read from the entry itself.
        ActionParameters.Resolve("Email", new Dictionary<string, string>
        {
            ["To"] = to,
            ["Subject"] = "Your receipt",
            ["Body"] = "<p>Attached.</p>",
            ["Attachments"] = attachments,
        }, entry);

    /// <summary>An entry whose Receipt field names a file, last saved by <paramref name="savedBy"/>.</summary>
    private static Content Registration(Guid receipt, Guid savedBy) => new()
    {
        Id = Guid.NewGuid(),
        ContentType = "registration",
        LastModifiedBy = savedBy,
        Data = new Dictionary<string, object> { ["Receipt"] = receipt.ToString() },
    };

    private static IWorkflowAction EmailActionOf(IServiceScope scope)
    {
        var emails = scope.ServiceProvider.GetServices<IWorkflowAction>().Where(a => a.Type == "Email").ToList();
        emails.Should().HaveCount(1, "the host registers one Email action, and it is the real one");
        return emails[0];
    }

    private async Task<WorkflowActionResult> SendAsync(string to, Content entry, string? tenant = null, string attachments = "{{data.Receipt}}")
    {
        using var scope = tenant is null ? _factory.Services.CreateScope() : _factory.Services.CreateScopeForTenant(tenant);
        return await EmailActionOf(scope).RunAsync(Mail(to, entry, attachments), entry, TestContext.Current.CancellationToken);
    }

    private List<RecordingEmailService.Sent> SentTo(string to) => _factory.Email.Messages.Where(m => m.To == to).ToList();

    private static void ShouldBeRefused(WorkflowActionResult result, Guid file)
    {
        result.Succeeded.Should().BeFalse();
        result.Retryable.Should().BeFalse();
        result.Error.Should().Contain(Refused);
        result.Error.Should().NotContain(file.ToString());
    }

    private void ShouldHaveSentOnly(string to, Uploaded file)
    {
        var sent = SentTo(to);
        sent.Should().HaveCount(1);
        sent[0].Attachments.Should().HaveCount(1);
        sent[0].Attachments[0].Content.Should().Equal(file.Bytes);
    }

    [Fact]
    public async Task The_seam_hands_out_a_public_file_and_reads_a_private_one_as_absent()
    {
        var open = await UploadPdfAsync(isPublic: true, name: "terms.pdf");
        var closed = await UploadPdfAsync(isPublic: false);

        using var scope = _factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IFileStore>();

        var info = await store.FindPublicAsync(open.Id, TestContext.Current.CancellationToken);
        info.Should().NotBeNull();
        info!.Id.Should().Be(open.Id);
        info.FileName.Should().Be("terms.pdf");
        info.ContentType.Should().Be("application/pdf");
        info.Size.Should().Be(open.Bytes.Length);

        var stream = await store.OpenPublicAsync(open.Id, TestContext.Current.CancellationToken);
        stream.Should().NotBeNull();
        (await ReadAllAsync(stream!)).Should().Equal(open.Bytes);

        (await store.FindPublicAsync(closed.Id, TestContext.Current.CancellationToken)).Should().BeNull();
        (await store.OpenPublicAsync(closed.Id, TestContext.Current.CancellationToken)).Should().BeNull();
        (await store.FindPublicAsync(Guid.NewGuid(), TestContext.Current.CancellationToken)).Should().BeNull();
    }

    [Fact]
    public async Task The_seam_hands_out_nothing_from_another_tenant_not_even_a_public_file()
    {
        var open = await UploadPdfAsync(isPublic: true);

        using (var own = _factory.Services.CreateScope())
        {
            (await own.ServiceProvider.GetRequiredService<IFileStore>().FindPublicAsync(open.Id, TestContext.Current.CancellationToken))
                .Should().NotBeNull("the control: it is found in the tenant that holds it");
        }

        using (var other = _factory.Services.CreateScopeForTenant("attach-seam-other"))
        {
            var store = other.ServiceProvider.GetRequiredService<IFileStore>();
            (await store.FindPublicAsync(open.Id, TestContext.Current.CancellationToken)).Should().BeNull();
            (await store.OpenPublicAsync(open.Id, TestContext.Current.CancellationToken)).Should().BeNull();
        }
    }

    [Fact]
    public async Task A_cached_resize_is_not_a_file_the_seam_hands_out()
    {
        var parent = await UploadPdfAsync(isPublic: true);
        var variant = new StoredFile
        {
            FileName = "receipt.pdf",
            ContentType = "application/pdf",
            StorageKey = $"{FileKeys.Prefix(true)}{Guid.NewGuid():N}.pdf",
            IsPublic = true,
            UploadedBy = parent.Uploader,
            ParentFileId = parent.Id,
            VariantWidth = 400,
        };

        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Store(variant);
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);

        var store = scope.ServiceProvider.GetRequiredService<IFileStore>();

        (await store.FindPublicAsync(parent.Id, TestContext.Current.CancellationToken)).Should().NotBeNull("the control: its original is found");
        (await store.FindPublicAsync(variant.Id, TestContext.Current.CancellationToken)).Should().BeNull();
        (await store.OpenPublicAsync(variant.Id, TestContext.Current.CancellationToken)).Should().BeNull();
    }

    /// <summary>
    /// The last writer is the file's own uploader and a SuperAdmin, who can download it through the
    /// API. The workflow still does not send it, and sends the public file beside it.
    /// </summary>
    [Fact]
    public async Task An_entry_saved_by_the_files_uploader_attaches_a_public_file_and_not_a_private_one()
    {
        var closed = await UploadPdfAsync(isPublic: false);
        var open = await UploadPdfAsync(isPublic: true, name: "terms.pdf");
        var to = Address();

        ShouldBeRefused(await SendAsync(to, Registration(closed.Id, savedBy: closed.Uploader)), closed.Id);
        SentTo(to).Should().BeEmpty();

        var allowed = await SendAsync(to, Registration(open.Id, savedBy: open.Uploader));
        allowed.Succeeded.Should().BeTrue(allowed.Error ?? string.Empty);

        ShouldHaveSentOnly(to, open);
        SentTo(to)[0].Attachments[0].FileName.Should().Be("terms.pdf");
        SentTo(to)[0].Attachments[0].ContentType.Should().Be("application/pdf");
    }

    /// <summary>
    /// A public form's submission is stored with no user. It can carry a file anyone can already
    /// download, and nothing else.
    /// </summary>
    [Fact]
    public async Task An_entry_with_no_user_attaches_a_public_file_and_not_a_private_one()
    {
        var closed = await UploadPdfAsync(isPublic: false);
        var open = await UploadPdfAsync(isPublic: true);
        var to = Address();

        ShouldBeRefused(await SendAsync(to, Registration(closed.Id, savedBy: Guid.Empty)), closed.Id);
        SentTo(to).Should().BeEmpty();

        var allowed = await SendAsync(to, Registration(open.Id, savedBy: Guid.Empty));
        allowed.Succeeded.Should().BeTrue(allowed.Error ?? string.Empty);
        ShouldHaveSentOnly(to, open);
    }

    /// <summary>
    /// A tenant of its own, an administrator who holds Admin there through a membership and nowhere
    /// else, and files stored in that tenant, one of them uploaded by that administrator. The run is
    /// in the tenant, so the tenant is not what refuses the private file.
    /// </summary>
    [Fact]
    public async Task An_entry_saved_by_a_tenants_own_administrator_attaches_a_public_file_and_not_a_private_one()
    {
        var tenant = NewName("attach-club-");
        var administrator = Guid.NewGuid();

        using (var scope = _factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            var admin = await session.Query<Role>().FirstOrDefaultAsync(r => r.Name == "Admin", TestContext.Current.CancellationToken);
            admin.Should().NotBeNull("the seeder creates the Admin role, and the membership below has to hold a real one");

            session.Store(new User { Id = administrator, Username = $"attach-{administrator}", Email = $"attach-{administrator}@example.com" });
            session.Store(new Membership
            {
                Id = Guid.NewGuid(),
                UserId = administrator,
                TenantSlug = tenant,
                RoleIds = new() { admin!.Id },
                Status = MembershipStatus.Active,
            });
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var closed = await StoreInTenantAsync(tenant, administrator, isPublic: false, name: "receipt.pdf");
        var open = await StoreInTenantAsync(tenant, administrator, isPublic: true, name: "terms.pdf");
        var to = Address();

        ShouldBeRefused(await SendAsync(to, Registration(closed.Id, savedBy: administrator), tenant), closed.Id);
        SentTo(to).Should().BeEmpty();

        var allowed = await SendAsync(to, Registration(open.Id, savedBy: administrator), tenant);
        allowed.Succeeded.Should().BeTrue(allowed.Error ?? string.Empty);
        ShouldHaveSentOnly(to, open);
    }

    /// <summary>
    /// A public file is refused from any tenant but its own, in both directions: the default
    /// tenant's from a named tenant, and a named tenant's from the default one.
    /// </summary>
    [Fact]
    public async Task A_public_file_of_another_tenant_is_refused_with_the_same_reason_as_any_other()
    {
        var tenant = NewName("attach-club-");
        var uploader = await SuperAdminAsync();
        var inDefault = await UploadPdfAsync(isPublic: true);
        var inTenant = await StoreInTenantAsync(tenant, uploader, isPublic: true, name: "terms.pdf");
        var to = Address();

        ShouldBeRefused(await SendAsync(to, Registration(inDefault.Id, savedBy: inDefault.Uploader), tenant), inDefault.Id);
        ShouldBeRefused(await SendAsync(to, Registration(inTenant.Id, savedBy: uploader)), inTenant.Id);
        SentTo(to).Should().BeEmpty();

        // The control: each sends from the tenant that holds it.
        (await SendAsync(to, Registration(inDefault.Id, savedBy: inDefault.Uploader))).Succeeded.Should().BeTrue();
        (await SendAsync(to, Registration(inTenant.Id, savedBy: uploader), tenant)).Succeeded.Should().BeTrue();
        SentTo(to).Should().HaveCount(2);
    }

    /// <summary>
    /// An id typed into the workflow. The file is public and in the tenant; the entry names a
    /// different file, so this one is not sent.
    /// </summary>
    [Fact]
    public async Task A_public_file_the_entry_does_not_name_is_refused()
    {
        var named = await UploadPdfAsync(isPublic: true);
        var notNamed = await UploadPdfAsync(isPublic: true);
        var to = Address();
        var entry = Registration(named.Id, savedBy: named.Uploader);

        ShouldBeRefused(await SendAsync(to, entry, attachments: notNamed.Id.ToString()), notNamed.Id);
        SentTo(to).Should().BeEmpty();

        var allowed = await SendAsync(to, entry, attachments: named.Id.ToString());
        allowed.Succeeded.Should().BeTrue(allowed.Error ?? string.Empty);
        ShouldHaveSentOnly(to, named);
    }

    /// <summary>
    /// An API key writes as its owner, so an entry created through a SuperAdmin's key records that
    /// SuperAdmin as its last writer. The key itself cannot download any file.
    /// </summary>
    [Fact]
    public async Task An_entry_saved_through_an_administrators_api_key_attaches_a_public_file_and_not_a_private_one()
    {
        var closed = await UploadPdfAsync(isPublic: false);
        var open = await UploadPdfAsync(isPublic: true);
        var owner = closed.Uploader;
        var to = Address();

        var secret = "bcms_" + Guid.NewGuid().ToString("N");
        using (var scope = _factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Store(new ApiKey
            {
                Id = Guid.NewGuid(),
                Name = "attachments",
                KeyHash = ApiKeyService.Hash(secret),
                Prefix = secret[..12],
                UserId = owner,
                TenantSlug = Tenant.DefaultSlug,
                Scopes = new() { "content:read", "content:write" },
            });
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        async Task<Content> CreateThroughKeyAsync(Guid receipt)
        {
            var type = NewName("attachkey");
            using (var definitions = _factory.Services.CreateScope())
            {
                var session = definitions.ServiceProvider.GetRequiredService<IDocumentSession>();
                session.Store(new ContentTypeDefinition
                {
                    Id = Guid.NewGuid(),
                    Name = type,
                    DisplayName = "Registration",
                    Fields = [new FieldDefinition { Name = "Receipt", DisplayName = "Receipt", Type = "string" }],
                });
                await session.SaveChangesAsync(TestContext.Current.CancellationToken);
            }

            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/contents");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret);
            request.Content = JsonContent.Create(new
            {
                contentType = type,
                data = new Dictionary<string, object> { ["Receipt"] = receipt.ToString() },
            });
            var response = await _client.SendAsync(request, TestContext.Current.CancellationToken);
            response.IsSuccessStatusCode.Should().BeTrue(
                "got {0}: {1}", response.StatusCode, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

            using var scope = _factory.Services.CreateScope();
            var entries = await scope.ServiceProvider.GetRequiredService<IQuerySession>()
                .Query<Content>().Where(c => c.ContentType == type).ToListAsync(TestContext.Current.CancellationToken);
            entries.Should().HaveCount(1);
            entries[0].LastModifiedBy.Should().Be(owner, "the key writes as its owner, which is the situation under test");
            return entries[0];
        }

        ShouldBeRefused(await SendAsync(to, await CreateThroughKeyAsync(closed.Id)), closed.Id);
        SentTo(to).Should().BeEmpty();

        var allowed = await SendAsync(to, await CreateThroughKeyAsync(open.Id));
        allowed.Succeeded.Should().BeTrue(allowed.Error ?? string.Empty);
        ShouldHaveSentOnly(to, open);
    }

    /// <summary>
    /// A list field as the database gives it back. The entry is stored, then loaded in a session
    /// of its own, so its list is whatever a real run would be handed.
    /// </summary>
    [Fact]
    public async Task A_stored_entrys_list_field_attaches_every_file_in_it()
    {
        var first = await UploadPdfAsync(isPublic: true, name: "one.pdf");
        var second = await UploadPdfAsync(isPublic: true, name: "two.pdf");
        var to = Address();
        var entryId = Guid.NewGuid();
        var store = _factory.Services.GetRequiredService<IDocumentStore>();

        await using (var session = store.LightweightSession())
        {
            session.Store(new Content
            {
                Id = entryId,
                ContentType = "registration",
                Data = new Dictionary<string, object>
                {
                    ["Files"] = new List<object> { first.Id.ToString(), $"/api/public/files/{second.Id}" },
                },
            });
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        Content? loaded;
        await using (var query = store.QuerySession())
        {
            loaded = await query.LoadAsync<Content>(entryId, TestContext.Current.CancellationToken);
        }

        loaded.Should().NotBeNull();
        loaded!.Data["Files"].Should().NotBeOfType<string>("the stored list comes back as a list, which is the case under test");

        var result = await SendAsync(to, loaded, attachments: "{{data.Files}}");

        result.Succeeded.Should().BeTrue(result.Error ?? string.Empty);

        var sent = SentTo(to);
        sent.Should().HaveCount(1);
        sent[0].Attachments.Should().HaveCount(2);
        sent[0].Attachments.Select(a => a.FileName).Should().Equal("one.pdf", "two.pdf");
        sent[0].Attachments[0].Content.Should().Equal(first.Bytes);
        sent[0].Attachments[1].Content.Should().Equal(second.Bytes);
    }

    /// <summary>
    /// The path a deployment sends through: a queued run picked up by the runner, which loads the
    /// entry itself and builds the action from a scope of the run's tenant.
    /// </summary>
    [Fact]
    public async Task A_queued_email_run_attaches_the_public_files_its_entry_names()
    {
        var first = await UploadPdfAsync(isPublic: true, name: "one.pdf");
        var second = await UploadPdfAsync(isPublic: true, name: "two.pdf");
        var to = Address();
        var store = _factory.Services.GetRequiredService<IDocumentStore>();
        var contentId = Guid.NewGuid();
        var runId = Guid.NewGuid();

        await using (var session = store.LightweightSession())
        {
            session.Store(new Content
            {
                Id = contentId,
                ContentType = "registration",
                Status = ContentStatus.Published,
                Data = new Dictionary<string, object>
                {
                    ["Files"] = new List<object> { first.Id.ToString(), second.Id.ToString() },
                },
            });

            var run = new WorkflowRun
            {
                Id = runId,
                WorkflowDefinitionId = Guid.NewGuid(),
                WorkflowName = "Attachments through the runner",
                // The runner looks only at the 20 oldest unfinished runs, so other tests' runs waiting
                // on a retry can hide a new one for the whole loop (#695). Oldest means it is seen.
                CreatedAt = DateTimeOffset.UnixEpoch,
                ContentId = contentId,
                ContentType = "registration",
                TriggerEvent = "Published",
                TriggeringEventSequence = 1,
                Actions =
                [
                    new WorkflowActionAttempt
                    {
                        Ordinal = 0,
                        ActionType = "Email",
                        IdempotencyKey = $"{Guid.NewGuid():N}",
                        Parameters = new()
                        {
                            ["To"] = to,
                            ["Subject"] = "Your files",
                            ["Body"] = "<p>Attached.</p>",
                            ["Attachments"] = "{{data.Files}}",
                        },
                    },
                ],
            };
            run.Recompute();
            session.Store(run);
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        // Either this runner or the fixture's hosted one may claim it; both send through the
        // fixture's recording transport, so wait for the message rather than for a particular runner.
        var runner = NewRunner();

        List<RecordingEmailService.Sent> sent = [];
        for (var i = 0; i < 100 && sent.Count == 0; i++)
        {
            await runner.RunOnceAsync(TestContext.Current.CancellationToken);
            sent = SentTo(to);
            if (sent.Count == 0) await Task.Delay(100, TestContext.Current.CancellationToken);
        }

        sent.Should().ContainSingle("the queued run sends exactly one email; {0}", await DescribeRunAsync(runId));
        sent[0].Attachments.Should().HaveCount(2);
        sent[0].Attachments.Select(a => a.FileName).Should().Equal("one.pdf", "two.pdf");
        sent[0].Attachments[0].Content.Should().Equal(first.Bytes);
        sent[0].Attachments[1].Content.Should().Equal(second.Bytes);
    }

    /// <summary>
    /// The registration case end to end: a stranger submits a public form naming a file, a
    /// SuperAdmin confirms the submission, and the confirmation fires an email that attaches the
    /// named file. The entry's last writer is then the SuperAdmin, who could download a private
    /// file. A private file the submission named is not sent, and a public one is.
    /// </summary>
    [Fact]
    public async Task A_private_file_named_by_a_form_submission_is_not_sent_after_an_administrator_confirms_it()
    {
        var closed = await UploadPdfAsync(isPublic: false);
        var open = await UploadPdfAsync(isPublic: true, name: "terms.pdf");
        var to = Address();
        var type = await FormTypeWithConfirmationEmailAsync(to);

        var approverToken = _factory.CreateToken(new[] { "SuperAdmin" }, (await SuperAdminAsync()).ToString());
        var approver = _factory.CreateClient();
        approver.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", approverToken);

        var withPrivate = await SubmitAndConfirmAsync(type, approver, closed.Id);
        var failed = await WaitForRunAsync(withPrivate, attempt => attempt.Status == AttemptStatus.Failed);

        failed.Error.Should().Contain(Refused);
        failed.Retryable.Should().NotBe(true, "the refusal is permanent, so the runner does not try it again");
        SentTo(to).Should().BeEmpty("the submission named a private file, and confirming it must not send that file");

        // The control: the same form, workflow and approver, with a submission naming a public file.
        var withPublic = await SubmitAndConfirmAsync(type, approver, open.Id);
        await WaitForRunAsync(withPublic, attempt => attempt.Status == AttemptStatus.Succeeded);

        ShouldHaveSentOnly(to, open);
    }

    private WorkflowRunner NewRunner() => new(
        _factory.Services,
        _factory.Services.GetRequiredService<Microsoft.Extensions.Logging.ILogger<WorkflowRunner>>(),
        _factory.Services.GetRequiredService<IConfiguration>());

    /// <summary>A form-enabled type with a Confirm transition, and a workflow that emails on it.</summary>
    private async Task<string> FormTypeWithConfirmationEmailAsync(string to)
    {
        var type = $"form-{Guid.NewGuid():N}";

        using (var scope = _factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Store(new ContentTypeDefinition
            {
                Id = Guid.NewGuid(),
                Name = type,
                DisplayName = "Registration",
                Fields =
                [
                    new FieldDefinition { Name = "name", DisplayName = "Name", Type = "string", IsRequired = true },
                    new FieldDefinition { Name = "receipt", DisplayName = "Receipt", Type = "string" },
                ],
                Lifecycle = new LifecycleDefinition
                {
                    States = ["New", "Confirmed"],
                    InitialState = "New",
                    Transitions = [new StateTransition { Name = "Confirm", From = "New", To = "Confirmed" }],
                },
            });
            session.Store(new WorkflowDefinition
            {
                Id = Guid.NewGuid(),
                Name = NewName("wf"),
                TriggerContentType = type,
                TriggerEvent = WorkflowEvents.ForTransition("Confirm"),
                Actions =
                [
                    new WorkflowAction
                    {
                        Type = "Email",
                        Parameters = new Dictionary<string, string>
                        {
                            ["To"] = to,
                            ["Subject"] = "Registration confirmed",
                            ["Body"] = "<p>Confirmed.</p>",
                            ["Attachments"] = "{{data.receipt}}",
                        },
                    },
                ],
            });
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var admin = _factory.CreateClient();
        admin.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", await _factory.StoredUserTokenAsync("Admin"));
        var enabled = await admin.PutAsJsonAsync($"/api/forms/{type}", new { enabled = true }, TestContext.Current.CancellationToken);
        enabled.StatusCode.Should().Be(HttpStatusCode.OK, await enabled.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        return type;
    }

    /// <summary>Submits the form as a stranger, then confirms the submission as the approver. Answers the entry's id.</summary>
    private async Task<Guid> SubmitAndConfirmAsync(string type, HttpClient approver, Guid receipt)
    {
        var name = $"Ana {Guid.NewGuid():N}";

        // Its own client address: the submit route allows five requests per address per window.
        var visitor = _factory.CreateClient();
        var bytes = Guid.NewGuid().ToByteArray();
        visitor.DefaultRequestHeaders.Add(
            TestRemoteIpFilter.Header,
            $"2001:db8:806::{bytes[0]:x2}{bytes[1]:x2}:{bytes[2]:x2}{bytes[3]:x2}");

        var submitted = await visitor.PostAsJsonAsync(
            $"/api/public/forms/{type}", new { data = new { name, receipt = receipt.ToString() } }, TestContext.Current.CancellationToken);
        submitted.StatusCode.Should().Be(HttpStatusCode.Accepted, await submitted.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        Guid id;
        using (var scope = _factory.Services.CreateScope())
        {
            var entries = (await scope.ServiceProvider.GetRequiredService<IQuerySession>()
                    .Query<Content>().Where(c => c.ContentType == type).ToListAsync(TestContext.Current.CancellationToken))
                .Where(c => Equals(c.Data.GetValueOrDefault("name"), name))
                .ToList();
            entries.Should().HaveCount(1);
            entries[0].LastModifiedBy.Should().Be(Guid.Empty, "a submission is stored with no user");
            id = entries[0].Id;
        }

        var confirmed = await approver.PutAsJsonAsync(
            $"/api/contents/{id}/status", new { id, transition = "Confirm" }, TestContext.Current.CancellationToken);
        confirmed.IsSuccessStatusCode.Should().BeTrue(
            "got {0}: {1}", confirmed.StatusCode, await confirmed.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        using (var scope = _factory.Services.CreateScope())
        {
            var entry = await scope.ServiceProvider.GetRequiredService<IQuerySession>()
                .LoadAsync<Content>(id, TestContext.Current.CancellationToken);
            entry.Should().NotBeNull();
            entry!.LastModifiedBy.Should().NotBe(Guid.Empty, "the confirmation stamps the approver, which is the situation under test");
        }

        return id;
    }

    /// <summary>
    /// Waits for the Email attempt of the run the confirmation queued to reach the wanted state,
    /// helping the runner along, and answers that attempt.
    /// </summary>
    private async Task<WorkflowActionAttempt> WaitForRunAsync(Guid contentId, Func<WorkflowActionAttempt, bool> wanted)
    {
        var store = _factory.Services.GetRequiredService<IDocumentStore>();
        var runner = NewRunner();
        var deadline = DateTime.UtcNow + PollTimeout;
        var last = "no run was queued for the entry";

        while (DateTime.UtcNow < deadline)
        {
            await runner.RunOnceAsync(TestContext.Current.CancellationToken);

            await using (var query = store.QuerySession())
            {
                var runs = await query.Query<WorkflowRun>()
                    .Where(r => r.ContentId == contentId)
                    .ToListAsync(TestContext.Current.CancellationToken);

                if (runs.Count > 0)
                {
                    runs.Should().HaveCount(1, "one confirmation queues one run");
                    runs[0].Actions.Should().HaveCount(1);
                    var attempt = runs[0].Actions[0];

                    if (wanted(attempt))
                    {
                        return attempt;
                    }

                    last = $"the attempt is {attempt.Status}, attempts {attempt.Attempts}, error '{attempt.Error}'";
                }
            }

            await Task.Delay(250, TestContext.Current.CancellationToken);
        }

        throw new Xunit.Sdk.XunitException($"Timed out after {PollTimeout.TotalSeconds:0}s: {last}.");
    }

    private async Task<string> DescribeRunAsync(Guid runId)
    {
        await using var query = _factory.Services.GetRequiredService<IDocumentStore>().QuerySession();
        var run = await query.LoadAsync<WorkflowRun>(runId, TestContext.Current.CancellationToken);

        return run is null
            ? "the run is gone"
            : $"run {run.Status}; " + string.Join("; ", run.Actions.Select(a =>
                $"action {a.Ordinal} {a.Status}, attempts {a.Attempts}, error '{a.Error}', next {a.NextAttemptAt:O}"));
    }
}
