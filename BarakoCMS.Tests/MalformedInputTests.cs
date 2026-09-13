using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// Malformed or boundary input is the caller's mistake, so it gets a 4xx, not a 500 carrying a
/// framework or Postgres message. See #648.
/// </summary>
[Collection("Sequential")]
public class MalformedInputTests
{
    private readonly IntegrationTestFixture _factory;
    private readonly HttpClient _client;

    public MalformedInputTests(IntegrationTestFixture factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    /// <summary>
    /// A page number past the last representable offset returns an empty page, not a 500.
    /// </summary>
    /// <remarks>
    /// <c>(Page - 1) * PageSize</c> overflowed int32 into a negative OFFSET, which Postgres refuses
    /// with 2201X. Page is clamped the way PageSize already is, so the request still succeeds. Only
    /// endpoints that page in SQL are listed: one that pages in memory skipped a negative count as
    /// zero and passed before the fix too.
    /// </remarks>
    [Theory]
    [InlineData("/api/audit")]
    [InlineData("/api/users")]
    [InlineData("/api/files")]
    [InlineData("/api/roles")]
    public async Task A_page_past_the_int32_offset_returns_an_empty_page(string url)
    {
        await AuthenticateAsSuperAdminAsync();

        var response = await _client.GetAsync($"{url}?page=2147483647&pageSize=100");
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        body.Should().NotContain("2201X");
        using var document = JsonDocument.Parse(body);
        document.RootElement.GetProperty("items").GetArrayLength().Should().Be(0);
    }

    /// <summary>
    /// The positive control for the clamp: an ordinary page still reports the page it was asked for.
    /// </summary>
    [Fact]
    public async Task An_ordinary_page_number_is_not_clamped()
    {
        await AuthenticateAsSuperAdminAsync();

        var response = await _client.GetAsync("/api/roles?page=3&pageSize=5");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("page").GetInt32().Should().Be(3);
    }

    /// <summary>
    /// A NUL character in a username or email is refused by validation before it reaches Postgres.
    /// </summary>
    /// <remarks>
    /// Postgres text cannot hold U+0000, so the value passed the length rule and failed at the
    /// database with 22P05 or 22021, as a 500, on an anonymous endpoint. The body carries the JSON
    /// escape, so the NUL only exists once the server has decoded it.
    /// </remarks>
    [Theory]
    [InlineData("""{"username":"nul\u0000name","email":"nul-user@example.com","password":"a-long-enough-password-1A!"}""")]
    [InlineData("""{"username":"nulemail","email":"nul\u0000user@example.com","password":"a-long-enough-password-1A!"}""")]
    public async Task A_nul_character_in_registration_is_a_validation_error(string json)
    {
        _client.DefaultRequestHeaders.Add(
            TestRemoteIpFilter.Header, $"2001:db8:648::{Random.Shared.Next(1, 0xffff):x}");

        var response = await _client.PostAsync(
            "/api/auth/register", new StringContent(json, Encoding.UTF8, "application/json"));
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, body);
        body.Should().NotContain("22P05").And.NotContain("22021");
    }

