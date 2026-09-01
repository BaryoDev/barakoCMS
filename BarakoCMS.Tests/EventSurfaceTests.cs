using System.Reflection;
using FluentAssertions;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// DECISIONS.md D4: the event stream is internal, and history is exposed only as a projected,
/// versioned view. No API response carries an event type name or an event payload.
///
/// The constraint held by luck until this test existed. Nobody chose it; the history endpoint
/// projects to a DTO because whoever wrote it projected out of ordinary API hygiene. The way it
/// gets removed is not a bad decision, it is a reasonable request: a client wants to tell a status
/// change from an edit, someone puts a ContentCreated on a response, and every event record's shape
/// is public API from that release on, so reshaping one behind an upcaster is a wire break.
///
/// Response types are taken from what the endpoints actually declare rather than from a list kept
/// here, because a list passes forever the moment somebody adds the next endpoint.
/// </summary>
public class EventSurfaceTests
{
    private static readonly Assembly Core = typeof(barakoCMS.Modules.IBarakoModule).Assembly;

    private const string EventNamespace = "barakoCMS.Events.";

    /// <summary>
    /// Every response type an endpoint declares, plus every type under Features/ named as a
    /// response. The second half catches a response record written before its endpoint is wired up;
    /// the first half catches the ones that are not named Response at all, such as MetricsSummary.
    /// </summary>
    private static Type[] ResponseRoots()
    {
        var declared = Core.GetTypes()
            .Where(t => t.FullName?.StartsWith("barakoCMS.Features.", StringComparison.Ordinal) == true)
            .Select(DeclaredResponseType)
            .Where(t => t is not null)
            .Select(t => t!);

        var byName = Core.GetTypes()
            .Where(t => t.FullName?.StartsWith("barakoCMS.Features.", StringComparison.Ordinal) == true)
            .Where(t => t.Name.EndsWith("Response", StringComparison.Ordinal));

        return declared.Concat(byName).Distinct().OrderBy(t => t.FullName, StringComparer.Ordinal).ToArray();
    }

    /// <summary>
    /// The TResponse an endpoint is declared in terms of, or null if it does not return one.
    /// </summary>
    /// <remarks>
    /// Every FastEndpoints base collapses to Endpoint&lt;TRequest, TResponse&gt;: Endpoint&lt;TRequest&gt;
    /// derives from it with object, EndpointWithoutRequest&lt;TResponse&gt; derives from it with
    /// EmptyRequest. So walking the base chain for that one open generic finds all four spellings,
    /// and taking the second argument is the response in every case. Reading the argument by
    /// position off whatever generic base appears first would take TRequest from Endpoint&lt;TRequest&gt;.
    /// </remarks>
    private static Type? DeclaredResponseType(Type endpoint)
    {
        for (var t = endpoint.BaseType; t is not null; t = t.BaseType)
        {
            if (!t.IsGenericType || t.GetGenericTypeDefinition() != typeof(FastEndpoints.Endpoint<,>))
                continue;

            var response = t.GetGenericArguments()[1];
            return response == typeof(object) ? null : response;
        }

        return null;
    }

    private sealed record Walk(List<string> Leaks, HashSet<Type> Examined);

    /// <summary>
    /// Follows everything that can put a type on the wire: property types, constructor parameters,
    /// public fields, array elements and generic arguments. A Dictionary&lt;string, ContentUpdated&gt;
    /// or a List&lt;ContentCreated&gt; is the same leak one level down, and a positional record puts
    /// its payload in a constructor parameter before it is ever a property.
    /// </summary>
    private static Walk Reachable(IEnumerable<Type> roots)
    {
        var walk = new Walk([], []);

        foreach (var root in roots)
            Visit(root, root.Name, walk);

        return walk;
    }

    private static void Visit(Type type, string path, Walk walk)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;

        if (type.IsGenericParameter || !walk.Examined.Add(type))
            return;

        if (type.HasElementType)
        {
            Visit(type.GetElementType()!, path + "[]", walk);
            return;
        }

