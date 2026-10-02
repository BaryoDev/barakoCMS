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

## The value

A token is characters picked by `RandomNumberGenerator` from `0123456789abcdefghjkmnpqrstvwxyz`:
the digits and the lower case letters without `i`, `l`, `o` and `u`, so one that is printed and
typed back has no look-alikes. That is 32 characters, five bits each: a 16 character token is 80
bits and the default of 32 characters is 160.

Before a token is used it is checked against the entries stored under the same content type name in
the tenant, and against tokens the same request generated and has not committed yet. A value
already held is thrown away and another is generated, up to five times, after which the write fails
and nothing is stored. No database index backs the check, so two requests committing at the same
moment are not compared with each other. At the shortest length two of them matching is as likely
as two random 80 bit numbers matching.

A token is not logged.

## Writes

A value a caller sends for a token field is discarded, without an error and without being read.
This is what the API does with a field the caller may not see: the stored value is put back. For a
token it applies to every caller, a SuperAdmin included, and with `Sensitivity:Mode` set to `Off`.

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
it without asking. Two ways around the writer exist and neither is reachable over HTTP: code that
stores a `Content` document through the session itself, and the obsolete synchronous
`IContentWriter.Create` and `Append`, which cannot read a content type. Entries written either way
get no token and keep whatever they were given.

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
| Everything under `/api/public/`, feeds, the sitemap, the delivery OpenAPI document | Nobody. |
| Webhook bodies, connector requests, public forms, semantic search | Nobody. Each takes only Public fields. |
| `GET /api/portability/export` | Nobody. The field is left out of every exported entry. |

The search matches substrings, so matching a token would give it away a character at a time. That
is why it is left out even for a caller who may read the field. Such a caller looks an entry up by
its token with an `eq` filter.

A workflow reads the stored entry when it fills a placeholder, so `{{data.ClaimToken}}` in an email
or SMS action carries the token to whoever the action sends to. That is how a token reaches the
person it is for, and it is decided by whoever may edit workflows. The filled parameters are kept in
the workflow's execution log, which the API serves, so a caller who may read that log reads the
token there.

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
