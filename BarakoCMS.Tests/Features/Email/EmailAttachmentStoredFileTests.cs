// Aliased: Microsoft.Extensions.DependencyInjection also ships a ServiceCollectionExtensions.
using Host = barakoCMS.Extensions.ServiceCollectionExtensions;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using barakoCMS.Core.Interfaces;
using barakoCMS.Features.Workflows;
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
/// The Files module behind the core's file seam, and the registered Email action on top of it:
/// an uploaded file goes out only for the tenant that holds it, and only when it is public or the
/// user who last saved the entry could download it.
/// </summary>
[Collection("Sequential")]
public class EmailAttachmentStoredFileTests
{
    private const string Refused = "cannot be attached";

    private readonly IntegrationTestFixture _factory;
    private readonly HttpClient _client;

    public EmailAttachmentStoredFileTests(IntegrationTestFixture factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private sealed record UploadResponse(Guid Id, string FileName, string ContentType, long Size, bool IsPublic, string? PublicUrl);

    private sealed record Uploaded(Guid Id, Guid Uploader, byte[] Bytes);

    /// <summary>Stores a user, with the SuperAdmin role or with no role at all, and answers its id.</summary>
    private async Task<Guid> UserAsync(bool admin)
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var roleIds = new List<Guid>();

        if (admin)
        {
            var role = await session.Query<Role>().FirstOrDefaultAsync(r => r.Name == "SuperAdmin", TestContext.Current.CancellationToken)
                       ?? new Role { Id = barakoCMS.Data.DataSeeder.SuperAdminRoleId, Name = "SuperAdmin", Permissions = new() };
            session.Store(role);
            roleIds.Add(role.Id);
        }

        var userId = Guid.NewGuid();
        session.Store(new User { Id = userId, Username = $"attach-{userId}", Email = $"attach-{userId}@example.com", RoleIds = roleIds });
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        return userId;
    }

