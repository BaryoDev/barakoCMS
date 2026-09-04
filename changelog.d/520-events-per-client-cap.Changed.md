- **Events stream: a per-client connection cap under the instance cap.** `Delivery:Events:MaxConnections`
  counted every stream on the instance and nothing keyed on the caller, so one anonymous client could
  hold every slot and every other tenant on the instance got 503 from `GET /api/public/events`.
  `Delivery:Events:MaxConnectionsPerClient` (5) caps open streams per client address, resolved the
  way the rate limiter resolves it (the socket peer, or the forwarded client when `ForwardedHeaders`
  names the proxy). The next stream from that address gets 503 with a body naming the per-client
  limit while another address still connects, and the slot comes back when the stream closes. Zero
  turns the per-client cap off. Closes #520.
