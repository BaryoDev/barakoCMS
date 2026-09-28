- **Values placed into an email body were not HTML-encoded.** The sign-in code email wrote the
  device description (taken from the request's user-agent) and IP address into its HTML as they
  were, and a workflow `Email` action did the same with entry fields, which can come from a public
  form. Both are encoded now, as is the app name in every system email. In a workflow email, values
  in `Subject` and `To` have their line breaks replaced by a space, and a `Conditional` resolves its
  children's parameters when each child runs instead of substituting values into the branch JSON.
