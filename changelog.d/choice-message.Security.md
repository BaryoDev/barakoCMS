- **A refused choice value was repeated back in the 400.** The message now names the field and the
  values it accepts, never the value received. A choice the entry already holds is not checked
  again on an edit, so a stored value outside the current options, including one in a field the
  caller cannot see and did not send, no longer blocks saving the rest of the entry.
