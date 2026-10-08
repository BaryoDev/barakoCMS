- **`authorization` and `bearer` now read as credential names.** A setting key or a workflow
  parameter holding either word is treated like one holding `token` or `password`: a setting under
  it can only be cleared, and a workflow parameter under it is encrypted at startup and not
  returned. `auth` on its own is not a credential word, so a name like `Author` is unaffected.
