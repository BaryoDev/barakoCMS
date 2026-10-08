using Amazon.S3;
using barakoCMS.Features.Workflows;
using barakoCMS.Infrastructure.Http;
using barakoCMS.Infrastructure.Jobs;
using BarakoCMS.Files.S3;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// The S3 client's retries and timeout, bounded so one call stays inside the lease of the job or
/// workflow action it runs in. No container: nothing here makes a call.
/// </summary>
public class S3ClientBoundsTests
{
    [Fact]
    public void The_module_copies_of_the_core_lease_settings_match_the_core()
    {
        S3StorageOptions.WorkflowLease.Should().Be(WorkflowRetryPolicy.LeaseDuration);
        S3StorageOptions.LeaseShare.Should().Be(OutboundResilienceOptions.LeaseShare);
        S3StorageOptions.JobLeaseSecondsKey.Should().Be(JobOptions.LeaseSecondsKey);
        S3StorageOptions.DefaultJobLeaseSeconds.Should().Be(JobOptions.DefaultLeaseSeconds);
    }

    [Fact]
    public void The_default_worst_case_fits_in_the_shortest_lease()
    {
        var defaults = new S3StorageOptions();

        defaults.MaxCallDuration.Should().Be(TimeSpan.FromSeconds(195), "three tries of 45 s and two waits of up to 30 s");
        defaults.MaxCallDuration.Should().BeLessThanOrEqualTo(WorkflowRetryPolicy.LeaseDuration * OutboundResilienceOptions.LeaseShare);
        defaults.Problem(JobOptions.DefaultLeaseSeconds).Should().BeNull();
    }

    /// <summary>
    /// A host whose leases already fit the outbound defaults must not be refused by these.
    /// </summary>
    [Fact]
    public void The_default_worst_case_is_no_longer_than_the_slowest_outbound_action()
    {
        new S3StorageOptions().MaxCallDuration.Should().BeLessThanOrEqualTo(new OutboundResilienceOptions().MaxActionDuration);
    }

    [Fact]
    public void The_client_is_built_with_the_configured_bounds()
    {
        using var provider = Build(new() { ["Modules:Files.S3:MaxErrorRetry"] = "1", ["Modules:Files.S3:TimeoutSeconds"] = "20" });

        var config = ((AmazonS3Client)provider.GetRequiredService<IAmazonS3>()).Config;

        config.MaxErrorRetry.Should().Be(1);
        config.Timeout.Should().Be(TimeSpan.FromSeconds(20));
    }

    [Fact]
    public void The_client_is_built_with_the_default_bounds()
    {
        using var provider = Build(new());

        var config = ((AmazonS3Client)provider.GetRequiredService<IAmazonS3>()).Config;

        config.MaxErrorRetry.Should().Be(2, "the SDK's own default is 4");
        config.Timeout.Should().Be(TimeSpan.FromSeconds(45));
    }

    [Fact]
    public void Settings_that_could_outlast_the_lease_stop_the_host_at_startup()
    {
        using (var accepted = Build(new()))
        {
            var start = () => accepted.GetRequiredService<IStartupValidator>().Validate();
            start.Should().NotThrow("the defaults fit, so the refusals below are about the values");
        }

        using (var longTries = Build(new() { ["Modules:Files.S3:TimeoutSeconds"] = "120" }))
        {
            var start = () => longTries.GetRequiredService<IStartupValidator>().Validate();
            start.Should().Throw<OptionsValidationException>().WithMessage("*Modules:Files.S3*shortest lease (300 s*");
        }

        using (var shortLease = Build(new() { [JobOptions.LeaseSecondsKey] = "120" }))
        {
            var start = () => shortLease.GetRequiredService<IStartupValidator>().Validate();
            start.Should().Throw<OptionsValidationException>().WithMessage("*shortest lease (120 s*");
        }
    }

    [Theory]
    [InlineData("-1", "45")]
    [InlineData("11", "45")]
    [InlineData("2", "0")]
    public void Settings_out_of_range_are_refused(string retries, string timeout)
    {
        using var provider = Build(new()
        {
            ["Modules:Files.S3:MaxErrorRetry"] = retries,
            ["Modules:Files.S3:TimeoutSeconds"] = timeout,
        });

        var start = () => provider.GetRequiredService<IStartupValidator>().Validate();

        start.Should().Throw<OptionsValidationException>().WithMessage("*Modules:Files.S3:*");
    }

    private static ServiceProvider Build(Dictionary<string, string?> settings)
    {
        settings["Modules:Files.S3:Bucket"] = "media";
        settings["Modules:Files.S3:AccessKey"] = "barako-test";
        settings["Modules:Files.S3:SecretKey"] = "barako-test-secret";
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        new S3FilesModule().ConfigureServices(services, configuration.GetSection("Modules:Files.S3"));
        return services.BuildServiceProvider();
    }
}
