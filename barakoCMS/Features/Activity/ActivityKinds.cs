namespace barakoCMS.Features.Activity;

/// <summary>
/// The kinds of activity row. The dotted names are the API's, stable, and a client renders one it
/// does not know by its name. Severity is what the console's Needs you list reads.
/// </summary>
internal static class ActivityKinds
{
    public const string Published = "published";
    public const string Scheduled = "scheduled";
    public const string Edited = "edited";
    public const string RunSucceeded = "run.succeeded";
    public const string RunFailed = "run.failed";
    public const string Bounced = "bounced";
    public const string SignIn = "signin";
    public const string Imported = "imported";
    public const string TypeChanged = "type.changed";

    public static readonly IReadOnlyList<string> All =
    [
        Published, Scheduled, Edited, RunSucceeded, RunFailed, Bounced, SignIn, Imported, TypeChanged,
    ];

    public const string Info = "info";
    public const string Warning = "warning";
    public const string Error = "error";

    public static string SeverityOf(string kind) => kind switch
    {
        RunFailed => Error,
        Bounced => Warning,
        _ => Info,
    };

    /// <summary>
    /// The sentence for a kind. Fixed text with no value of the entry in it: the title is in
    /// <c>subject.title</c> and a client sets it in whatever markup it uses, escaped.
    /// </summary>
    public static string TextOf(string kind, string? detail = null) => kind switch
    {
        Published => "Published an entry.",
        Scheduled => "Scheduled an entry to publish.",
        Edited => "Edited an entry.",
        RunSucceeded => "A workflow run finished.",
        RunFailed => "A workflow run failed.",
        Bounced => "An email bounced.",
        SignIn => "Signed in.",
        Imported => detail switch
        {
            "redirect.imported" => "Imported redirects.",
            "tenant.imported" => "Imported a tenant bundle.",
            _ => "Imported data.",
        },
        TypeChanged => "Changed a content type.",
        _ => "Something happened.",
    };
}
