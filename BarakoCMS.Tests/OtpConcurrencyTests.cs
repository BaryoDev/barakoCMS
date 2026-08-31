using System.Net;
using System.Net.Http.Json;
using barakoCMS.Features.Auth.Otp;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Hosting;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// One sign-in code, two requests arriving together.
/// </summary>
/// <remarks>
/// <c>OtpCode</c> carries optimistic concurrency so both cannot consume it. That is the point, but
/// it moves the problem: the loser's <c>SaveChangesAsync</c> throws, and an uncaught
/// <c>ConcurrencyException</c> turns an already-used code into a 500. These pin both halves, that
/// exactly one caller is let in and that the other is refused rather than erroring.
/// </remarks>
[Collection("Sequential")]
public class OtpConcurrencyTests
{
    private readonly IntegrationTestFixture _factory;

    /*
     * The "auth" rate limit is 5 requests per 15 minutes, partitioned by client IP, and every test
     * in the suite otherwise shares one. These tests spend four of those five, so without their own
     * partition they pass and leave the next auth test to fail on a 429 that has nothing to do with
     * it. TestRemoteIpFilter gives each test a private IP, and so a private bucket.
     */
    private readonly string _ip = $"203.0.113.{Random.Shared.Next(2, 250)}";

    /*
     * Built once, not per request. Calling WithWebHostBuilder for each call stands up a fresh host
     * every time, which is slow enough that two "concurrent" requests no longer overlap, and the
     * racing test below then passes against the unfixed endpoint. One client, reused.
     */
    private readonly HttpClient _client;

    public OtpConcurrencyTests(IntegrationTestFixture factory)
    {
        _factory = factory;
        _client = factory
            .WithWebHostBuilder(b => b.ConfigureServices(s =>
                s.AddSingleton<Microsoft.AspNetCore.Hosting.IStartupFilter, TestRemoteIpFilter>()))
            .CreateClient();
    }

    private async Task<string> SeedUserWithCodeAsync(string plainCode)
    {
        var email = $"otp-race-{Guid.NewGuid():N}@example.com";
        using var scope = _factory.Services.CreateScope();
        var s = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        s.Store(new User
        {
            Id = Guid.NewGuid(),
            Email = email,
            Username = email,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(Guid.NewGuid().ToString()),
            CreatedAt = DateTime.UtcNow,
        });
        s.Store(new OtpCode
        {
            Id = Guid.NewGuid(),
            Email = email.ToLowerInvariant(),
            CodeHash = BCrypt.Net.BCrypt.HashPassword(plainCode),
            ExpiresAt = DateTime.UtcNow.AddMinutes(10),
        });
        await s.SaveChangesAsync();
        return email;
    }

    private Task<HttpResponseMessage> Verify(string email, string code)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/auth/otp/verify")
        {
            Content = JsonContent.Create(new OtpVerifyRequest { Email = email, Code = code }),
        };
        req.Headers.Add(TestRemoteIpFilter.Header, _ip);
        return _client.SendAsync(req);
    }

    [Fact]
    public async Task Two_requests_racing_one_code_never_both_succeed()
    {
        var email = await SeedUserWithCodeAsync("123456");

        var responses = await Task.WhenAll(Verify(email, "123456"), Verify(email, "123456"));

        var ok = responses.Count(r => r.StatusCode == HttpStatusCode.OK);
        ok.Should().BeLessThanOrEqualTo(1, "one code must not sign in twice");

        foreach (var r in responses)
            ((int)r.StatusCode).Should().BeLessThan(500,
                "a code another request already consumed is a refusal, not a server error");
    }

    [Fact]
    public async Task A_code_that_was_already_consumed_is_refused_not_errored()
    {
        var email = await SeedUserWithCodeAsync("654321");

        var first = await Verify(email, "654321");
        first.StatusCode.Should().Be(HttpStatusCode.OK, "the control: the code is good the first time");

        // The sequential case. It shares the endpoint's refusal path with the lost race, so if this
        // ever starts returning 500 the racing test above is no longer proving anything either.
        var second = await Verify(email, "654321");
        second.StatusCode.Should().NotBe(HttpStatusCode.OK, "a consumed code cannot be reused");
        ((int)second.StatusCode).Should().BeLessThan(500, "and reuse is refused, not an error");
    }
}
