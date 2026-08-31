- **The seven modules that shipped with no tests have them.** `ExternalAuth`, `DeviceTrust`,
  `Portability`, `FeatureFlags`, `Email.Resend`, `Import` and `Analytics.Umami` were built, packed and
  pushed to NuGet on every release with no assertion anywhere covering them, and two of the seven are
  authentication surface. 52 tests, each one checked by breaking the thing it covers and watching it
  go red.

  What is pinned is the behaviour that would hurt if it broke rather than a coverage number. An
  account with MFA enrolled gets a challenge from a social sign-in and never a token, which is the
  bypass 0.1.5 shipped. An OAuth callback with a missing or mismatched `state` mints nothing, and one
  that matches its own state signs a verified account in. A device-bound token is refused from any
  other device, a token with no `did` claim is deliberately left alone, revoking a device kills its
  refresh tokens and nobody else's, and nobody can revoke a device they do not own. An exported bundle
  imports into a clean tenant with its schema and content intact, twice over without duplicating the
  type, into the calling tenant only. A percentage rollout gives the same person the same answer every
  time. The Resend API key travels as a bearer header and appears in no URL, body or exception. A bad
  row stops an all-or-nothing import before anything is written. The Umami account never reaches the
  browser, and data requests to Umami carry the exchanged token rather than the credential.

  `BarakoCMS.Tests` now references `DeviceTrust`, `Import` and `Analytics.Umami` as well, so all seven
  are reachable from a test at all, which four of them were not.
