# Troubleshooting a start that fails

The API checks its configuration before it takes a request, and refuses to start on a value it
cannot run safely with. The refusal is the message, so this page is indexed by the message. Find
yours, read why, apply the fix.

Where to read it: `docker compose logs app` (or `docker compose -f docker-compose.prod.yml logs app`).
The process exits with code 1. A refusal that comes out of the host build is logged as
`Host terminated unexpectedly` followed by the message. A refusal to read the secrets directory is
written to standard error before logging starts.

In the blocks below, `...` stands for a value the message fills in: a key name, a number, or a
setting you wrote. A test checks every block against the source (see the end of this page), so a
message that changes there fails the build until this page changes with it. No message below
contains a key, a password or a token. Where a message names a value, it is a setting name, a
length, a role name, a host or an address you configured.

The class that raises each one is named in its entry, so you can find the source.

## Secrets and credentials

### JWT:Key is missing, short, a placeholder or an example

Raised by `JwtKeyGuard`.

<!-- source: barakoCMS/Infrastructure/Security/JwtKeyGuard.cs -->
```text
JWT:Key must be configured and at least ... characters (256 bits) for security.
JWT:Key is still the placeholder shipped in k8s/02-secret.yaml. Set a real, secret, randomly generated key (at least 32 characters) before starting.
JWT:Key is an example or development key from the compose files, the samples or the docs. Set a real, secret, randomly generated key (at least 32 characters) before starting outside Development.
```

Why: the key signs every token. The first message is a key that is unset or under 32 characters. The
second is the placeholder in the Kubernetes secret manifest. The third is a key this repository prints
in its own compose files and docs, refused outside Development.

Fix: set the key to a random value of 32 characters or more, for example `openssl rand -base64 48`.
The variable is `JWT_KEY` in `quickstart/docker-compose.yml` and `docker-compose.prod.yml`,
`JWT_SECRET` in `docker-compose.yml` and `docker-compose.hub.yml`, or `JWT__Key` when you run the
image yourself. Keep it ASCII. Do not reuse it as a key under `Secrets`, `Mfa` or
`Connectors`.

### InitialAdmin:Password is refused

Raised by `InitialAdminGuard`. Outside Development, and only when a password is set.

<!-- source: barakoCMS/Infrastructure/Security/InitialAdminGuard.cs -->
```text
InitialAdmin:Password is refused: ... Set InitialAdmin:Password (environment variable InitialAdmin__Password) to a random value of at least 12 characters, or leave it unset and the first boot generates one.
```

The reason after the colon is one of: it is shorter than 12 characters, it is the same as the user
name or its email, or it is an example or default value from the samples and docs. The password is
never printed.

Fix: set `ADMIN_PASSWORD` in `.env` (the compose files pass it on as `InitialAdmin__Password`) to a
random value of 12 characters or more. In `docker-compose.yml` and `docker-compose.hub.yml` you can
instead remove it and read the generated one from the first boot. `quickstart/docker-compose.yml`
and `docker-compose.prod.yml` require it, so there it must be set.

### The database password is an example value

Raised by `DatabasePasswordGuard`. Outside Development. It reads `ConnectionStrings:DefaultConnection`,
`DATABASE_URL` and `PGPASSWORD`.

<!-- source: barakoCMS/Infrastructure/Security/DatabasePasswordGuard.cs -->
```text
The database password in ConnectionStrings:DefaultConnection (or DATABASE_URL) is refused: it is an example or default value from the samples and docs. Set a random password before starting outside Development.
In the compose files the API connects as the app role, so the variable is DB_APP_PASSWORD; DB_PASSWORD is the database owner's, which the API does not use, and the app role job refuses example values for it.
```

Fix: set `DB_APP_PASSWORD` to a random value. If the database already exists, run
`docker compose up -d` again: the role job sets the role's password from `DB_APP_PASSWORD` on every
run. Outside compose, run `scripts/db-app-role.sql` or `ALTER ROLE` yourself, or the API starts and
then cannot sign in.

### The API connects as a superuser

Raised by `DatabaseRoleGuard`, when the schema is applied. Outside Development.

