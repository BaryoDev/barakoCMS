using System.Net;

namespace BarakoCMS.Analytics.Umami.Features;

/// <summary>
/// The strict hostname check in front of the favicon fetch. The domain becomes the host of an
/// outbound request, so anything that is not a plain DNS name (a scheme, path, port, userinfo, an IP
/// literal) is refused rather than cleaned up.
/// </summary>
public static class FaviconHostname
{
    public const int MaxLength = 253;

    public static bool TryNormalize(string? value, out string host)
    {
        host = "";
        if (string.IsNullOrEmpty(value) || value.Length > MaxLength)
        {
            return false;
        }

        // Before lowercasing: U+212A (Kelvin sign) lowercases to an ASCII "k".
        if (value.Any(c => c > 127))
        {
            return false;
        }

        var lower = value.ToLowerInvariant();
        var labels = lower.Split('.');
        // A dotted name only: "localhost" and single-label intranet names have no public favicon.
        if (labels.Length < 2)
        {
            return false;
        }

        foreach (var label in labels)
        {
            if (label.Length is 0 or > 63 || label[0] == '-' || label[^1] == '-')
            {
                return false;
            }

            foreach (var c in label)
            {
                if (!(c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-'))
                {
                    return false;
                }
            }
        }

        // A real top-level domain is never all digits, which is also what an IPv4 literal looks like.
        if (labels[^1].All(char.IsAsciiDigit) || IPAddress.TryParse(lower, out _))
        {
            return false;
        }

        host = lower;
        return true;
    }
}
