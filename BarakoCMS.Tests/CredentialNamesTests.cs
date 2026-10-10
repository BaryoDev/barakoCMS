using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;
using barakoCMS.Features.Workflows.Actions;
using barakoCMS.Infrastructure.Security;

namespace BarakoCMS.Tests;

/// <summary>
/// One rule decides whether a name reads as a credential, for a setting key and for a workflow
/// parameter alike.
/// </summary>
/// <remarks>
/// The two word lists below are the ones the settings endpoint and the workflow code each carried
/// before there was one rule, copied as they stood. A word either of them held has to stay a
/// credential name: dropping one would leave a value that was refused, encrypted or left out of a
/// response yesterday stored or returned as it is today.
/// </remarks>
public class CredentialNamesTests
{
    private static readonly string[] WordsTheSettingsEndpointHeld =
        ["apikey", "api_key", "password", "secret", "token", "credential", "privatekey"];

    private static readonly string[] WordsTheWorkflowCodeHeld =
    [
        "secret", "password", "passwd", "pwd", "token", "apikey", "api_key",
        "credential", "privatekey", "private_key", "accesskey", "access_key",
    ];

    private static List<string> EveryWordEitherListHeld() =>
        WordsTheSettingsEndpointHeld.Concat(WordsTheWorkflowCodeHeld).Distinct(StringComparer.Ordinal).ToList();

    [Fact]
    public void Every_word_either_earlier_list_held_is_still_a_credential_name()
    {
        var words = EveryWordEitherListHeld();
        words.Should().HaveCount(12, "seven words from settings and twelve from workflows, seven of them shared");

        foreach (var word in words)
        {
            CredentialNames.IsCredential(word).Should().BeTrue("'{0}' was a credential word", word);
            CredentialNames.IsCredential(word.ToUpperInvariant()).Should().BeTrue("'{0}' in capitals is the same word", word);
            CredentialNames.IsCredential($"Smtp:{word}").Should().BeTrue("'{0}' at the end of a setting key still names a credential", word);
            CredentialNames.IsCredential($"My{word}Value").Should().BeTrue("'{0}' inside a longer name still names a credential", word);
        }
    }

    [Theory]
    [InlineData("Storage:AccessKey")]
    [InlineData("S3__access_key")]
    [InlineData("Smtp:Passwd")]
    [InlineData("Db:Pwd")]
    [InlineData("Jwt:private_key")]
    [InlineData("Resend:ApiKey")]
    [InlineData("ClientSecret")]
    [InlineData("AccessToken")]
    [InlineData("Credential")]
    public void A_name_as_somebody_would_type_it_is_a_credential_name(string name)
    {
        CredentialNames.IsCredential(name).Should().BeTrue();
    }

    [Theory]
    [InlineData("Authorization")]
    [InlineData("Proxy-Authorization")]
    [InlineData("Webhook:AuthorizationHeader")]
    [InlineData("Bearer")]
    [InlineData("BearerValue")]
    public void A_name_an_http_credential_header_goes_by_is_a_credential_name(string name)
    {
        CredentialNames.IsCredential(name).Should().BeTrue();
    }

    [Theory]
    [InlineData("Author")]
    [InlineData("AuthorName")]
    [InlineData("Authority")]
    public void A_name_that_only_starts_like_auth_is_not_a_credential_name(string name)
    {
        CredentialNames.IsCredential(name).Should().BeFalse();
    }

    [Theory]
    [InlineData("Kubernetes__Enabled")]
    [InlineData("HealthChecksUI__Enabled")]
    [InlineData("Serilog__WriteToFile")]
    [InlineData("Url")]
    [InlineData("Method")]
    [InlineData("Body")]
    [InlineData("ContentType")]
    [InlineData("")]
    [InlineData(null)]
    public void An_ordinary_name_is_not_a_credential_name(string? name)
    {
        CredentialNames.IsCredential(name).Should().BeFalse();
    }

    [Fact]
    public void A_workflow_parameter_and_a_setting_key_get_the_same_answer()
    {
        var names = EveryWordEitherListHeld()
            .Concat(["Storage:AccessKey", "TokenUrl", "Url", "Method", "ContentType", "Serilog__WriteToFile"])
            .ToList();
        names.Should().HaveCount(18);

        foreach (var name in names)
        {
            WebhookSigning.IsSensitiveParameterName(name).Should().Be(
                CredentialNames.IsCredential(name), "'{0}' must not read one way on one surface and another way on the other", name);
        }
    }

    /// <summary>
    /// A second word list is how two surfaces came to disagree. This is a tripwire for one, not a
    /// proof there is none: it looks for eight of the twelve words, the ones too specific to turn up
    /// as a string literal for another reason more than once in a file, and a list of the other four
    /// (secret, password, token, apikey) or one built from constants gets past it.
    /// </summary>
    /// <remarks>
    /// One of the words on its own is not a list. A module reading <c>configuration["AccessKey"]</c>
    /// or a provider posting <c>"api_key"</c> is ordinary code, so a file is named only when it holds
    /// two or more different words.
    /// </remarks>
    [Fact]
    public void No_other_production_file_keeps_a_credential_word_list()
    {
        var literal = new Regex(
            "\"(passwd|pwd|privatekey|private_key|api_key|accesskey|access_key|credential)\"",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        var root = RepoRoot();
        var classifier = Path.Combine(root, "barakoCMS", "Infrastructure", "Security", "CredentialNames.cs");
        File.Exists(classifier).Should().BeTrue("the classifier is the one file allowed to hold the words");
        WordsFound(literal, classifier).Should().HaveCount(8,
            "the scan has to see all eight words where they do live, or finding none elsewhere proves nothing");

        var files = ProductionSourceFiles(root);
        files.Count.Should().BeGreaterThan(100, "the scan has to have read the production code");

        var secondLists = files
            .Where(file => !string.Equals(file, classifier, StringComparison.Ordinal))
            .Where(file => WordsFound(literal, file).Count >= 2)
            .Select(file => Path.GetRelativePath(root, file))
            .ToList();

        secondLists.Should().BeEmpty(
            "each file named here holds a second list of credential words, and a name is classified by "
            + "CredentialNames.IsCredential so that two surfaces cannot disagree");
    }

    private static HashSet<string> WordsFound(Regex literal, string file) =>
        literal.Matches(File.ReadAllText(file))
            .Select(match => match.Groups[1].Value.ToLowerInvariant())
            .ToHashSet(StringComparer.Ordinal);

    private static List<string> ProductionSourceFiles(string root)
    {
        var separator = Path.DirectorySeparatorChar;

        return Directory.EnumerateDirectories(root)
            .Where(directory =>
            {
                var name = Path.GetFileName(directory);
                return name == "barakoCMS"
                    || (name.StartsWith("BarakoCMS.", StringComparison.Ordinal) && name != "BarakoCMS.Tests");
            })
            .SelectMany(directory => Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories))
            .Where(file => !file.Contains($"{separator}bin{separator}", StringComparison.Ordinal)
                && !file.Contains($"{separator}obj{separator}", StringComparison.Ordinal))
            .ToList();
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Directory.Build.props")))
            dir = dir.Parent;
        dir.Should().NotBeNull("the test must be able to find the repository root");
        return dir!.FullName;
    }
}
