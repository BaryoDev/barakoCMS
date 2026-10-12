- **Nothing recorded who read a Sensitive or Hidden value, or who used a share link.** A read of
  `GET /api/contents/{id}`, `GET /api/contents/by-slug/{type}/{slug}` or `GET /api/contents` that
  sends a value above Public now writes one `content.sensitive.read` audit row per request, naming
  the entries and fields and never a value. Opening or redeeming a share link writes
  `site.share_link.redeemed`, naming the link by id and the caller by address. A read whose row
  cannot be written answers 500 instead of going out unrecorded. See `docs/access-control.md` (#916).
