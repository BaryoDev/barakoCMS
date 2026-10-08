- **The UpdateField workflow action stored any text in an email or choice field.** It now checks
  the value as an entry write does and fails the step for good when it is refused, without naming
  the value. A multiple choice cannot be set from one text value. A Field spelled in another case
  than the stored key now replaces that value instead of adding a second key.
