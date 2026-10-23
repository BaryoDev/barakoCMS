# Build triggers

A static site has to be told when to rebuild. The `BuildHook` workflow action does that when content
publishes, and fires once for a burst of publishes instead of once per entry.

## How it behaves

- The first publish in a quiet period writes a pending build and sets it to fire at that moment plus
  `QuietSeconds` (default 60).
- Every later publish in the period finds the pending build and does nothing.
- A sweep in the background posts the hook when the period ends, then removes the pending build. The
  next publish starts a new period.
- With several instances running, one of them claims the pending build and the rest skip it.
- The post goes through the same code as the `Webhook` action. The address checks apply, and every
  post leaves a row in `WebhookDeliveries` with the status code and the start of the response body. A
  hook that answers 500 shows up there. The hook URL is stored without its path, because the token
  is usually in it.
- A failed post is not retried. Retrying would start a build the moment a provider recovers from an
  outage. The next publish schedules a new one.

Twelve publishes inside one quiet period give one post.

## Parameters

| Parameter | Meaning |
| --- | --- |
| `Preset` | `netlify`, `vercel`, `cloudflare-pages` or `github-dispatch`. |
| `Url` | The build hook URL. Required except for `github-dispatch` with a `Repository`. |
| `Secret` | `github-dispatch` only: a token that may dispatch to the repository. Stored encrypted. |
| `Repository` | `github-dispatch` only: `owner/name`. Builds the `api.github.com` dispatch URL. |
| `EventType` | `github-dispatch` only: the `event_type` sent. Default `barako-build`. |
| `QuietSeconds` | Length of the quiet period, 1 to 86400. Default 60. |

What each preset sends, always as a POST with a JSON body:

- `netlify`: `{"trigger_title":"barakoCMS publish"}`
- `vercel`, `cloudflare-pages`: `{}`
- `github-dispatch`: `{"event_type":"barako-build","client_payload":{"tenant":"...","workflowId":"..."}}` with
  `Authorization: Bearer <Secret>`, `Accept: application/vnd.github+json` and a `User-Agent`. The
  token is never written to the delivery row, and it is refused over plain http.

## Netlify

1. In Netlify, open the site, then Site configuration, Build and deploy, Build hooks. Add a build
   hook and copy its URL.
2. Create a workflow triggered by `Published` on the content type that feeds the site:

```json
{
  "name": "rebuild-site",
  "triggerContentType": "article",
  "triggerEvent": "Published",
  "actions": [
    {
      "type": "BuildHook",
      "parameters": {
        "Preset": "netlify",
        "Url": "https://api.netlify.com/build_hooks/<id>",
        "QuietSeconds": "60"
      }
    }
  ]
}
```

3. Publish an entry. After the quiet period, `GET /api/webhook-deliveries` shows one row for the
   hook, and Netlify shows one build triggered by `barakoCMS publish`.

## Cloudflare Pages

1. In Cloudflare, open the Pages project, Settings, Builds, Deploy hooks. Add a hook for the
   production branch and copy its URL.
2. Use the same workflow with `"Preset": "cloudflare-pages"` and that URL.
3. Publish an entry and check `WebhookDeliveries` the same way.

## Limits

A publish that lands while the hook is being posted can miss the build already in flight and is not
scheduled again until the post finishes. Set `QuietSeconds` longer than your usual edit bursts.
