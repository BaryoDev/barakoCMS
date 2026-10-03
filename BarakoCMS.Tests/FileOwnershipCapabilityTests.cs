using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using barakoCMS.Infrastructure.Auth;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using FileCapabilities = BarakoCMS.Files.FileCapabilities;

namespace BarakoCMS.Tests;

/// <summary>
/// Who reads or deletes a private file somebody else uploaded is decided by the
/// <c>manage_all_files</c> capability, read from the caller's stored roles, and not by the role
/// names Admin and SuperAdmin in the token (issue #886).
/// </summary>
/// <remarks>
/// Real HTTP against the Files routes. Every caller is a stored user, and each file is uploaded by a
/// stored SuperAdmin who is never the caller under test, so the uploader branch of the rule cannot
/// be what answers.
/// </remarks>
[Collection("Sequential")]
public class FileOwnershipCapabilityTests
{
    private readonly IntegrationTestFixture _factory;
    private readonly HttpClient _client;

    public FileOwnershipCapabilityTests(IntegrationTestFixture factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_role_of_any_name_holding_manage_all_files_downloads_another_users_private_file()
    {
        var bytes = FileSamples.Png(8, 8, 6);
        var id = await UploadPrivateAsync(await OwnerTokenAsync(), bytes);

        var siteManager = await HolderAsync("Site Manager", FileCapabilities.ManageAllFiles);
        var download = await SendAsync(_client, HttpMethod.Get, $"/api/files/{id}", siteManager);

        download.StatusCode.Should().Be(HttpStatusCode.OK, "the role holds manage_all_files, whatever it is called");
        (await download.Content.ReadAsByteArrayAsync(Ct)).Should().Equal(bytes);

        // The control: the capability is what opened it. upload_files on its own still does not.
        var mediaEditor = await HolderAsync("Media Editor", FileCapabilities.UploadFiles);
        (await SendAsync(_client, HttpMethod.Get, $"/api/files/{id}", mediaEditor))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task With_upload_files_as_well_it_deletes_another_users_file()
    {
        var owner = await OwnerTokenAsync();
        var id = await UploadPrivateAsync(owner, FileSamples.Png(8, 8, 7));

        var siteManager = await HolderAsync(
            "Site Manager", FileCapabilities.UploadFiles, FileCapabilities.ManageAllFiles);
        var delete = await SendAsync(_client, HttpMethod.Delete, $"/api/files/{id}", siteManager);

        delete.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await SendAsync(_client, HttpMethod.Get, $"/api/files/{id}/meta", owner))
            .StatusCode.Should().Be(HttpStatusCode.NotFound, "the file is gone");
    }

    /// <summary>
    /// The delete route's gate is unchanged: it asks for <c>upload_files</c> before anything about
    /// the file, so the new capability alone destroys nothing.
    /// </summary>
    [Fact]
    public async Task Manage_all_files_alone_does_not_pass_the_delete_gate()
    {
        var owner = await OwnerTokenAsync();
        var id = await UploadPrivateAsync(owner, FileSamples.Png(8, 8, 8));

        var reader = await HolderAsync("File Reader", FileCapabilities.ManageAllFiles);
        var delete = await SendAsync(_client, HttpMethod.Delete, $"/api/files/{id}", reader);

        delete.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await SendAsync(_client, HttpMethod.Get, $"/api/files/{id}/meta", owner))
            .StatusCode.Should().Be(HttpStatusCode.OK, "the refused delete removed nothing");
    }

    /// <summary>
    /// A token that says Admin and SuperAdmin, for an account whose stored roles carry nothing, is
    /// refused. The names used to be the whole override.
    /// </summary>
    [Fact]
    public async Task A_role_name_in_the_token_opens_no_file_the_stored_roles_do_not()
    {
        var owner = await OwnerTokenAsync();
        var bytes = FileSamples.Png(8, 8, 9);
        var id = await UploadPrivateAsync(owner, bytes);

        var named = await StoredCallerAsync(tokenRoles: ["Admin", "SuperAdmin"]);
        var download = await SendAsync(_client, HttpMethod.Get, $"/api/files/{id}", named);

        download.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "the account holds no role, so the names in its token decide nothing");

