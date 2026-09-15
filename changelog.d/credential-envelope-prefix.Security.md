- **A workflow credential that happened to be base64 or hex was stored in clear and then refused at
  every run.** Whether a value was already encrypted was decided by its shape, and a 64 character
  hex API key or a base64 32 byte secret decodes to enough bytes to pass for an envelope. Such a
  value skipped encryption on save and in the startup migration, and the runner then failed each
  attempt permanently trying to decrypt it. Values encrypted with `ISecretProtector` now start with
  `enc:v1:`, and that prefix is what marks one as encrypted. A workflow's credentials are always
  encrypted when it is created, and `Password`, `Token`, `ApiKey` and the other names are no longer
  trimmed (`Secret` still is). The startup migration gives an envelope written before the prefix
  its prefix, encrypts a value stored in clear, and leaves an envelope-shaped `Secret` that will not
  decrypt as it is, logging the parameter name and workflow id. `IWorkflowEngine.ProcessEventAsync`
  now decrypts credentials before running an action, as the runner does, and records a failure when
  one will not decrypt.
