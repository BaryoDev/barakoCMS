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
    private static readonly Assembly Core = typeof(barakoCMS.Data.DataSeeder).Assembly;

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

                var response = Unwrapped(args[^1]);

                if (StoredDocuments.Contains(response))
                    offenders.Add($"{type.FullName} returns {response.Name}");
            }
        }

        offenders.Should().BeEmpty(
            "an endpoint that returns the stored document has no shape of its own, so renaming a "
          + "stored property is a silent wire break and adding one publishes it to every client");
    }

    private static readonly Assembly Package = typeof(barakoCMS.Models.WorkflowDefinition).Assembly;

    /// <summary>Whether a type is one the package stores or hands to a module, and so not a request.</summary>
    /// <remarks>
    /// The rule: a class in <c>barakoCMS.Models</c> of the package assembly, apart from the paging
    /// requests, which are the two types in that namespace written to be bound. Read from the
    /// namespace and not from a list, so a document added later is covered without anybody
    /// remembering to add it here. It is wider than the documents the store registers: a model
    /// that is never stored is still not a request.
    /// </remarks>
    private static bool IsPackageModel(Type type) =>
        type.IsClass
        && !type.IsArray
        && type.Assembly == Package
        && type.Namespace == "barakoCMS.Models"
        && !typeof(barakoCMS.Models.PaginatedRequest).IsAssignableFrom(type);

    /// <summary>
    /// The package model a request binds: the request itself, a type it derives from, or the element
    /// of an array or collection it is, however deeply those are nested. Null when it binds none.
    /// </summary>
    /// <remarks>
    /// An array is unwrapped before anything is asked of it. The runtime gives <c>Role[]</c> the
    /// namespace and assembly of <c>Role</c>, so the array would otherwise pass for a model itself
    /// and be reported under the wrong name.
    ///
    /// Properties are not followed. A model nested inside a request, as the sample entry of a
    /// workflow dry run is, is not found.
    /// </remarks>
    private static Type? BoundModel(Type request) => BoundModel(request, depth: 0);

    private static Type? BoundModel(Type request, int depth)
    {
        // A collection of collections ends somewhere. The bound stops a type that enumerates itself.
        if (depth > 8) return null;

        if (request.IsArray)
        {
            return BoundModel(request.GetElementType()!, depth + 1);
        }

        for (var b = request; b is not null; b = b.BaseType)
        {
            if (IsPackageModel(b)) return b;
        }

        foreach (var shape in request.GetInterfaces().Append(request))
        {
            if (!shape.IsGenericType || shape.GetGenericTypeDefinition() != typeof(IEnumerable<>)) continue;

            if (BoundModel(shape.GetGenericArguments()[0], depth + 1) is { } element) return element;
        }

        return null;
    }

    /// <summary>
    /// The same rule on the way in. An endpoint that binds the stored document lets a request set
    /// every property the document has, including one added later that no caller should choose.
    /// </summary>
    [Fact]
    public void No_endpoint_binds_a_stored_document_as_its_request()
    {
        var requests = new List<(string Endpoint, Type Request)>();

        foreach (var type in Core.GetTypes())
        {
            for (var b = type.BaseType; b is not null; b = b.BaseType)
            {
                if (!b.IsGenericType) continue;

                // Every FastEndpoints base named Endpoint puts the request first. The one named
                // EndpointWithoutRequest has none, and its own base is counted with an empty request.
                if (!b.GetGenericTypeDefinition().Name.StartsWith("Endpoint`", StringComparison.Ordinal)) continue;

                requests.Add((type.FullName ?? type.Name, b.GetGenericArguments()[0]));
            }
        }

        requests.Should().HaveCountGreaterThan(50,
            "the control: with no endpoints found there would be no offenders and nothing proven");

        var offenders = requests
            .Select(r => (r.Endpoint, r.Request, Model: BoundModel(r.Request)))
            .Where(r => r.Model is not null)
            .Select(r => $"{r.Endpoint} binds {r.Request.Name}, which is or holds {r.Model!.Name}")
            .Distinct()
            .ToList();

        offenders.Should().BeEmpty(
            "an endpoint that binds the stored document accepts every property it has, so a "
          + "server-owned one added later can be set by any caller the moment it is saved");
    }

    private sealed class WorkflowSubclassRequest : barakoCMS.Models.WorkflowDefinition;

    private sealed class RunPageRequest : barakoCMS.Models.ListRequest
    {
        public string? Status { get; init; }
    }

    private sealed class PlainRequest
    {
        public string Name { get; init; } = string.Empty;
    }

    [Fact]
    public void A_subclass_or_a_list_of_a_package_model_is_caught_and_a_paging_request_is_not()
    {
        BoundModel(typeof(barakoCMS.Models.WorkflowDefinition)).Should().Be(typeof(barakoCMS.Models.WorkflowDefinition));
        BoundModel(typeof(WorkflowSubclassRequest)).Should().Be(typeof(barakoCMS.Models.WorkflowDefinition),
            "a subclass binds every property of the document it derives from");
        BoundModel(typeof(List<barakoCMS.Models.Role>)).Should().Be(typeof(barakoCMS.Models.Role));
        BoundModel(typeof(barakoCMS.Models.Role[])).Should().Be(typeof(barakoCMS.Models.Role),
            "an array shares its element's namespace, and the model is the element");
        BoundModel(typeof(barakoCMS.Models.Role[][])).Should().Be(typeof(barakoCMS.Models.Role));
        BoundModel(typeof(IEnumerable<barakoCMS.Models.Role>)).Should().Be(typeof(barakoCMS.Models.Role));
        BoundModel(typeof(List<barakoCMS.Models.Role[]>)).Should().Be(typeof(barakoCMS.Models.Role));
        BoundModel(typeof(WorkflowSubclassRequest[])).Should().Be(typeof(barakoCMS.Models.WorkflowDefinition));
        BoundModel(typeof(List<WorkflowSubclassRequest>)).Should().Be(typeof(barakoCMS.Models.WorkflowDefinition));
        BoundModel(typeof(barakoCMS.Models.Connector)).Should().Be(typeof(barakoCMS.Models.Connector),
            "the rule is the namespace, so a document nobody listed is covered");

        BoundModel(typeof(barakoCMS.Models.PaginatedRequest)).Should().BeNull();
        BoundModel(typeof(barakoCMS.Models.ListRequest)).Should().BeNull();
        BoundModel(typeof(RunPageRequest)).Should().BeNull("a request that only pages derives from a type made to be bound");
        BoundModel(typeof(barakoCMS.Models.ListRequest[])).Should().BeNull("an array of paging requests holds no model");
        BoundModel(typeof(PlainRequest[])).Should().BeNull();
        BoundModel(typeof(PlainRequest)).Should().BeNull();
        BoundModel(typeof(string)).Should().BeNull();
    }

    /// <summary>A paginated envelope is a wrapper; what matters is what it wraps.</summary>
    /// <remarks>
    /// Walks the base types, because an envelope that adds a field of its own is a subclass of
    /// <c>PaginatedResponse</c> and still publishes the item type it pages.
    /// </remarks>
    private static Type Unwrapped(Type response)
    {
        for (var b = response; b is not null; b = b.BaseType)
        {
            if (b.IsGenericType && b.GetGenericTypeDefinition() == typeof(barakoCMS.Models.PaginatedResponse<>))
            {
                return b.GetGenericArguments()[0];
            }
        }

        return response;
    }

    private sealed class RolePage : barakoCMS.Models.PaginatedResponse<barakoCMS.Models.Role>
    {
        public int Extra { get; init; }
    }

    private sealed class NamePage : barakoCMS.Models.PaginatedResponse<string>;

    [Fact]
    public void An_envelope_subclass_is_unwrapped_to_the_item_it_pages()
    {
        Unwrapped(typeof(barakoCMS.Models.PaginatedResponse<barakoCMS.Models.Role>))
            .Should().Be(typeof(barakoCMS.Models.Role), "the control: the envelope itself");
        Unwrapped(typeof(NamePage)).Should().Be(typeof(string));
        Unwrapped(typeof(string)).Should().Be(typeof(string), "a response that is no envelope is itself");

        Unwrapped(typeof(RolePage)).Should().Be(typeof(barakoCMS.Models.Role),
            "an envelope with a field of its own still publishes the item type it pages");
        StoredDocuments.Should().Contain(Unwrapped(typeof(RolePage)),
            "so an endpoint returning a subclass around a stored document is an offender");
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
