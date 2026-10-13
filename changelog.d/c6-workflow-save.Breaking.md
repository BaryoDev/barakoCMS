- **Saving a workflow refuses a malformed `capture`, `compensate` or `executor`.** An action's
  `capture` names the outputs a run keeps (#575), its `compensate` the action that undoes it (#577),
  and the workflow's `executor` what runs it (#698). A workflow sent with any of these was saved
  with it ignored before. Now a `capture` whose names are not letters, digits and underscores, are
  repeated, or number more than 20 is refused with 400, and so is a `compensate` that is a
  `Conditional`, an unregistered action type or missing a required parameter, and an `executor`
  other than the built-in one. `ApiContract.Version` moves to 8. The delivery number does not move.
