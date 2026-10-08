- **A workflow email that never left is tried again inside the attempt.** `EmailNotSentException`
  in `BarakoCMS.Abstractions` lets an email provider say a message cannot have been delivered. The
  SMTP module throws it for a failure to connect or log in and for a 4xx or 5xx answer to MAIL FROM
  or RCPT TO, and the Resend module for a connection not made and for a 429 or 503. The workflow
  email action tries again on that exception only, up to `Workflows:Outbound:Retries` more times,
  cut so the tries fit in the lease. `IEmailService.MaxNotSentDuration`, with a default of null, is
  how a provider says how long a not-sent try can take; a provider with none is sent once. Any other failure, a timeout included, is
  still sent once and left to the durable queue. It is an `InvalidOperationException`, so code that
  caught that from a provider still catches it (#1114).
