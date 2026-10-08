- **A connector setting could hold a credential in plain text.** `POST /api/connectors` and
  `PUT /api/connectors/{slug}` now refuse a setting whose name reads as a credential with a 400
  pointing at `secrets`, which are encrypted and never returned. The settings the auth modes read
  (`TokenUrl` among them) are still accepted.
