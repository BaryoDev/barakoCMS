# Review rules

Read by the adversarial-review skill (arnelirobles/lean-agent-method) before any code. Each rule is answered yes or no with a line. CLAUDE.md is the coding standard; these are the questions reviews keep needing.

## Contract

- **Two contracts, versioned separately.** The HTTP surface moves `ApiContract.Version` in `barakoCMS/Features/Monitoring/Meta/ApiContract.cs`. The module contract (what a module compiles against) is described in MODULES.md.
- **What counts as breaking on the HTTP surface** (CLAUDE.md section 6): removing or renaming a response field, changing a field's type, changing a status code, or tightening request validation so a request that used to be accepted is refused. Adding an optional field is not breaking.
- **Consumers and the range each accepts:**
  - barakoBrew, `src/lib/api-contract.ts`, `SUPPORTED_CONTRACT`. An API whose version is outside the range stops the console.
  - barakoPress reads delivery, site settings and the Pages module bodies, which carry their own `contract` integer.
- **A change that moves `ApiContract.Version` merges only after barakoBrew's range includes the new value,** and a release carries at most one move.

## Boundaries

- **Core or module:** apply the tests in README "Module, or core?". A module depends on the core only, never on another module, and the core never references a module.
- **Nothing secret, or hashed from a secret, in a publicly deliverable type,** including the `site` settings entry.
- **A config default preserves today's behaviour** (CLAUDE.md section 3).
- **A list endpoint is bounded,** and an anonymous endpoint is rate limited.

## Data and tenancy

- Every session, raw query and cache key names its tenant.
- A new document, event, index or column has a migration, `scripts/upgrade-check.sh` passes, and existing data survives it.
- A read-then-write on a shared row names its lock or runs inside the write transaction.
- Anything stored from an exception, a request or a workflow parameter is redacted before it reaches a log, a response or the audit log.

## Tests

- A bug fix names the test that fails without it (CLAUDE.md section 4).
- An assertion over a collection first asserts the collection is not empty.
- `scripts/preflight.sh` ran with the classes the change touches, and its exit code was read.

## Style

- No em dashes or arrow glyphs, no banned words, and no attribution lines in code comments, docs, commits, PR bodies or review threads.
- Comments explain why, and do not record provenance.
