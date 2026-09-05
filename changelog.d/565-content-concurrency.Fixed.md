- **Content now catches a concurrent write instead of silently losing it.** Two editors saving the
  same entry used to leave one edit gone with no error, and the history recorded the surviving write
  as though the other never happened. `Content` gets Marten's own optimistic concurrency, `GET
  /api/contents/{id}` returns the entry's version as an `ETag`, and `PUT` accepts it back as
  `If-Match`, answering 412 when it does not match. Two writers racing with no version sent at all
  now also get one success and one 412, rather than a second write nobody could see coming.
  `Content:Concurrency:Require` (default `false` in 4.x) decides whether a write that sends no
  version is refused instead; a 3.x client upgrading in place sends none, so the default keeps that
  path working. Same shape as `Lifecycle:EnforceTransitions`. Event-sourced content types are
  unaffected: they already refuse a stale or missing version on the stream (D3).
