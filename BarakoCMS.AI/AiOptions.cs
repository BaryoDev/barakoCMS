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

    public bool IsConfigured => Enabled && !string.IsNullOrWhiteSpace(EmbeddingBaseUrl);
}
