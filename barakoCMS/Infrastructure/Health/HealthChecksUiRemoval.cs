namespace barakoCMS.Infrastructure.Health;

/// <summary>
/// The HealthChecks UI dashboard left core to keep EF Core and Roslyn out of the image. A
/// deployment that still switches it on gets one warning at startup instead of a silent loss.
/// </summary>
internal static class HealthChecksUiRemoval
{
    public const string SettingName = "HealthChecksUI:Enabled";

    public const string RestoringPackage = "AspNetCore.HealthChecks.UI";

    public static bool WarnIfEnabled(Microsoft.Extensions.Configuration.IConfiguration configuration, Action<string> warn)
    {
        if (!configuration.GetValue<bool>(SettingName))
        {
            return false;
        }

        warn($"{SettingName} is set, but the HealthChecks UI dashboard is no longer part of barakoCMS and /health-ui is not served. "
            + $"The plain /health, /health/live and /health/ready endpoints still answer. To get a dashboard back, host it in "
            + $"your own application with the {RestoringPackage} package, pointed at this app's /health.");
        return true;
    }
}
