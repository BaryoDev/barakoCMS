namespace barakoCMS.Infrastructure.Http;

internal static class HttpClientNames
{
    /// <summary>
    /// The outbound client that checks the target address before it connects. A misspelt name would
    /// return an unguarded client, so every caller uses this constant.
    /// </summary>
    public const string ExternalApi = "ExternalApi";
}
