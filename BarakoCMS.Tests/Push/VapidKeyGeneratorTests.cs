using BarakoCMS.Push;
using Xunit;

namespace BarakoCMS.Tests.Push;

public class VapidKeyGeneratorTests
{
    private static byte[] Decode(string s) =>
        Convert.FromBase64String(s.Replace('-', '+').Replace('_', '/').PadRight((s.Length + 3) / 4 * 4, '='));

    [Fact]
    public void Public_key_is_an_uncompressed_P256_point()
    {
        var keys = VapidKeyGenerator.Generate();
        var pub = Decode(keys.PublicKey);
        Assert.Equal(65, pub.Length);
        Assert.Equal(0x04, pub[0]);
    }

    [Fact]
    public void Private_key_is_32_bytes_and_keys_are_unpadded_base64url()
    {
        var keys = VapidKeyGenerator.Generate();
        Assert.Equal(32, Decode(keys.PrivateKey).Length);
        Assert.DoesNotContain('=', keys.PublicKey + keys.PrivateKey);
        Assert.DoesNotContain('+', keys.PublicKey + keys.PrivateKey);
        Assert.DoesNotContain('/', keys.PublicKey + keys.PrivateKey);
    }

    [Fact]
    public void Two_calls_give_different_keys()
    {
        Assert.NotEqual(VapidKeyGenerator.Generate().PrivateKey, VapidKeyGenerator.Generate().PrivateKey);
    }
}