<!-- source: barakoCMS/Infrastructure/Security/DatabaseRoleGuard.cs -->
```text
The API connects to Postgres as '...', which ...
Outside Development the API refuses to start as a role that bypasses row level security. Connect as a role without superuser: scripts/db-app-role.sql creates one, and docs/deploy-on-a-managed-platform.md has the steps for a managed database. A deployment that has decided to run this way sets Database:AllowSuperuser=true (environment variable Database__AllowSuperuser).
```

Why: a superuser or a role with `BYPASSRLS` skips row level security, which tenant isolation depends
on. The role name is in the message. The reason says what is wrong with it: superuser, `BYPASSRLS`,
or a member of such a role. A second form, `The API could not read the attributes of the database
role it connects as`, means the check itself failed, so it cannot tell.

Fix: connect as the app role that `scripts/db-app-role.sql` creates. The shipped compose files do
this for you; a connection string that points at the `postgres` user does not.
`Database__AllowSuperuser=true` is for a deployment that has decided to accept it.

### A secrets directory or file cannot be read

Raised by `SecretFiles`, which reads the directory named by `BARAKO_SECRETS_DIR` (default
`/run/secrets`). Written to standard error.

<!-- source: barakoCMS/Infrastructure/Security/SecretFiles.cs -->
```text
The secrets directory ... cannot be listed. Check that the user the API runs as (uid 1654 in the shipped image) can read and enter it.
The secret file '...' in ... cannot be read. Check that the user the API runs as (uid 1654 in the shipped image) owns it or can read it.
```

Fix: make the directory and each file readable by uid 1654: `chmod o+rx` on the directory and
`chmod o+r` on each file, or `chown 1654` on both. The message
names the file, never its content.

### A key ring under Secrets, Mfa or Connectors is wrong

Raised by `KeyRing`. `...` below is `Secrets`, `Mfa` or `Connectors`.

<!-- source: barakoCMS/Infrastructure/Security/KeyRing.cs -->
```text
...:CurrentKey is '...', but there are no keys under ...:Keys.
...:Keys has ... keys and ...:CurrentKey does not say which one new values are written under.
...:CurrentKey is '...', which is not one of the ids under ...:Keys.
...:Keys has an entry named '...'. A key id is 1 to 32 letters, digits, '-' or '_', because it is written into every value the key encrypts.
...:Keys:... is empty. Give it key material, or remove the entry.
...:Keys:... is ... characters. It needs at least 32.
...:Keys:... is the same value as JWT:Key. Rotating the signing key would then retire every stored secret under it.
```

Fix: each entry under `Keys` needs an id of 1 to 32 letters, digits, `-` or `_` and a value of 32
characters or more that is not the JWT key. With more than one key, `CurrentKey` names the one new
values are written under. The id is shown, the key material never is.

### Connectors:Key is short or shared

Raised by `ConnectorOptions`, only when `Connectors:Key` is set.

<!-- source: barakoCMS/Infrastructure/Connectors/ConnectorOptions.cs -->
```text
Connectors:Key is ... characters. It needs at least ..., the same floor the JWT signing key has,
Connectors:Key is the same value as .... Give it its own, so rotating one does not silently retire the other
```

Fix: give `Connectors:Key` its own random value of 32 characters or more. It may not equal `JWT:Key`,
`Mfa:Key` or `Secrets:Key`.

### Email verification is off without an acknowledgement

Raised by `EmailVerificationOptions`.

<!-- source: barakoCMS/Infrastructure/Auth/EmailVerificationOptions.cs -->
```text
Auth:RequireEmailVerification is false, which lets anyone create an account against an address they do not own
Set Auth:AcknowledgeUnverifiedRegistration to true to confirm this deployment's registration form is not reachable by the public, or leave verification on.
```

Fix: remove `Auth:RequireEmailVerification=false` (verification is on by default), or set
`Auth:AcknowledgeUnverifiedRegistration=true` if the registration form is not public.

## Hosts and proxies

### AllowedHosts is `*` or empty

Raised by `HostFilteringGuard`. Outside Development. With `Security:AllowAnyHost=true` it logs a
warning instead.

