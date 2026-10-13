# BarakoCMS.Secrets.Aws

Loads barakoCMS configuration values from **AWS Secrets Manager** at startup, so the keys that
protect stored secrets (`JWT:Key`, `Secrets:Key`, `Connectors:Key`, `Mfa:Key`) and module
credentials do not have to sit in environment variables on the same machine as the database.

It is a configuration source, not a module: there is nothing to enable. It authenticates with the
AWS SDK's default credentials, which on AWS is the instance or task role, and reads no key from
barakoCMS configuration. With nothing mapped it adds nothing and makes no call, so installing it
changes nothing until you configure it.

## Install

```bash
dotnet add package BarakoCMS.Secrets.Aws
```

The published image (`ghcr.io/baryodev/barako-cms`) already includes it. A host of your own adds the
source to its configuration before `AddBarakoCMS`:

```csharp
using BarakoCMS.Secrets.Aws;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddBarakoAwsSecrets();
builder.Services.AddBarakoCMS(builder.Configuration);
```

## Configure

Map each configuration key to the secret that holds its value, under `Secrets:Aws:Map`. The key keeps
its own colons, so in environment variables each becomes `__`:

```bash
Secrets__Aws__Region=eu-west-1                                  # optional; else the SDK decides
Secrets__Aws__Map__JWT__Key=barakocms/jwt-key
Secrets__Aws__Map__Secrets__Key=barakocms/secrets-key
Secrets__Aws__Map__Modules__Files.S3__SecretKey=barakocms/s3-secret-key
```

Each secret is read as a plain string (`SecretString`) and becomes the value of its key; a secret
stored as binary stops startup. A mapped
value wins over the same key set anywhere else, since naming it in the map is the request to take it
from the store. The role needs `secretsmanager:GetSecretValue` on those secrets, and `kms:Decrypt`
when they use a customer managed key.

## When a secret cannot be read

Startup stops. A secret that does not exist, is empty, or cannot be read (access denied, no region,
no network within 30 seconds) throws, and the message names the secret and the key it was for, never
a value:

```
Secrets:Aws: the secret 'barakocms/jwt-key', mapped to JWT:Key, does not exist. Startup stops here
rather than run without JWT:Key. ...
```

That is deliberate: a host that started without `JWT:Key` or `Secrets:Key` would either refuse every
token or encrypt with the wrong key.

## What it does not do

Rotation. Changing `Secrets:Key`, `Connectors:Key` or `Mfa:Key` still makes the values encrypted
under the old key unreadable, as [SECURITY.md](../SECURITY.md) says; loading the key from a vault
does not change that. Values are read once, at startup; a restart picks up a new version.

AWS Secrets Manager has no free allowance. See
[BarakoCMS.Secrets.Gcp](../BarakoCMS.Secrets.Gcp/README.md) for Google Secret Manager.

## License

[MIT](LICENSE) © BaryoDev
