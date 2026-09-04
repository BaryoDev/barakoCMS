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
        options.Invoking(o => o.Validate()).Should().NotThrow();
    }

    [Fact]
    public void Configured_values_are_read()
    {
        var options = JobOptions.FromConfiguration(Config(
            (JobOptions.MaxAttemptsKey, "3"),
            (JobOptions.BackoffBaseSecondsKey, "5"),
            (JobOptions.BackoffMaxSecondsKey, "60"),
            (JobOptions.StorageProbeSecondsKey, "2")));

        options.MaxAttempts.Should().Be(3);
        options.BackoffBaseSeconds.Should().Be(5);
        options.BackoffMaxSeconds.Should().Be(60);
        options.StorageProbeSeconds.Should().Be(2);
    }

    [Fact]
    public void Zero_attempts_is_refused()
    {
        var options = JobOptions.FromConfiguration(Config((JobOptions.MaxAttemptsKey, "0")));

        options.Invoking(o => o.Validate()).Should().Throw<InvalidOperationException>()
            .WithMessage($"*{JobOptions.MaxAttemptsKey}*");
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
}
