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
/// Every error the core returns is ProblemDetails, and a status change says what it did.
/// </summary>
/// <remarks>
/// Four error shapes shipped from an API configured for RFC7807: ProblemDetails, a hand-rolled
/// {message}, a hand-rolled {errors: []}, and bodyless. ContentType/Create emitted two of them from
/// one endpoint depending on which check failed. Every consumer then had to write the same
/// shape-probing function, and the admin's had a bug that rendered every validation failure as
/// "[object Object]".
///
/// These assert on the ProblemDetails entry carrying a `reason`, because that is the field the
/// client reads and the one the admin was getting wrong.
/// </remarks>
[Collection("Sequential")]
public class ErrorContractTests
{
    private readonly IntegrationTestFixture _factory;
    private readonly HttpClient _client;

    public ErrorContractTests(IntegrationTestFixture factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    /// <summary>
    /// A content type that fails field validation returns ProblemDetails, not a bare error list.
    /// </summary>
    /// <remarks>
    /// This is the endpoint that emitted two shapes: this path returned {errors: [...]} while a
    /// duplicate name a few lines later threw ProblemDetails.
    /// </remarks>
    [Fact]
    public async Task An_invalid_content_type_returns_problem_details()
    {
        await AuthenticateAsync("Admin", "SuperAdmin");

        var response = await _client.PostAsJsonAsync("/api/content-types", new
        {
            name = "bad-type-" + Guid.NewGuid().ToString("n")[..8],
            displayName = "Bad Type",
            fields = new[] { new { name = "X", type = "NotARealFieldType" } },
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var reasons = await ProblemDetailReasonsAsync(response);
        reasons.Should().NotBeEmpty("a failed validation has to say what was wrong");
    }

    /// <summary>
    /// A duplicate content-type name returns the same shape as a field-validation failure.
    /// </summary>
    [Fact]
    public async Task A_duplicate_content_type_returns_the_same_shape()
    {
        await AuthenticateAsync("Admin", "SuperAdmin");
        var name = "dupe-type-" + Guid.NewGuid().ToString("n")[..8];
        var body = new { name, displayName = "Dupe", fields = new[] { new { name = "Title", type = "Text" } } };

        var first = await _client.PostAsJsonAsync("/api/content-types", body);
        first.IsSuccessStatusCode.Should().BeTrue(
            "the first create has to succeed for the second to be a duplicate, got {0}: {1}",
            first.StatusCode, await first.Content.ReadAsStringAsync());

        var response = await _client.PostAsJsonAsync("/api/content-types", body);

        // 409 rather than the 400 a field-validation failure gets: the request is well formed and
        // conflicts with what is already there. The shape is what this test is about, and it is the
        // same ProblemDetails either way.
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ProblemDetailReasonsAsync(response))
            .Should().Contain(reason => reason.Contains("already exists", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Content that fails its schema's validation returns ProblemDetails.
    /// </summary>
    /// <remarks>
    /// This path used to return <c>{message: "Validation Failed: ..."}</c> with every field error
    /// flattened into one string, so a client could not tell which field was wrong.
    ///
    /// The content type carries a required field and the request omits it. A content type that does
    /// not exist would not do: <c>ContentValidatorService</c> treats an unknown type as loose mode
    /// and allows anything, deliberately, so that request succeeds and proves nothing.
    /// </remarks>
    [Fact]
    public async Task A_failed_content_validation_returns_problem_details()
    {
        await AuthenticateAsync("Admin", "SuperAdmin");

        var typeName = "required-field-" + Guid.NewGuid().ToString("n")[..8];
        var typeResponse = await _client.PostAsJsonAsync("/api/content-types", new
        {
            name = typeName,
            displayName = "Required Field",
            fields = new[] { new { name = "Title", type = "Text", isRequired = true } },
        });
        typeResponse.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}",
            typeResponse.StatusCode, await typeResponse.Content.ReadAsStringAsync());

        var response = await _client.PostAsJsonAsync("/api/contents", new
        {
            contentType = typeName,
            data = new Dictionary<string, object> { ["Unrelated"] = "value" },
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ProblemDetailReasonsAsync(response))
            .Should().Contain(reason => reason.Contains("required", StringComparison.OrdinalIgnoreCase));

        var body = await response.Content.ReadAsStringAsync();
        body.Should().NotContain("Validation Failed:",
            "the hand-rolled flattened {message} shape is what this replaced");
    }

    /// <summary>
    /// A status change with no status named is refused rather than quietly setting Draft.
    /// </summary>
    /// <remarks>
    /// <c>NewStatus</c> was a non-nullable enum, so an absent or misspelled field bound to 0, which
    /// is Draft, and <c>IsInEnum</c> accepted it. A client sending <c>{"status": 1}</c> got back
    /// "Content status changed to Draft" and its content moved to Draft. Found by driving the real
    /// API during the 4.0 upgrade check, not by reading the code.
    /// </remarks>
    [Fact]
    public async Task A_status_change_that_names_no_status_is_refused()
    {
        await AuthenticateAsync("Admin", "SuperAdmin");
        var contentId = await CreateContentAsync();

        var response = await PutRawAsync($"/api/contents/{contentId}/status", """{"status":1}""");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        var content = await session.LoadAsync<barakoCMS.Models.Content>(contentId);
        content!.Status.Should().Be(barakoCMS.Models.ContentStatus.Published,
            "a refused status change must not move the content anywhere");
    }

    /// <summary>
    /// A status change that names a status still works, and reports the status it applied.
    /// </summary>
    /// <remarks>
    /// The positive control for the test above. Without it, making the endpoint refuse everything
    /// would pass.
    /// </remarks>
    [Fact]
    public async Task A_status_change_that_names_a_status_applies_it()
    {
        await AuthenticateAsync("Admin", "SuperAdmin");
        var contentId = await CreateContentAsync();

        var response = await PutRawAsync($"/api/contents/{contentId}/status", """{"newStatus":2}""");

        response.IsSuccessStatusCode.Should().BeTrue();
        (await response.Content.ReadAsStringAsync()).Should().Contain("Archived");

        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        var content = await session.LoadAsync<barakoCMS.Models.Content>(contentId);
        content!.Status.Should().Be(barakoCMS.Models.ContentStatus.Archived);
    }

    private async Task<Guid> CreateContentAsync()
    {
        var typeName = "status-probe-" + Guid.NewGuid().ToString("n")[..8];
        var typeResponse = await _client.PostAsJsonAsync("/api/content-types", new
        {
            name = typeName,
            displayName = "Status Probe",
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
        created.IsSuccessStatusCode.Should().BeTrue();

        using var document = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("id").GetGuid();
    }

    private Task<HttpResponseMessage> PutRawAsync(string url, string json) =>
        _client.PutAsync(url, new StringContent(json, Encoding.UTF8, "application/json"));

    /// <summary>
    /// Pulls the <c>reason</c> off each ProblemDetails entry, which is the field a client reads and
    /// the one the admin was reading wrong.
    /// </summary>
    private static async Task<List<string>> ProblemDetailReasonsAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.TryGetProperty("errors", out var errors)
            .Should().BeTrue("ProblemDetails carries its entries under 'errors'");

        return errors.EnumerateArray()
            .Select(entry => entry.TryGetProperty("reason", out var reason) ? reason.GetString() ?? "" : "")
            .Where(reason => reason.Length > 0)
            .ToList();
    }

    private async Task AuthenticateAsync(params string[] roles)
    {
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
            Username = $"error-contract-{userId:n}",
            Email = $"error-contract-{userId:n}@example.com",
            RoleIds = roleIds,
        });
        await session.SaveChangesAsync();

        _client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer", _factory.CreateToken(roles: roles, userId: userId.ToString()));
    }

    /// <summary>
    /// A failed sign-in is 401 and still says why.
    /// </summary>
    /// <remarks>
    /// It was 400, which standard client middleware classifies as a caller bug rather than an
    /// authentication failure, and which the admin's refresh interceptor keys off the wrong side of.
    ///
    /// The body matters as much as the code. The admin falls back to "Your session has expired" for
    /// a 401 with nothing readable in it, so a 401 that dropped the reason would replace a correct
    /// message with a misleading one on the most visible error in the product.
    /// </remarks>
    [Fact]
    public async Task A_failed_sign_in_is_unauthorized_and_says_why()
    {
        var response = await _client.PostAsJsonAsync("/api/auth/login", new
        {
            username = "no-such-user-" + Guid.NewGuid().ToString("n")[..8],
            password = "wrong-password",
        });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await ProblemDetailReasonsAsync(response))
            .Should().Contain(reason => reason.Contains("Invalid credentials", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The content-type resource answers on both its new name and its deprecated one.
    /// </summary>
    /// <remarks>
    /// Read was at /api/schemas while create and the delivery toggle were at /api/content-types.
    /// Consolidating without keeping the alias would break every existing client on the read path,
    /// so both have to work until 5.0, and both have to be exercised or the alias rots.
    /// </remarks>
    [Theory]
    [InlineData("/api/content-types")]
    [InlineData("/api/schemas")]
    public async Task The_content_type_list_answers_on_both_routes(string url)
    {
        await AuthenticateAsync("Admin", "SuperAdmin");

        var response = await _client.GetAsync(url);

        response.IsSuccessStatusCode.Should().BeTrue("{0} returned {1}", url, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.TryGetProperty("items", out _).Should().BeTrue();
    }
}
