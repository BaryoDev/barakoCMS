namespace BarakoCMS.Forms;

/// <summary>
/// The module's settings, read from <c>Modules:Forms</c>. Every default is the behaviour a
/// deployment gets when it sets nothing.
/// </summary>
public sealed class FormsOptions
{
    public const int DefaultSubmissionsPerMinute = 5;
    public const int DefaultMaxBodyBytes = 32 * 1024;
    public const int DefaultMaxFieldChars = 4000;
    public const int DefaultExportMaxRows = 10_000;
    public const int DefaultNotifyTimeoutSeconds = 10;

    /// <summary>
    /// A field a visitor never sees. Any value in it means a bot filled the form, and the
    /// submission is acknowledged and dropped. A definition may not declare a field by this name.
    /// </summary>
    public string HoneypotField { get; set; } = "website";

    /// <summary>Submissions one client address may make per minute across every form.</summary>
    public int SubmissionsPerMinute { get; set; } = DefaultSubmissionsPerMinute;

    /// <summary>The largest request body the public endpoint reads. Larger is 413.</summary>
    public int MaxBodyBytes { get; set; } = DefaultMaxBodyBytes;

    /// <summary>The longest value one field accepts, measured as JSON text. Longer is 400.</summary>
    public int MaxFieldChars { get; set; } = DefaultMaxFieldChars;

    /// <summary>The most rows one CSV export returns, newest first.</summary>
    public int ExportMaxRows { get; set; } = DefaultExportMaxRows;

    /// <summary>
    /// How long the notification send may hold the response. The 202 goes out either way; a send
    /// that runs past this is abandoned and logged.
    /// </summary>
    public int NotifyTimeoutSeconds { get; set; } = DefaultNotifyTimeoutSeconds;
}
