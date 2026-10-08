- **SMTP sends have a timeout of their own.** `Modules:Email.Smtp:TimeoutSeconds` (default 30) is
  MailKit's limit on each exchange with the relay, which was 120 s, and one limit over the connect
  and login together. A relay that accepts the connection and never answers now fails as not sent
  within it (#1114).
