using System.Net;
using System.Net.Http.Json;

namespace BarakoCMS.Tests.Features.Config;

/// <summary>
/// Content types and entries sent through the config engine in the shape an older export file has:
/// <c>{ version, contentTypes: [...], contents: [...] }</c>.
/// </summary>
internal static class BundleCalls
{
    public static object Document(IEnumerable<object> types, IEnumerable<object> records) => new
    {
        version = 1,
        contentTypes = types.ToArray(),
        contents = records.ToArray(),
    };

    /// <summary>
    /// <c>POST /api/config/apply</c> for the document, or <c>POST /api/config/plan</c> when
    /// <paramref name="dryRun"/> is set. A plan answers 200 and names what it would refuse inside
    /// the body, where an apply answers 400.
    /// </summary>
    public static async Task<(HttpStatusCode Status, string Body)> ApplyAsync(
        HttpClient client, IEnumerable<object> types, IEnumerable<object> records, bool dryRun = false,
        bool allowDestructive = false)
    {
        var ct = TestContext.Current.CancellationToken;
        var document = Document(types, records);
        var response = dryRun
            ? await client.PostAsJsonAsync("/api/config/plan", new { document }, ct)
            : await client.PostAsJsonAsync("/api/config/apply", new { document, allowDestructive }, ct);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(ct));
    }

    /// <summary>The same call for a document written out as JSON text.</summary>
    public static async Task<(HttpStatusCode Status, string Body)> ApplyJsonAsync(HttpClient client, string documentJson)
    {
        var ct = TestContext.Current.CancellationToken;
        var body = $$"""{ "document": {{documentJson}}, "allowDestructive": false }""";
        var response = await client.PostAsync(
            "/api/config/apply", new StringContent(body, System.Text.Encoding.UTF8, "application/json"), ct);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(ct));
    }
}
