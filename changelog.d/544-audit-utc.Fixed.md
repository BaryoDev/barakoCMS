- **`GET /api/audit` compares `from` and `to` in UTC.** `CreatedAt` is stored in UTC, but the two
  query values were compared straight against it with whatever `Kind` the model binder gave them.
  A caller filtering in a non-UTC zone had their window shifted by the offset, silently missing
  rows at both edges. Fixed the same way as the Forms module's submissions list (`AsUtc`): a value
  with an offset is converted from local to UTC, a value already tagged UTC passes through, and a
  bare value with no zone is taken as UTC, which is what `ListRequest.From` and `ListRequest.To`
  already documented.
