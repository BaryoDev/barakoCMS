- **Values placed into an email body were not HTML-encoded.** The sign-in code email wrote the
  device description (taken from the request's user-agent) and IP address into its HTML as they
  were, and a workflow `Email` action did the same with entry fields, which can come from a public
  form. Both are encoded now, as is the app name in every system email. In a workflow email, values
  in `Subject` and `To` have their line breaks replaced by a space, and a `Conditional` resolves its
  children's parameters when each child runs instead of substituting values into the branch JSON.
  This changes how a rich text or markdown field looks in a workflow email: a `Body` of
  `{{data.Body}}` holding `<p>Hello <b>world</b></p>` now shows the tags as text instead of a bold
  "world". A workflow email's `To` must now resolve to exactly one address; a list, or a value that
  is not an address, fails the action and sends nothing. The sign-in code email names the device
  only when it recognises the browser or system, and says "an unrecognised device" otherwise,
  instead of repeating the user-agent.
