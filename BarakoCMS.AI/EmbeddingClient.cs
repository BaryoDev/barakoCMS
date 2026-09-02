using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace BarakoCMS.AI;

public interface IEmbeddingClient
{
    /// <summary>Embed a piece of text into a vector, or null if the embedding backend is unreachable.</summary>
    Task<float[]?> EmbedAsync(string text, CancellationToken ct);
    bool IsConfigured { get; }
}

/// <summary>
/// Calls an Ollama-style embeddings endpoint (POST /api/embeddings). Registered as a typed HttpClient.
/// Returns null on any transport error so a search/index degrades to "no results" rather than 500.
/// </summary>
public sealed class OllamaEmbeddingClient : IEmbeddingClient
{
    private readonly HttpClient _http;
    private readonly AiOptions _opts;

    public OllamaEmbeddingClient(HttpClient http, IOptions<AiOptions> opts)
    {
        _http = http;
        _opts = opts.Value;
    }

    public bool IsConfigured => _opts.IsConfigured;

    private sealed record EmbedRequest([property: JsonPropertyName("model")] string Model,
                                       [property: JsonPropertyName("prompt")] string Prompt);
    private sealed record EmbedResponse([property: JsonPropertyName("embedding")] float[]? Embedding);

    public async Task<float[]?> EmbedAsync(string text, CancellationToken ct)
    {
        if (!_opts.IsConfigured || string.IsNullOrWhiteSpace(text)) return null;
        try
        {
            var url = $"{_opts.EmbeddingBaseUrl.TrimEnd('/')}/api/embeddings";
            using var res = await _http.PostAsJsonAsync(url, new EmbedRequest(_opts.EmbeddingModel, text), ct);
            if (!res.IsSuccessStatusCode) return null;
            var body = await res.Content.ReadFromJsonAsync<EmbedResponse>(cancellationToken: ct);
            return body?.Embedding is { Length: > 0 } v ? v : null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // A cancelled request is not an unreachable backend. Swallowing this returned null, so
            // an abandoned search reported "no results" instead of stopping, and the caller could
            // not tell an empty index from a request that never finished.
            throw;
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>Cosine similarity between two equal-length vectors; 0 if mismatched or empty.</summary>
public static class Vectors
{
    public static double Cosine(float[] a, float[] b)
    {
        if (a.Length == 0 || a.Length != b.Length) return 0;
        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            na += a[i] * a[i];
            nb += b[i] * b[i];
        }
        if (na == 0 || nb == 0) return 0;
        return dot / (Math.Sqrt(na) * Math.Sqrt(nb));
    }
}
