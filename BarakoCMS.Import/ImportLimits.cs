using Microsoft.Extensions.Configuration;

namespace BarakoCMS.Import;

/// <summary>How many records one <c>POST /api/import/content</c> may create.</summary>
/// <remarks>
/// Every record is validated, run through its type's hooks and staged in one unit of work, so the
/// record count is time and memory held by one request. Past the limit the request answers 400
/// before any record is checked.
/// </remarks>
public static class ImportLimits
{
    public const string MaxRecordsKey = "Import:MaxRecords";

    public const int DefaultMaxRecords = 5000;

    /// <summary>The configured limit, or the default when it is missing or not positive.</summary>
    public static int MaxRecords(IConfiguration configuration)
    {
        var configured = configuration.GetValue<int?>(MaxRecordsKey);
        return configured is > 0 ? configured.Value : DefaultMaxRecords;
    }
}
