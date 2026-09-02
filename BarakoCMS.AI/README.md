<div align="center">
  <img src="https://raw.githubusercontent.com/BaryoDev/barakoCMS/master/assets/icon.png" width="96" height="96" alt="BarakoCMS.AI logo" />
  <h1>BarakoCMS.AI</h1>
  <p><em>Semantic (vector) search over published content, with no third-party API key.</em></p>
</div>

---

Adds meaning-based search to barakoCMS. A query for "coffee brewing" finds a post titled "how to
pull a good espresso" even though they share no words, because both are compared as embeddings
rather than as text.

Embeddings are produced by a **self-hosted model — Ollama by default** — so content is never sent to
a third-party service and there is no API key to manage or leak.

## Enable it

```csharp
builder.Services.AddBarakoCMS(builder.Configuration, modules =>
{
    modules.Add(new BarakoCMS.AI.AiModule());
});

var app = builder.Build();
app.UseBarakoCMS();
```


## What it will and will not index

The boundaries are deliberate, because a search index is an easy place to leak something:

- Indexes **only fields explicitly marked Public** in the content type's schema.
- Searches **only Published content whose document sensitivity is Public**.
- A Sensitive or Hidden document is never embedded, so it cannot surface through a similarity
  search even by accident.

## Endpoints

| Method & path | Purpose | Access |
|---|---|---|
| `GET  /api/public/{type}/semantic?q=` | Semantic search within a content type | Anonymous |
| `POST /api/ai/index/{type}` | Build or rebuild the index for a type | `Admin` / `SuperAdmin` |

## Configuration

```json
{
  "Ai": {
    "Enabled": true,
    "EmbeddingBaseUrl": "http://localhost:11434",
    "EmbeddingModel": "nomic-embed-text"
  }
}
```

The module ships **inert**: without `Ai:Enabled` it registers and does nothing, so adding the
package cannot change how an existing site behaves. Point `EmbeddingBaseUrl` at your own Ollama
instance. Keep it on a private network — an embedding endpoint
exposed publicly is an open compute endpoint for anyone who finds it.

## Part of barakoCMS

This is an optional module for [barakoCMS](https://github.com/BaryoDev/barakoCMS), an open-source
headless CMS for .NET 10. Every module is published under the `barakocms-module` tag, so a single
search on nuget.org returns the whole set.

Contributions are welcome — including a module icon or other design work. See
[CONTRIBUTING.md](https://github.com/BaryoDev/barakoCMS/blob/master/CONTRIBUTING.md).

Licensed under MPL-2.0.

If barakoCMS is useful to you, a star on the [repository](https://github.com/BaryoDev/barakoCMS) helps other people find it.