    /// <summary>Uploads a PDF into the default tenant as a new administrator, the way an editor does.</summary>
    private async Task<Uploaded> UploadPdfAsync(bool isPublic = false, string name = "receipt.pdf")
    {
        // Distinct per upload, so a test that attaches two files can tell them apart.
        var bytes = FileSamples.Pdf().Concat(Guid.NewGuid().ToByteArray()).ToArray();
        var uploader = await UserAsync(admin: true);
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

    private static async Task<byte[]> ReadAllAsync(Stream stream)
    {
        await using (stream)
        {
            using var held = new MemoryStream();
            await stream.CopyToAsync(held, TestContext.Current.CancellationToken);
            return held.ToArray();
        }
    }

    private static Dictionary<string, string> Mail(string to, Content entry, string attachments = "{{data.Receipt}}") =>
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

    private static string Address() => $"attach-{Guid.NewGuid():N}@example.com";

    [Fact]
    public async Task The_seam_hands_a_private_file_to_its_uploader_and_to_an_administrator_and_to_nobody_else()
    {
        var file = await UploadPdfAsync();
        var otherAdmin = await UserAsync(admin: true);
        var plainUser = await UserAsync(admin: false);
        var nobodyStored = Guid.NewGuid();

        using var scope = _factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IFileStore>();

        var info = await store.FindReadableAsync(file.Id, file.Uploader, TestContext.Current.CancellationToken);
        info.Should().NotBeNull();
        info!.Id.Should().Be(file.Id);
        info.FileName.Should().Be("receipt.pdf");
        info.ContentType.Should().Be("application/pdf");
        info.Size.Should().Be(file.Bytes.Length);

        var stream = await store.OpenReadableAsync(file.Id, file.Uploader, TestContext.Current.CancellationToken);
        stream.Should().NotBeNull();
        (await ReadAllAsync(stream!)).Should().Equal(file.Bytes);

        (await store.FindReadableAsync(file.Id, otherAdmin, TestContext.Current.CancellationToken))
            .Should().NotBeNull("an administrator may download any file of the tenant");

        foreach (var user in new Guid?[] { plainUser, nobodyStored, Guid.Empty, null })
        {
            (await store.FindReadableAsync(file.Id, user, TestContext.Current.CancellationToken)).Should().BeNull();
            (await store.OpenReadableAsync(file.Id, user, TestContext.Current.CancellationToken)).Should().BeNull();
        }
    }

    [Fact]
    public async Task The_seam_hands_a_public_file_to_anyone_including_no_user()
    {
        var file = await UploadPdfAsync(isPublic: true, name: "terms.pdf");
        var plainUser = await UserAsync(admin: false);

        using var scope = _factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IFileStore>();

        (await store.FindReadableAsync(file.Id, null, TestContext.Current.CancellationToken)).Should().NotBeNull();
        (await store.FindReadableAsync(file.Id, plainUser, TestContext.Current.CancellationToken)).Should().NotBeNull();

        var stream = await store.OpenReadableAsync(file.Id, null, TestContext.Current.CancellationToken);
        stream.Should().NotBeNull();
        (await ReadAllAsync(stream!)).Should().Equal(file.Bytes);
    }

    [Fact]
    public async Task The_seam_hands_out_nothing_from_another_tenant_even_a_public_file_or_to_its_uploader()
    {
        var closed = await UploadPdfAsync();
        var open = await UploadPdfAsync(isPublic: true);

        using (var own = _factory.Services.CreateScope())
        {
            var store = own.ServiceProvider.GetRequiredService<IFileStore>();
            (await store.FindReadableAsync(closed.Id, closed.Uploader, TestContext.Current.CancellationToken))
                .Should().NotBeNull("the control: both are found in the tenant that holds them");
            (await store.FindReadableAsync(open.Id, null, TestContext.Current.CancellationToken)).Should().NotBeNull();
        }

        using (var other = _factory.Services.CreateScopeForTenant("attach-seam-other"))
        {
            var store = other.ServiceProvider.GetRequiredService<IFileStore>();
            (await store.FindReadableAsync(closed.Id, closed.Uploader, TestContext.Current.CancellationToken)).Should().BeNull();
            (await store.OpenReadableAsync(closed.Id, closed.Uploader, TestContext.Current.CancellationToken)).Should().BeNull();
            (await store.FindReadableAsync(open.Id, null, TestContext.Current.CancellationToken)).Should().BeNull();
            (await store.OpenReadableAsync(open.Id, null, TestContext.Current.CancellationToken)).Should().BeNull();
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
            StorageKey = $"public/{Guid.NewGuid():N}.pdf",
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

        (await store.FindReadableAsync(parent.Id, parent.Uploader, TestContext.Current.CancellationToken))
            .Should().NotBeNull("the control: its original is found");
        (await store.FindReadableAsync(variant.Id, parent.Uploader, TestContext.Current.CancellationToken)).Should().BeNull();
        (await store.OpenReadableAsync(variant.Id, parent.Uploader, TestContext.Current.CancellationToken)).Should().BeNull();
    }

    [Fact]
    public async Task A_private_file_uploaded_by_the_entrys_last_writer_is_attached()
    {
        var file = await UploadPdfAsync();
        var to = Address();

        var result = await SendAsync(to, Registration(file.Id, savedBy: file.Uploader));

        result.Succeeded.Should().BeTrue(result.Error ?? string.Empty);

        var sent = SentTo(to);
        sent.Should().HaveCount(1);
        sent[0].Attachments.Should().HaveCount(1);
        sent[0].Attachments[0].FileName.Should().Be("receipt.pdf");
        sent[0].Attachments[0].ContentType.Should().Be("application/pdf");
        sent[0].Attachments[0].Content.Should().Equal(file.Bytes);
    }

    /// <summary>
    /// Entry B holds its own receipt. Entry A is saved by a user who cannot download that file and
    /// names it in the same templated field. A's workflow must not mail B's file.
    /// </summary>
    [Fact]
    public async Task An_entry_naming_another_entrys_private_file_through_a_templated_field_is_refused()
    {
        var receiptOfB = await UploadPdfAsync();
        var writerOfA = await UserAsync(admin: false);
        var to = Address();

        var entryB = Registration(receiptOfB.Id, savedBy: receiptOfB.Uploader);
        var entryA = Registration(receiptOfB.Id, savedBy: writerOfA);

        var refused = await SendAsync(to, entryA);

        refused.Succeeded.Should().BeFalse();
        refused.Retryable.Should().BeFalse();
        refused.Error.Should().Contain(Refused);
        refused.Error.Should().NotContain(receiptOfB.Id.ToString());
        SentTo(to).Should().BeEmpty();

        // The control: entry B, with the same field, template and file, sends it.
        var allowed = await SendAsync(to, entryB);
        allowed.Succeeded.Should().BeTrue(allowed.Error ?? string.Empty);
        SentTo(to).Should().HaveCount(1);
    }

    /// <summary>
    /// The entry names the file, and the two users differ only in what the Files module lets them
    /// download: one holds no role, the other administers the tenant.
    /// </summary>
    [Fact]
    public async Task A_private_file_uploaded_by_someone_else_goes_out_only_when_the_last_writer_administers_the_tenant()
    {
        var file = await UploadPdfAsync();
        var plainUser = await UserAsync(admin: false);
        var otherAdmin = await UserAsync(admin: true);
        var to = Address();

        var refused = await SendAsync(to, Registration(file.Id, savedBy: plainUser));

        refused.Succeeded.Should().BeFalse();
        refused.Retryable.Should().BeFalse();
        refused.Error.Should().Contain(Refused);
        SentTo(to).Should().BeEmpty();

        var allowed = await SendAsync(to, Registration(file.Id, savedBy: otherAdmin));
        allowed.Succeeded.Should().BeTrue(allowed.Error ?? string.Empty);

        var sent = SentTo(to);
        sent.Should().HaveCount(1);
        sent[0].Attachments.Should().HaveCount(1);
        sent[0].Attachments[0].Content.Should().Equal(file.Bytes);
    }

    /// <summary>
    /// A public form's submission is stored with no user. It can carry a file anyone can already
    /// download, and nothing else.
    /// </summary>
    [Fact]
    public async Task A_submission_with_no_user_attaches_a_public_file_and_not_a_private_one()
    {
        var closed = await UploadPdfAsync();
        var open = await UploadPdfAsync(isPublic: true, name: "terms.pdf");
        var to = Address();

        var refused = await SendAsync(to, Registration(closed.Id, savedBy: Guid.Empty));

        refused.Succeeded.Should().BeFalse();
        refused.Retryable.Should().BeFalse();
        refused.Error.Should().Contain(Refused);
        SentTo(to).Should().BeEmpty();

        var allowed = await SendAsync(to, Registration(open.Id, savedBy: Guid.Empty));
        allowed.Succeeded.Should().BeTrue(allowed.Error ?? string.Empty);

        var sent = SentTo(to);
        sent.Should().HaveCount(1);
        sent[0].Attachments.Should().HaveCount(1);
        sent[0].Attachments[0].FileName.Should().Be("terms.pdf");
        sent[0].Attachments[0].Content.Should().Equal(open.Bytes);
    }

    /// <summary>
    /// The entry names the file and its last writer uploaded it. What refuses it is the tenant: the
    /// run's scope is another tenant's and cannot load the file.
    /// </summary>
    [Fact]
    public async Task A_file_of_another_tenant_is_refused_with_the_same_reason_as_any_other()
    {
        var file = await UploadPdfAsync();
        var to = Address();
        var entry = Registration(file.Id, savedBy: file.Uploader);

        var refused = await SendAsync(to, entry, tenant: "attach-action-other");

        refused.Succeeded.Should().BeFalse();
        refused.Retryable.Should().BeFalse();
        refused.Error.Should().Contain(Refused);
        SentTo(to).Should().BeEmpty();

        // The control: the same entry and parameters in the file's own tenant send it.
        var allowed = await SendAsync(to, entry);
        allowed.Succeeded.Should().BeTrue(allowed.Error ?? string.Empty);
        SentTo(to).Should().HaveCount(1);
    }

    /// <summary>
    /// A list field as the database gives it back. The entry is stored, then loaded in a session
    /// of its own, so its list is whatever a real run would be handed.
    /// </summary>
    [Fact]
    public async Task A_stored_entrys_list_field_attaches_every_file_in_it()
    {
        var first = await UploadPdfAsync(name: "one.pdf");
        var second = await UploadPdfAsync(name: "two.pdf");
        var writer = await UserAsync(admin: true);
        var to = Address();
        var entryId = Guid.NewGuid();
        var store = _factory.Services.GetRequiredService<IDocumentStore>();

        await using (var session = store.LightweightSession())
        {
            session.Store(new Content
            {
                Id = entryId,
                ContentType = "registration",
                LastModifiedBy = writer,
                Data = new Dictionary<string, object>
                {
                    ["Files"] = new List<object> { first.Id.ToString(), $"/api/files/{second.Id}" },
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
    public async Task A_queued_email_run_attaches_the_files_its_entry_names()
    {
        var first = await UploadPdfAsync(name: "one.pdf");
        var second = await UploadPdfAsync(name: "two.pdf");
        var writer = await UserAsync(admin: true);
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
                LastModifiedBy = writer,
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
        var runner = new WorkflowRunner(
            _factory.Services,
            _factory.Services.GetRequiredService<Microsoft.Extensions.Logging.ILogger<WorkflowRunner>>(),
            _factory.Services.GetRequiredService<IConfiguration>());

        List<RecordingEmailService.Sent> sent = [];
        for (var i = 0; i < 100 && sent.Count == 0; i++)
        {
            await runner.RunOnceAsync(TestContext.Current.CancellationToken);
            sent = SentTo(to);
            if (sent.Count == 0) await Task.Delay(100, TestContext.Current.CancellationToken);
        }

        sent.Should().ContainSingle("the queued run sends exactly one email; {0}", await WhyNotSentAsync(store, runId));
        sent[0].Attachments.Should().HaveCount(2);
        sent[0].Attachments.Select(a => a.FileName).Should().Equal("one.pdf", "two.pdf");
        sent[0].Attachments[0].Content.Should().Equal(first.Bytes);
        sent[0].Attachments[1].Content.Should().Equal(second.Bytes);
    }

    private static async Task<string> WhyNotSentAsync(IDocumentStore store, Guid runId)
    {
        await using var query = store.QuerySession();
        var run = await query.LoadAsync<WorkflowRun>(runId, TestContext.Current.CancellationToken);

        return run is null
            ? "the run is gone"
            : $"run {run.Status}; " + string.Join("; ", run.Actions.Select(a =>
                $"action {a.Ordinal} {a.Status}, attempts {a.Attempts}, error '{a.Error}', next {a.NextAttemptAt:O}"));
    }
}
