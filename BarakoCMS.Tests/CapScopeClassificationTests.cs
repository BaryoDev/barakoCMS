using System.Reflection;
using barakoCMS.Infrastructure.Auth;
using barakoCMS.Models;
using FluentAssertions;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// Every capability is classified for <c>cap:</c> scopes, so a new one cannot become reachable from
/// an API key by accident: adding a constant to <see cref="SystemCapabilities"/> fails this test
/// until it is listed below as denied or allowed.
/// </summary>
public class CapScopeClassificationTests
{
    private static readonly HashSet<string> Allowed = new(StringComparer.OrdinalIgnoreCase)
    {
        SystemCapabilities.ViewAuditLog,
        SystemCapabilities.ManageSettings,
        SystemCapabilities.ManageContentTypes,
        SystemCapabilities.ManagePublicDelivery,
        SystemCapabilities.ViewModules,
        SystemCapabilities.ViewModuleConfiguration,
        SystemCapabilities.ViewMonitoring,
        SystemCapabilities.ManageRedirects,
        SystemCapabilities.ManageQueries,
        SystemCapabilities.ManageRequests,
        SystemCapabilities.ViewConnectors,
        SystemCapabilities.ManageWorkflows,
        SystemCapabilities.RunWorkflows,
        SystemCapabilities.ViewWorkflowRuns,
        SystemCapabilities.ViewWebhookResponseBodies,
        SystemCapabilities.RetryWorkflowActions,
        SystemCapabilities.RollbackContent,
        SystemCapabilities.EraseContent,
        SystemCapabilities.ViewJobs,
        SystemCapabilities.ManageCollectionSyncs,
        SystemCapabilities.ViewSensitive,
        SystemCapabilities.ViewNumberGaps,
    };

    private static IEnumerable<string> Constants() =>
        typeof(SystemCapabilities).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!);

    [Fact]
    public void Every_system_capability_is_classified_as_denied_or_allowed_for_cap_scopes()
    {
        var unclassified = Constants().Where(c => !CapScopes.IsDenied(c) && !Allowed.Contains(c)).ToList();

        unclassified.Should().BeEmpty("a new capability must be added to CapScopes.Denied or to Allowed in this test");
    }

    [Fact]
    public void A_capability_is_not_both_denied_and_allowed()
    {
        Allowed.Where(CapScopes.IsDenied).Should().BeEmpty();
    }

    [Fact]
    public void Stations_config_and_connector_management_are_denied()
    {
        CapScopes.IsDenied(SystemCapabilities.ManageStations).Should().BeTrue();
        CapScopes.IsDenied(SystemCapabilities.ManageConfig).Should().BeTrue();
        CapScopes.IsDenied(SystemCapabilities.ManageConnectors).Should().BeTrue();
    }
}
