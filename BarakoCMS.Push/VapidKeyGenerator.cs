using System.Security.Cryptography;

namespace BarakoCMS.Push;

public sealed record VapidKeys(string PublicKey, string PrivateKey);

public static class VapidKeyGenerator
{
    public static VapidKeys Generate()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var p = ecdsa.ExportParameters(true);
        var pub = new byte[65];
        pub[0] = 0x04;
        Pad(p.Q.X!, 32).CopyTo(pub, 1);
        Pad(p.Q.Y!, 32).CopyTo(pub, 33);
        return new VapidKeys(Encode(pub), Encode(Pad(p.D!, 32)));
    }

    internal static byte[] Pad(byte[] value, int length)
    {
        if (value.Length == length) return value;
        var padded = new byte[length];
        value.CopyTo(padded, length - value.Length);
        return padded;
    }

    internal static string Encode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    internal static bool TryDecode(string? value, int expectedLength, out byte[] bytes)
    {
        bytes = [];
        if (string.IsNullOrWhiteSpace(value)) return false;
        try
        {
            var s = value.Replace('-', '+').Replace('_', '/');
            s = s.PadRight((s.Length + 3) / 4 * 4, '=');
            bytes = Convert.FromBase64String(s);
            return bytes.Length == expectedLength;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
