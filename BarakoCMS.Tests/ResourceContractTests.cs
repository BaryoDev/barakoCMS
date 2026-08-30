using System.Reflection;
using FluentAssertions;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// No endpoint returns a stored Marten document as its wire contract.
/// </summary>
/// <remarks>
/// Five resources did: Role, UserGroup, Tenant, WorkflowDefinition and ContentTypeDefinition. Once
/// 4.0 freezes the contract that costs twice over. Renaming a stored property becomes a silent wire
/// break, and adding a stored-only property publishes it to every client the moment it is saved,
/// with no review step where anyone decides it should be public.
///
/// Asserted structurally rather than resource by resource, because the failure this prevents is the
/// sixth one, written by somebody who reasonably copies the shape of an endpoint that already exists.
/// </remarks>
public class ResourceContractTests
{
    private static readonly Assembly Core = typeof(barakoCMS.Modules.IBarakoModule).Assembly;

    /// <summary>The stored documents that must not be a response type.</summary>
    private static readonly Type[] StoredDocuments =
    [
        typeof(barakoCMS.Models.Role),
        typeof(barakoCMS.Models.UserGroup),
        typeof(barakoCMS.Models.Tenant),
        typeof(barakoCMS.Models.WorkflowDefinition),
        typeof(barakoCMS.Models.ContentTypeDefinition),
        typeof(barakoCMS.Models.Content),
        typeof(barakoCMS.Models.User),
    ];

    [Fact]
    public void No_endpoint_declares_a_stored_document_as_its_response()
    {
        var offenders = new List<string>();

        foreach (var type in Core.GetTypes())
        {
            for (var b = type.BaseType; b is not null; b = b.BaseType)
            {
                if (!b.IsGenericType) continue;

                var name = b.GetGenericTypeDefinition().Name;
                if (!name.StartsWith("Endpoint", StringComparison.Ordinal)) continue;

                // The response is the last type argument on every FastEndpoints base that has one.
                var args = b.GetGenericArguments();
                if (args.Length == 0) continue;

                var response = args[^1];

                // A paginated envelope is a wrapper; what matters is what it wraps.
                if (response.IsGenericType
                    && response.GetGenericTypeDefinition() == typeof(barakoCMS.Models.PaginatedResponse<>))
                {
                    response = response.GetGenericArguments()[0];
                }

                if (StoredDocuments.Contains(response))
                    offenders.Add($"{type.FullName} returns {response.Name}");
            }
        }

        offenders.Should().BeEmpty(
            "an endpoint that returns the stored document has no shape of its own, so renaming a "
          + "stored property is a silent wire break and adding one publishes it to every client");
    }

    // The control. A structural check that walks nothing passes on an empty set, and this project
    // has shipped that shape of gate before.
    [Fact]
    public void The_scan_actually_finds_endpoints()
    {
        var endpoints = Core.GetTypes().Count(t =>
        {
            for (var b = t.BaseType; b is not null; b = b.BaseType)
                if (b.IsGenericType && b.GetGenericTypeDefinition().Name.StartsWith("Endpoint", StringComparison.Ordinal))
                    return true;
            return false;
        });

        endpoints.Should().BeGreaterThan(20,
            "only {0} endpoints were examined, so an empty offender list proves nothing", endpoints);
    }
}
