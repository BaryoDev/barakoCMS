using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using barakoCMS.Features.Auth.Otp;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// A code check for an address with no pending code takes as long as one with a pending code.
/// </summary>
[Collection("Integration1")]
public class OtpVerifyTimingTests
{
    private readonly HttpClient _client;
    private readonly string _ip = $"203.0.113.{Random.Shared.Next(2, 250)}";

    public OtpVerifyTimingTests(IntegrationTestFixture factory)
    {
        _client = factory
            .WithWebHostBuilder(b => b.ConfigureServices(s =>
                s.AddSingleton<Microsoft.AspNetCore.Hosting.IStartupFilter, TestRemoteIpFilter>()))
            .CreateClient();
    }

    private async Task<TimeSpan> TimeVerifyAsync(string email)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/auth/otp/verify")
        {
            Content = JsonContent.Create(new OtpVerifyRequest { Email = email, Code = "123456" }),
        };
        req.Headers.Add(TestRemoteIpFilter.Header, _ip);
        var watch = Stopwatch.StartNew();
        var response = await _client.SendAsync(req);
        watch.Stop();
        response.StatusCode.Should().NotBe(HttpStatusCode.OK);
        ((int)response.StatusCode).Should().BeLessThan(500);
        return watch.Elapsed;
    }

    [Fact]
    public async Task An_address_with_no_pending_code_pays_for_a_hash_check_like_one_with_a_code()
    {
        var hash = BCrypt.Net.BCrypt.HashPassword("654321");
        var local = Stopwatch.StartNew();
        BCrypt.Net.BCrypt.Verify("123456", hash);
        local.Stop();
        local.Elapsed.Should().BeGreaterThan(TimeSpan.FromMilliseconds(5), "the hash check is the cost being compared");

        await TimeVerifyAsync($"nobody-{Guid.NewGuid():N}@example.com");
        var miss = await TimeVerifyAsync($"nobody-{Guid.NewGuid():N}@example.com");

        miss.Should().BeGreaterThan(local.Elapsed / 2,
            "a miss that skips the hash is quick enough to tell apart from a real check");
    }
}
