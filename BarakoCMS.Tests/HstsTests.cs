using System.Text.RegularExpressions;
using barakoCMS.Infrastructure.Security;
using FluentAssertions;
using Microsoft.AspNetCore.HttpsPolicy;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// Strict-Transport-Security as the running application and the shipped nginx config actually send
/// it (#130).
/// </summary>
/// <remarks>
/// Two defects sat behind the missing header. The application configured HSTS twice, once through
/// UseHsts and once by appending the header by hand, and a browser keeps the first value it is given,
/// so the effective policy was the framework's 30 day default rather than the one written in the
/// pipeline. The hand-written copy also ignored the environment, which pinned a developer's browser
/// against https://localhost. The deployments in front of the app sent nothing at all.
/// </remarks>
[Collection("Sequential")]
public class HstsTests
{
    private readonly IntegrationTestFixture _factory;

    public HstsTests(IntegrationTestFixture factory) => _factory = factory;

    /// <summary>
    /// The policy the pipeline will apply, read from the host that AddBarakoCMS built. Asserting the
    /// registered options rather than a header is deliberate: UseHsts only runs outside Development
    /// and the test host is Development, so the header itself is not observable here, while a wrong
    /// or missing registration is.
    /// </summary>
    [Fact]
    public void The_host_registers_the_ninety_day_policy_rather_than_the_framework_default()
    {
        var options = _factory.Services.GetRequiredService<IOptions<HstsOptions>>().Value;

        options.MaxAge.Should().Be(TimeSpan.FromDays(HstsPolicy.DefaultMaxAgeDays),
            "the framework default is 30 days, so an unregistered policy looks like a working one");
        options.IncludeSubDomains.Should().BeFalse();
        options.Preload.Should().BeFalse();
    }

    /// <summary>
    /// Development has no TLS worth pinning, and a browser that accepts HSTS for localhost applies it
    /// to every other project the developer runs there.
    /// </summary>
    [Fact]
    public async Task Development_sends_no_hsts_header_even_over_https()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
        });

        var response = await client.GetAsync("/health");

        response.Headers.Contains("Strict-Transport-Security").Should().BeFalse(
            "UseHsts is only in the pipeline outside Development, and nothing else may write this header");
    }

    // The nginx side. The header is added by the reverse proxy as well as the app, because the app
    // only sees HTTPS when forwarded headers are configured and they are off by default.

    private static string NginxConfig()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Directory.Build.props")))
            dir = dir.Parent;
        dir.Should().NotBeNull("the tests must be able to find the repository root");

        var path = Path.Combine(dir!.FullName, "site", "nginx-barakocms.com.conf");
        File.Exists(path).Should().BeTrue("{0} is the shipped nginx config", path);
        return File.ReadAllText(path);
    }

    private static readonly Regex Hsts =
        new(@"add_header\s+Strict-Transport-Security\s+""[^""]*max-age=\d+[^""]*""\s+always\s*;", RegexOptions.Compiled);

    private static readonly Regex AnyAddHeader = new(@"add_header\s", RegexOptions.Compiled);

    /// <summary>Top-level blocks, each paired with its own text minus the nested location blocks.</summary>
    private static List<(string Whole, string OwnLevel, List<string> Nested)> Blocks(string conf)
    {
        var blocks = new List<(string, string, List<string>)>();
        var depth = 0;
        var blockStart = -1;
        var ownLevel = new System.Text.StringBuilder();
        var nested = new List<string>();
        var nestedStart = -1;

        for (var i = 0; i < conf.Length; i++)
        {
            var c = conf[i];

            if (c == '{')
            {
                depth++;
                if (depth == 1) { blockStart = i; ownLevel.Clear(); nested = new List<string>(); }
                else if (depth == 2) nestedStart = i;
                continue;
            }

            if (c == '}')
            {
                depth--;
                if (depth == 0 && blockStart >= 0)
                    blocks.Add((conf.Substring(blockStart, i - blockStart + 1), ownLevel.ToString(), nested));
                else if (depth == 1 && nestedStart >= 0)
                    nested.Add(conf.Substring(nestedStart, i - nestedStart + 1));
                continue;
            }

            if (depth == 1) ownLevel.Append(c);
        }

        return blocks;
    }

    [Fact]
    public void Every_tls_server_block_sends_hsts_and_sends_it_on_error_responses_too()
    {
        var tls = Blocks(NginxConfig()).Where(b => b.Whole.Contains("listen 443")).ToList();

        tls.Should().NotBeEmpty("the config must still have the TLS server blocks this asserts over");

        foreach (var block in tls)
        {
            Hsts.IsMatch(block.OwnLevel).Should().BeTrue(
                "every TLS server block sends Strict-Transport-Security with `always`, or the header "
              + "is dropped on exactly the error responses a downgrade produces. Block: {0}",
                block.OwnLevel.Trim());
        }
    }

    /// <summary>
    /// nginx replaces the inherited header set at any level that declares one of its own, so a
    /// location block with a Cache-Control header silently loses the server block's HSTS.
    /// </summary>
    [Fact]
    public void A_location_that_sets_its_own_headers_repeats_hsts()
    {
        var tls = Blocks(NginxConfig()).Where(b => b.Whole.Contains("listen 443")).ToList();

        var offenders = tls
            .SelectMany(b => b.Nested)
            .Where(l => AnyAddHeader.IsMatch(l))
            .Where(l => !Hsts.IsMatch(l))
            .ToList();

        offenders.Should().BeEmpty(
            "add_header does not merge across levels, so a location declaring one drops every header "
          + "the server block set, HSTS included");
    }

    [Fact]
    public void The_nginx_max_age_matches_the_application_default()
    {
        var seconds = (int)TimeSpan.FromDays(HstsPolicy.DefaultMaxAgeDays).TotalSeconds;

        var values = Regex.Matches(NginxConfig(), @"Strict-Transport-Security\s+""max-age=(\d+)([^""]*)""")
            .Select(m => (Seconds: int.Parse(m.Groups[1].Value), Flags: m.Groups[2].Value))
            .ToList();

        values.Should().NotBeEmpty();
        values.Should().OnlyContain(v => v.Seconds == seconds,
            "the proxy and the app must not disagree about how long a browser is pinned");
        values.Should().OnlyContain(v => !v.Flags.Contains("includeSubDomains"),
            "includeSubDomains at this apex would cover subdomains that are not all on HTTPS");
        values.Should().OnlyContain(v => !v.Flags.Contains("preload"));
    }
}
