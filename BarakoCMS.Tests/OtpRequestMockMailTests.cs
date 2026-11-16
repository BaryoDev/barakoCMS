using System.Net;
using System.Net.Http.Json;
using barakoCMS.Core.Interfaces;
using barakoCMS.Features.Settings.Email;
using barakoCMS.Infrastructure.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// POST /api/auth/otp/request refuses with 503 outside Development when only the mock email
/// service is registered, because a 200 would promise a code that is never sent (#8241).
/// </summary>
[Collection("Integration1")]
public class OtpRequestMockMailTests
{
    private static int _ip;

    private readonly IntegrationTestFixture _fixture;

    public OtpRequestMockMailTests(IntegrationTestFixture fixture) => _fixture = fixture;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private HttpClient MockMailClient(string environment)
    {
        var host = _fixture.WithWebHostBuilder(b =>
        {
            b.UseEnvironment(environment);
            b.ConfigureServices(s =>
            {
                s.RemoveAll<IEmailService>();
                s.AddSingleton<IEmailService, MockEmailService>();
            });
        });
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Add(TestRemoteIpFilter.Header, $"2001:db8:8241:{Interlocked.Increment(ref _ip):x}::1");
        return client;
    }

    [Fact]
    public async Task Otp_request_answers_503_outside_Development_when_only_the_mock_is_registered()
    {
        using var client = MockMailClient("Production");

        var known = await client.PostAsJsonAsync("/api/auth/otp/request", new { email = "nobody@example.com" }, Ct);
        var blank = await client.PostAsJsonAsync("/api/auth/otp/request", new { email = "" }, Ct);

        known.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        blank.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable, "the refusal must not depend on the address");
    }

    [Fact]
    public async Task Otp_request_still_answers_200_in_Development_with_the_mock()
    {
        using var client = MockMailClient("Development");

        var response = await client.PostAsJsonAsync("/api/auth/otp/request", new { email = "nobody@example.com" }, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("Production", true, true)]
    [InlineData("Staging", true, true)]
    [InlineData("Development", true, false)]
    [InlineData("Production", false, false)]
    public void CannotSendCodes_is_true_only_for_the_mock_outside_Development(string environment, bool mock, bool expected)
    {
        IEmailService email = mock ? new MockEmailService(Substitute.For<Microsoft.Extensions.Logging.ILogger<MockEmailService>>())
                                   : Substitute.For<IEmailService>();
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns(environment);

        EmailProvider.CannotSendCodes(email, env).Should().Be(expected);
    }
}
