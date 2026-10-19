using barakoCMS.Core;
using FluentAssertions;

namespace BarakoCMS.Tests;

public class CredentialRedactionTests
{
    [Theory]
    [InlineData("failed: Bearer abcDEF123456xyz was refused", "abcDEF123456xyz")]
    [InlineData("GET https://x.test/cb?token=s3cretValue99&a=1 returned 401", "s3cretValue99")]
    public void A_credential_in_a_message_is_removed_and_the_rest_is_kept(string message, string secret)
    {
        var redacted = CredentialRedaction.Redact(message);

        redacted.Should().NotBeNullOrEmpty();
        redacted.Should().NotContain(secret);
        redacted.Should().Contain(message[..6]);
    }

    [Fact]
    public void Nothing_in_a_message_with_no_credential_changes()
    {
        CredentialRedaction.Redact("the provider answered 503").Should().Be("the provider answered 503");
    }

    [Fact]
    public void A_guid_shaped_value_in_a_message_is_masked_because_a_key_can_look_like_one()
    {
        var id = Guid.Parse("3f2b8c1d-9a4e-4b7f-8d21-5c6e0a9b1f34");

        var redacted = CredentialRedaction.Redact($"Content {id} not found");

        redacted.Should().NotContain(id.ToString());
        redacted.Should().Contain("not found");
    }

    [Fact]
    public void A_reason_that_only_contains_the_letters_auth_is_kept_and_an_authorization_header_is_not()
    {
        const string reason = "Unauthorized: invalid API credentials";

        CredentialRedaction.Redact(reason).Should().Be(reason);

        var header = CredentialRedaction.Redact("Authorization: Bearer abc");
        header.Should().NotContain("abc");
        header.Should().StartWith("Authorization");
    }

    [Theory]
    [InlineData("auth: hunter2value", "hunter2value")]
    [InlineData("auth_token=hunter2value", "hunter2value")]
    [InlineData("x-auth-token: hunter2value", "hunter2value")]
    [InlineData("Authorization: hunter2value", "hunter2value")]
    public void A_value_after_an_auth_key_is_removed(string message, string secret)
    {
        CredentialRedaction.Redact(message).Should().NotContain(secret);
    }
}
