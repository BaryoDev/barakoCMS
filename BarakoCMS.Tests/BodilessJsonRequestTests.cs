using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// A request with no body is not a malformed JSON body, whatever Content-Type it declares.
/// </summary>
/// <remarks>
/// FastEndpoints decides to read a JSON body from the Content-Type header alone, so a GET sent by a
/// client that sets <c>Content-Type: application/json</c> on every call reached the serializer with
/// zero bytes and came back 400 naming <c>serializerErrors</c>. See issue #681.
/// </remarks>
[Collection("Sequential")]
public class BodilessJsonRequestTests
{
    private readonly IntegrationTestFixture _factory;
    private readonly HttpClient _client;

    public BodilessJsonRequestTests(IntegrationTestFixture factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task A_get_with_a_json_content_type_and_no_body_is_answered_like_one_without()
    {
        await AuthenticateAsync();
        var contentId = await CreateContentAsync();

        var plain = await _client.GetAsync($"/api/contents/{contentId}");
        plain.StatusCode.Should().Be(HttpStatusCode.OK, "the control request without the header has to work");

        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/contents/{contentId}")
        {
            Content = EmptyJsonContent(),
        };
        var withHeader = await _client.SendAsync(request);

        withHeader.StatusCode.Should().Be(HttpStatusCode.OK,
            "the header alone must not change the outcome, got: {0}", await withHeader.Content.ReadAsStringAsync());
        (await withHeader.Content.ReadAsStringAsync()).Should().Be(await plain.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_delete_with_a_json_content_type_and_no_body_binds_its_route()
    {
        await AuthenticateAsync();
        var contentId = await CreateContentAsync();

        using var request = new HttpRequestMessage(HttpMethod.Delete, $"/api/contents/{contentId}/erase")
        {
            Content = EmptyJsonContent(),
        };
        var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_post_with_a_malformed_json_body_is_still_refused()
    {
        await AuthenticateAsync();

        var response = await _client.PostAsync("/api/contents",
            new StringContent("{\"contentType\":", Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("open JSON object",
            "the serializer's own message has to survive, since it is what tells a caller where the body broke");
    }

    [Fact]
    public async Task A_post_with_a_json_content_type_and_no_body_is_still_refused()
    {
        await AuthenticateAsync();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/contents") { Content = EmptyJsonContent() };
        var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "a POST is expected to carry a body, so an empty one is still the caller's mistake");
    }

    private static ByteArrayContent EmptyJsonContent()
    {
        var content = new ByteArrayContent([]);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        return content;
    }

    private async Task<Guid> CreateContentAsync()
    {
        var typeName = "bodiless-probe-" + Guid.NewGuid().ToString("n")[..8];
        var typeResponse = await _client.PostAsJsonAsync("/api/content-types", new
        {
            name = typeName,
            displayName = "Bodiless Probe",
            fields = new[] { new { name = "Title", type = "Text" } },
        });
        typeResponse.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}",
            typeResponse.StatusCode, await typeResponse.Content.ReadAsStringAsync());

        var created = await _client.PostAsJsonAsync("/api/contents", new
        {
            contentType = typeName,
            data = new Dictionary<string, object> { ["Title"] = "probe" },
            status = 1,
        });
        created.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}",
            created.StatusCode, await created.Content.ReadAsStringAsync());

        using var document = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("id").GetGuid();
    }

    private async Task AuthenticateAsync()
    {
        var roles = new[] { "Admin", "SuperAdmin" };
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();

        var roleIds = new List<Guid>();
        foreach (var roleName in roles)
        {
            var role = await session.Query<barakoCMS.Models.Role>().FirstOrDefaultAsync(r => r.Name == roleName);
            if (role is null)
            {
                role = new barakoCMS.Models.Role { Id = Guid.NewGuid(), Name = roleName };
                session.Store(role);
            }

            roleIds.Add(role.Id);
        }

        var userId = Guid.NewGuid();
        session.Store(new barakoCMS.Models.User
        {
            Id = userId,
            Username = $"bodiless-{userId:n}",
            Email = $"bodiless-{userId:n}@example.com",
            RoleIds = roleIds,
        });
        await session.SaveChangesAsync();

        _client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer", _factory.CreateToken(roles: roles, userId: userId.ToString()));
    }
}
