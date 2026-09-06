- **A Secret parameter is now protected on every workflow action type, not only Webhook.**
  `WorkflowActionResponse` already hid the `Secret` parameter and reported `secretSet` regardless of
  action type, so a custom action reusing that parameter name was shown as protected while it was
  actually stored in clear. `ProtectSecrets` now encrypts `Secret` for every action, the same way it
  already did for Webhook, closing that gap.
- **A stored secret that predates encryption now refuses with a message that says what to do about
  it.** A Webhook action carrying a plaintext `Secret` from before it was ever protected already
  refused to send rather than sign or deliver anything with it. The failure used to read the same as
  a rotated `Secrets:Key`: "could not be decrypted, enter it again". That is the wrong instruction
  here, because entering the same secret again produces the same unprotected value; the fix is to
  recreate the workflow. The two cases are now told apart and the row says which one applies.
