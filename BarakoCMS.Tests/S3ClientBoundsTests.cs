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
        var (retries, timeout, problem) = new S3StorageOptions().Resolve(JobOptions.DefaultLeaseSeconds);

        problem.Should().BeNull();
        retries.Should().Be(2);
        timeout.Should().Be(TimeSpan.FromSeconds(45));
        S3StorageOptions.MaxCallDuration(retries, timeout).Should().Be(TimeSpan.FromSeconds(195), "three tries of 45 s and two waits of up to 30 s");
        S3StorageOptions.MaxCallDuration(retries, timeout)
            .Should().BeLessThanOrEqualTo(WorkflowRetryPolicy.LeaseDuration * OutboundResilienceOptions.LeaseShare);
    }

    /// <summary>
    /// A host whose leases already fit the outbound defaults must not be refused by these.
    /// </summary>
    [Fact]
    public void The_default_worst_case_is_no_longer_than_the_slowest_outbound_action()
    {
        S3StorageOptions.MaxCallDuration(S3StorageOptions.DefaultMaxErrorRetry, TimeSpan.FromSeconds(S3StorageOptions.DefaultTimeoutSeconds))
            .Should().BeLessThanOrEqualTo(new OutboundResilienceOptions().MaxActionDuration);
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

        using (var longTries = Build(new() { ["Modules:Files.S3:TimeoutSeconds"] = "120", ["Modules:Files.S3:MaxErrorRetry"] = "2" }))
        {
            var start = () => longTries.GetRequiredService<IStartupValidator>().Validate();
            start.Should().Throw<OptionsValidationException>().WithMessage("*Modules:Files.S3*shortest lease (300 s*");
        }

        using (var shortLease = Build(new() { [JobOptions.LeaseSecondsKey] = "120", ["Modules:Files.S3:MaxErrorRetry"] = "2", ["Modules:Files.S3:TimeoutSeconds"] = "45" }))
        {
            var start = () => shortLease.GetRequiredService<IStartupValidator>().Validate();
            start.Should().Throw<OptionsValidationException>().WithMessage("*shortest lease (120 s*",
                "both set by hand, and three tries of 45 s cannot fit in 96 s");
        }
    }

    /// <summary>
    /// A host that starts today with a short lease and no S3 bounds set must keep starting: the unset
    /// values give way to the lease instead of refusing it.
    /// </summary>
    [Fact]
    public void Unset_bounds_shrink_to_fit_a_short_lease_rather_than_stop_the_host()
    {
        using var provider = Build(new() { [JobOptions.LeaseSecondsKey] = "200" });

        var start = () => provider.GetRequiredService<IStartupValidator>().Validate();
        start.Should().NotThrow();

        var config = ((AmazonS3Client)provider.GetRequiredService<IAmazonS3>()).Config;
        config.MaxErrorRetry.Should().Be(1, "two tries of 45 s and one wait of 30 s is 120 s, inside the 160 s; three is 195 s");
        config.Timeout.Should().Be(TimeSpan.FromSeconds(45));
        S3StorageOptions.MaxCallDuration(config.MaxErrorRetry, config.Timeout!.Value)
            .Should().BeLessThanOrEqualTo(TimeSpan.FromSeconds(160));
    }

    [Fact]
    public void An_unset_timeout_shrinks_when_no_retries_still_do_not_fit()
    {
        var (retries, timeout, problem) = new S3StorageOptions { MaxErrorRetry = 0 }.Resolve(jobLeaseSeconds: 30);

        problem.Should().BeNull();
        retries.Should().Be(0);
        timeout.Should().Be(TimeSpan.FromSeconds(24), "80% of a 30 s lease");
    }

    [Fact]
    public void An_unset_retry_count_gives_way_to_a_set_timeout()
    {
        var (retries, _, problem) = new S3StorageOptions { TimeoutSeconds = 100 }.Resolve(JobOptions.DefaultLeaseSeconds);

        problem.Should().BeNull();
        retries.Should().Be(1, "two tries of 100 s and a wait of 30 s is 230 s of the 240 s");
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
