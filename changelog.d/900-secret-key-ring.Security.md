- **Rotating an encryption key lost every stored secret under it.** The email API key, workflow
  credentials, MFA secrets and connector credentials were each encrypted under one key, and changing
  it made them unreadable with nothing said at startup. Each class now takes a key ring
  (`Secrets:Keys`, `Mfa:Keys`, `Connectors:Keys`, each with a `CurrentKey`): a value names the key
  that wrote it, so it still decrypts after another key becomes current. A startup pass re-wraps
  stored values under the current key and logs how many no configured key decrypts. Values written
  before the ring read as before, and a deployment that configures no ring stores exactly what it
  stored before (#900).
