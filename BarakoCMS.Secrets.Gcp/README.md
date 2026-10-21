# BarakoCMS.Secrets.Gcp

Loads barakoCMS configuration values from **Google Secret Manager** at startup, so the keys that
protect stored secrets (`JWT:Key`, `Secrets:Key`, `Connectors:Key`, `Mfa:Key`) and module
credentials do not have to sit in environment variables on the same machine as the database.

It is a configuration source, not a module: there is nothing to enable. It authenticates with
Google's application default credentials, which on Google Cloud is the service account the instance
runs as, and reads no key from barakoCMS configuration. With nothing mapped it adds nothing and
makes no call, so installing it changes nothing until you configure it.

## Install

```bash
dotnet add package BarakoCMS.Secrets.Gcp
```

The published image (`ghcr.io/baryodev/barako-cms`) already includes it. A host of your own adds the
source to its configuration before `AddBarakoCMS`:

```csharp
using BarakoCMS.Secrets.Gcp;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddBarakoGcpSecrets();
builder.Services.AddBarakoCMS(builder.Configuration);
```

## Configure

Name the project, then map each configuration key to the secret that holds its value, under
`Secrets:Gcp:Map`. The key keeps its own colons, so in environment variables each becomes `__`:

```bash
Secrets__Gcp__Project=my-shop
Secrets__Gcp__Map__JWT__Key=barakocms-jwt-key
Secrets__Gcp__Map__Secrets__Key=barakocms-secrets-key
Secrets__Gcp__Map__Mfa__Key=projects/shared-keys/secrets/mfa/versions/3
```

A short name reads the latest version of that secret in `Secrets:Gcp:Project`. A full name,
`projects/<project>/secrets/<secret>` or with `/versions/<n>`, is used as it is, so a pinned version
or another project works without the project setting. The payload is read as UTF-8 text and becomes
the value of its key. A mapped value wins over the same key set anywhere else, and in the published
image a key mapped under both `Secrets:Aws:Map` and `Secrets:Gcp:Map` takes the Google value, the
source added second. The service account needs `roles/secretmanager.secretAccessor` on those secrets.

## When a secret cannot be read

Startup stops. A secret that does not exist, is empty, or cannot be read (permission denied, no
credentials, no network within 30 seconds) throws, and the message names the secret and the key it
was for, never a value. A short name with no project set stops startup before anything is asked.

That is deliberate: a host that started without `JWT:Key` or `Secrets:Key` would either refuse every
token or encrypt with the wrong key.

## What it does not do

Rotation. Changing `Secrets:Key`, `Connectors:Key` or `Mfa:Key` still makes the values encrypted
under the old key unreadable, as [SECURITY.md](../SECURITY.md) says; loading the key from a vault
does not change that. Values are read once, at startup; a restart picks up a new version.

Google Secret Manager's free tier covers 6 active secret versions and 10,000 access operations a
month, which a barakoCMS instance reading four keys at each start stays well inside. See
[BarakoCMS.Secrets.Aws](../BarakoCMS.Secrets.Aws/README.md) for AWS Secrets Manager.

## License

[MIT](LICENSE) © BaryoDev
