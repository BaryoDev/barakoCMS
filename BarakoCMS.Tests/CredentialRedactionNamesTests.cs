using barakoCMS.Core;
using FluentAssertions;

namespace BarakoCMS.Tests;

public class CredentialRedactionNamesTests
{
    [Theory]
    [InlineData("Password")]
    [InlineData("Smtp:ApiKey")]
    [InlineData("db_connection_string")]
    [InlineData("ClientSecret")]
    public void A_name_that_reads_like_a_credential_is_one(string name)
    {
        CredentialRedaction.IsCredentialName(name).Should().BeTrue();
    }

    [Theory]
    [InlineData("Host")]
    [InlineData("Region")]
    [InlineData("")]
    [InlineData(null)]
    public void A_plain_name_is_not(string? name)
    {
        CredentialRedaction.IsCredentialName(name).Should().BeFalse();
    }

    [Theory]
    [InlineData("https://user:pw@db.test/x")]
    [InlineData("Bearer abcDEF123456xyz")]
    [InlineData("password=hunter2")]
    [InlineData("Pwd=hunter2;Server=x")]
    [InlineData("eyJhbGciOi.eyJzdWIiOiIx.abc")]
    [InlineData("a1b2c3d4e5f6a7b8c9d0a1b2c3d4e5f6")]
    public void A_value_holding_a_credential_is_one(string value)
    {
        CredentialRedaction.ValueHoldsCredential(value).Should().BeTrue();
    }

    [Theory]
    [InlineData("smtp.example.test")]
    [InlineData("eu-west-1")]
    [InlineData("587")]
    [InlineData(null)]
    public void A_plain_value_is_not(string? value)
    {
        CredentialRedaction.ValueHoldsCredential(value).Should().BeFalse();
    }
}
