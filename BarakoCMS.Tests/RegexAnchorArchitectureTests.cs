using System.Reflection;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// A whole-string pattern ends with <c>\z</c>. In .NET a final <c>$</c> also matches before a
/// trailing line feed, so "name\n" would pass the check.
/// </summary>
/// <remarks>
/// Reads every <c>GeneratedRegex</c> method and every static <see cref="Regex"/> field and property in
/// the production assemblies. A <c>.Matches("...")</c> or <c>Regex.IsMatch(x, "...")</c> literal is
/// invisible here, so those are hoisted into a <c>GeneratedRegex</c> instead.
/// </remarks>
public class RegexAnchorArchitectureTests
{
    private static readonly string[] AssemblyNames =
    [
        "barakoCMS", "BarakoCMS.Abstractions", "BarakoCMS.AI", "BarakoCMS.Diagnostics", "BarakoCMS.Email.Smtp",
        "BarakoCMS.ExternalAuth", "BarakoCMS.FeatureFlags", "BarakoCMS.Files", "BarakoCMS.Files.S3",
        "BarakoCMS.Import", "BarakoCMS.Pages", "BarakoCMS.Portability",
    ];

    private static IEnumerable<(string Owner, string Pattern)> Patterns()
    {
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance
            | BindingFlags.DeclaredOnly;

        foreach (var name in AssemblyNames)
        {
            var assembly = Assembly.Load(name);
            foreach (var type in assembly.GetTypes())
            {
                foreach (var member in type.GetMembers(all))
                {
                    var generated = member.GetCustomAttributesData()
                        .FirstOrDefault(a => a.AttributeType == typeof(GeneratedRegexAttribute));
                    if (generated?.ConstructorArguments is [{ Value: string pattern }, ..])
                    {
                        yield return ($"{type.FullName}.{member.Name}", pattern);
                    }

                    if (member is FieldInfo { IsStatic: true } field && field.FieldType == typeof(Regex)
                        && field.GetValue(null) is Regex fromField)
                    {
                        yield return ($"{type.FullName}.{field.Name}", fromField.ToString());
                    }

                    if (member is PropertyInfo { GetMethod.IsStatic: true } property && property.PropertyType == typeof(Regex)
                        && property.GetIndexParameters().Length == 0 && property.GetValue(null) is Regex fromProperty)
                    {
                        yield return ($"{type.FullName}.{property.Name}", fromProperty.ToString());
                    }
                }
            }
        }
    }

    internal static bool EndsWithBareDollar(string pattern)
    {
        if (!pattern.EndsWith('$'))
        {
            return false;
        }

        var slashes = 0;
        for (var i = pattern.Length - 2; i >= 0 && pattern[i] == '\\'; i--)
        {
            slashes++;
        }

        return slashes % 2 == 0;
    }

    [Theory]
    [InlineData("^abc$", true)]
    [InlineData(@"^abc\z", false)]
    [InlineData(@"^abc\$", false)]
    [InlineData(@"^abc\\$", true)]
    public void BareDollarIsRecognised(string pattern, bool expected) =>
        EndsWithBareDollar(pattern).Should().Be(expected);

    [Fact]
    public void TheScanFindsThePatterns() =>
        Patterns().Count().Should().BeGreaterThan(30, "a scan that finds nothing passes every pattern");

    [Fact]
    public void NoPatternEndsWithADollar()
    {
        var offenders = Patterns()
            .Where(p => EndsWithBareDollar(p.Pattern))
            .Select(p => $"{p.Owner}: {p.Pattern}")
            .ToList();

        offenders.Should().BeEmpty("a whole-string pattern ends with \\z, because $ also matches before a final line feed");
    }
}
