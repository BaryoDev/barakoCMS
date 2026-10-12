- **A workflow could not be changed, only deleted and created again.** `PUT /api/workflows/{id}`
  replaces a workflow, validated the way a create is, and is audited as `workflow.updated`. An
  action sent back unchanged keeps its stored credentials, since the API never returns them; a
  changed action that leaves one out is refused, so a secret never follows an action to a new URL.
