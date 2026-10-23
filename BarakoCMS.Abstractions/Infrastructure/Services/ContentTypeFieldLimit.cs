using Microsoft.Extensions.Configuration;

namespace barakoCMS.Infrastructure.Services;

/// <summary>
/// The most fields one content type may hold, read from <c>ContentTypes:MaxFields</c>.
/// </summary>
/// <remarks>
/// A definition is returned whole by <c>GET /api/content-types</c>, and pagination cannot bound an
/// item that is itself unbounded, so without a cap one create with 50,000 fields made every later
/// list call return megabytes. There is no endpoint that deletes a type, so it could not be undone
/// either. See #650.
///
/// Only growth is refused. A type already stored with more fields than this keeps working: it is
/// read, delivered and written to as before, and only adding a field to it is refused.
///
/// Public because the Portability module imports content types and has to apply the same cap.
/// </remarks>
public static class ContentTypeFieldLimit
{
    public const string ConfigKey = "ContentTypes:MaxFields";

    /// <summary>Several times the largest built-in blueprint type, which has under 50.</summary>
    public const int Default = 200;

    public static int Resolve(IConfiguration? configuration) =>
        Math.Max(1, configuration?.GetValue<int?>(ConfigKey) ?? Default);

    public static string TooMany(int max, int count) =>
        $"A content type may hold at most {max} fields, and this one would have {count}.";
}
