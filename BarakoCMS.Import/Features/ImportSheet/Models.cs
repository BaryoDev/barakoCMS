using BarakoCMS.Import.Features.BulkCreate;

namespace BarakoCMS.Import.Features.ImportSheet;

/// <summary>Multipart form: the file in <c>file</c>, and these as form fields.</summary>
internal sealed class Request
{
    public string ContentType { get; set; } = string.Empty;

    /// <summary>Zero-based row of the header, as <c>/api/import/analyze</c> numbers rows. Data starts on the next row.</summary>
    public int HeaderRow { get; set; }

    /// <summary>JSON object of content field name to zero-based column index, for example {"Title":0,"Body":2}.</summary>
    public string Mapping { get; set; } = string.Empty;

    /// <summary>False (default): any bad row writes nothing. True: good rows are written, bad ones reported.</summary>
    public bool ContinueOnError { get; set; }

    /// <summary>Content status of the created entries: Draft, Published or Archived. Published when empty.</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>Check every row and write nothing. The counts are what a real run would produce.</summary>
    public bool DryRun { get; set; }
}

internal sealed class Response
{
    /// <summary>Entries written, or that a real run would write when <c>dryRun</c> is true.</summary>
    public int Created { get; set; }

    public int Failed { get; set; }

    /// <summary>Rows below the header that held a value. Created plus failed, unless all or nothing refused the batch.</summary>
    public int DataRows { get; set; }

    /// <summary>Rows below the header with every cell blank. Skipped, and counted here.</summary>
    public int SkippedBlankRows { get; set; }

    public bool DryRun { get; set; }

    /// <summary>Each error's <c>row</c> is the zero-based sheet row, the same numbering analyze returns.</summary>
    public List<BulkCreate.Response.RowError> Errors { get; set; } = new();
}
