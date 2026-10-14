using FastEndpoints;
using FluentValidation.Results;
using Marten;

namespace barakoCMS.Infrastructure.Jobs;

/// <summary>Marks an endpoint that rewrites the definition of the type its <c>{name}</c> route value names.</summary>
internal sealed class FieldChangeGate;

/// <summary>Refuses an edit to a type's definition, with 409, while a field change runs on the type.</summary>
/// <remarks>
/// An editor loads the whole definition and stores it back. A field change's definition step
/// landing in between would be overwritten, leaving the type declaring a field under the name a
/// rename just moved every value away from, or a raised field back at its old level.
/// </remarks>
internal sealed class FieldChangeGateProcessor : IGlobalPreProcessor
{
    public async Task PreProcessAsync(IPreProcessorContext context, CancellationToken ct)
    {
        var http = context.HttpContext;
        if (http.Response.HasStarted || http.GetEndpoint()?.Metadata.GetMetadata<FieldChangeGate>() is null)
        {
            return;
        }

        if (http.GetRouteValue("name") is not string name || string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        var session = http.RequestServices.GetRequiredService<IQuerySession>();
        if (await FieldChanges.RunningAsync(session, name, ct) is { } running)
        {
            await http.Response.SendErrorsAsync([new ValidationFailure("name", FieldChanges.Busy(running))], 409, cancellation: ct);
        }
    }
}

internal static class FieldChangeGateExtensions
{
    /// <summary>Refuses the endpoint with 409 while a field change runs on the type its <c>{name}</c> route value names.</summary>
    public static void RefusesWhileFieldChangeRuns(this EndpointDefinition definition) => definition.Metadata(new FieldChangeGate());
}