<!-- source: barakoCMS/Infrastructure/Security/HostFilteringGuard.cs -->
```text
AllowedHosts is '*' or empty, which accepts any Host header. Set AllowedHosts to the host names this API answers to, for example AllowedHosts=api.example.com (environment variable AllowedHosts). To accept any host on purpose, set Security:AllowAnyHost=true (environment variable Security__AllowAnyHost).
```

`0.0.0.0` and `[::]` count as open too, alone or anywhere in a list, and the message still says
`*`.

Fix: set `ALLOWED_HOSTS` (compose) or `AllowedHosts` to the names the API answers to, separated by
semicolons. Keep `APP_BASE_URL` set too. See [quickstart/README.md](../quickstart/README.md#behind-a-domain-production).

### A request gets a 400 and never reaches the API

The API is up. The `Host` header of the request is not in `AllowedHosts`, and ASP.NET Core host
filtering answers 400 before any endpoint runs. It happens when you open the API by an IP or a name
you did not list, and when a Kubernetes `httpGet` probe sends the pod IP as `Host`.

Fix: add the name or IP to `ALLOWED_HOSTS` (the quickstart default is `localhost;127.0.0.1`), for
example `ALLOWED_HOSTS=localhost;127.0.0.1;192.168.1.20`, then recreate the container. For a
probe, add a `Host` header to it.

### ForwardedHeaders is on without a trusted hop, or lists a bad address

Raised by `ForwardedHeadersSetup`.

<!-- source: barakoCMS/Infrastructure/Security/ForwardedHeadersSetup.cs -->
```text
ForwardedHeaders:KnownProxies contains '...', which is not an IP address.
ForwardedHeaders:KnownNetworks contains '...', which is not CIDR notation such as 10.0.0.0/8.
ForwardedHeaders:Enabled is true but neither ForwardedHeaders:KnownProxies nor ForwardedHeaders:KnownNetworks names a trusted hop.
```

Fix: list your proxy's address under `ForwardedHeaders:KnownProxies`, or its network in CIDR form
under `ForwardedHeaders:KnownNetworks`, or leave `ForwardedHeaders:Enabled` off. The first two
messages echo the address you wrote.

### Hsts:MaxAgeDays is zero or negative

Raised by `HstsPolicy`.

<!-- source: barakoCMS/Infrastructure/Security/HstsPolicy.cs -->
```text
Hsts:MaxAgeDays is .... A max-age of zero or less tells browsers to drop the policy, so it disables HSTS rather than tightening it. Remove the setting to take the 90 day default, or give it a positive number of days.
```

Fix: remove `Hsts:MaxAgeDays` or set a positive number.

## Limits

### A request limit is not a positive number

Raised by the limit checks in `ServiceCollectionExtensions`. A `Kestrel:Limits` key that is not
listed by name is covered by the `must be a positive` line.

<!-- source: barakoCMS/Extensions/ServiceCollectionExtensions.cs -->
```text
RequestLimits:MaxBodyBytes must be a positive number.
Kestrel:Limits:MaxRequestBodySize is not supported. Set RequestLimits:MaxBodyBytes, which sets the request body limit and the multipart form limit together.
Kestrel:Limits:... is not valid. It must be a positive number or a positive time span (hh:mm:ss).
Kestrel:Limits:... must be a positive ....
Kestrel:Limits:...:BytesPerSecond must be a positive number.
Kestrel:Limits:...:GracePeriod must be a time span longer than 1 second.
```

Fix: set `RequestLimits:MaxBodyBytes` to a number of bytes above zero (the default is 10 MiB). Do
not set `Kestrel:Limits:MaxRequestBodySize`; `RequestLimits:MaxBodyBytes` sets it and the form limit
together. Every other `Kestrel:Limits` key takes a positive number or a time span such as `00:00:30`.
Zero does not mean off.

### A rate limit setting is wrong

Raised by `RateLimitSetup`. The ones you meet most:

<!-- source: barakoCMS/Infrastructure/Security/RateLimitSetup.cs -->
```text
RateLimiting:Renderer:Key is set but shorter than 32 characters.
must be greater than zero. A limit cannot be turned off by setting it to zero
cannot be negative
RateLimiting:...:PermitLimit is not set, but another value under RateLimiting:... is.
RateLimiting:...:Partition is '...'. Use Ip, User or ApiKey.
RateLimiting:...:... is '...', which is not a whole number.
A route names a rate limit policy that does not exist:
```

The `must be greater than zero` and `cannot be negative` lines finish a message of the form
`RateLimiting:<policy>:<key> is <value>, and it <rule>.`

Fix: give the policy a `PermitLimit` above zero, a `WindowSeconds` above zero and a `QueueLimit` of
zero or more, spell a `Partition` as `Ip`, `User` or `ApiKey`, write each number as a whole number,
and define a policy under `RateLimiting:Policies` with the same spelling, case included, as the
route that names it. A `RateLimiting:Renderer:Key` is a secret of 32 characters or more, or
absent. The message gives the length, never the key. The full list of rate limit settings is in
[configuration.md](configuration.md).

## Database and modules

### No database connection string

Raised in `ServiceCollectionExtensions`, outside Development.

<!-- source: barakoCMS/Extensions/ServiceCollectionExtensions.cs -->
```text
No database connection string. Set ConnectionStrings:DefaultConnection or the DATABASE_URL environment variable.
```

Fix: set one of them. The compose files build it from `DB_APP_PASSWORD`, so this happens when you run the image outside them.

### DATABASE_URL has an unknown sslmode

Raised in `ServiceCollectionExtensions` as an `ArgumentException`.

<!-- source: barakoCMS/Extensions/ServiceCollectionExtensions.cs -->
```text
Invalid sslmode '...' in DATABASE_URL.
```

Fix: use one of the `sslmode` values Postgres defines, for example `?sslmode=require`. The message
echoes the `sslmode` value, not the password.

### A module declares a contract this core does not support

Raised in `ServiceCollectionExtensions`, when modules load.

<!-- source: barakoCMS/Extensions/ServiceCollectionExtensions.cs -->
```text
This barakoCMS implements module contract v... and supports v... through v.... Refusing to load:
Update the module, or run a core that implements its contract version.
```

Fix: update the module, or run a core whose contract range includes it. How the range works is in
[versioning.md](versioning.md#modules-and-the-core-they-run-on) and in `MODULES.md`.

### The schema is behind the code

A release that changes an existing database object ships a SQL file for it, and the host refuses
to start until the file has run (see [upgrading-to-5.0.md](upgrading-to-5.0.md)). The refusal text
comes from the schema library, not from this repository, so it is not quoted here. Run
`db-migrate` as the owner role before the API starts. See [migrations.md](migrations.md).

## Other settings the API refuses

### Erasure, Tenancy and Sensitivity

<!-- source: barakoCMS/Infrastructure/Erasure/ErasureOptions.cs -->
```text
Erasure:Mode is '...', which is not a mode. Valid values:
Erasure:Mode is CryptoShred, which is not available yet.
Erasure:Mode is None, which leaves no way to erase content. Set Erasure:AcknowledgeNoErasure to true to confirm this deployment's content never holds personal data, or use Delete.
```

<!-- source: barakoCMS/Infrastructure/Multitenancy/TenancyOptions.cs -->
```text
Tenancy:Mode is '...', which is not a mode. Valid values:
```

<!-- source: barakoCMS/Infrastructure/Services/SensitivityService.cs -->
```text
Sensitivity:Mode is All, which is declared but not implemented
```

Fix: `Erasure:Mode` is `Delete` (the default) or `None` with `Erasure:AcknowledgeNoErasure=true`;
`CryptoShred` is refused. `Tenancy:Mode` is `Single` (the default) or `Multi`. For
`Sensitivity:Mode`, leave it unset or use `SensitiveOnly`; only `All` is refused. Mode names are
case insensitive, and the message echoes what you wrote.

### Everything else

Settings that are not on this page, such as a time span or a number that will not parse, are listed
with their bounds in the "Blank or malformed" column of [configuration.md](configuration.md). A
module that cannot start names its own setting. `MODULES.md`, "What the host checks at startup, and
what it does not", lists what the host refuses on a module's behalf.

## Compose

Compose stops before any container starts when a required variable is empty. Docker prints
`required variable NAME is missing a value:` and then the text below. Run
`docker compose config` first to see every one at once.

`quickstart/docker-compose.yml`:

<!-- source: quickstart/docker-compose.yml -->
```text
set DB_PASSWORD in .env
set DB_APP_PASSWORD in .env
pin a version in .env, for example 5.9.0
set a 32+ char JWT_KEY in .env
set ADMIN_PASSWORD in .env
```

`docker-compose.prod.yml`:

<!-- source: docker-compose.prod.yml -->
```text
set DOMAIN_API in .env, for example api.example.com
set ACME_EMAIL in .env
pin a version in .env, for example 3.21.0
set DB_APP_PASSWORD in .env, the API connects as this role
set JWT_KEY in .env to at least 32 random characters
set FRONTEND_ORIGINS in .env
set ADMIN_PASSWORD in .env, this is your first sign-in
```

`docker-compose.hub.yml`:

<!-- source: docker-compose.hub.yml -->
```text
set DB_PASSWORD
set DB_APP_PASSWORD, the API connects as this role
pin a version in .env or your shell, for example 5.9.0
set JWT_SECRET to a random value of 32+ characters
```

In `docker-compose.prod.yml` a missing `JWT_KEY`, `ADMIN_PASSWORD` or `DB_APP_PASSWORD` is reported
as `required variable SECRETS_AS_FILES`, because the file also accepts those as secret files. The
text after the colon names the variable to set.

Fix: copy `quickstart/.env.example` (quickstart) or `.env.prod.example` (production) to `.env` and fill each one. `BARAKO_TAG` has no
default on purpose: pin a published version, not `latest`.

## Port already in use

Docker prints `Bind for ...: port is already allocated`, or the OS prints `address already in use`.
This is Docker's and the OS's text, not the API's. Something else holds the port.

- Quickstart: it publishes `${API_BIND:-127.0.0.1}:${API_PORT:-5005}`. Set `API_PORT` in `.env` to a
  free port.
- Production: Caddy binds 80 and 443 on the host. Stop the web server that holds them, or put your
  own proxy in front and use the quickstart file instead.

## Caddy fails with Address already in use

`docker compose -f docker-compose.prod.yml up -d` stops at `caddy` with `Address already in use`
while the app runs. This is Docker's text about an address on the `proxy` network, not a host port.
The app starts before Caddy and Docker gives it the lowest free address on that network, so a
`PROXY_ADDRESS` at the low end of `PROXY_SUBNET`, such as `10.87.51.2` from an older example, is
already the app's.

Fix: set `PROXY_ADDRESS` in `.env` to the last usable address of `PROXY_SUBNET` (`10.87.51.14` for
the default `10.87.51.0/28`), or remove the line to take that default, and run `up -d` again.
[deploy-in-production.md](deploy-in-production.md#client-ips-behind-caddy) explains the network.

## The certificate is requested before the domain resolves

In `docker-compose.prod.yml` Caddy asks Let's Encrypt for a certificate on first boot, and Let's
Encrypt validates over HTTP on port 80. If the DNS A record for `DOMAIN_API` does not resolve to this
machine yet, or port 80 is blocked, the request fails and Caddy logs the failure in its own words
(`docker compose -f docker-compose.prod.yml logs caddy`). That text is Caddy's, not this
repository's, so it is not quoted here.

Fix: create the A record, wait until `dig +short $DOMAIN_API` returns this machine's address, open
port 80, then `docker compose -f docker-compose.prod.yml restart caddy`. Caddy retries by itself, but
Let's Encrypt rate limits repeated failures, so fix DNS first. See
[deploy-in-production.md](deploy-in-production.md).

## How this page is kept true

`BarakoCMS.Tests/TroubleshootingPageTests.cs` reads every `text` block on this page, takes the
`source` comment above it, and checks that each fixed part of each line is in that file. Numbers the page states, such as the minimum key and password lengths, are checked against the code's constants. A message
that changes in the code and not here fails. It also fails when a class under
`barakoCMS/Infrastructure/Security/` that can stop startup is not named on this page, and when a
class on its list stops throwing. That check is at file level: a new refusal inside a class
the page already names is not caught, so the page covers the common refusals, not every one.
