- **Changing a connector's address kept its stored credentials.** `PUT /api/connectors/{slug}`
  keeps a secret the request leaves out, which is how the console saves an edit without showing the
  token. That now holds only while the base URL keeps its scheme, host and port (compared with the
  host lowercased and in punycode, and the default port made explicit). When any of those change,
  every stored secret has to be sent again or cleared in the same request, and the update answers 400
  with one error per missing secret, named `secrets.<Key>`. A path change on the same origin keeps
  the secrets as before. A request definition whose path template resolves to a different scheme,
  host or port from its connector (an absolute or `//` URL) is now refused when it is composed.
  A connector's `probePath` has to start with a single `/` and hold no backslash, so `health`,
  `//host/` and `https://host/` are answered 400, and the test button refuses a stored probe path
  that resolves to another origin. These requests used to succeed, so `X-Api-Contract-Version`
  moves to 5, the same bump the platform role change makes, and the barakoBrew console has to
  accept contract 5 before it runs against this release.
