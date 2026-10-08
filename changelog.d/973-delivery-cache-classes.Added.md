- **A renderer could not tell how long a delivery read may be kept, and nothing answered 304.**
  Every delivery read now sends `X-Barako-Cache-Class` (`short`, `long` or `no-store`) beside the
  `Cache-Control` it always sent, which is unchanged. Each `short` or `long` read carries a weak
  ETag over the tenant and the body, `Last-Modified` where one timestamp covers the body (a type
  description, a public file), and answers `If-None-Match` and `If-Modified-Since` with a 304
  and no body. `Surrogate-Key` and `Cache-Tag` name the tenant, the type, every type it refers to
  or includes, and every entry or file the response was built from, all under `t:<tenant>`, at
  most 32 tags and 1024 bytes per header, with `X-Barako-Cache-Tags-Dropped` counting any left
  out. A preview read stays `no-store` with no tag or ETag, and so does a 404 for a public file
  whose bytes are missing. Covers the public routes, the feed, the sitemap, the type description,
  redirect lookup, public files and their metadata (BarakoCMS.Files 4.5.0) and semantic search
  (BarakoCMS.AI 4.4.0). See "Cache classes, validators and tags" in `docs/delivery-api.md` (#973,
  #561).
