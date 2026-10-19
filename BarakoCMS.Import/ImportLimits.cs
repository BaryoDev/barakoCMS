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

    public const string MaxUploadBytesKey = "Import:MaxUploadBytes";

    public const long DefaultMaxUploadBytes = 10L * 1024 * 1024;

    public const string MaxSecondsKey = "Import:MaxSeconds";

    public const int DefaultMaxSeconds = 120;

    /// <summary>Largest spreadsheet <c>POST /api/import/spreadsheet</c> reads. Default 10 MB.</summary>
    public static long MaxUploadBytes(IConfiguration configuration) =>
        configuration.GetValue<long?>(MaxUploadBytesKey) is > 0 and var bytes ? bytes : DefaultMaxUploadBytes;

    /// <summary>How long a spreadsheet import may run before it is stopped and rolled back. Default 120.</summary>
    public static int MaxSeconds(IConfiguration configuration) =>
        configuration.GetValue<int?>(MaxSecondsKey) is > 0 and var seconds ? seconds : DefaultMaxSeconds;

    /// <summary>The configured limit, or the default when it is missing or not positive.</summary>
    public static int MaxRecords(IConfiguration configuration)
    {
        var configured = configuration.GetValue<int?>(MaxRecordsKey);
        return configured is > 0 ? configured.Value : DefaultMaxRecords;
    }
}
