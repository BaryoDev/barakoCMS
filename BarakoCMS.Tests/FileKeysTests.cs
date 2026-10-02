using BarakoCMS.Files;
using FluentAssertions;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// <see cref="FileKeys.Contradicts"/> is what keeps a private object out from under
/// <c>public/</c>, so it reads a key the way the most forgiving store would.
/// </summary>
public class FileKeysTests
{
    [Theory]
    [InlineData("public/x.pdf")]
    [InlineData("/public/x.pdf")]
    [InlineData("//public/x.pdf")]
    [InlineData("./public/x.pdf")]
    [InlineData("././/public/x.pdf")]
    [InlineData("../public/x.pdf")]
    [InlineData("a/../public/x.pdf")]
    [InlineData("a/b/../../public/x.pdf")]
    [InlineData("Public/x.pdf")]
    [InlineData("PUBLIC/x.pdf")]
    [InlineData("public\\x.pdf")]
    [InlineData("public//x.pdf")]
    [InlineData("public")]
    public void A_key_a_store_could_read_as_under_public_contradicts_a_private_file(string key)
    {
        FileKeys.Contradicts(key, isPublic: false).Should().BeTrue();
        FileKeys.Contradicts(key, isPublic: true).Should().BeFalse("the same key is where a public file belongs");
    }

    [Theory]
    [InlineData("private/x.png")]
    [InlineData("/private/x.png")]
    [InlineData("./private/x.png")]
    [InlineData("a/../private/x.png")]
    [InlineData("Private/x.png")]
    public void A_key_a_store_could_read_as_under_private_contradicts_a_public_file(string key)
    {
        FileKeys.Contradicts(key, isPublic: true).Should().BeTrue();
        FileKeys.Contradicts(key, isPublic: false).Should().BeFalse("the same key is where a private file belongs");
    }

    /// <summary>
    /// Keys under neither prefix, the flat keys of every file stored before the prefixes among them.
    /// A guard that refused everything would pass the two theories above.
    /// </summary>
    [Theory]
    [InlineData("0f3a9c.png")]
    [InlineData("0f3a9c_w320.png")]
    [InlineData("a/one.txt")]
    [InlineData("a/public/x.png")]
    [InlineData("a/private/x.png")]
    [InlineData("publicity/x.png")]
    [InlineData("privateer/x.png")]
    [InlineData("public.png")]
    [InlineData("public/../x.png")]
    [InlineData("")]
    [InlineData("/")]
    [InlineData("..")]
    public void A_key_under_neither_prefix_contradicts_nothing(string key)
    {
        FileKeys.Contradicts(key, isPublic: false).Should().BeFalse();
        FileKeys.Contradicts(key, isPublic: true).Should().BeFalse();
    }

    [Fact]
    public void The_prefix_follows_the_visibility()
    {
        FileKeys.Prefix(isPublic: true).Should().Be("public/");
        FileKeys.Prefix(isPublic: false).Should().Be("private/");
        FileKeys.PublicPrefix.Should().Be("public/");
        FileKeys.PrivatePrefix.Should().Be("private/");
    }
}
