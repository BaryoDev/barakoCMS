using NJsonSchema;
using NJsonSchema.Visitors;
using NSwag.Generation.Processors;
using NSwag.Generation.Processors.Contexts;

namespace barakoCMS.Infrastructure.OpenApi;

/// <summary>
/// Writes a pattern's closing <c>\z</c> as <c>$</c> in the served document.
/// </summary>
/// <remarks>
/// A whole-string check here ends with <c>\z</c>, because in .NET <c>$</c> also matches before a
/// final line feed. A JSON Schema pattern is ECMA-262, where <c>$</c> already means the end of the
/// input and <c>\z</c> is not an anchor: a client checking against the document would read it as a
/// literal "z", or refuse the pattern. FastEndpoints copies a validator's regex into the schema as
/// it is, so this runs once the document is built.
/// </remarks>
internal sealed class EcmaPatternProcessor : IDocumentProcessor
{
    public void Process(DocumentProcessorContext context) => new Visitor().Visit(context.Document);

    /// <summary>The pattern with a trailing <c>\z</c> written as <c>$</c>; any other pattern unchanged.</summary>
    internal static string? ToEcma(string? pattern)
    {
        if (pattern is null || !pattern.EndsWith("\\z", StringComparison.Ordinal))
        {
            return pattern;
        }

        // An even run of backslashes before the z means they escape each other and the z is a letter.
        var slashes = 0;
        for (var i = pattern.Length - 2; i >= 0 && pattern[i] == '\\'; i--)
        {
            slashes++;
        }

        return slashes % 2 == 1 ? pattern[..^2] + "$" : pattern;
    }

    private sealed class Visitor : JsonSchemaVisitorBase
    {
        protected override JsonSchema VisitSchema(JsonSchema schema, string path, string typeNameHint)
        {
            schema.Pattern = ToEcma(schema.Pattern);
            return schema;
        }
    }
}
