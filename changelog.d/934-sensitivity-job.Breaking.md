- **Changing a field's sensitivity held the request open while every entry's search text was
  rebuilt.** `PUT /api/content-types/{name}/fields/{field}/sensitivity` now applies the rebuild and
  the definition change as one field change, in the order it always used (raising rebuilds first,
  lowering changes the definition first). A type of up to 200 entries is still changed before the
  answer; a larger one is changed by the job queue, and the response's new `change` field names it,
  with `entriesReindexed` 0 until it completes and the definition taking the new level when it does.
  While any field change runs on the type, the endpoint answers 409, as every edit to the type's
  definition does, and an entry write to the type answers 400. A larger type's answer no longer
  means the level is in force, and these refusals are new to requests 4.9 accepted, so
  `ApiContract.Version` moves to 8.
