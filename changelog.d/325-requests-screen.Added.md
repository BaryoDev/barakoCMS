- **A screen for outbound requests.** The request endpoints had no interface, so composing an
  outbound call meant POSTing JSON by hand. Settings now has one: the list, an editor for the
  connector, method, path, headers, body template and success rule, and the dry run.

  The dry run leads the screen, because it is how an operator finds out what a template produces
  while they can still change it. It composes the call against a real entry and renders exactly what
  came back: the finished URL, every header and the body, laid out when it is JSON and left as
  composed when it will not parse, since that is the case worth seeing. A refusal shows the reason
  instead, which is what happens when a template names a field that is not Public or a query that
  does not exist yet.

  Nothing on the screen sends anything, and it says so where a verdict could be misread: the result
  panel is headed "Dry run. Nothing was sent.", the verdict reads "Would be sent" rather than "Sent",
  and the button says compose rather than send. Header blocks are pasted as "Name: value" lines and
  a line the parser cannot read is refused rather than dropped, because a dropped line is a header
  the operator believes they set.
