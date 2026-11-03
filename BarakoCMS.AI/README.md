<div align="center">
  <img src="https://raw.githubusercontent.com/BaryoDev/barakoCMS/master/assets/icon.png" width="96" height="96" alt="BarakoCMS.AI logo" />
  <h1>BarakoCMS.AI</h1>
  <p><em>Semantic (vector) search over published content, and a completion client for other modules. Self-hosted unless you set a hosted key.</em></p>
</div>

---

Adds meaning-based search to barakoCMS. A query for "coffee brewing" finds a post titled "how to
pull a good espresso" even though they share no words, because both are compared as embeddings
rather than as text.

Embeddings are produced by a **self-hosted model, Ollama by default**, so content is never sent to
a third-party service and semantic search needs no API key. That stays true until you set a hosted
completion key (see "Completion client" below).

The API lists this module as `AI` (the module's `Name`). The NuGet package is `BarakoCMS.AI`. They are
the same module.

## Enable it

```sh
dotnet add package BarakoCMS.AI
```

```csharp
builder.Services.AddBarakoCMS(builder.Configuration);

var app = builder.Build();
app.UseBarakoCMS();
```

The package reference plus a restart is the install. `AddBarakoCMS` finds every module in the
application's dependency context, and `BarakoCMS:Modules:Enabled` decides which of them run
(`BarakoCMS__Modules__Enabled=AI`). Unset, every referenced module runs and the API logs
one warning saying so. To name it by hand instead, put
`modules.Add(new BarakoCMS.AI.AiModule())`
in the `AddBarakoCMS` callback; discovery skips a type the host already added. See `MODULES.md` in
the repository.


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
    "EmbeddingModel": "nomic-embed-text",
    "SemanticSearchScanLimit": 1000
  }
}
```

`SemanticSearchScanLimit` caps how many stored embeddings one search reads for a type, since the
search endpoint is anonymous. The default, 1000, is twice what one index run writes. A type with
more embeddings than the limit is searched over that many only, and the response says so with
`"truncated": true`.

## Completion client

`ICompletionClient` lets another module ask a model for text through one interface. Nothing in barakoCMS
or in this module calls it yet, and it is off by default. It is registered with the rest of the module,
and a module that wants it takes `ICompletionClient` from the container.

```json
{
  "Modules": {
    "AI": {
      "Enabled": true,
      "Completion": {
        "Enabled": true,
        "Provider": "Ollama",
        "OllamaModel": "llama3.2",
        "TimeoutSeconds": 30,
        "MaxInputChars": 20000,
        "MaxOutputChars": 8000
      }
    }
  }
}
```

A request is a system block, a data block and a max length. The data block is sent inside `<data>` tags
and the model is told to treat it as data. The reply carries `Text`, `Provider` and `Model`, so a caller
can print which provider and model produced it.

The size cap, the output cap and the timeout live in the client. A call over a cap, past the timeout,
with completion off, or that the provider fails throws a `CompletionException` whose `Reason` says which.
It never throws an HTTP exception.

**What leaves the instance.** With the default `Ollama` provider, requests go to `EmbeddingBaseUrl`, your
own server. Setting `Provider` to `OpenAI` and `Completion:OpenAIKey` sends the system block and the data
block of every completion call to `api.openai.com`. Setting `Provider` to `Anthropic` and
`Completion:AnthropicKey` sends them to `api.anthropic.com` (model `claude-sonnet-5-5` unless
`AnthropicModel` says otherwise). A hosted provider with no key makes no call at all. Keep the keys in
environment variables or a secret store. The client never logs or returns them, and the module declares
no configuration keys for `GET /api/modules/{name}/configuration` to show.

## Filters

Semantic search takes the delivery list's `filter[field][op]=value` parameters, with the same
Public field rule and the same caps (see `docs/delivery-api.md` in the barakoCMS repository):

```text
GET /api/public/news/semantic?q=flood+warning&filter[Category][eq]=advisory
```

The filter runs before ranking. The entries that match are read first, up to
`SemanticSearchScanLimit`, and only their embeddings are ranked. When more entries match than the
limit, the response sets `"truncated": true`. A filter on a field that is not Public, an unknown
field or operator, a sixth filter and a value over 256 characters each return 400.

The module ships **inert**: without `Ai:Enabled` (`Modules:AI:Enabled` in a module-scoped host) it registers and does nothing, so adding the
package cannot change how an existing site behaves. Point `EmbeddingBaseUrl` at your own Ollama
instance. Keep it on a private network — an embedding endpoint
exposed publicly is an open compute endpoint for anyone who finds it.

## Part of barakoCMS

This is an optional module for [barakoCMS](https://github.com/BaryoDev/barakoCMS), an open-source
headless CMS for .NET 10. Every module is published under the `barakocms-module` tag, so a single
search on nuget.org returns the whole set.

Contributions are welcome — including a module icon or other design work. See
[CONTRIBUTING.md](https://github.com/BaryoDev/barakoCMS/blob/master/CONTRIBUTING.md).

Licensed under [MIT](LICENSE).

If barakoCMS is useful to you, a star on the [repository](https://github.com/BaryoDev/barakoCMS) helps other people find it.
