# URL redirects

Rebuilding a site changes its URLs. The old ones have to keep working or the client loses their
search ranking and every link anyone ever shared. barakoCMS keeps that map with the content, because
which URLs moved is something the person who rebuilt the site knows and the person who runs nginx
does not.

## Managing them

`GET /api/redirects` lists, `POST /api/redirects` creates or edits, `DELETE /api/redirects/{id}`
removes. SuperAdmin and Admin, like the rest of the editorial surface.

```json
POST /api/redirects
{ "fromPath": "/old-about", "toPath": "/about", "permanent": true, "note": "2026 rebuild" }
```

Both paths are normalised on the way in: a leading slash, no trailing slash, no query or fragment.
So `/about`, `about` and `/about/` are one rule and cannot be entered as three. Case is preserved,
because paths are case sensitive on most servers and a rule that quietly matched a different case
would be a rule nobody wrote.

`permanent` defaults to **false**, which is a 302. That is the safe default rather than the common
one: a browser caches a 301 indefinitely and will not ask again, so a permanent redirect entered by
mistake is not fixed by deleting the rule. Everyone who saw it keeps following it. Set `permanent`
once you are sure.

## Resolving one

```
GET /api/public/redirects/resolve?path=/old-about
```

Anonymous, because the caller is a frontend rendering for a visitor who has no session. It answers
`404` when nothing moved, which is the point: an empty `200` would make "no redirect" and "a
redirect to nowhere" the same answer.

```json
{ "fromPath": "/old-about", "toPath": "/about", "status": 301 }
```

`status` is 301 or 302 so a frontend can pass it straight to its own response.

Ask it on the 404 path, after your own lookup has failed. It is one indexed equality lookup with no
wildcards and no scan, and the response is cached for five minutes.

## Importing a migration

A rebuild brings hundreds at once.

```json
POST /api/redirects/import
{ "csv": "from,to,permanent\n/old-a,/a,true\n/old-b,/b", "dryRun": true }
```

The columns are `from,to[,permanent[,note]]`. A header row is optional and skipped. `permanent`
accepts `true` or `301`. At most 5000 lines per upload.

A bad line is rejected and named by line number; the rest still import. That is the opposite of the
content importer, which is all or nothing, and the difference is what each is for: a content bundle
is one export that should arrive whole, and a redirect list is a spreadsheet somebody typed, where
the useful answer is "these four hundred worked and these three did not".

`dryRun` reports what would happen and writes nothing.

## Loops are refused when you save, not when a visitor arrives

A loop found at request time is found on the 404 path, which is when the site is already having a bad
day, by a visitor who cannot fix it, and it presents as "too many redirects" long after anybody
remembers writing the rule.

So the save path refuses:

- a path that redirects to itself
- a rule that closes a circle with rules already stored, however long the circle
- a chain longer than ten hops, even when it terminates, because browsers give up around twenty and
  the fix is to point at the final destination

An import checks each line against the rules already stored **and against the lines above it in the
same file**. Checking only the database would let one upload introduce a circle that neither line
creates on its own, which is the loop nobody can find afterwards because no single rule looks wrong.

## What this is not

There are no wildcards and no regular expressions. Every rule is one exact path. That keeps the
lookup a single indexed comparison on the path where a site can least afford anything else, and it
keeps a rule's meaning obvious to whoever reads the list in two years. If a pattern is genuinely
needed, it should be an explicit second kind of rule rather than an implicit reinterpretation of
these.

Paths, not URLs. A redirect goes somewhere on the same site. Storing an absolute URL would make this
an open redirector, where anybody who can add a rule can point a trusted domain at their own.
