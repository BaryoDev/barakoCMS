- **A module could not replace the OTP, email verification or email settings services, and nothing
  said so.** Core registered its own after the modules', so a module's implementation was never
  called. Those three are now registered with `TryAdd` and a module's registration wins.
  `IContentWriter`, `ISensitivityService`, `IContentSourcingPolicy` and `ITemplateVariableExtractor`
  stay core's own, and a module that registers one now gets a startup warning naming the module and
  the interface (#697).
