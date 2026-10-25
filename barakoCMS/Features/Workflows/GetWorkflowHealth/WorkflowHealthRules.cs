using barakoCMS.Models;

namespace barakoCMS.Features.Workflows.GetWorkflowHealth;

/// <summary>
/// The rate and the verdict, written once. The health endpoint uses it, and so can any read that
/// wants the same verdict for runs it counted itself.
/// </summary>
/// <remarks>
/// Only Succeeded, Failed and PartiallyFailed count towards the rate. Pending, Running and
/// Cancelled runs say nothing about whether the workflow works, so they are left out.
/// </remarks>
internal static class WorkflowHealthRules
{
    internal const string Ok = "ok";
    internal const string Warn = "warn";
    internal const string Bad = "bad";

    /// <summary>A success rate at or above this many percent is ok.</summary>
    internal const int OkAtOrAbovePercent = 90;

    /// <summary>A success rate at or above this many percent, and below ok, is warn. Under it is bad.</summary>
    internal const int WarnAtOrAbovePercent = 70;

    /// <summary>The statuses the rate is taken over.</summary>
    internal static readonly RunStatus[] CountedStatuses = [RunStatus.Succeeded, RunStatus.Failed, RunStatus.PartiallyFailed];

    /// <summary>Succeeded over finished, from 0 to 1. Null when nothing has finished, which is not the same as 0 or 1.</summary>
    internal static double? Rate(int succeeded, int finished) =>
        finished <= 0 ? null : (double)succeeded / finished;

    /// <summary>Ok, warn or bad. Nothing finished is ok: there is nothing to be wrong about.</summary>
    internal static string Verdict(int succeeded, int finished)
    {
        if (finished <= 0) return Ok;

        var percent = succeeded * 100L;
        if (percent >= OkAtOrAbovePercent * (long)finished) return Ok;
        return percent >= WarnAtOrAbovePercent * (long)finished ? Warn : Bad;
    }

    /// <summary>ok for Succeeded, warn for PartiallyFailed, bad for Failed, and null for a status that is not counted.</summary>
    internal static string? Outcome(RunStatus status) => status switch
    {
        RunStatus.Succeeded => Ok,
        RunStatus.PartiallyFailed => Warn,
        RunStatus.Failed => Bad,
        _ => null,
    };

    internal static string Summary(int runs, int succeeded, int finished, string verdict)
    {
        if (runs == 0) return "No runs in the last 24 hours.";
        if (finished == 0) return $"{runs} {Plural(runs)} in the last 24 hours, none finished yet.";

        var percent = (int)(succeeded * 100L / finished);
        var word = verdict switch { Ok => "healthy", Warn => "degraded", _ => "failing" };
        return $"{succeeded} of {finished} finished {Plural(finished)} succeeded in the last 24 hours ({percent}%), {word}.";
    }

    private static string Plural(int n) => n == 1 ? "run" : "runs";
}
