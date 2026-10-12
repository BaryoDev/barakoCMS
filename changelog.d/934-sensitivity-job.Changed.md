- **Changing a field's sensitivity held the request open while every entry's search text was
  rebuilt.** `PUT /api/content-types/{name}/fields/{field}/sensitivity` now applies the rebuild and
  the definition change as one field change, in the order it always used (raising rebuilds first,
  lowering changes the definition first). A type of up to 200 entries is still changed before the
  answer; a larger one is changed by the job queue, and the response's new `change` field names it,
  with `entriesReindexed` 0 until it completes and the definition taking the new level when it does.
