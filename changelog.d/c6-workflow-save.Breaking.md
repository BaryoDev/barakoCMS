- **Saving a workflow refuses a malformed `capture`.** An action's `capture` names the outputs a run
  keeps (#575). A workflow sent with that field was saved with it ignored before; one whose names
  are not letters, digits and underscores, are repeated, or number more than 20 is now refused with
  400. `ApiContract.Version` moves to 8. The delivery number does not move.
