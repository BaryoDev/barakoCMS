- **A workflow action's credential was only redacted when it was called "Secret".** Action
  parameters are free-form, so a credential arrives under whatever name the third party uses, and
  anything called `Password`, `Token`, `ApiKey` or the like was stored on the run record verbatim and
  served to anyone who can read workflow runs. Redaction now matches credential-bearing names
  case-insensitively, and errs towards hiding: a parameter named `TokenUrl` is hidden too, which
  costs a lookup rather than a credential.
- **A caller could forge a log line.** The request path is URL-decoded before anything reads it, so
  a request for a path containing an encoded newline arrived with a real one, and against a
  one-line-per-entry sink that became a second, attacker-written entry. Control characters in the
  values the middleware logs are replaced with a space and the value is capped.
