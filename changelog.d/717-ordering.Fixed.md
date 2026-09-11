- **The public slug route could serve either of two entries sharing a slug.** It resolved with an
  unordered `FirstOrDefaultAsync`, so on a deployment that already holds duplicates the same URL
  answered with either page and could change its mind between requests. It resolves oldest first now,
  with the entry id as the tiebreak, so the answer is at least stable while the duplicates are
  cleaned up.
