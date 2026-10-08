- **An update was checked against the entry as stored only.** `PUT /api/contents/{id}`, a
  collection push, a rollback and a transition carrying values now also check the rule that
  granted the write against the entry as it will be stored, so a rule with conditions, such as a
  branch field or `$CURRENT_USER`, holds after the write as well as before it. A write that would
  leave the entry outside the rule answers 403 and stores nothing.
