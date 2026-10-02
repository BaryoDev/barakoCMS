using FluentAssertions;
using barakoCMS.Core.Validation;
using barakoCMS.Models;

namespace BarakoCMS.Tests;

/// <summary>
/// The value a token field is given: its length, the characters it is drawn from, and that two of
/// them are not the same.
/// </summary>
public class TokenGenerationTests
{
    [Theory]
    [InlineData(16)]
    [InlineData(32)]
    [InlineData(128)]
    public void A_token_has_the_length_asked_for_and_only_characters_of_the_alphabet(int length)
    {
        var token = TokenFields.Generate(length);

        token.Should().HaveLength(length);
        token.Should().MatchRegex("^[0-9abcdefghjkmnpqrstvwxyz]+$");
    }

    [Fact]
    public void The_alphabet_is_32_distinct_characters_with_no_look_alikes()
    {
        TokenFields.Alphabet.Should().HaveLength(32, "five bits a character, and no character drawn more often than another");
        TokenFields.Alphabet.Distinct().Should().HaveCount(32);
        TokenFields.Alphabet.Should().NotContainAny("i", "l", "o", "u");
        TokenFields.Alphabet.Should().Be(TokenFields.Alphabet.ToLowerInvariant());
    }

    [Fact]
    public void A_thousand_tokens_of_the_shortest_length_are_all_different()
    {
        var tokens = Enumerable.Range(0, 1000).Select(_ => TokenFields.Generate(TokenFields.MinLength)).ToList();

        tokens.Should().HaveCount(1000);
        tokens.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Every_character_of_the_alphabet_turns_up()
    {
        var seen = string.Concat(Enumerable.Range(0, 200).Select(_ => TokenFields.Generate(TokenFields.DefaultLength)));

        seen.Should().HaveLength(6400);
        seen.Distinct().Should().HaveCount(32, "6400 draws from 32 characters miss one with a chance below 1 in 10^80");
    }

    [Theory]
    [InlineData(null, 32)]
    [InlineData(16, 16)]
    [InlineData(100, 100)]
    [InlineData(8, 32)]
    [InlineData(500, 32)]
    public void The_length_used_is_the_declared_one_inside_the_range_and_32_otherwise(int? declared, int used)
    {
        var field = new FieldDefinition { Name = "ClaimToken", Type = "token", TokenLength = declared };

        TokenFields.LengthOf(field).Should().Be(used);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0123456789abcde")]
    [InlineData("chosen-by-an-editor")]
    [InlineData("0123456789ABCDEF")]
    [InlineData("0123456789abcdei")]
    public void A_stored_value_the_server_could_not_have_generated_is_not_a_token(string? stored)
    {
        TokenFields.IsWellFormed(stored).Should().BeFalse();
    }

    [Fact]
    public void A_generated_token_of_any_allowed_length_is_well_formed_and_a_number_is_not()
    {
        TokenFields.IsWellFormed(TokenFields.Generate(TokenFields.MinLength)).Should().BeTrue();
        TokenFields.IsWellFormed(TokenFields.Generate(TokenFields.MaxLength)).Should().BeTrue();
        TokenFields.IsWellFormed(TokenFields.Generate(TokenFields.MaxLength + 1)).Should().BeFalse();
        TokenFields.IsWellFormed(1234567890123456L).Should().BeFalse();
    }
}
