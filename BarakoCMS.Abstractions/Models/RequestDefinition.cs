namespace barakoCMS.Models;

/// <summary>
/// What to send through a <see cref="Connector"/>, held as configuration rather than as code.
/// </summary>
/// <remarks>
/// A connector says where and who. This says what. Together they replace the C# somebody would
/// otherwise write per integration, which is the whole point of #325.
///
/// Nothing here is a secret. The credential lives on the connector, encrypted, and is attached to
/// the finished request after this has composed it, so a template can never resolve one.
/// </remarks>
public class RequestDefinition
{
    public Guid Id { get; set; }

    /// <summary>The admin's label, "Post to the company Facebook page".</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>What a workflow action references. Unique per tenant.</summary>
    public string Slug { get; set; } = string.Empty;

    /// <summary>Which connector supplies the base URL and the credentials.</summary>
    public string ConnectorSlug { get; set; } = string.Empty;

    public string Method { get; set; } = "POST";

    /// <summary>Appended to the connector's base URL. May carry <c>{{...}}</c> holes.</summary>
    public string PathTemplate { get; set; } = string.Empty;

    public Dictionary<string, string> HeaderTemplates { get; set; } = new();

    public string? BodyTemplate { get; set; }

    public string BodyContentType { get; set; } = "application/json";

    /// <summary>
    /// A named <see cref="QueryDefinition"/> supplying <c>{{query.*}}</c> values.
    /// </summary>
    /// <remarks>
    /// <c>{{query.rows}}</c> resolves to a JSON array of the query's result rows, each one holding
    /// exactly the fields the query selects. <c>{{query.SomeField}}</c> resolves to that field from
    /// the first row, or an empty string when the query matched nothing. A hole naming a query that
    /// does not exist, or a field the query does not select, is refused rather than sent with the
    /// hole still in it: a request that posts the literal text "{{query.rows}}" to a third party is
    /// worse than one that does not run.
    /// </remarks>
    public string? QuerySlug { get; set; }

    public SuccessRule Success { get; set; } = SuccessRule.TwoHundredRange;

    /// <summary>
    /// A JSON path that must be absent for the call to count as successful, for a provider that
    /// answers 200 with an error in the body.
    /// </summary>
    public string? SuccessJsonPath { get; set; }

    /// <summary>
    /// Values to keep from the response as step outputs: the output's name, and a plain path into
    /// the JSON body such as <c>$.choices[0].message.content</c>.
    /// </summary>
    /// <remarks>
    /// Only these paths are read, never the whole body. A later step reads one the workflow
    /// captures as <c>{{actions.N.name}}</c>, and what it holds is data and is never run as a
    /// template.
    /// </remarks>
    public Dictionary<string, string> Outputs { get; set; } = new();

    /// <summary>What happens when an output's path finds nothing in the response.</summary>
    public MissingOutputRule MissingOutput { get; set; } = MissingOutputRule.Fail;

    /// <summary>
    /// The longest an output may be, in characters. A longer one fails the step rather than being
    /// cut.
    /// </summary>
    public int MaxOutputLength { get; set; } = DefaultMaxOutputLength;

    /// <summary>The cap when a definition does not set one, and the most a workflow run keeps.</summary>
    public const int DefaultMaxOutputLength = 2000;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>How to decide whether a call did what it was configured to do.</summary>
/// <remarks>
/// "It returned 200" is not the same as "it worked". Several providers answer 200 with an error in
/// the body, and an integration that reports success on a failed post is worse than one that fails
/// loudly, because this whole feature exists so that people stop watching it.
/// </remarks>
public enum SuccessRule
{
    /// <summary>Any 2xx.</summary>
    TwoHundredRange,

    /// <summary>Any 2xx, and <see cref="RequestDefinition.SuccessJsonPath"/> absent from the body.</summary>
    TwoHundredAndJsonPathAbsent,

    /// <summary>Any response at all, for a provider whose status codes mean nothing useful.</summary>
    AnyResponse,
}

/// <summary>What a Request step does when an output's path finds nothing in the response.</summary>
public enum MissingOutputRule
{
    /// <summary>The step fails, naming the output and its path. Nothing is stored.</summary>
    Fail,

    /// <summary>The output is kept as an empty string.</summary>
    Empty,
}
