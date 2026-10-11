# Realtime: change feed and hub

Two pieces, used together. The hub says "something changed". The feed says what.

## Change feed

`GET /api/changes?after=<sequence>&topics=<topic>&topics=<topic>&limit=<n>`

- `after`: the last sequence the client has. Start at 0.
- `topics`: one or more topic names. Unknown names give 400. A topic the caller does not hold gives 403 with the missing capability in the body.
- `limit`: 1 to 1000, default 200.

The response has `items` (each with `sequence`, `streamId`, `type`, `topics`, `timestamp`, `data`) and `next`. Store `next` and send it as `after` on the next call. When a page comes back shorter than `limit`, the client is caught up.

Only the caller's tenant is read. The feed never reads past the event store's high-water mark, so an event that commits after a higher-numbered one is still delivered: the feed waits for it instead of skipping it.

## Hub

SignalR at `/hubs/realtime`. Sign in with the same bearer token as HTTP (or `access_token` in the query string, which only hub paths accept). Then:

```
await connection.InvokeAsync("Subscribe", "queue.tickets");
connection.On<RealtimeNotice>("changed", n => /* read the feed from your last sequence */);
```

A notice is `{ topic, sequence, streamId, type }`. It never carries event data.

### What is checked, and when

- `Subscribe` checks the topic's capability, that the token has not been revoked, and the session epoch. A revoked token or a stale session is refused there.
- An open connection closes when its access token expires. The client reconnects with a fresh token and subscribes again.
- Until then, a connection whose capability was revoked after it subscribed keeps hearing notices. Those notices carry no record data, and the feed checks the capability on every read, so the read that follows is refused. Revocation takes effect at the feed, not at the hub.

Notices come from the server instance that committed the write. On reconnect, read the feed from the last stored sequence; a missed notice never loses data.
