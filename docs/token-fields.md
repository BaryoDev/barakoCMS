# Token fields

A `token` field holds a random value the server generates when an entry is created. No caller can
set it or change it. It is for a value that has to be unguessable: a claim stub, an unsubscribe
link, a one-time lookup code.

```json
{ "name": "ClaimToken", "displayName": "Claim token", "type": "token" }
```

## What the field declares

- **tokenLength** is how many characters a generated token has, from 16 to 128. Leave it out and it
  is 32. It is read when a token is generated, so changing it leaves stored tokens as they are.
  `tokenLength` on a field of any other type is refused.
- **sensitivity** is `Hidden` or `Sensitive`, never `Public`. A token field sent as `Public`, which
  is what leaving `sensitivity` out sends, is stored `Hidden`. Setting one to `Public` later with
  `PUT /api/content-types/{name}/fields/{field}/sensitivity` answers 400.
- `isRequired`, `defaultValue` and every validation rule are refused on a token field: the server
  fills it, so there is nothing for a caller to supply or for a rule to check.
- A transition cannot name a token field in `requiredFields` or `optionalFields`.
- An event-sourced content type cannot have one. Its fields have to stay Public, and a token is
  never Public.
- A token field cannot be named `Slug`.
- A token field cannot be added under a name entries of the type already hold a value under
  (`POST /api/content-types`, `POST /api/content-types/{name}/fields` and a bundle import answer
  with the count of such entries). Entry data can hold keys no field declares, and such a value
  would otherwise become the entry's token.

## The value

A token is characters picked by `RandomNumberGenerator` from `0123456789abcdefghjkmnpqrstvwxyz`:
the digits and the lower case letters without `i`, `l`, `o` and `u`, so one that is printed and
typed back has no look-alikes. That is 32 characters, five bits each: a 16 character token is 80
bits and the default of 32 characters is 160.

Uniqueness rests on that randomness: a new token is not looked up among the stored ones, because
the lookup cannot use an index and would scan every entry of the type for each token. Tokens one
request generates are kept apart. Among n tokens of L characters the chance that any two match is
below n squared divided by 2 to the power 5L + 1. At the shortest length, 16 characters, and a
million entries that is about 4 in 10 to the 13; at the default 32 it is below 1 in 10 to the 36.

A stored value that is not text of this alphabet, 16 to 128 characters long, is not taken as the
entry's token: the next save replaces it with a generated one.

A token is not logged.

## Writes

A value a caller sends for a token field is discarded, without an error and without being read.
This is what the API does with a field the caller may not see: the stored value is put back. For a
token it applies to every caller, a SuperAdmin included, and with `Sensitivity:Mode` set to `Off`.
It happens before permission field sets are judged, so a sent token is never the reason a write
outside a rule's `writableFields` is refused, and a blind write on an entry the caller may not read
cannot set it.

| Write | What happens to a sent value | The token afterwards |
| --- | --- | --- |
| `POST /api/contents` | discarded | generated |
| `PUT /api/contents/{id}` | discarded, sent or left out | the stored one |
| `POST /api/import/content` | discarded, for every row | generated per row |
| `POST /api/portability/import` | discarded (and an export carries none) | generated per entry |
| `POST /api/collections/{type}/push` | discarded | generated on create, stored one on update |
| `POST /api/public/forms/{slug}` | 400, a token is not a field a form accepts | generated |
| Collection sync | a sync cannot map onto a field that is not Public | generated on create, stored one on update |
| `UpdateField` workflow action | the action fails, and is not retried | the stored one |
| `CreateTask` workflow action | discarded | generated |
| A transition carrying `data` | the type refuses a transition that names a token | the stored one |
| `POST /api/contents/{id}/rollback/{versionId}` | the older version's value is discarded | the stored one |
| Scheduled publish and unpublish, status changes | no data is written | unchanged |

The rule lives in the content writer, which every one of these ends at, so a route added later gets
it without asking. Two ways around the writer exist: code that stores a `Content` document through
the session itself, and the obsolete synchronous `IContentWriter.Create` and `Append`, which cannot
read a content type. Entries written either way get no token and keep whatever they were given. One
of the first is reachable over HTTP: the Accounting module's `POST /api/accounting/accounts` and its
account upsert store the account entry directly and replace its data whole. A token field added to
the account type would not be filled for those entries, and an upsert would remove a stored one, so
the next save through the writer would issue a different token. Do not put a token field on it.

## Entries that predate the field

Adding a token field to a type that already has entries rewrites none of them. An entry gets its
token the next time its data is saved, by any of the writes above. Until then it has none. To give
every entry one, save each once: a `PUT /api/contents/{id}` with the entry's own data is enough.

## Reads

| Read | Who gets the token |
| --- | --- |
| `GET /api/contents/{id}`, `GET /api/contents`, `GET /api/contents/{id}/history` | A `Hidden` field: the roles in `visibleToRoles`, or with none listed a role holding `view_hidden`, and SuperAdmin. A `Sensitive` field: the same with `view_sensitive`. Everyone else gets the field removed (`Hidden`) or masked (`Sensitive`), as for any such field. |
| `GET /api/contents?search=` | Nobody. A token is never matched by the search, for any caller. |
| `GET /api/contents?filter[Field][eq]=` | A caller who may read the field. For anyone else the filter is refused, in the words used for a field that does not exist. |
| Everything under `/api/public/`, feeds, the sitemap, the delivery OpenAPI document | Nobody. The slug those serve comes from a field of type `slug`, or from a Public text field named `Slug`, never from a token or another field that is not Public. |
| Webhook bodies, connector requests, public forms, semantic search | Nobody. Each takes only Public fields. |
| `GET /api/portability/export` | Nobody. The field is left out of every exported entry. |

The search matches substrings, so matching a token would give it away a character at a time. That
is why it is left out even for a caller who may read the field, a Read rule whose `readableFields`
names it included. Such a caller looks an entry up by
its token with an `eq` filter.

A workflow reads the stored entry when it fills a placeholder, so `{{data.ClaimToken}}` in an email
or SMS action carries the token to whoever the action sends to. That is how a token reaches the
person it is for, and it is decided by whoever may edit workflows. The workflow's execution log does
not keep it: the log records the values of structural parameters only (content type, status, field
name, target id and the like) and replaces every other value, such as a recipient, a subject, a body
or a URL, with `[redacted]`.

## History, the audit log and backups

The entry's event stream holds the token in its created event and in every later data update, the
same value each time. `GET /api/contents/{id}/history` masks it by the rule in the table above. The
audit log holds no field values, so no token. A database backup holds both the entry and its
stream, token included.

A bundle export carries no token and an import generates new ones, so moving a site through a
bundle changes every token. To keep tokens that are already printed, move the database.

## Sensitivity mode

`Sensitivity:Mode` set to `Off` stops masking fields on the authoring API, a token included: any
caller who may read the entry then reads its token. The write rule, the search rule and everything
under `/api/public/` do not depend on the mode.
