- **Diagnostics, ExternalAuth, FeatureFlags and Import state an HTTP contract version.** Each served
  endpoints but reported `httpContractVersion` 0 (unstated) in `GET /api/meta/describe`. They report 1
  now. No route or body changed, so no contract number moves. A test fails when any module that maps
  endpoints reports 0 (#902).
