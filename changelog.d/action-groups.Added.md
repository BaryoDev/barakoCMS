- **The workflow builder had no way to group action kinds without keeping its own list of names.**
  `GET /api/workflows/actions` now returns `group` for each action: Content, Delivery, Comms, Data or
  Flow, set with `Group` on `[WorkflowActionMetadata]`. Email and SMS are Comms, Webhook and Request
  are Delivery, CreateTask and UpdateField are Content, Conditional is Flow. A custom action that
  does not set it still loads and reports `group: null`.
