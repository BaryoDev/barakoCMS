- **`SmsAction` and `EmailAction` no longer report success when nothing was sent.** Both actions
  implemented only the obsolete `ExecuteAsync`, so the default `RunAsync` always returned
  `WorkflowActionResult.Success()` after calling it, whatever the underlying provider did. On a
  stock install the default `ISmsService` and `IEmailService` are mock providers that log and
  return without sending anything or throwing, so a workflow with an SMS or Email action recorded
  success for a message nobody received.

  Both actions now implement `RunAsync` directly. A send against the mock provider returns
  `PermanentFailure`, since retrying will not change anything until a real provider is registered;
  a provider throwing is caught and returned as a retryable `Failure` naming the exception type,
  never the exception message, which routinely names the recipient. The error text stored on the
  run record never carries a phone number, email address or provider credential.
