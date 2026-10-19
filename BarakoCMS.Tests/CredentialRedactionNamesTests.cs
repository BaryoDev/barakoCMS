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
    [InlineData("Relay:Password")]
    [InlineData("A:B:ApiKey")]
    [InlineData("Secrets:Foo")]
    [InlineData("SigningKey")]
    [InlineData("ClientKey")]
    [InlineData("EncryptionKey")]
    [InlineData("HmacSecret")]
    [InlineData("Passphrase")]
    [InlineData("Salt")]
    [InlineData("Pin")]
    [InlineData("admin pin")]
    [InlineData("Admin Pin")]
    [InlineData("Relay/pin")]
    [InlineData("pass\uD800")]
    [InlineData("\uD800")]
    [InlineData("pin")]
    [InlineData("pw")]
    [InlineData("admin.pin")]
    [InlineData("db.pw")]
    [InlineData("Relay:pin")]
    [InlineData("AdminPin")]
    [InlineData("PinNumber")]
    [InlineData("ADMINPIN")]
    [InlineData("admin_pin")]
    [InlineData("Pw")]
    [InlineData("db_pw")]
    [InlineData("AdminPw")]
    [InlineData("Pwd")]
    [InlineData("Cookie")]
    [InlineData("Otp")]
    [InlineData("Dsn")]
    [InlineData("BearerValue")]
    [InlineData("SessionId")]
    [InlineData("Signature")]
    [InlineData("\uFF30\uFF41\uFF53\uFF53\uFF57\uFF4F\uFF52\uFF44")]
    [InlineData("SasToken")]
    [InlineData("CertPfx")]
    [InlineData("Jwt:Issuer")]
    public void A_name_that_reads_like_a_credential_is_one(string name)
    {
        CredentialRedaction.IsCredentialName(name).Should().BeTrue();
    }

    [Theory]
    [InlineData("Host")]
    [InlineData("Region")]
    [InlineData("Relay:Host")]
    [InlineData("Shipping")]
    [InlineData("Spinning")]
    [InlineData("Pinned")]
    [InlineData("Pwned")]
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
