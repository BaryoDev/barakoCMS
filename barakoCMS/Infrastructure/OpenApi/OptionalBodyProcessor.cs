using FastEndpoints;
using NSwag.Generation.AspNetCore;
using NSwag.Generation.Processors;
using NSwag.Generation.Processors.Contexts;

namespace barakoCMS.Infrastructure.OpenApi;

/// <summary>Marks a request type whose JSON body a caller may leave out.</summary>
[AttributeUsage(AttributeTargets.Class)]
internal sealed class OptionalBodyAttribute : Attribute;

/// <summary>
/// Documents the body of a request type marked <see cref="OptionalBodyAttribute"/> as not required.
/// </summary>
/// <remarks>
/// A body is documented as required by default. An endpoint that took no body and now takes an
/// optional one would otherwise change the contract for every caller that sends none.
/// </remarks>
internal sealed class OptionalBodyProcessor : IOperationProcessor
{
    public bool Process(OperationProcessorContext context)
    {
        var request = (context as AspNetCoreOperationProcessorContext)?
            .ApiDescription.ActionDescriptor.EndpointMetadata
            .OfType<EndpointDefinition>()
            .FirstOrDefault()?.ReqDtoType;

        if (request is not null
            && Attribute.IsDefined(request, typeof(OptionalBodyAttribute))
            && context.OperationDescription.Operation.RequestBody is { } body)
        {
            body.IsRequired = false;
        }

        return true;
    }
}
