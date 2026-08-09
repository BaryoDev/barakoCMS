using BarakoCMS.AI;

namespace BarakoCMS.Tests;

/// <summary>
/// A deterministic, backend-free embedder for tests: a bag-of-words vector, so texts sharing words get
/// a higher cosine similarity.
///
/// The bucket hash must be stable across processes, not merely within one. <c>string.GetHashCode</c> is
/// seeded per process in .NET Core, so it changes which words collide on every run — and a collision
/// between an unrelated word and a query word lifts that document above the similarity floor. Tests
/// asserting "the unrelated entry is not returned" then fail for a few runs in a hundred, blocking a
/// merge for reasons that have nothing to do with the change under review.
/// </summary>
public sealed class FakeEmbeddingClient : IEmbeddingClient
{
    public bool IsConfigured => true;
    private const int Dim = 128;

    public Task<float[]?> EmbedAsync(string text, CancellationToken ct)
    {
        var v = new float[Dim];
        foreach (var word in Tokenize(text))
            v[(int)(StableHash(word) % Dim)] += 1f;
        return Task.FromResult<float[]?>(v);
    }

    /// <summary>FNV-1a: same input, same output, in every process and on every platform.</summary>
    private static uint StableHash(string s)
    {
        const uint offsetBasis = 2166136261;
        const uint prime = 16777619;
        var hash = offsetBasis;
        foreach (var c in s)
        {
            hash ^= c;
            hash *= prime;
        }
        return hash;
    }

    private static IEnumerable<string> Tokenize(string? t) =>
        (t ?? string.Empty).ToLowerInvariant()
            .Split(new[] { ' ', '\n', '\t', '.', ',', '!', '?', '#', '*', '`', '-', '(', ')', '[', ']', '/' },
                   StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length > 2);
}
