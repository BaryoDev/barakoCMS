using barakoCMS.Infrastructure.Filters;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace BarakoCMS.Tests;

public class IdempotencyOptionsTests
{
    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.ToDictionary(v => v.Key, v => (string?)v.Value))
            .Build();

    [Fact]
    public void Defaults_are_a_day_and_64_KiB()
    {
        var options = IdempotencyOptions.FromConfiguration(Config());

        options.KeyHours.Should().Be(24);
        options.KeyLifetime.Should().Be(TimeSpan.FromHours(24));
        options.MaxStoredResponseBytes.Should().Be(65536);
        options.Invoking(o => o.Validate()).Should().NotThrow();
    }

    [Theory]
    [InlineData("1")]
    [InlineData("720")]
    public void Key_hours_at_either_bound_are_accepted(string value)
    {
        var options = IdempotencyOptions.FromConfiguration(Config((IdempotencyOptions.KeyHoursKey, value)));

        options.KeyHours.Should().Be(int.Parse(value));
        options.Invoking(o => o.Validate()).Should().NotThrow();
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("721")]
    public void Key_hours_out_of_range_are_refused(string value)
    {
        var options = IdempotencyOptions.FromConfiguration(Config((IdempotencyOptions.KeyHoursKey, value)));

        options.Invoking(o => o.Validate()).Should().Throw<InvalidOperationException>()
            .WithMessage($"*{IdempotencyOptions.KeyHoursKey}*");
    }

    [Theory]
    [InlineData("0")]
    [InlineData("1048576")]
    public void A_stored_response_limit_at_either_bound_is_accepted(string value)
    {
        var options = IdempotencyOptions.FromConfiguration(Config((IdempotencyOptions.MaxStoredResponseBytesKey, value)));

        options.Invoking(o => o.Validate()).Should().NotThrow();
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("1048577")]
    public void A_stored_response_limit_out_of_range_is_refused(string value)
    {
        var options = IdempotencyOptions.FromConfiguration(Config((IdempotencyOptions.MaxStoredResponseBytesKey, value)));

        options.Invoking(o => o.Validate()).Should().Throw<InvalidOperationException>()
            .WithMessage($"*{IdempotencyOptions.MaxStoredResponseBytesKey}*");
    }
}
