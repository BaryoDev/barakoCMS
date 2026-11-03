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

    public const string SemanticSearchRateLimitPolicy = "ai-semantic-search";

    /// <summary>Semantic searches allowed per client IP in <see cref="SemanticSearchWindowSeconds"/>.</summary>
    public int SemanticSearchPermitLimit { get; set; } = 60;

    public int SemanticSearchWindowSeconds { get; set; } = 60;

    public bool IsConfigured => Enabled && !string.IsNullOrWhiteSpace(EmbeddingBaseUrl);

    /// <summary>The completion client's settings, bound from <c>Modules:AI:Completion</c>.</summary>
    public CompletionOptions Completion { get; set; } = new();
}

/// <summary>
/// Settings for <see cref="ICompletionClient"/>. Off by default. The provider is Ollama, on
/// <see cref="AiOptions.EmbeddingBaseUrl"/>, unless <see cref="Provider"/> names OpenAI or Anthropic and that
/// provider's key is set. A hosted provider with no key makes no call. The keys are never logged or returned.
/// </summary>
public sealed class CompletionOptions
{
    public bool Enabled { get; set; }

    /// <summary>Ollama (default), OpenAI or Anthropic.</summary>
    public string Provider { get; set; } = CompletionProviders.Ollama;

    public string OllamaModel { get; set; } = "llama3.2";

    /// <summary>Secret. Setting it and choosing OpenAI as the provider sends the request text to api.openai.com.</summary>
    public string? OpenAIKey { get; set; }

    public string OpenAIModel { get; set; } = "gpt-4o-mini";

    /// <summary>Secret. Setting it and choosing Anthropic as the provider sends the request text to api.anthropic.com.</summary>
    public string? AnthropicKey { get; set; }

    public string AnthropicModel { get; set; } = "claude-sonnet-5-5";

    public const int DefaultTimeoutSeconds = 30;
    public const int DefaultMaxInputChars = 20_000;
    public const int DefaultMaxOutputChars = 8_000;

    /// <summary>Seconds a call may take before it fails with <see cref="CompletionFailure.TimedOut"/>.</summary>
    public int TimeoutSeconds { get; set; } = DefaultTimeoutSeconds;

    /// <summary>Most characters in the system and data blocks together.</summary>
    public int MaxInputChars { get; set; } = DefaultMaxInputChars;

    /// <summary>Most characters a caller may ask back.</summary>
    public int MaxOutputChars { get; set; } = DefaultMaxOutputChars;
}
