using barakoCMS.Infrastructure.Security;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;
using Xunit;

namespace BarakoCMS.Tests.Security;

/// <summary>
/// Every startup guard takes Development from the <see cref="IHostEnvironment"/> the host
/// registered, and a host with none is Production.
/// </summary>
public class DevelopmentHostTests
{
    [Theory]
    [InlineData("Development", true)]
    [InlineData("development", true)]
    [InlineData("Production", false)]
    [InlineData("Staging", false)]
    [InlineData("", false)]
    public void An_environment_name_decides_the_host(string name, bool development)
    {
        DevelopmentHost.Of(new HostingEnvironment { EnvironmentName = name }).Should().Be(development);
    }

    [Fact]
    public void No_environment_is_Production()
    {
        DevelopmentHost.Of((IHostEnvironment?)null).Should().BeFalse();
        DevelopmentHost.Of(new ServiceCollection()).Should().BeFalse();
    }

    [Fact]
    public void The_last_registration_in_the_collection_wins()
    {
        var services = new ServiceCollection()
            .AsHost(Environments.Development)
            .AsHost(Environments.Production);

        DevelopmentHost.Of(services).Should().BeFalse();
    }

    [Fact]
    public void A_later_factory_registration_is_Production_and_nothing_earlier_counts()
    {
        var services = new ServiceCollection().AsHost(Environments.Development);
        services.AddSingleton<IHostEnvironment>(_ => new HostingEnvironment { EnvironmentName = Environments.Development });

        DevelopmentHost.Of(services).Should().BeFalse();
    }

    [Fact]
    public void A_later_type_registration_is_Production()
    {
        var services = new ServiceCollection().AsHost(Environments.Development);
        services.AddSingleton<IHostEnvironment, HostingEnvironment>();

        DevelopmentHost.Of(services).Should().BeFalse();
    }

    [Theory]
    [InlineData("Development", true)]
    [InlineData("Production", false)]
    public void A_WebApplicationBuilder_registers_the_environment_it_was_given(string name, bool development)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = name });

        DevelopmentHost.Of(builder.Services).Should().Be(development);
    }

    [Theory]
    [InlineData("Development", true)]
    [InlineData("Production", false)]
    public void A_generic_host_registers_the_environment_it_was_given(string name, bool development)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = name });

        DevelopmentHost.Of(builder.Services).Should().Be(development);
    }

    [Fact]
    public void The_name_the_middleware_compares_is_Development_or_Production()
    {
        var development = new ServiceCollection().AsHost("development").BuildServiceProvider();
        var staging = new ServiceCollection().AsHost("Staging").BuildServiceProvider();
        var none = new ServiceCollection().BuildServiceProvider();

        DevelopmentHost.NameFor(development).Should().Be("Development");
        DevelopmentHost.NameFor(staging).Should().Be("Production");
        DevelopmentHost.NameFor(none).Should().Be("Production");
    }
}