    /// <summary>
    /// A NUL that reaches Postgres on an endpoint with no rule against it is 400, not 500.
    /// </summary>
    /// <remarks>
    /// Sign-in has no reason to validate the characters of a username, and should not need one:
    /// the lookup fails in Postgres, and that is the caller's input whichever endpoint carried it.
    /// </remarks>
    [Fact]
    public async Task A_nul_character_that_reaches_the_database_is_a_bad_request()
    {
        _client.DefaultRequestHeaders.Add(
            TestRemoteIpFilter.Header, $"2001:db8:649::{Random.Shared.Next(1, 0xffff):x}");

        var response = await _client.PostAsync("/api/auth/login", new StringContent(
            """{"username":"nul\u0000login","password":"whatever-password"}""",
            Encoding.UTF8, "application/json"));
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, body);
        body.Should().NotContain("22021").And.NotContain("22P05");
    }

    /// <summary>
    /// A body over the configured limit is 413, and the response does not echo the server's reason.
    /// </summary>
    /// <remarks>
    /// On a real Kestrel host, because the in-memory test server does not enforce
    /// <c>MaxRequestBodySize</c>: the same request there is read in full and succeeds. The request
    /// asks for 100-continue so Kestrel can refuse on Content-Length before the body is sent;
    /// otherwise it answers and closes while the client is still writing, and the client sees a
    /// broken pipe instead of the status.
    /// </remarks>
    [Fact]
    public async Task A_body_over_the_limit_is_413()
    {
        using var host = _factory.WithWebHostBuilder(_ => { });
        host.UseKestrel(0);
        host.StartServer();
        using var client = host.CreateClient();

        var oversized = new string('a', (int)(10L * 1024 * 1024) + 1024);
        var json = $$$"""{"contentType":"too-big","data":{"Title":"{{{oversized}}}"}}""";

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/contents")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = await SuperAdminHeaderAsync();
        request.Headers.ExpectContinue = true;

        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge, body);
        body.Should().NotContain("Request body too large");
    }

    /// <summary>
    /// A multipart body the form reader cannot parse is 400, not 500 with the parser's message.
    /// </summary>
    [Theory]
    [InlineData("multipart/form-data", "--x\r\nContent-Disposition: form-data; name=\"file\"; filename=\"a.png\"\r\n\r\nabc")]
    [InlineData("multipart/form-data; boundary=x", "--x\r\nContent-Disposition: form-data; name=\"file\"; filename=\"a.png\"\r\n\r\nabc")]
    public async Task A_malformed_multipart_upload_is_400(string contentType, string rawBody)
    {
        await AuthenticateAsSuperAdminAsync();

        var content = new ByteArrayContent(Encoding.ASCII.GetBytes(rawBody));
        content.Headers.TryAddWithoutValidation("Content-Type", contentType);
        var response = await _client.PostAsync("/api/files", content);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, body);
        body.Should().NotContain("boundary").And.NotContain("Unexpected end of Stream");
    }

    /// <summary>
    /// A number too large for any finite double is refused as 400, not stored as Infinity.
    /// </summary>
    /// <remarks>
    /// <c>1e400</c> read as <c>double.PositiveInfinity</c>, passed the money check, and then threw
    /// when the document was serialised, since JSON has no way to write Infinity.
    /// </remarks>
    [Fact]
    public async Task A_non_finite_money_value_is_a_bad_request()
    {
        await AuthenticateAsSuperAdminAsync();
        var typeName = "money-probe-" + Guid.NewGuid().ToString("n")[..8];
        var typeResponse = await _client.PostAsJsonAsync("/api/content-types", new
        {
            name = typeName,
            displayName = "Money Probe",
            fields = new[] { new { name = "Price", type = "money" } },
        });
        typeResponse.IsSuccessStatusCode.Should().BeTrue(await typeResponse.Content.ReadAsStringAsync());

        var response = await _client.PostAsync("/api/contents", new StringContent(
            $$"""{"contentType":"{{typeName}}","data":{"Price":1e400},"status":1}""",
            Encoding.UTF8, "application/json"));
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, body);
    }

    private async Task AuthenticateAsSuperAdminAsync() =>
        _client.DefaultRequestHeaders.Authorization = await SuperAdminHeaderAsync();

    private async Task<AuthenticationHeaderValue> SuperAdminHeaderAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var role = await session.Query<barakoCMS.Models.Role>().FirstOrDefaultAsync(r => r.Name == "SuperAdmin")
                   ?? new barakoCMS.Models.Role
                   {
                       Id = barakoCMS.Data.DataSeeder.SuperAdminRoleId, Name = "SuperAdmin", Permissions = new(),
                   };
        session.Store(role);
        var userId = Guid.NewGuid();
        session.Store(new barakoCMS.Models.User
        {
            Id = userId,
            Username = $"malformed-{userId:n}",
            Email = $"malformed-{userId:n}@example.com",
            RoleIds = new() { role.Id },
        });
        await session.SaveChangesAsync();

        return new AuthenticationHeaderValue(
            "Bearer", _factory.CreateToken(new[] { "SuperAdmin" }, userId.ToString()));
    }
}
