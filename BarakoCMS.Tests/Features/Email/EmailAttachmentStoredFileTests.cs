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
/// an uploaded PDF is attached for the tenant that holds it and for no other.
/// </summary>
[Collection("Sequential")]
public class EmailAttachmentStoredFileTests
{
    private readonly IntegrationTestFixture _factory;
    private readonly HttpClient _client;

    public EmailAttachmentStoredFileTests(IntegrationTestFixture factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private sealed record UploadResponse(Guid Id, string FileName, string ContentType, long Size, bool IsPublic, string? PublicUrl);

    private async Task<string> AdminTokenAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var role = await session.Query<Role>().FirstOrDefaultAsync(r => r.Name == "SuperAdmin", TestContext.Current.CancellationToken)
                   ?? new Role { Id = barakoCMS.Data.DataSeeder.SuperAdminRoleId, Name = "SuperAdmin", Permissions = new() };
        session.Store(role);
        var userId = Guid.NewGuid();
        session.Store(new User { Id = userId, Username = $"admin-{userId}", Email = $"admin-{userId}@example.com", RoleIds = new() { role.Id } });
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        return _factory.CreateToken(new[] { "SuperAdmin" }, userId.ToString());
    }

    /// <summary>Uploads a private PDF into the default tenant, the way an editor does.</summary>
    private async Task<Guid> UploadPdfAsync(byte[] bytes, string name = "receipt.pdf")
    {
        var token = await AdminTokenAsync();

        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        form.Add(file, "file", name);
        form.Add(new StringContent("false"), "isPublic");

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/files") { Content = form };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var response = await _client.SendAsync(request, TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        var body = await response.Content.ReadFromJsonAsync<UploadResponse>(TestContext.Current.CancellationToken);
        return body!.Id;
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

    private static Dictionary<string, string> Mail(string to, Content entry) =>
        // The runner's own step: the template is filled from the entry before the action sees it.
        ActionParameters.Resolve("Email", new Dictionary<string, string>
        {
            ["To"] = to,
            ["Subject"] = "Your receipt",
            ["Body"] = "<p>Attached.</p>",
            ["Attachments"] = "{{data.Receipt}}",
        }, entry);

    private static Content Registration(Guid receipt) => new()
    {
        Id = Guid.NewGuid(),
        ContentType = "registration",
        Data = new Dictionary<string, object> { ["Receipt"] = receipt.ToString() },
    };

    private static IWorkflowAction EmailActionOf(IServiceScope scope)
    {
        var emails = scope.ServiceProvider.GetServices<IWorkflowAction>().Where(a => a.Type == "Email").ToList();
        emails.Should().HaveCount(1, "the host registers one Email action, and it is the real one");
        return emails[0];
    }

    [Fact]
    public async Task A_stored_pdf_is_read_through_the_core_seam_in_its_own_tenant_only()
    {
        var bytes = FileSamples.Pdf();
        var id = await UploadPdfAsync(bytes);

        using (var scope = _factory.Services.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IFileStore>();

            var info = await store.FindAsync(id, TestContext.Current.CancellationToken);
            info.Should().NotBeNull();
            info!.Id.Should().Be(id);
            info.FileName.Should().Be("receipt.pdf");
            info.ContentType.Should().Be("application/pdf");
            info.Size.Should().Be(bytes.Length);

            var stream = await store.OpenReadAsync(id, TestContext.Current.CancellationToken);
            stream.Should().NotBeNull();
            (await ReadAllAsync(stream!)).Should().Equal(bytes);
        }

        using (var other = _factory.Services.CreateScopeForTenant("attach-seam-other"))
        {
            var store = other.ServiceProvider.GetRequiredService<IFileStore>();

            (await store.FindAsync(id, TestContext.Current.CancellationToken)).Should().BeNull();
            (await store.OpenReadAsync(id, TestContext.Current.CancellationToken)).Should().BeNull();
        }
    }

    [Fact]
    public async Task A_cached_resize_is_not_a_file_the_seam_hands_out()
    {
        var parent = await UploadPdfAsync(FileSamples.Pdf());
        var variant = new StoredFile
        {
            FileName = "receipt.pdf",
            ContentType = "application/pdf",
            StorageKey = $"{Guid.NewGuid():N}.pdf",
            ParentFileId = parent,
            VariantWidth = 400,
        };

        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Store(variant);
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);

        var store = scope.ServiceProvider.GetRequiredService<IFileStore>();

        (await store.FindAsync(parent, TestContext.Current.CancellationToken)).Should().NotBeNull("the control: its original is found");
        (await store.FindAsync(variant.Id, TestContext.Current.CancellationToken)).Should().BeNull();
        (await store.OpenReadAsync(variant.Id, TestContext.Current.CancellationToken)).Should().BeNull();
    }

    [Fact]
    public async Task The_registered_email_action_attaches_a_stored_pdf_the_entry_names()
    {
        var bytes = FileSamples.Pdf();
        var id = await UploadPdfAsync(bytes);
        var to = $"attach-{Guid.NewGuid():N}@example.com";
        var entry = Registration(id);

        using var scope = _factory.Services.CreateScope();
        var result = await EmailActionOf(scope).RunAsync(Mail(to, entry), entry, TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeTrue(result.Error ?? string.Empty);

        var sent = _factory.Email.Messages.Where(m => m.To == to).ToList();
        sent.Should().HaveCount(1);
        sent[0].Attachments.Should().HaveCount(1);
        sent[0].Attachments[0].FileName.Should().Be("receipt.pdf");
        sent[0].Attachments[0].ContentType.Should().Be("application/pdf");
        sent[0].Attachments[0].Content.Should().Equal(bytes);
    }

    /// <summary>
    /// The entry names the file, so the entry rule is satisfied. What refuses it is the tenant: the
    /// file is another tenant's, and the run's scope cannot load it.
    /// </summary>
    [Fact]
    public async Task A_file_of_another_tenant_is_refused_even_when_the_entry_names_it()
    {
        var id = await UploadPdfAsync(FileSamples.Pdf());
        var to = $"attach-{Guid.NewGuid():N}@example.com";
        var entry = Registration(id);

        using (var other = _factory.Services.CreateScopeForTenant("attach-action-other"))
        {
            var refused = await EmailActionOf(other).RunAsync(Mail(to, entry), entry, TestContext.Current.CancellationToken);

            refused.Succeeded.Should().BeFalse();
            refused.Retryable.Should().BeFalse();
            refused.Error.Should().Contain("not a stored file of this tenant");
        }

        _factory.Email.Messages.Where(m => m.To == to).Should().BeEmpty();

        // The control: the same entry and parameters in the file's own tenant send it.
        using (var own = _factory.Services.CreateScope())
        {
            var allowed = await EmailActionOf(own).RunAsync(Mail(to, entry), entry, TestContext.Current.CancellationToken);
            allowed.Succeeded.Should().BeTrue(allowed.Error ?? string.Empty);
        }

        _factory.Email.Messages.Where(m => m.To == to).Should().HaveCount(1);
    }
}
