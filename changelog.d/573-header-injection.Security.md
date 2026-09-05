- **A composed request header carrying a line break is now refused, closing a pre-existing
  injection.** Any value substituted into a request definition's header template reached
  `Escaping.None` with nothing stripping or refusing a carriage return or newline, then reached
  `ConnectorSender`'s `TryAddWithoutValidation` unchecked. A content field of
  `"safe\r\nX-Injected: evil"` composed verbatim and sent as two headers, which is a way to forge a
  header on an outbound call made with the connector's own credentials attached, for anyone who can
  write a content field a request template names. Wiring queries into requests widened what reaches
  the same sink, so the fix covers both: any value landing in a header, from content or from a
  query, is checked.

  Refused rather than stripped, naming the header and never the value: stripping the control
  character would send a request the operator did not write, silently, the same reason a Sensitive
  field is refused rather than masked.
