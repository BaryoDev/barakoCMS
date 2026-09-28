- **An upload whose bytes do not match its declared type is now refused.** `POST /api/files`
  answers 400 for a type that only starts with an allowed one (`image/pngx`) and for a file whose
  content is not the declared format, such as a PNG sent as `image/jpeg`. Both used to be stored.
  `ApiContract.Version` moves to 5.
