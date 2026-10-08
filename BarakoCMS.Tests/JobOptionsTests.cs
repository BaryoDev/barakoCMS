using barakoCMS.Infrastructure.Jobs;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace BarakoCMS.Tests;

public class JobOptionsTests
{
    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.ToDictionary(v => v.Key, v => (string?)v.Value))
            .Build();

    [Fact]
    public void Defaults_are_five_attempts_thirty_seconds_doubling_to_an_hour()
    {
        var options = JobOptions.FromConfiguration(Config());

        options.MaxAttempts.Should().Be(5);
        options.BackoffBaseSeconds.Should().Be(30);
        options.BackoffMaxSeconds.Should().Be(3600);
        options.StorageProbeSeconds.Should().Be(60);
        options.LeaseSeconds.Should().Be(600);
        options.Invoking(o => o.Validate()).Should().NotThrow();
    }

    [Fact]
    public void Configured_values_are_read()
    {
        var options = JobOptions.FromConfiguration(Config(
            (JobOptions.MaxAttemptsKey, "3"),
            (JobOptions.BackoffBaseSecondsKey, "5"),
            (JobOptions.BackoffMaxSecondsKey, "60"),
            (JobOptions.StorageProbeSecondsKey, "2"),
            (JobOptions.LeaseSecondsKey, "45")));

        options.MaxAttempts.Should().Be(3);
        options.BackoffBaseSeconds.Should().Be(5);
        options.BackoffMaxSeconds.Should().Be(60);
        options.StorageProbeSeconds.Should().Be(2);
        options.LeaseSeconds.Should().Be(45);
    }

    [Fact]
    public void Zero_attempts_is_refused()
    {
        var options = JobOptions.FromConfiguration(Config((JobOptions.MaxAttemptsKey, "0")));

        options.Invoking(o => o.Validate()).Should().Throw<InvalidOperationException>()
            .WithMessage($"*{JobOptions.MaxAttemptsKey}*");
    }

    [Fact]
    public void A_lease_below_one_second_is_refused()
    {
        var options = JobOptions.FromConfiguration(Config((JobOptions.LeaseSecondsKey, "0")));

        options.Invoking(o => o.Validate()).Should().Throw<InvalidOperationException>()
            .WithMessage($"*{JobOptions.LeaseSecondsKey}*");
    }

    [Fact]
    public void A_cap_below_the_base_is_refused()
    {
        var options = JobOptions.FromConfiguration(Config(
            (JobOptions.BackoffBaseSecondsKey, "120"),
            (JobOptions.BackoffMaxSecondsKey, "60")));

        options.Invoking(o => o.Validate()).Should().Throw<InvalidOperationException>()
            .WithMessage($"*{JobOptions.BackoffMaxSecondsKey}*");
    }

    [Fact]
    public void Dead_letters_are_kept_forever_by_default_and_a_retention_is_opted_into()
    {
        var defaults = JobOptions.FromConfiguration(Config());
        defaults.DeadLetterRetentionDays.Should().Be(0, "a default must keep what deployments kept before");
        defaults.MetricsIntervalSeconds.Should().Be(30);
        defaults.Invoking(o => o.Validate()).Should().NotThrow();

        var on = JobOptions.FromConfiguration(Config((JobOptions.DeadLetterRetentionDaysKey, "90")));
        on.DeadLetterRetentionDays.Should().Be(90);
        on.Invoking(o => o.Validate()).Should().NotThrow();
    }

    [Fact]
    public void A_retention_past_ten_years_is_refused()
    {
        var options = JobOptions.FromConfiguration(Config((JobOptions.DeadLetterRetentionDaysKey, "3651")));

        options.Invoking(o => o.Validate()).Should().Throw<InvalidOperationException>()
            .WithMessage($"*{JobOptions.DeadLetterRetentionDaysKey}*");
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("3601")]
    public void A_metrics_interval_out_of_range_is_refused(string value)
    {
        var options = JobOptions.FromConfiguration(Config((JobOptions.MetricsIntervalSecondsKey, value)));

        options.Invoking(o => o.Validate()).Should().Throw<InvalidOperationException>()
            .WithMessage($"*{JobOptions.MetricsIntervalSecondsKey}*");
    }
}
