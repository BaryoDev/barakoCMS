- **The last of the core endpoints ask for a capability instead of a role name.** Monitoring,
  redirects, saved queries, request definitions, connectors, workflows, workflow runs, the content
  rollback and the content erasure are all gated on a capability now, so a role created at runtime
  can be granted any of them without a code change. Eleven names: `view_monitoring`,
  `manage_redirects`, `manage_queries`, `manage_requests`, `view_connectors`, `manage_connectors`,
  `manage_workflows`, `view_workflow_runs`, `retry_workflow_actions`, `rollback_content` and
  `erase_content`.

  Three areas are split rather than given one name each. Connectors split read from write, because a
  connector is the only document in core holding a third party's credentials: the reads return the
  configuration and the names of the secrets, the writes take secret values, and the probe spends
  them against the configured base URL. Workflow runs split reading from retrying, because a retry
  queues a real attempt and the mail is actually sent, while "did the notification go out" needs the
  run list and nothing else. The rollback and the erasure are separate because their old gates
  differed, `Roles("SuperAdmin", "Admin")` against `Roles("SuperAdmin")`, and one name would have had
  to widen one of them.

  Queries and requests are deliberately one name each, preview and dry run included. The dry run
  composes a call without making it and holds no credential; the preview shows the author rows a
  saved query would have sent to a third party anyway, bounded to fields whose sensitivity is
  `Public`.

  Admin's defaults gain everything migrated here except `erase_content`, which was
  `Roles("SuperAdmin")` and destroys content and its history irrecoverably. Nothing is narrowed, and
  `Auth:LegacyRoleFallback` still honours the old role names while it is on.

  Two core routes stay on role names on purpose, `GET /api/modules` and
  `POST /api/content-types/{name}/seo-fields`, and `RoleGateTests` pins that list so it cannot drift.
