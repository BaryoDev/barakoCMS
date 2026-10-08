- **An SMTP email could be sent twice when closing the connection failed.** A failure after the
  relay accepted the message is now logged and the send counts as sent, so the durable queue no
  longer sends a second copy (#1114).
