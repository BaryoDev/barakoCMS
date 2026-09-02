using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// The two content write endpoints read the UserId claim the same way (#271).
/// </summary>
/// <remarks>
/// Update used Guid.Parse on the claim and Create used Guid.TryParse. Configure() calls
/// Claims("UserId") on both, so a request with no claim never reaches either handler, and this
/// server only ever mints a Guid there. What the difference decided was the answer to a token that
/// carries the claim with something else in it: Create said 400, Update threw a FormatException the
/// exception handler turned into a 500.
///
/// A 500 is not a vulnerability here, it is a lie: the request was malformed, and answering "server
/// error" sends an operator looking at the server. Both endpoints are asserted together, so the next
/// person to add a third write endpoint has the pair to copy rather than the odd one out.
/// </remarks>
[Collection("Sequential")]
public class ContentWriteClaimParsingTests
{
    private readonly IntegrationTestFixture _factory;

    public ContentWriteClaimParsingTests(IntegrationTestFixture factory) => _factory = factory;

    private HttpClient ClientWithUserId(string userId)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", _factory.CreateToken(roles: ["SuperAdmin", "Admin"], userId: userId));
        return client;
    }

    [Fact]
    public async Task Update_refuses_a_token_whose_user_id_is_not_a_guid()
    {
        var response = await ClientWithUserId("not-a-guid").PutAsJsonAsync(
            $"/api/contents/{Guid.NewGuid()}",
            new { Data = new Dictionary<string, object> { ["Title"] = "x" }, Version = 0 });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "the claim is malformed, so this is the caller's error. Guid.Parse threw a "
          + "FormatException here and the request came back as a 500");
    }

    [Fact]
    public async Task Create_refuses_a_token_whose_user_id_is_not_a_guid()
    {
        var response = await ClientWithUserId("not-a-guid").PostAsJsonAsync(
            "/api/contents",
            new { ContentType = "Article", Data = new Dictionary<string, object> { ["Title"] = "x" } });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "Create already parsed defensively, and this pins the answer Update was aligned to");
    }

    /// <summary>
    /// The control. A well-formed UserId for a user that does not exist must get past the parse and
    /// be refused by authorisation instead, so the two assertions above are about the claim's shape
    /// rather than about every request being rejected.
    /// </summary>
    [Fact]
    public async Task A_well_formed_user_id_gets_past_the_parse()
    {
        var response = await ClientWithUserId(Guid.NewGuid().ToString()).PutAsJsonAsync(
            $"/api/contents/{Guid.NewGuid()}",
            new { Data = new Dictionary<string, object> { ["Title"] = "x" }, Version = 0 });

        response.StatusCode.Should().NotBe(HttpStatusCode.BadRequest,
            "a parseable claim is not a validation failure; this one fails later, on the content "
          + "not existing or on authorisation");
    }
}
