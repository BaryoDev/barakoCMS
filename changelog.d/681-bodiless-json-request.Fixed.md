- **A GET sent with `Content-Type: application/json` and no body was answered 400.** FastEndpoints
  reads a JSON body whenever the content type says JSON, so a client that sets that header on every
  call, as most `curl` examples do, got a serializer error about a payload it never sent. A GET,
  HEAD, DELETE or OPTIONS request with no body now binds the same as one without the header. POST,
  PUT and PATCH are unchanged, so an empty or malformed body there is still refused. See #681.
