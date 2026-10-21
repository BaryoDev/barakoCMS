using System.Security.Cryptography;
using System.Text;

namespace barakoCMS.Features.Workflows.Actions;

/// <summary>An id made from a key, so a retry of the same attempt names the same entry.</summary>
internal static class DeterministicId
{
    public static Guid From(string key)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(key))[..16];
        bytes[7] = (byte)((bytes[7] & 0x0F) | 0x80);   // version 8, custom
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);   // RFC 4122 variant
        return new Guid(bytes);
    }
}
