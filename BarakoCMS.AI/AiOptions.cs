namespace BarakoCMS.AI;

/// <summary>
/// Configuration for the AI module, bound from the "Ai" section. Off by default, so the module ships
/// inert. Points at an OpenAI-compatible/Ollama embeddings endpoint; nothing leaves the host except a
/// call to that endpoint (self-hosted by default).
/// </summary>
public sealed class AiOptions
{
    public const string SectionName = "Ai";

    public bool Enabled { get; set; }

    /// <summary>Base URL of the embedding server, e.g. http://ollama:11434 (Ollama).</summary>
    public string EmbeddingBaseUrl { get; set; } = "http://localhost:11434";

    /// <summary>Embedding model name, e.g. nomic-embed-text.</summary>
    public string EmbeddingModel { get; set; } = "nomic-embed-text";

    public const int DefaultSemanticSearchScanLimit = 1000;

    /// <summary>
    /// Most embeddings one semantic search reads for a type. The endpoint is anonymous, so this is what
    /// bounds the memory a caller can make one request cost. When a type holds more, the search ranks
    /// only this many and the response sets <c>truncated</c>, since a better match may sit outside them.
    /// The default is twice the 500 entries one index run writes, so a type indexed that way is still
    /// searched whole, and at 768 dimensions a full scan stays near 8 MB.
    /// </summary>
    public int SemanticSearchScanLimit { get; set; } = DefaultSemanticSearchScanLimit;

    public bool IsConfigured => Enabled && !string.IsNullOrWhiteSpace(EmbeddingBaseUrl);
}
