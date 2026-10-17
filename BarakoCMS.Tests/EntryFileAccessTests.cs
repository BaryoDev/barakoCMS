using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using barakoCMS.Infrastructure.Auth;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// A file field marked FileFollowsEntry lets anyone who may read the entry download the private
/// file it names, and nothing more: no delete, and no file held in a field that does not follow.
/// </summary>
[Collection("Sequential")]
public class EntryFileAccessTests
{
    private readonly IntegrationTestFixture _factory;
    private readonly HttpClient _client;

    public EntryFileAccessTests(IntegrationTestFixture factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_reader_of_the_entry_downloads_the_private_file_it_holds()
    {
        var (_, reader, _, fileId, _) = await DocumentWithFileAsync(follows: true);
        (await SendAsync(_client, HttpMethod.Get, $"/api/files/{fileId}", reader)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_caller_who_cannot_read_the_entry_still_gets_404()
    {
        var (_, _, outsider, fileId, _) = await DocumentWithFileAsync(follows: true);
        (await SendAsync(_client, HttpMethod.Get, $"/api/files/{fileId}", outsider)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Without_the_flag_nothing_changes()
    {
        var (_, reader, _, fileId, _) = await DocumentWithFileAsync(follows: false);
        (await SendAsync(_client, HttpMethod.Get, $"/api/files/{fileId}", reader)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_reader_of_the_entry_cannot_delete_the_file()
    {
        var (_, reader, _, fileId, _) = await DocumentWithFileAsync(follows: true);
        (await SendAsync(_client, HttpMethod.Delete, $"/api/files/{fileId}", reader)).StatusCode.Should().NotBe(HttpStatusCode.NoContent);
        (await SendAsync(_client, HttpMethod.Get, $"/api/files/{fileId}", reader)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_file_held_by_a_different_field_that_does_not_follow_is_not_opened()
    {
        var (_, reader, _, fileId, _) = await DocumentWithFileAsync(follows: true, holdIn: "Attachment");
        (await SendAsync(_client, HttpMethod.Get, $"/api/files/{fileId}", reader)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_role_whose_readable_fields_omit_the_file_field_gets_404()
    {
        var (_, reader, _, fileId, _) = await DocumentWithFileAsync(follows: true, readable: ["Title"]);
        (await SendAsync(_client, HttpMethod.Get, $"/api/files/{fileId}", reader)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_role_whose_readable_fields_include_the_file_field_downloads()
    {
        var (_, reader, _, fileId, _) = await DocumentWithFileAsync(follows: true, readable: ["Title", "File"]);
        (await SendAsync(_client, HttpMethod.Get, $"/api/files/{fileId}", reader)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_hidden_entry_does_not_open_its_file()
    {
        var (_, reader, _, fileId, _) = await DocumentWithFileAsync(follows: true, sensitivity: SensitivityLevel.Hidden);
        (await SendAsync(_client, HttpMethod.Get, $"/api/files/{fileId}", reader)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_differently_cased_key_and_an_uppercase_id_still_download()
    {
        var (_, reader, _, fileId, _) = await DocumentWithFileAsync(follows: true, lowerKeyUpperId: true);
        (await SendAsync(_client, HttpMethod.Get, $"/api/files/{fileId}", reader)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_reader_of_one_entry_cannot_attach_its_file_to_an_entry_of_their_own()
    {
        var (type, reader, _, fileId, _) = await DocumentWithFileAsync(follows: true, canCreate: true);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/contents")
        {
            Content = JsonContent.Create(new
            {
                contentType = type,
                data = new Dictionary<string, object> { ["Title"] = "Mine", ["Attachment"] = fileId.ToString() },
            }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", reader);
        var response = await _client.SendAsync(request, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task The_store_resolves_the_file_for_display_to_a_reader_but_does_not_offer_it_for_attaching()
    {
        var (_, reader, outsider, fileId, _) = await DocumentWithFileAsync(follows: true);

        using var scope = _factory.Services.CreateScope();
        var files = scope.ServiceProvider.GetRequiredService<barakoCMS.Core.Interfaces.IFileStore>();

        (await files.FindManyAsync([fileId], Principal(reader), Ct)).Should().ContainKey(fileId);
        (await files.FindManyAsync([fileId], Principal(outsider), Ct)).Should().BeEmpty();
        (await files.FindAsync(fileId, Principal(reader), Ct)).Should().BeNull();
    }

    [Fact]
    public async Task A_caller_from_another_tenant_gets_404()
    {
        var (_, _, _, fileId, _) = await DocumentWithFileAsync(follows: true);
        var slug = $"club-{Guid.NewGuid():N}"[..14].ToLowerInvariant();
        var userId = Guid.NewGuid();
        var role = new Role
        {
            Id = Guid.NewGuid(), Name = $"Other {Guid.NewGuid():N}",
            Permissions = [new ContentTypePermission { ContentTypeSlug = "doc", Read = new PermissionRule { Enabled = true } }],
        };
        using (var scope = _factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Store(new Tenant { Id = Guid.NewGuid(), Slug = slug, Name = slug });
            session.Store(role);
            session.Store(new User { Id = userId, Username = $"xt-{userId:N}"[..14], Email = $"xt-{userId:N}@example.com", RoleIds = [role.Id] });
            session.Store(new Membership { Id = Guid.NewGuid(), UserId = userId, TenantSlug = slug, Status = MembershipStatus.Active, RoleIds = [role.Id] });
            await session.SaveChangesAsync(Ct);
        }

        var token = _factory.CreateToken([role.Name], userId.ToString(), new Dictionary<string, string> { ["tenant"] = slug });
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/files/{fileId}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("X-Tenant", slug);
        request.Headers.Add(TestRemoteIpFilter.Header, "10.77.2.9");

        (await _client.SendAsync(request, Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    private static System.Security.Claims.ClaimsPrincipal Principal(string token)
    {
        var jwt = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler().ReadJwtToken(token);
        return new System.Security.Claims.ClaimsPrincipal(
            new System.Security.Claims.ClaimsIdentity(jwt.Claims, "test"));
    }

    private async Task<(string Type, string Reader, string Outsider, Guid FileId, Guid EntryId)> DocumentWithFileAsync(
        bool follows, string holdIn = "File", List<string>? readable = null,
        SensitivityLevel sensitivity = SensitivityLevel.Public, bool lowerKeyUpperId = false, bool canCreate = false)
    {
        var type = "doc" + Guid.NewGuid().ToString("N")[..8];
        var owner = await OwnerTokenAsync();
        var fileId = await UploadPrivateAsync(owner, FileSamples.Png(8, 8, 21));

        var readRole = new Role
        {
            Id = Guid.NewGuid(), Name = $"Readers {Guid.NewGuid():N}",
            Permissions = [new ContentTypePermission
            {
                ContentTypeSlug = type,
                Read = new PermissionRule { Enabled = true, ReadableFields = readable },
                Create = canCreate ? new PermissionRule { Enabled = true } : null,
            }],
        };
        var noRule = new Role { Id = Guid.NewGuid(), Name = $"Nobody {Guid.NewGuid():N}" };
        var entry = new Content
        {
            Id = Guid.NewGuid(), ContentType = type, Status = ContentStatus.Published, Sensitivity = sensitivity,
            Data = new Dictionary<string, object>
            {
                ["Title"] = "Minutes",
                [lowerKeyUpperId ? holdIn.ToLowerInvariant() : holdIn] =
                    lowerKeyUpperId ? fileId.ToString().ToUpperInvariant() : fileId.ToString(),
            },
            CreatedBy = Guid.NewGuid(), LastModifiedBy = Guid.NewGuid(),
        };

        using (var scope = _factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Store(new ContentTypeDefinition
            {
                Id = Guid.NewGuid(), Name = type, DisplayName = type,
                Fields =
                [
                    new FieldDefinition { Name = "Title", DisplayName = "Title", Type = "string" },
                    new FieldDefinition { Name = "File", DisplayName = "File", Type = "file", FileFollowsEntry = follows },
                    new FieldDefinition { Name = "Attachment", DisplayName = "Attachment", Type = "file" },
                ],
            });
            session.Store(readRole, noRule);
            session.Store(entry);
            await session.SaveChangesAsync(Ct);
        }

        var reader = await StoredCallerAsync(tokenRoles: [readRole.Name], readRole.Id);
        var outsider = await StoredCallerAsync(tokenRoles: [noRule.Name], noRule.Id);
        return (type, reader, outsider, fileId, entry.Id);
    }

    private Task<string> OwnerTokenAsync() =>
        StoredCallerAsync(tokenRoles: ["SuperAdmin"], SystemRoles.SuperAdminRoleId);

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
