using FluentAssertions;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// The security headers as the running pipeline actually sends them.
/// </summary>
/// <remarks>
/// SecurityHeadersTests covers the CSP string on its own. This covers what reaches a client, which
/// is the only place an absence can be proved: deleting a line from the middleware and reading the
/// diff proves the line is gone, not that no other registration puts the header back.
///
/// The four headers that must be present are asserted alongside the one that must not, and that
/// pairing is the point. An absence assertion on a response that never went through the middleware
/// passes for the wrong reason, and this repository has shipped that shape of gate before.
/// </remarks>
[Collection("Sequential")]
public class ResponseSecurityHeadersTests
{
    private readonly IntegrationTestFixture _factory;

    public ResponseSecurityHeadersTests(IntegrationTestFixture factory) => _factory = factory;

    [Theory]
    [InlineData("X-Content-Type-Options")]
    [InlineData("X-Frame-Options")]
    [InlineData("Referrer-Policy")]
    [InlineData("Content-Security-Policy")]
    public async Task The_headers_the_pipeline_owns_are_on_the_response(string header)
    {
        var response = await _factory.CreateClient().GetAsync("/health");

        response.Headers.Contains(header).Should().BeTrue(
            "{0} is written by the security-headers middleware, and without it the absence "
          + "assertion below would pass on a response that never reached that middleware", header);
    }

    /// <summary>
    /// X-XSS-Protection was dropped in 4.0 (#271).
    /// </summary>
    /// <remarks>
    /// Every current browser ignores it, and the filter it used to switch on was itself a
    /// cross-origin information leak: with "mode=block" an attacker could infer what was on a page
    /// from which loads the filter refused. The CSP is what carries this now.
    /// </remarks>
    [Fact]
    public async Task No_response_carries_the_deprecated_xss_filter_header()
    {
        var response = await _factory.CreateClient().GetAsync("/health");

        response.Headers.Contains("X-XSS-Protection").Should().BeFalse(
            "the header is deprecated and its filter caused vulnerabilities of its own; nothing in "
          + "the pipeline may write it back");
    }
}
