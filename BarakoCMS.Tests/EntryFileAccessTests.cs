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

    private async Task<(string Type, string Reader, string Outsider, Guid FileId, Guid EntryId)> DocumentWithFileAsync(
        bool follows, string holdIn = "File")
    {
        var type = "doc" + Guid.NewGuid().ToString("N")[..8];
        var owner = await OwnerTokenAsync();
        var fileId = await UploadPrivateAsync(owner, FileSamples.Png(8, 8, 21));

        var readRole = new Role
        {
            Id = Guid.NewGuid(), Name = $"Readers {Guid.NewGuid():N}",
            Permissions = [new ContentTypePermission { ContentTypeSlug = type, Read = new PermissionRule { Enabled = true } }],
        };
        var noRule = new Role { Id = Guid.NewGuid(), Name = $"Nobody {Guid.NewGuid():N}" };
        var entry = new Content
        {
            Id = Guid.NewGuid(), ContentType = type, Status = ContentStatus.Published,
            Data = new Dictionary<string, object> { ["Title"] = "Minutes", [holdIn] = fileId.ToString() },
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
