using BarakoCMS.AI;

namespace BarakoCMS.Tests;

/// <summary>
/// A deterministic, backend-free embedder for tests: a bag-of-words vector, so texts sharing words get
/// a higher cosine similarity. Within one test process the hashing is stable, so index-time and
/// query-time embeddings line up.
/// </summary>
public sealed class FakeEmbeddingClient : IEmbeddingClient
{
    public bool IsConfigured => true;
    private const int Dim = 128;

    public Task<float[]?> EmbedAsync(string text, CancellationToken ct)
    {
        var v = new float[Dim];
        foreach (var word in Tokenize(text))
            v[(int)((uint)word.GetHashCode() % Dim)] += 1f;
        return Task.FromResult<float[]?>(v);
    }

    private static IEnumerable<string> Tokenize(string? t) =>
        (t ?? string.Empty).ToLowerInvariant()
            .Split(new[] { ' ', '\n', '\t', '.', ',', '!', '?', '#', '*', '`', '-', '(', ')', '[', ']', '/' },
                   StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length > 2);
}
