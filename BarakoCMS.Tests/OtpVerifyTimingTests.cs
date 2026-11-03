using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using barakoCMS.Features.Auth.Otp;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
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
    private readonly IntegrationTestFixture _factory;
    private readonly HttpClient _client;
    private static int _ipCounter;

    public OtpVerifyTimingTests(IntegrationTestFixture factory)
    {
        _factory = factory;
        _client = factory
            .WithWebHostBuilder(b => b.ConfigureServices(s =>
                s.AddSingleton<Microsoft.AspNetCore.Hosting.IStartupFilter, TestRemoteIpFilter>()))
            .CreateClient();
    }

    private static string NextIp() =>
        $"203.0.113.{Interlocked.Increment(ref _ipCounter) % 250 + 1}";

    private async Task<string> StorePendingCodeAsync()
    {
        var email = $"pending-{Guid.NewGuid():N}@example.com";
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Store(new OtpCode
        {
            Id = Guid.NewGuid(),
            Email = email,
            CodeHash = BCrypt.Net.BCrypt.HashPassword("654321"),
            ExpiresAt = DateTime.UtcNow.AddMinutes(10),
        });
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        return email;
    }

    private async Task<TimeSpan> TimeVerifyAsync(string email)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/auth/otp/verify")
        {
            Content = JsonContent.Create(new OtpVerifyRequest { Email = email, Code = "123456" }),
        };
        req.Headers.Add(TestRemoteIpFilter.Header, NextIp());
        var watch = Stopwatch.StartNew();
        var response = await _client.SendAsync(req, TestContext.Current.CancellationToken);
        watch.Stop();
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "a refusal that is not a plain wrong-code answer, such as a 429, is not the path being timed");
        return watch.Elapsed;
    }

    private static TimeSpan Median(List<TimeSpan> times)
    {
        times.Should().NotBeEmpty();
        return times.Order().ElementAt(times.Count / 2);
    }

    [Fact]
    public async Task An_address_with_no_pending_code_pays_for_a_hash_check_like_one_with_a_code()
    {
        await TimeVerifyAsync($"nobody-{Guid.NewGuid():N}@example.com");

        var misses = new List<TimeSpan>();
        var checks = new List<TimeSpan>();
        for (var i = 0; i < 5; i++)
        {
            misses.Add(await TimeVerifyAsync($"nobody-{Guid.NewGuid():N}@example.com"));
            checks.Add(await TimeVerifyAsync(await StorePendingCodeAsync()));
        }

        misses.Should().HaveCount(5);
        checks.Should().HaveCount(5);
        // A real wrong-code check is a hash plus two writes and a read, so a miss that runs the hash
        // lands within a small factor of it. A miss that skips the hash is a query or two, a
        // fraction of one hash.
        Median(misses).Should().BeGreaterThan(Median(checks) * 0.5,
            "a miss that skips the hash is quick enough to tell apart from a real check");
    }
}
