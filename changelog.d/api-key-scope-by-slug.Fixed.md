- **An API key could not read by slug from a type named `erase` or `rollback`.** The scope check read
  the fourth path segment as the action, so `GET /api/contents/by-slug/erase/{slug}` was taken for an
  erase and a key with `content:read` and `content:write` got 403 instead of the entry or 404. The
  check now matches only the erase and rollback route shapes, and never treats a by-slug read as
  destructive.
