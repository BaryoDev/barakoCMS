using Microsoft.Extensions.Configuration;

namespace BarakoCMS.Portability;

/// <summary>How much one import may carry.</summary>
/// <remarks>
/// Every record is validated, run through its type's hooks and staged in one unit of work, so the
/// size of a bundle is time and memory held by one request. Past the limit the import answers 400
/// before reading anything; a larger site is moved in several bundles, one or a few types each.
/// </remarks>
public static class PortabilityLimits
{
    public const string MaxImportRecordsKey = "Portability:MaxImportRecords";

    public const int DefaultMaxImportRecords = 5000;

    public const int MaxImportContentTypes = 500;

    /// <summary>The configured record limit, or the default when it is missing or not positive.</summary>
    public static int MaxImportRecords(IConfiguration configuration)
    {
        var configured = configuration.GetValue<int?>(MaxImportRecordsKey);
        return configured is > 0 ? configured.Value : DefaultMaxImportRecords;
    }
}
