using barakoCMS.Data;
using barakoCMS.Extensions;
using barakoCMS.Infrastructure.Security;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BarakoCMS.Tests.Security;

/// <summary>
/// The guards read the host's environment and not the process variable. These tests set
/// ASPNETCORE_ENVIRONMENT for the process, which every host started in parallel would see, so they
/// run alone: nothing else runs while this collection does.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ProcessEnvironmentCollection
{
    public const string Name = "ProcessEnvironment";
}

[Collection(ProcessEnvironmentCollection.Name)]
public class HostEnvironmentGuardTests
{
    private static IConfiguration Config(params (string Key, string? Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
            .Build();

    // The next six tests share one setup: the host runs Production, and every other place that could
    // say Development does. A guard that took its answer from any of them would let the start
    // through. Each test names the guard it holds.

    private const string ValidHosts = "api.example.com";

    private static IServiceCollection ProductionHost() => new ServiceCollection().AsHost(Environments.Production);

    private static IConfiguration ConfigSaysDevelopment(params (string Key, string? Value)[] values) =>
        Config(
            [
                ("environment", "Development"),
                ("ENVIRONMENT", "Development"),
                ("ASPNETCORE_ENVIRONMENT", "Development"),
                ("DOTNET_ENVIRONMENT", "Development"),
                .. values,
            ]);

    /// <summary>Sets the process variable to Development for the test and puts back what was there.</summary>
    private sealed class ProcessSaysDevelopment : IDisposable
    {
        private const string Name = "ASPNETCORE_ENVIRONMENT";
        private readonly string? _before = Environment.GetEnvironmentVariable(Name);

        public ProcessSaysDevelopment() => Environment.SetEnvironmentVariable(Name, "Development");

        public void Dispose() => Environment.SetEnvironmentVariable(Name, _before);
    }

    [Fact]
    public void HostFilteringGuard_ReadsTheHostEnvironment_NotConfigurationOrTheProcess()
    {
        using var _ = new ProcessSaysDevelopment();

        var act = () => ServiceCollectionExtensions.ValidateStartupGuards(
            ProductionHost(), ConfigSaysDevelopment(("AllowedHosts", "*")));

        act.Should().Throw<InvalidOperationException>().WithMessage("*AllowedHosts*");
    }

    [Fact]
    public void InitialAdminGuard_ReadsTheHostEnvironment_NotConfigurationOrTheProcess()
    {
        using var _ = new ProcessSaysDevelopment();

        var act = () => ServiceCollectionExtensions.ValidateStartupGuards(
            ProductionHost(),
            ConfigSaysDevelopment(("AllowedHosts", ValidHosts), ("InitialAdmin:Username", "admin"), ("InitialAdmin:Password", "short1!")));

        act.Should().Throw<InvalidOperationException>().WithMessage("*InitialAdmin:Password*");
    }

    [Fact]
    public void DatabasePasswordGuard_ReadsTheHostEnvironment_NotConfigurationOrTheProcess()
    {
        using var _ = new ProcessSaysDevelopment();

        var act = () => ServiceCollectionExtensions.ValidateStartupGuards(
            ProductionHost(),
            ConfigSaysDevelopment(
                ("AllowedHosts", ValidHosts),
                ("ConnectionStrings:DefaultConnection", "Host=db;Database=d;Username=u;Password=postgres")));

        act.Should().Throw<InvalidOperationException>().WithMessage("*database password*refused*");
    }

    [Fact]
    public void JwtKeyGuard_ReadsTheHostEnvironment_NotConfigurationOrTheProcess()
    {
        using var _ = new ProcessSaysDevelopment();

        var act = () => ServiceCollectionExtensions.AddJwtAndApiKeyAuth(
            ProductionHost(), ConfigSaysDevelopment(("JWT:Key", JwtKeyGuard.ComposeDevKey)));

        act.Should().Throw<InvalidOperationException>().WithMessage("*JWT:Key*outside Development*");
    }

    [Fact]
    public async Task DatabaseRoleGuard_ReadsTheHostEnvironment_NotConfigurationOrTheProcess()
    {
        using var _ = new ProcessSaysDevelopment();
        var reached = new InvalidOperationException("the guard asked the database");

        var act = async () => await DatabaseRoleGuard.AssertAsync(
            ConfigSaysDevelopment(),
            DevelopmentHost.Of(ProductionHost()),
            _ => throw reached,
            NullLogger.Instance,
            new object(),
            DatabaseRoleGuard.RoleQuery,
            TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Should().BeSameAs(reached);
    }

    [Fact]
    public void DemoAccounts_ReadTheHostEnvironment_NotConfigurationOrTheProcess()
    {
        using var _ = new ProcessSaysDevelopment();
        var environment = new HostingEnvironment { EnvironmentName = Environments.Production };

        DataSeeder.SeedsDemoAccounts(environment).Should().BeFalse();
        DataSeeder.SeedsDemoAccounts(new HostingEnvironment { EnvironmentName = Environments.Development }).Should().BeTrue();
    }
}
