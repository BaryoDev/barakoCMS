- **Saving a workflow refuses a malformed `capture` or `compensate`.** An action's `capture` names
  the outputs a run keeps (#575), and `compensate` the action that undoes it (#577). A workflow sent
  with either field was saved with it ignored before. Now a `capture` whose names are not letters,
  digits and underscores, are repeated, or number more than 20 is refused with 400, and so is a
  `compensate` that is a `Conditional`, an unregistered action type or missing a required
  parameter. `ApiContract.Version` moves to 8. The delivery number does not move.
