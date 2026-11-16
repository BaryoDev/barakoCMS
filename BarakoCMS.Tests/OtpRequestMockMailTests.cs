using System.Net;
using System.Net.Http.Json;
using barakoCMS.Core.Interfaces;
using barakoCMS.Features.Settings.Email;
using barakoCMS.Infrastructure.Services;
using FluentAssertions;
using Microsoft.AspNetCore.TestHost;
using Marten;
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

    private sealed class StubEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "BarakoCMS.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }

    private sealed class ApprovalRequiredGate : IDeviceGate
    {
        public Task<IReadOnlyList<System.Security.Claims.Claim>> TrustOnOtpAsync(
            barakoCMS.Models.User user, barakoCMS.Infrastructure.DeviceContext device, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<System.Security.Claims.Claim>>([]);

        public Task<DeviceGateResult> EvaluatePasswordAsync(
            barakoCMS.Models.User user, barakoCMS.Infrastructure.DeviceContext device, CancellationToken ct) =>
            Task.FromResult(new DeviceGateResult(DeviceDecision.ApprovalRequired, []));
    }

    private HttpClient MockMailClient(string environment, bool unknownDevice = false)
    {
        var host = _fixture.WithWebHostBuilder(b =>
        {
            // Not UseEnvironment: which environment a derived host resolves to depends on the order
            // hosts were built in (see RefreshCookieTests). The endpoint reads IHostEnvironment
            // from DI, so that is what gets replaced.
            b.ConfigureTestServices(s =>
            {
                s.RemoveAll<IEmailService>();
                s.AddSingleton<IEmailService, MockEmailService>();
                s.RemoveAll<IHostEnvironment>();
                s.AddSingleton<IHostEnvironment>(new StubEnvironment { EnvironmentName = environment });
                if (unknownDevice)
                {
                    s.RemoveAll<IDeviceGate>();
                    s.AddScoped<IDeviceGate, ApprovalRequiredGate>();
                }
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

    [Fact]
    public async Task Device_approval_answers_503_and_stores_no_code_outside_Development_with_only_the_mock()
    {
        const string password = "ValidPassword123!";
        var id = Guid.NewGuid();
        var user = new barakoCMS.Models.User
        {
            Id = id,
            Username = $"dv{id:N}"[..20],
            Email = $"dv{id:N}@example.com",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(password),
        };
        using (var scope = _fixture.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Store(user);
            await session.SaveChangesAsync(Ct);
        }

        using var client = MockMailClient("Production", unknownDevice: true);
        var response = await client.PostAsJsonAsync("/api/auth/login", new { username = user.Username, password }, Ct);
        var body = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement;

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        body.GetProperty("requiresDeviceApproval").GetBoolean().Should().BeTrue();
        using var check = _fixture.Services.CreateScope();
        var stored = await check.ServiceProvider.GetRequiredService<IQuerySession>()
            .Query<barakoCMS.Models.OtpCode>().Where(o => o.Email == user.Email).ToListAsync(Ct);
        stored.Should().BeEmpty("no code is issued when nothing can send it");
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
