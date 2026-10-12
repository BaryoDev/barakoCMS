# Public lookup

A content type can answer one anonymous question: "is there an entry with this reference, and does
this identifier belong to it?" A runner checks a registration with the number on their receipt and
their last name, without an account, and gets back the status and bib number and nothing else.

It is separate from public delivery. A type with a lookup is not listed, searched or served by slug
unless it is also publicly deliverable, which a type holding registrations usually is not.

## Setting it up

`PUT /api/content-types/{name}/lookup`, with the `manage_public_delivery` capability, the same gate
as turning public delivery on:

```json
{
  "lookup": {
    "matchField": "Reference",
    "verifyFields": ["LastName", "Email"],
    "returnFields": ["Status", "BibNumber"],
    "reveal": {
      "fields": ["ClaimToken"],
      "settingType": "race-settings",
      "settingField": "KitReleaseOpen"
    }
  }
}
```

- `matchField` is compared with the reference the caller sends.
- `verifyFields` are compared with the identifier; any one of them matching is enough. The match
  field cannot also be a verify field, or the reference alone would return the entry.
- `returnFields` are what a found entry hands back. Each must be Public. A Sensitive, Hidden or
  token field is refused with 400, and a field made Sensitive or Hidden after the lookup was set is
  left out of the answer.
- `reveal` is optional. Its fields are added to the answer only while `settingField`, a bool field
  on the singleton type `settingType`, is true on that type's Published entry. A reveal field may be
  Public or a token, so a claim token can be handed out from the day an editor opens kit collection.

Send `{"lookup": null}` to remove it. Each change is recorded in the audit log as
`contenttype.lookup.changed`. `GET /api/content-types` lists each type's lookup as `lookup`.

## Asking

```
POST /api/public/{type}/lookup
{"reference": "CMF-2026-0042", "verify": "dela cruz"}
```

Both values are compared ignoring case and surrounding spaces. A POST, so a last name or an email
is in the body and not in a URL that a proxy logs. Any entry but an Archived one is found, whatever
its status. A match answers 200:

```json
{"data": {"Status": "Paid", "BibNumber": "B-1042"}}
```

Everything else answers the same empty 404: a type that does not exist or has no lookup, a reference
nobody holds, and a reference whose identifier does not match. The route cannot be used to learn
whether a reference exists. Responses are never cached.

## Throttling

Two limits, both counted in memory on each instance like every other limit:

| Section | Default | Counts |
| --- | --- | --- |
| `RateLimiting:Lookup` | 10 in 60 seconds | lookups per tenant and visitor |
| `RateLimiting:LookupReference` | 5 in 900 seconds | lookups of one reference, from any address |

The second is what stops many addresses guessing the identifier for one known reference. Every
attempt counts, found or not, so a reference nobody holds is throttled exactly like one somebody
does. The cost is that anyone can use up a reference's attempts for fifteen minutes, the same trade
account lockout makes.

A renderer that answers a lookup form server side can name the visitor the way it does for share
links, with `RateLimiting:Renderer:Key` and `X-Barako-Visitor-IP`; see
[deploy-in-production.md](deploy-in-production.md).
