- **A screen for workflow runs.** The run endpoints have been there since the outbox split and had
  no interface, so the only way to find out whether a workflow actually fired was to query Postgres.
  `/workflow-runs` lists every run newest first, filtered by status, with the status carried by a
  tinted badge rather than a word in a column, and opening one shows its actions in execution order
  with the attempt count, how long each took, the response status and the error when there is one.

  A retry button appears on a failed action and on an unknown one, and nowhere else. Unknown is a
  timeout, where the request may well have arrived and only the response was lost, so retrying it is
  a decision to accept possible duplicate delivery and a person has to make it. Succeeded, Running,
  Pending and Skipped get no button at all: `POST .../retry` refuses a succeeded action with a 409
  because sending it twice is the hazard the idempotency key exists for, and offering a control that
  can only answer 409 teaches an operator to distrust the screen.

  Pressing retry refetches the run rather than rendering what the endpoint returned. The response is
  the run as it stood at the moment of the write, and the runner can claim the attempt a tick later,
  so painting that body on screen would show a Pending action that is already Running.