        // The control: the file is there, and its uploader still reads it.
        var own = await SendAsync(_client, HttpMethod.Get, $"/api/files/{id}", owner);
        own.StatusCode.Should().Be(HttpStatusCode.OK);
        (await own.Content.ReadAsByteArrayAsync(Ct)).Should().Equal(bytes);
    }

    /// <summary>
    /// The seeded Admin role reaches what it reached before, through the capability the module's
    /// seed gave it and not through its name.
    /// </summary>
    [Fact]
    public async Task The_seeded_admin_role_still_downloads_and_deletes_another_users_file()
    {
        Role? adminRole;
        using (var scope = _factory.Services.CreateScope())
        {
            adminRole = await scope.ServiceProvider.GetRequiredService<IQuerySession>()
                .LoadAsync<Role>(SystemRoles.AdminRoleId, Ct);
        }

        adminRole.Should().NotBeNull("the fixture seeds the Admin role under its fixed id");
        adminRole!.SystemCapabilities.Should().NotBeEmpty();
        adminRole.SystemCapabilities.Should().Contain(
            new[] { FileCapabilities.UploadFiles, FileCapabilities.ManageAllFiles },
            "the Files module grants both to the seeded Admin role at seed");

        var owner = await OwnerTokenAsync();
        var bytes = FileSamples.Png(8, 8, 10);
        var id = await UploadPrivateAsync(owner, bytes);

        var admin = await StoredCallerAsync(tokenRoles: ["Admin"], SystemRoles.AdminRoleId);

        var download = await SendAsync(_client, HttpMethod.Get, $"/api/files/{id}", admin);
        download.StatusCode.Should().Be(HttpStatusCode.OK);
        (await download.Content.ReadAsByteArrayAsync(Ct)).Should().Equal(bytes);

        (await SendAsync(_client, HttpMethod.Delete, $"/api/files/{id}", admin))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    /// <summary>
    /// With <c>Auth:LegacyRoleFallback</c> on, the role name still opens the file, as it opens every
    /// capability gate on such a host.
    /// </summary>
    [Fact]
    public async Task With_the_legacy_role_fallback_on_the_role_name_still_opens_it()
    {
        var bytes = FileSamples.Png(8, 8, 11);
        var id = await UploadPrivateAsync(await OwnerTokenAsync(), bytes);

        var named = await StoredCallerAsync(tokenRoles: ["Admin"]);

        var onLegacyHost = await SendAsync(LegacyFallbackHost().CreateClient(), HttpMethod.Get, $"/api/files/{id}", named);
        onLegacyHost.StatusCode.Should().Be(HttpStatusCode.OK);
        (await onLegacyHost.Content.ReadAsByteArrayAsync(Ct)).Should().Equal(bytes);

        // The same token on the host with the default setting.
        (await SendAsync(_client, HttpMethod.Get, $"/api/files/{id}", named))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    /// <summary>
    /// No route requires <c>manage_all_files</c> outright, so it reaches the vocabulary only because
    /// the download route names it. Without that a console could not list it, and a host that
    /// refuses unknown capabilities could not put it on a role.
    /// </summary>
    [Fact]
    public void Manage_all_files_is_a_capability_this_instance_knows()
    {
        var vocabulary = _factory.Services.GetRequiredService<CapabilityVocabulary>();

        vocabulary.Entries.Should().NotBeEmpty();
        vocabulary.IsKnown(FileCapabilities.UploadFiles).Should().BeTrue("the control: a gated name is known");
        vocabulary.IsKnown(FileCapabilities.ManageAllFiles).Should().BeTrue();
        vocabulary.Entries.Where(e => e.Name == FileCapabilities.ManageAllFiles).Should().ContainSingle()
            .Which.Source.Should().NotBe(CapabilityVocabulary.CoreSource, "the Files module declares it, not core");
    }

    private static readonly Lock LegacyHostGate = new();
    private static WebApplicationFactory<Program>? _legacyHost;

    /// <summary>One derived host for the class, since each build costs a server. Never disposed.</summary>
    private WebApplicationFactory<Program> LegacyFallbackHost()
    {
        lock (LegacyHostGate)
        {
            return _legacyHost ??= _factory.WithSetting(CapabilityGateProcessor.LegacyRoleFallbackKey, "true");
        }
    }

    /// <summary>A stored SuperAdmin, who uploads the file and is never the caller under test.</summary>
    private Task<string> OwnerTokenAsync() =>
        StoredCallerAsync(tokenRoles: ["SuperAdmin"], SystemRoles.SuperAdminRoleId);

    /// <summary>A stored user whose one role is new, carries these capabilities and has a name of its own.</summary>
    private async Task<string> HolderAsync(string roleName, params string[] capabilities)
    {
        var role = new Role
        {
            Id = Guid.NewGuid(),
            Name = $"{roleName} {Guid.NewGuid():N}",
            SystemCapabilities = capabilities.ToList(),
        };

        using (var scope = _factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Store(role);
            await session.SaveChangesAsync(Ct);
        }

        return await StoredCallerAsync(tokenRoles: [role.Name], role.Id);
    }

    /// <summary>A stored user holding exactly these roles, and a token carrying these role names.</summary>
    private async Task<string> StoredCallerAsync(string[] tokenRoles, params Guid[] roleIds)
    {
        var userId = Guid.NewGuid();

        using (var scope = _factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Store(new User
            {
                Id = userId,
                Username = $"files-{userId:N}",
                Email = $"files-{userId:N}@example.com",
                RoleIds = roleIds.ToList(),
            });
            await session.SaveChangesAsync(Ct);
        }

        return _factory.CreateToken(tokenRoles, userId.ToString());
    }

    private async Task<Guid> UploadPrivateAsync(string token, byte[] bytes)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(file, "file", "pic.png");
        form.Add(new StringContent("false"), "isPublic");

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/files") { Content = form };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await _client.SendAsync(request, Ct);
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));

        var body = await response.Content.ReadFromJsonAsync<UploadResponse>(Ct);
        return body!.Id;
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string path, string token)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request, Ct);
    }

    private sealed record UploadResponse(Guid Id);
}
