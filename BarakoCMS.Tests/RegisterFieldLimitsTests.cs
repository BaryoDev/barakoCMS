using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// Registration bounds the fields it stores (#271).
/// </summary>
/// <remarks>
/// Username had a minimum and no maximum, and Email had a shape check and no length at all. Both
/// carry a unique btree index on the users document, so an oversized value is not just wasteful: a
/// postgres btree entry cannot exceed roughly 2.7KB, so past that the insert fails and an anonymous
/// endpoint answers 500. Below that it succeeds, and every sign-in afterwards indexes and compares
/// the whole thing.
///
/// The assertions are on the status rather than on the validator, because the validator running at
/// all is half of what is being claimed. A rule on a type FastEndpoints never binds does nothing.
/// </remarks>
[Collection("Sequential")]
public class RegisterFieldLimitsTests
{
    private readonly HttpClient _client;

    public RegisterFieldLimitsTests(IntegrationTestFixture factory)
    {
        _client = factory.CreateClient();
        _client.DefaultRequestHeaders.Remove(TestRemoteIpFilter.Header);
        _client.DefaultRequestHeaders.Add(TestRemoteIpFilter.Header, "203.0.113.71");
    }

    [Fact]
    public async Task An_oversized_username_is_refused()
    {
        var response = await _client.PostAsJsonAsync("/api/auth/register", new
        {
            Username = new string('u', 5000),
            Email = $"long_{Guid.NewGuid():n}@example.com",
            Password = "ValidPassword123!",
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "an unbounded username is stored, uniquely indexed and string-compared on every sign-in, "
          + "and past the btree entry limit the insert fails as a 500 on an anonymous endpoint");
    }

    [Fact]
    public async Task An_oversized_email_is_refused()
    {
        var response = await _client.PostAsJsonAsync("/api/auth/register", new
        {
            Username = $"len{Guid.NewGuid():n}"[..20],
            Email = new string('e', 5000) + "@example.com",
            Password = "ValidPassword123!",
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "Email carries the same unique index as Username, and EmailAddress() checks shape, not size");
    }

    /// <summary>
    /// The control. Without it the two assertions above pass on an endpoint that refuses everything,
    /// which is the shape of gate this repository has shipped before.
    /// </summary>
    [Fact]
    public async Task A_username_and_email_of_ordinary_length_still_register()
    {
        var username = $"ok{Guid.NewGuid():n}";

        var response = await _client.PostAsJsonAsync("/api/auth/register", new
        {
            Username = username,
            Email = $"{username}@example.com",
            Password = "ValidPassword123!",
        });

        response.IsSuccessStatusCode.Should().BeTrue(
            "the limits must bound abuse without refusing a normal account, but this answered {0}",
            response.StatusCode);
    }
}
