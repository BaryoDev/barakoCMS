using FastEndpoints;
using NSwag.Generation.Processors;
using NSwag.Generation.AspNetCore;
using NSwag.Generation.Processors.Contexts;

namespace barakoCMS.Infrastructure.OpenApi;

/// <summary>
/// Gives every operation with no tag of its own the tag derived from its namespace by
/// <see cref="EndpointTagConvention"/>.
/// </summary>
/// <remarks>
/// FastEndpoints' own auto-tagging takes a path segment, and every route here starts <c>/api/</c>,
/// so it tagged all but three operations <c>Api</c>. That is turned off (AutoTagPathSegmentIndex
/// is 0) and this fills the gap instead.
///
/// Only an operation with no tags is touched, so an endpoint that calls
/// <c>Options(x =&gt; x.WithTags("Monitoring"))</c> in its <c>Configure()</c> keeps that tag. That
/// is the escape hatch for a group whose namespace does not match the name it should carry.
/// </remarks>
internal sealed class NamespaceTagProcessor : IOperationProcessor
{
    public bool Process(OperationProcessorContext context)
    {
        if (context.OperationDescription.Operation.Tags.Count > 0)
            return true;

        var definition = (context as AspNetCoreOperationProcessorContext)?
            .ApiDescription.ActionDescriptor.EndpointMetadata
            .OfType<EndpointDefinition>()
            .FirstOrDefault();

        var tag = EndpointTagConvention.ForType(definition?.EndpointType);
        if (tag is not null)
            context.OperationDescription.Operation.Tags.Add(tag);

        return true;
    }
}
