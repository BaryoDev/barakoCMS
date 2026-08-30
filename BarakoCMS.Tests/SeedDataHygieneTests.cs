using System.Text.RegularExpressions;
using FluentAssertions;
using barakoCMS.Data;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// The seed is what every new install starts with, so it is also the worked example people copy.
/// </summary>
/// <remarks>
/// It used to carry three well-formed Social Security numbers, one of them 123-45-6789, which
/// data-loss-prevention and compliance scanners treat as a real SSN wherever they find it.
///
/// These assert on the shape rather than on the replacement values, because the point is that the
/// class of data stays out. Pinning the exact new strings would pass just as happily on a seed that
/// swapped in three different realistic numbers. See issue #265.
/// </remarks>
public class SeedDataHygieneTests
{
    /// <summary>A US Social Security number as a scanner matches one, separated or run together.</summary>
    private static readonly Regex SsnShaped =
        new(@"\b\d{3}[- ]?\d{2}[- ]?\d{4}\b", RegexOptions.Compiled);

    private static IEnumerable<(string Field, string Value)> SeededValues() =>
        DataSeeder.SampleAttendanceRecords()
            .SelectMany(r => r.Data)
            .Select(kv => (kv.Key, kv.Value?.ToString() ?? string.Empty));

    [Fact]
    public void No_seeded_value_is_shaped_like_a_social_security_number()
    {
        var offenders = SeededValues()
            .Where(v => SsnShaped.IsMatch(v.Value))
            .Select(v => $"{v.Field}={v.Value}")
            .ToArray();

        offenders.Should().BeEmpty(
            "seeded sample data must not contain anything a scanner reads as a Social Security "
          + "number, whatever the digits happen to be");
    }

    [Fact]
    public void Seeded_mail_addresses_use_the_reserved_documentation_domain()
    {
        var addresses = SeededValues()
            .Where(v => v.Value.Contains('@'))
            .Select(v => v.Value)
            .ToArray();

        addresses.Should().NotBeEmpty("the sample records carry an Email field");
        addresses.Should().OnlyContain(
            a => a.EndsWith("@example.com", StringComparison.OrdinalIgnoreCase),
            "example.com is reserved by RFC 2606 for documentation, so a demo record cannot address "
          + "a real mailbox at a registered domain");
    }

    [Fact]
    public void The_seeder_still_produces_a_full_sample_record()
    {
        var records = DataSeeder.SampleAttendanceRecords();

        records.Should().HaveCount(3, "the demo content type is shown off with three rows");
        records.Should().OnlyContain(
            r => r.Data.ContainsKey("SSN") && r.Data.ContainsKey("Email") && r.Data.ContainsKey("FirstName"),
            "sanitising the values must not quietly drop the fields the demo exists to show");
    }
}
