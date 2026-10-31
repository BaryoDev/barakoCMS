# Realtime: change feed and hub

Two pieces, used together. The hub says "something changed". The feed says what.

## Change feed

`GET /api/changes?after=<sequence>&topics=<topic>&topics=<topic>&limit=<n>`

- `after`: the last sequence the client has. Start at 0.
- `topics`: one or more topic names. Unknown names give 400. A topic the caller does not hold gives 403 with the missing capability in the body.
- `limit`: 1 to 1000, default 200.

The response has `items` (each with `sequence`, `streamId`, `type`, `topics`, `timestamp`, `data`) and `next`. Store `next` and send it as `after` on the next call. When a page comes back shorter than `limit`, the client is caught up to the high-water mark.

Only the caller's tenant is read. The feed never reads past the event store's high-water mark, so an event that commits after a higher-numbered one is still delivered: the feed waits for it instead of skipping it.

## Hub

SignalR at `/hubs/realtime`. Sign in with the same bearer token as HTTP (or `access_token` in the query string, which only hub paths accept). Then:

```
await connection.InvokeAsync("Subscribe", "desk.tickets");
connection.On<RealtimeNotice>("changed", n => /* read the feed from your last sequence */);
```

A notice is `{ topic, sequence, streamId, type }`. It never carries event data.

A notice goes out as soon as the write commits, but the feed stops at the high-water mark, which advances a little later. So a read right after a notice can come back with `next` below the notice's `sequence`. When that happens, read again after a short delay (for example 250 ms, backing off) until `next` reaches the `sequence`. Do not treat the short page as "nothing new" and wait for the next notice.

### The workflow-runs topic

`Subscribe("workflow-runs")` needs the `view_workflow_runs` capability. A notice goes out for each workflow run written in your tenant. It has no sequence (always 0) and `type` is `WorkflowRun`, because runs are documents and not events. `GET /api/changes` does not take this topic: read `GET /api/workflow-runs?changedSince=<cursor>` instead. See `docs/workflow-runs.md`. The name `workflow-runs` is reserved: a module or content type cannot declare it. When one commit writes more than 50 runs, a single notice goes out instead of one per run, with `streamId` of all zeros and `type` `WorkflowRunsChanged`. Treat it as "read the list again".

### What is checked, and when

- `Subscribe` checks the topic's capability, that the token has not been revoked, and the session epoch. A revoked token or a stale session is refused there.
- An open connection closes when its access token expires. The client reconnects with a fresh token and subscribes again.
- Until then, a connection whose capability was revoked after it subscribed keeps hearing notices. Those notices carry no record data, and the feed checks the capability on every read, so the read that follows is refused. Revocation takes effect at the feed, not at the hub.
- API keys are refused. The hub is for signed-in users and stations. A connection that authenticates with an API key is aborted when it opens, and `Subscribe` refuses it too.
- The device check is not applied on the hub. If a host supplies an `IDeviceGate` that refuses a device, a token bound to that device can still connect and subscribe. Core's default gate refuses nothing. Notices carry metadata only (topic, sequence, stream id, event type), and the feed applies the device check on every read.
- A revoked station stays in the groups it already joined until its 15 minute ticket expires and the connection closes. `Subscribe` refuses it at once, and the reconnect is refused at sign-in.
- A station should send its key in the `Authorization: Bearer bsta_...` header. The hub also reads the key from `access_token` in the query string, for a browser display board that cannot set headers on a WebSocket. A station key never expires, and the full URL, query string included, lands in reverse proxy and request logs, so the query form puts a permanent secret in your access logs. The .NET and MAUI SignalR clients can send the header.

Notices come from the server instance that committed the write. On reconnect, read the feed from the last stored sequence; a missed notice never loses data.

Content types can declare a topic too, see `docs/realtime-content-topics.md`.
