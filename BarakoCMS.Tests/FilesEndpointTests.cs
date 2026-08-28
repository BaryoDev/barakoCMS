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
}
