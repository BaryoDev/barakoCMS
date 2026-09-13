- **`GET /api/workflows/actions` now lists optional and secret parameters.** Each action carried
  `requiredParameters` only, so a form had to guess the rest from `exampleConfiguration`, which a
  custom action can leave a parameter out of. Actions now also return `optionalParameters`, declared
  with the new `OptionalParameters` on `[WorkflowActionMetadata]`, and `secretParameters`, the declared
  names the API leaves out when it returns a workflow. Webhook reports `Secret` as both. An action
  that declares nothing reports empty lists.
