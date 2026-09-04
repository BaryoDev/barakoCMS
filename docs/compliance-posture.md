# Security and compliance posture

Written so a security questionnaire has an answer that is not silence. Everything below is either
checkable in this repository or stated plainly as absent.

Last reviewed: 30 August 2026, against barakoCMS 4.0.

## Start here: barakoCMS is self-hosted

This is the single most useful sentence for most questionnaires, so it comes first.

**There is no barakoCMS service.** You run the process, you own the database, you choose the
region, you hold the backups, you decide the retention. We never receive your data and cannot
access it.

That answers a large share of a standard security review by moving the answer, not by providing
one:

| Question | Who answers it |
| --- | --- |
| Where is data stored, and in which region? | You. It is your Postgres. |
| Is data encrypted at rest? | You. Configure it on your database and volumes. |
| Who at your company can access customer data? | Nobody here can. There is no access path. |
| What are your subprocessors? | Ours: none in the data path. Yours are yours. |
| What is your data-retention and deletion policy? | Yours, with one caveat below on erasure. |
| What happens to data if we stop paying? | Nothing. There is nothing to pay, and no hosted copy. |
| What is your breach-notification process? | For a vulnerability in the software, see `SECURITY.md`. For a breach of your deployment, yours. |

The one place this framing does not hold is the right to erasure, covered below, because it is a
property of the software rather than of where it runs.

## What exists, and where to verify it

Each row is a control that is implemented and checkable, not a plan.

| Control | Where |
| --- | --- |
| Tamper-evident audit trail | `AuditChain` hashes each entry over its predecessor (SHA-256, `PrevHash`, genesis constant), so alteration is detectable as a broken chain |
| Multi-factor authentication | TOTP with recovery codes, honoured on every sign-in path including social; email OTP; device binding |
| Refresh-token rotation | With reuse detection that revokes the whole token family |
| Brute-force protection | Lockout via atomic SQL increment, plus a timing-safe dummy hash so a missing account and a wrong password cost the same |
| API keys | Scoped, with the scopes enforced by a processor rather than merely issued |
| Multi-tenant isolation | Marten conjoined tenancy: every document and event stream is tagged and auto-filtered by tenant |
| Field-level sensitivity | Per-field allowlist on public delivery, applied on both read and write |
| Static analysis | CodeQL on every pull request |
| Dependency vulnerabilities | Dependabot, plus a `dotnet list package --vulnerable` gate that fails the build on High or Critical |
| Secret scanning | Gitleaks on every pull request |
| Software bill of materials | CycloneDX per package and for the container image, attached to each release |
| Backup and restore | Every deployment path takes verified backups; CI restores one and boots against it on every pull request (`docs/backup-and-restore.md`) |
| Upgrade safety | CI upgrades a real database created by the previous release (`docs/upgrading-to-4.0.md`) |
| Vulnerability disclosure | Private channel with a stated timeline (`SECURITY.md`) |
| Licence | MPL-2.0. No seat cap, no revenue cap, no metered features |

## What does not exist

Said directly, because a hedge here costs more time than the admission.

- **No SOC 2 report.** Not Type I, not Type II. None is in progress.
- **No ISO 27001 certification.** None is in progress.
- **No third-party penetration test.** The security work in this repository is internal review and
  automated scanning. If a test is ever commissioned, its summary will be added here.
- **No HIPAA, PCI-DSS or FedRAMP attestation.** Whether a self-hosted barakoCMS can sit inside such
  an environment is a question about your infrastructure and your assessor, not about us.
- **No published RPO or RTO.** Backups run nightly, and the restore procedure is exercised in CI,
  but no recovery time has been measured on a production-sized database, so there is no number to
  put in a contract.
- **No 24/7 support, and no service-level agreement.** See `SECURITY.md` for the support policy,
  which is a maintenance commitment rather than an SLA.

This is category-normal for a self-hosted open-source CMS. Umbraco has no SOC 2 either. It is stated
here so that a reviewer gets an answer in one page instead of an unanswered email.

## The one that is genuinely ours, not yours

**Right to erasure against an immutable event stream.** Content history is append-only, which is
what makes rollback a forward event and the audit trail worth having. It also means that personal
data written into a content type cannot simply be deleted later: erasing the current document does
not erase the events that produced it.

**This is now answered.** `Erasure:Mode` decides how a deployment handles an erasure request:

| Mode | What it does |
| --- | --- |
| `Delete` (default) | `DELETE /api/contents/{id}/erase` removes the item's events, its stream and its read-model document in one transaction. The item's history goes with it, which is what erasure means. |
| `None` | No erasure path, for a deployment that has decided its content never holds personal data. Requires an explicit acknowledgement to start. |
| `CryptoShred` | Encrypt payloads per subject, destroy the key on erasure. **Not available yet**, and selecting it fails at startup rather than pretending. |

Two limits worth stating rather than discovering:

- **`CryptoShred` is unimplemented** because a CMS has no natural data subject: a blog post that
  mentions a person is not owned by them, so there is nothing to key on. That question is open
  (issue #301, decision D9). Until it is answered, erasure means deletion, not shredding.
- **The audit trail is a second erasure surface.** `AuditEvent` carries an actor username, and
  `AuditChain` hashes each entry over its predecessor, so removing one breaks the tamper-evidence
  the chain exists to provide. Erasure and tamper-evidence are in direct conflict there, and that is
  also unresolved.

The reasoning behind all of it is in `DECISIONS.md` under D9.

Per-type event sourcing, decided but not yet shipped, sidesteps this conflict rather than adding to
it: an event-sourced content type refuses non-Public fields, so personal data cannot enter a stream
whose value is never being altered. What that choice commits an operator to is in
[docs/event-sourced-content-types.md](event-sourced-content-types.md).

## Where the admin keeps your session

The admin, barakoBrew (its own repository), holds the access token in memory and the refresh token
in an httpOnly cookie the page
cannot read. Before 4.0 both sat in `localStorage`, where any script on the origin could read them,
which made one cross-site scripting bug worth seven days of renewable sessions rather than fifteen
minutes.

The reasoning, what you will notice, and what your deployment topology needs for the cookie to be
sent are all in `docs/session-and-token-storage.md`. Worth reading before answering a questionnaire
about session handling, because the honest answer includes what this does not fix.

## Reporting a vulnerability

`SECURITY.md`. Private channel, 48-hour acknowledgement, one-week initial assessment.

## Keeping this honest

This page is only worth anything if it is true on the day someone reads it. Review it at each major
release, and when any row changes. A control that has been removed and left listed here is worse
than one that was never claimed.