        if (type.FullName?.StartsWith(EventNamespace, StringComparison.Ordinal) == true)
        {
            walk.Leaks.Add($"{path} reaches {type.FullName}");
            return;
        }

        // Generic arguments are followed whoever declared the container, because the container is
        // usually a BCL collection and the payload inside it is ours.
        foreach (var argument in type.GetGenericArguments())
            Visit(argument, $"{path}<{argument.Name}>", walk);

        if (!IsOurs(type) || type.IsEnum)
            return;

        const BindingFlags Members = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        foreach (var property in type.GetProperties(Members))
            Visit(property.PropertyType, $"{path}.{property.Name}", walk);

        foreach (var field in type.GetFields(Members).Where(f => !f.IsPrivate))
            Visit(field.FieldType, $"{path}.{field.Name}", walk);

        foreach (var parameter in type.GetConstructors().SelectMany(c => c.GetParameters()))
            Visit(parameter.ParameterType, $"{path}({parameter.Name})", walk);
    }

    // Bounds the walk. Recursing into the members of BCL types reaches most of the framework and
    // proves nothing, and an event type cannot hide inside one except as a generic argument, which
    // is followed above regardless of who declared the container.
    private static bool IsOurs(Type type) =>
        type.FullName?.StartsWith("barakoCMS.", StringComparison.Ordinal) == true
     || type.FullName?.StartsWith("BarakoCMS.", StringComparison.Ordinal) == true;

    [Fact]
    public void No_response_type_reaches_an_event_type()
    {
        var walk = Reachable(ResponseRoots());

        walk.Leaks.Should().BeEmpty(
            "an event type on a response freezes that record's shape as public API, and the point of "
          + "keeping the stream internal is that upcasters can reshape it. Project to a DTO the "
          + "endpoint owns instead. Features/Content/History is the worked example: it maps events "
          + "onto VersionResponse and its changeType is a vocabulary decided server side rather than "
          + "the CLR type name");
    }

    // The controls. A reflection query with a typo finds nothing and the assertion above passes on
    // an empty set, which is the shape of gate this project has been bitten by repeatedly. Two are
    // needed: one that the query found real response types, and one that the walk actually reports a
    // leak when there is one, because a walk that returns nothing also passes.
    [Fact]
    public void The_guard_examines_the_real_response_surface()
    {
        var roots = ResponseRoots();
        var walk = Reachable(roots);

        roots.Should().HaveCountGreaterThan(50,
            "there are more than 60 endpoints under Features/ and most declare a response. A small "
          + "number here means the base-type walk stopped matching, not that the API shrank");

        roots.Should().Contain(typeof(barakoCMS.Features.Content.History.VersionResponse),
            "history is the response closest to the stream, so it is the one this guard exists for");

        walk.Examined.Should().HaveCountGreaterThan(100,
            "the roots are only the entry points. If the walk stops descending it still reports no "
          + "leaks, and nothing above would fail");
    }

    private sealed class Bait
    {
        public Dictionary<string, barakoCMS.Events.ContentUpdated>? Payloads { get; set; }
    }

    private sealed record NestedBait(List<Bait> Inner);

    private sealed record SafeBait(Guid Id, string Name, Dictionary<string, object>? Data);

    [Fact]
    public void The_walk_reports_an_event_type_buried_two_levels_down()
    {
        var walk = Reachable([typeof(NestedBait)]);

        walk.Leaks.Should().ContainSingle()
            .Which.Should().Contain("barakoCMS.Events.ContentUpdated",
                "the leak is a constructor parameter, then a List, then a property, then a "
              + "Dictionary value. Checking declared property types alone finds none of that");
    }

    [Fact]
    public void The_walk_reports_nothing_on_a_response_that_carries_no_event()
    {
        Reachable([typeof(SafeBait)]).Leaks.Should().BeEmpty(
            "otherwise the test above passes because the walk flags everything");
    }
}
