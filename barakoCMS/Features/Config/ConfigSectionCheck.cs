using barakoCMS.Core.Interfaces;

namespace barakoCMS.Features.Config;

/// <summary>
/// Refuses to start a host whose config sections cannot all be told apart in a document: two with
/// one name, or one named like a key the document already uses for something else.
/// </summary>
internal sealed class ConfigSectionCheck(IServiceScopeFactory scopes) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        using var scope = scopes.CreateScope();
        Check(scope.ServiceProvider.GetServices<IConfigProvider>().Select(p => p.Section).ToList());
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    internal static void Check(IReadOnlyList<string> sections)
    {
        var problems = sections
            .Where(s => string.IsNullOrWhiteSpace(s) || ConfigDocument.MetadataKeys.Contains(s) || s == ConfigDocument.TenantsSection)
            .Select(s => $"'{s}' is a reserved key of the config document")
            .Concat(sections.GroupBy(s => s, StringComparer.Ordinal).Where(g => g.Count() > 1)
                .Select(g => $"'{g.Key}' is registered by {g.Count()} config providers"))
            .ToList();

        if (problems.Count > 0)
        {
            throw new InvalidOperationException($"The config sections cannot be told apart: {string.Join("; ", problems)}.");
        }
    }
}
