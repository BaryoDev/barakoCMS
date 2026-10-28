using barakoCMS.Features.Config;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace BarakoCMS.Tests;

public class SelfTransitionConfigCheckTests
{
    private static IConfiguration Config(params (string Key, string Value)[] pairs) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(pairs.ToDictionary(p => p.Key, p => (string?)p.Value))
            .Build();

    [Fact]
    public void An_unscoped_key_is_reported_and_a_scoped_one_is_not()
    {
        var config = Config(
            ("Lifecycle:AllowSelfTransition:Void", "true"),
            ("Lifecycle:AllowSelfTransition:attendance:Submit", "true"));

        SelfTransitionConfigCheck.UnscopedKeys(config).Should().Equal("Void");
    }

    [Fact]
    public void No_keys_means_nothing_to_report()
    {
        SelfTransitionConfigCheck.UnscopedKeys(Config()).Should().BeEmpty();
    }
}
