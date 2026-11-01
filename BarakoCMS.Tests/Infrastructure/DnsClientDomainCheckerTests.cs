using barakoCMS.Infrastructure.Multitenancy;
using DnsClient;
using FluentAssertions;
using NSubstitute;
using Xunit;

namespace BarakoCMS.Tests.Infrastructure;

/// <summary>The real checker's time budget (#1280). The record parsing is DnsClient's own.</summary>
public class DnsClientDomainCheckerTests
{
    private static ILookupClient Hanging()
    {
        var lookup = Substitute.For<ILookupClient>();
        lookup.QueryAsync(Arg.Any<string>(), Arg.Any<QueryType>(), Arg.Any<QueryClass>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                await Task.Delay(Timeout.Infinite, call.Arg<CancellationToken>());
                return null!;
            });
        return lookup;
    }

    [Fact]
    public async Task A_resolver_that_never_answers_ends_the_check_as_timed_out()
    {
        var checker = new DnsClientDomainChecker(Hanging(), TimeSpan.FromMilliseconds(50));

        var result = await checker.CheckAsync("shop.example", "edge.example.net", "_barako-challenge.shop.example", "barako-verify=x", TestContext.Current.CancellationToken);

        result.TimedOut.Should().BeTrue();
        result.Passed.Should().BeFalse();
    }

    [Fact]
    public async Task A_caller_that_cancels_gets_the_cancellation_and_not_a_result()
    {
        var checker = new DnsClientDomainChecker(Hanging(), TimeSpan.FromMinutes(5));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        var act = () => checker.CheckAsync("shop.example", "edge.example.net", "_barako-challenge.shop.example", "barako-verify=x", cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task A_name_DnsClient_will_not_send_is_a_failed_check_and_not_an_error()
    {
        var lookup = Substitute.For<ILookupClient>();
        lookup.QueryAsync(Arg.Any<string>(), Arg.Any<QueryType>(), Arg.Any<QueryClass>(), Arg.Any<CancellationToken>())
            .Returns<Task<IDnsQueryResponse>>(_ => throw new ArgumentException("The domain name is too long."));
        var checker = new DnsClientDomainChecker(lookup, TimeSpan.FromSeconds(5));

        var result = await checker.CheckAsync(new string('a', 300) + ".example", "edge.example.net", "_barako-challenge.x", "barako-verify=x", TestContext.Current.CancellationToken);

        result.Passed.Should().BeFalse();
        result.TimedOut.Should().BeFalse("the name is the problem, and a retry will not help");
    }
}
