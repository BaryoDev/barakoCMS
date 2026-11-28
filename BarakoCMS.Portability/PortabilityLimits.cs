using Microsoft.Extensions.Configuration;

namespace BarakoCMS.Portability;

/// <summary>How much one apply of the <c>contents</c> section may carry.</summary>
/// <remarks>
/// Every record is validated, run through its type's hooks and staged in one unit of work, so the
/// size of a document is time and memory held by one request. Past the limit the plan and the apply answer 400
/// before reading anything; a larger site is moved in several documents, one or a few types each.
/// </remarks>
public static class PortabilityLimits
{
    public const string MaxImportRecordsKey = "Portability:MaxImportRecords";

    public const int DefaultMaxImportRecords = 5000;

    /// <summary>The configured record limit, or the default when it is missing or not positive.</summary>
    public static int MaxImportRecords(IConfiguration configuration)
    {
        var configured = configuration.GetValue<int?>(MaxImportRecordsKey);
        return configured is > 0 ? configured.Value : DefaultMaxImportRecords;
    }
}
