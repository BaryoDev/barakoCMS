using barakoCMS.Infrastructure.Security;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// The host built by <c>AddBarakoCMS</c> runs the non-ASCII <c>JWT:Key</c> warning check (#1411).
/// Without the registration no host logs the warning, even though the check itself works.
/// </summary>
[Collection("Integration1")]
public class JwtKeyWarningRegistrationTests(IntegrationTestFixture fixture)
{
    [Fact]
    public void The_host_registers_the_key_warning_check_once()
    {
        fixture.Services.GetServices<IHostedService>().OfType<JwtKeyWarningCheck>().Should().ContainSingle();
    }
}
