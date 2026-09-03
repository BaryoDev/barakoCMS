- **Connectors had a backend and no interface, so a third party's credentials could only be entered
  with curl.** There is a screen now at Settings, Connectors: the list, add, edit, delete, and the
  test button, gated to SuperAdmin and Admin the way the endpoints are.
- **A credential is write only on the screen because it is write only in the API.** No endpoint
  returns a stored value, so the box starts blank every time and blank means "keep what is stored".
  Deleting a credential is a separate checkbox rather than an empty box, which is what
  `SaveConnectorRequest` already encodes: an absent key changes nothing, an empty value deletes.
  The alternative, showing asterisks and posting them back, would overwrite the token with asterisks
  the first time somebody corrected a base URL. The only values this screen ever sends are ones
  typed into it in that session.
- **The list answers the question an operator came with: did the last probe work.** `LastTestResult`
  is prose the server wrote ("HTTP 200 in 34 ms"), not a boolean, so the screen reads the status out
  of it and calls 200 to 299 a success, matching `IsSuccessStatusCode`, which is what the server
  used to decide it. A 302 to a login page counts as failing, which is what `ProbePath` exists to
  fix. It also names the gap before a probe is run: no Token stored, or no Username set, are both
  refusals `ConnectorSender` makes before a request is sent.
- **Deleting asks first, and says what goes with it.** The credentials go in the same transaction
  and any request definition naming that slug stops working, so the confirmation names the slug
  rather than asking a generic "are you sure".
