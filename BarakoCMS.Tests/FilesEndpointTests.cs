using Xunit;
using FluentAssertions;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using barakoCMS.Models;
using Marten;
using Microsoft.Extensions.DependencyInjection;

namespace BarakoCMS.Tests;

/// <summary>
/// The Files endpoints end to end (Postgres storage; no S3 configured), with the fail-closed public
/// download as the security-relevant case: a public upload is anonymously readable, a private one is
/// not (404, indistinguishable from missing), and a random id is 404.
/// </summary>
[Collection("Sequential")]
public class FilesEndpointTests
{
    private readonly IntegrationTestFixture _factory;
    private readonly HttpClient _client;

    public FilesEndpointTests(IntegrationTestFixture factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private async Task<string> AdminTokenAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var s = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var role = await s.Query<Role>().FirstOrDefaultAsync(r => r.Name == "SuperAdmin")
                   ?? new Role { Id = barakoCMS.Data.DataSeeder.SuperAdminRoleId, Name = "SuperAdmin", Permissions = new() };
        s.Store(role);
        var userId = Guid.NewGuid();
        s.Store(new User { Id = userId, Username = $"admin-{userId}", Email = $"admin-{userId}@example.com", RoleIds = new() { role.Id } });
        await s.SaveChangesAsync();
        return _factory.CreateToken(new[] { "SuperAdmin" }, userId.ToString());
    }

    private async Task<Guid> UploadAsync(string token, bool isPublic, byte[] bytes)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(file, "file", "pic.png");
        form.Add(new StringContent(isPublic ? "true" : "false"), "isPublic");

        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/files") { Content = form };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var res = await _client.SendAsync(req);
        res.StatusCode.Should().Be(HttpStatusCode.Created, because: await res.Content.ReadAsStringAsync());
        var body = await res.Content.ReadFromJsonAsync<UploadResponse>();
        return body!.Id;
    }

    [Fact]
    public async Task PublicFile_IsAnonymouslyReadable()
    {
        var token = await AdminTokenAsync();
        var bytes = new byte[] { 1, 2, 3, 4, 5 };
        var id = await UploadAsync(token, isPublic: true, bytes);

        var res = await _client.GetAsync($"/api/public/files/{id}"); /* no auth */
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        (await res.Content.ReadAsByteArrayAsync()).Should().Equal(bytes);
        res.Headers.CacheControl?.Public.Should().BeTrue();
    }

    [Fact]
    public async Task PrivateFile_Is404OnPublicEndpoint()
    {
        var token = await AdminTokenAsync();
        var id = await UploadAsync(token, isPublic: false, new byte[] { 9, 9, 9 });

        var res = await _client.GetAsync($"/api/public/files/{id}"); /* no auth */
        res.StatusCode.Should().Be(HttpStatusCode.NotFound, "a private file must not be publicly readable");
    }

    [Fact]
    public async Task PrivateFile_IsReadableWithAuth()
    {
        var token = await AdminTokenAsync();
        var bytes = new byte[] { 7, 7 };
        var id = await UploadAsync(token, isPublic: false, bytes);

        using var req = new HttpRequestMessage(HttpMethod.Get, $"/api/files/{id}");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var res = await _client.SendAsync(req);
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        (await res.Content.ReadAsByteArrayAsync()).Should().Equal(bytes);
    }

    [Fact]
    public async Task PublicEndpoint_RandomId_Is404()
    {
        var res = await _client.GetAsync($"/api/public/files/{Guid.NewGuid()}");
        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Upload_RequiresAuth()
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(new byte[] { 1 });
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(file, "file", "pic.png");
        var res = await _client.PostAsync("/api/files", form); /* no auth */
        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Upload_RejectsSvg_ToPreventStoredXss()
    {
        var token = await AdminTokenAsync();
        using var form = new MultipartFormDataContent();
        var svg = System.Text.Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>");
        var file = new ByteArrayContent(svg);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/svg+xml");
        form.Add(file, "file", "x.svg");
        form.Add(new StringContent("true"), "isPublic");

        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/files") { Content = form };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var res = await _client.SendAsync(req);
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest, "SVG can carry script and is excluded");
    }

    private sealed record UploadResponse(Guid Id, string FileName, string ContentType, long Size, bool IsPublic, string? PublicUrl);

    /// <summary>
    /// A signed-in account cannot read a file someone else uploaded.
    /// </summary>
    /// <remarks>
    /// Both endpoints had authentication and neither had authorization, so any account, including
    /// the User role every self-registration gets, could read any private file in the tenant given
    /// its id. Ids are GUIDs so this needed a leaked or logged id rather than a scan, which lowers
    /// the severity without making the check optional.
    ///
    /// The refusal is 404 rather than 403, matching PublicDownload: a 403 confirms the id exists,
    /// which turns a leaked id into a probe for what else is there.
    /// </remarks>
    [Fact]
    public async Task PrivateFile_IsNotReadableByAnotherUser()
    {
        var owner = await AdminTokenAsync();
        var id = await UploadAsync(owner, isPublic: false, new byte[] { 9, 9, 9 });

        var stranger = await UserTokenAsync();
        using var req = new HttpRequestMessage(HttpMethod.Get, $"/api/files/{id}");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", stranger);
        var res = await _client.SendAsync(req);

        res.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "a signed-in stranger must not read a file they did not upload, and must not learn it exists");
    }

    /// <summary>
    /// The positive control: the uploader still gets their own file back.
    /// </summary>
    /// <remarks>
    /// Without this, refusing every download would pass the test above.
    /// </remarks>
    [Fact]
    public async Task PrivateFile_IsReadableByItsUploader()
    {
        var owner = await AdminTokenAsync();
        var bytes = new byte[] { 4, 5, 6 };
        var id = await UploadAsync(owner, isPublic: false, bytes);

        using var req = new HttpRequestMessage(HttpMethod.Get, $"/api/files/{id}");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", owner);
        var res = await _client.SendAsync(req);

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        (await res.Content.ReadAsByteArrayAsync()).Should().Equal(bytes);
    }

    /// <summary>
    /// A User-role account cannot upload.
    /// </summary>
    /// <remarks>
    /// Upload carried no role gate at all, so every self-registered account could store 10 MB per
    /// call into the tenant and set isPublic, producing an anonymously readable URL on the
    /// deployment's own domain.
    /// </remarks>
    [Fact]
    public async Task Upload_IsRefusedToAUserRoleAccount()
    {
        var token = await UserTokenAsync();

        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(new byte[] { 1, 2, 3 });
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(file, "file", "pic.png");
        form.Add(new StringContent("true"), "isPublic");

        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/files") { Content = form };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var res = await _client.SendAsync(req);

        res.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "upload is a write, and every other write in the module set carries a role gate");
    }

    /// <summary>A signed-in account with only the User role, which is what self-registration grants.</summary>
    private async Task<string> UserTokenAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var s = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var role = await s.Query<Role>().FirstOrDefaultAsync(r => r.Name == "User")
                   ?? new Role { Id = Guid.NewGuid(), Name = "User", Permissions = new() };
        s.Store(role);
        var userId = Guid.NewGuid();
        s.Store(new User { Id = userId, Username = $"plain-{userId}", Email = $"plain-{userId}@example.com", RoleIds = new() { role.Id } });
        await s.SaveChangesAsync();
        return _factory.CreateToken(new[] { "User" }, userId.ToString());
    }
}
