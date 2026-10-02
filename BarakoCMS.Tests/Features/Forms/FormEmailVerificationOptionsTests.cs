using BarakoCMS.Forms;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BarakoCMS.Tests.Features.Forms;

/// <summary>
/// An email verification setting below 1 stops the Forms module at startup instead of being read
/// as 1, and an address is taken only as one bare mailbox.
/// </summary>
public class FormEmailVerificationOptionsTests
{
    [Theory]
    [InlineData("CodeLifetimeMinutes")]
    [InlineData("MaxAttempts")]
    [InlineData("RequestsPerClient")]
    [InlineData("RequestWindowSeconds")]
    [InlineData("CodesPerAddress")]
    [InlineData("CodesPerForm")]
    [InlineData("WindowMinutes")]
    [InlineData("SendTimeoutSeconds")]
    public void A_setting_below_one_stops_the_module_and_the_error_names_it(string setting)
    {
        foreach (var value in new[] { "0", "-1" })
        {
            var act = () => new FormsModule().ConfigureServices(new ServiceCollection(), Section((setting, value)));

            act.Should().Throw<InvalidOperationException>()
                .WithMessage($"*Modules:Forms:EmailVerification:{setting} is {value}*");
        }
    }

    [Fact]
    public void The_defaults_and_a_setting_of_one_are_accepted()
    {
        var defaults = () => new FormsModule().ConfigureServices(new ServiceCollection(), Section());
        var ones = () => new FormsModule().ConfigureServices(new ServiceCollection(), Section(
            ("CodeLifetimeMinutes", "1"), ("MaxAttempts", "1"), ("RequestsPerClient", "1"), ("RequestWindowSeconds", "1"),
            ("CodesPerAddress", "1"), ("CodesPerForm", "1"), ("WindowMinutes", "1"), ("SendTimeoutSeconds", "1")));

        defaults.Should().NotThrow();
        ones.Should().NotThrow();
    }

    [Fact]
    public void The_check_covers_every_setting_the_options_have()
    {
        var declared = typeof(FormEmailVerificationOptions).GetProperties()
            .Where(p => p.PropertyType == typeof(int))
            .Select(p => p.Name)
            .ToList();

        declared.Should().HaveCount(8);
        new FormEmailVerificationOptions().Values().Select(v => v.Name).Should().BeEquivalentTo(declared,
            "a setting left out of the check would be one that can still be set to zero");
    }

    [Theory]
    [InlineData("ana@example.com")]
    [InlineData("  Ana.Reyes+run@Mail.Example.com ")]
    [InlineData("o'brien_1@example-club.ph")]
    public void A_bare_mailbox_is_an_address(string address) =>
        FormEmailVerifier.IsAddress(address).Should().BeTrue();

    [Theory]
    [InlineData("")]
    [InlineData("ana")]
    [InlineData("ana@example")]
    [InlineData("ana@@example.com")]
    [InlineData("x<ana@example.com>")]
    [InlineData("(x)ana@example.com")]
    [InlineData("\"x\"ana@example.com")]
    [InlineData("ana@example.com.")]
    [InlineData(".ana@example.com")]
    [InlineData("ana.@example.com")]
    [InlineData("a..na@example.com")]
    [InlineData("ana@.example.com")]
    [InlineData("ana@example..com")]
    [InlineData("a,na@example.com")]
    [InlineData("a;na@example.com")]
    [InlineData("a:na@example.com")]
    [InlineData("a\\na@example.com")]
    [InlineData("a na@example.com")]
    [InlineData("ana@[192.0.2.1]")]
    [InlineData("ana@example.com\r\nBcc: other@example.com")]
    [InlineData("an\u0001a@example.com")]
    [InlineData("aná@example.com")]
    public void Anything_else_is_not(string address) =>
        FormEmailVerifier.IsAddress(address).Should().BeFalse();

    [Fact]
    public void An_address_longer_than_the_limit_is_not_an_address()
    {
        var host = string.Join('.', Enumerable.Repeat(new string('b', 60), 3)) + ".example.com";
        var atLimit = new string('a', FormEmailVerifier.MaxAddressLength - host.Length - 1) + "@" + host;

        atLimit.Length.Should().Be(FormEmailVerifier.MaxAddressLength);
        FormEmailVerifier.IsAddress(atLimit).Should().BeTrue("254 characters is the limit");
        FormEmailVerifier.IsAddress(atLimit + "m").Should().BeFalse("255 characters is past it");
        FormEmailVerifier.IsAddress(new string('a', 64) + "@example.com").Should().BeTrue("64 before the @ is allowed");
        FormEmailVerifier.IsAddress(new string('a', 65) + "@example.com").Should().BeFalse("65 before the @ is not");
    }

    private static IConfiguration Section(params (string Name, string Value)[] settings) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(settings.ToDictionary(s => $"EmailVerification:{s.Name}", s => (string?)s.Value))
            .Build();
}
