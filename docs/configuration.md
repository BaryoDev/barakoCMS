# Configuration reference

Every configuration key the app reads, with its default, its bounds and what a blank or malformed value does. A test (`ConfigurationReferenceTests`) scans the source and fails when a key is read and not listed here, or listed here and not read.

Set a key in `appsettings.json`, or as an environment variable. In an environment variable, `:` becomes `__` (so `Jobs:LeaseSeconds` is `Jobs__LeaseSeconds`). Names are not case sensitive.

Every limit has a safe default. A limit that can be turned off says so in the Allowed column, and only an explicit value does it. A blank or malformed value either keeps the default or throws (the host refuses to start, or the call fails and names the key). It never removes a limit.

Keys that hold secrets have no default and are marked Secret. Set them from a secret file or the environment, never a file in git.

A key written `{name}` or `*` stands for a name you choose. Keys under `Kestrel:Limits` follow the Kestrel server limits of the same name, and the rows list the ones the app sets.

## Core

### Host and URLs

Where the app answers and how it names itself.

| Key | Environment variable | Default | Allowed | Blank or malformed |
|---|---|---|---|---|
| `App:BaseUrl` | `App__BaseUrl` | none | An absolute http or https URL. | Unset: links in emails and feeds that need a public address fall back or are refused. Blank is treated as unset. |
| `App:ConsoleUrl` | `App__ConsoleUrl` | none | An absolute URL. | Unset: the template variable for the console link is empty. Blank is treated as unset. |
| `AllowedHosts` | `AllowedHosts` | none (any host) | A semicolon list of host names, or *. | Unset or *: any Host header is accepted, and start is refused unless Security:AllowAnyHost is true. Not checked in Development. |
| `Security:AllowAnyHost` | `Security__AllowAnyHost` | false | true or false. | Not read in Development. Elsewhere blank or malformed throws at start, and only when AllowedHosts accepts every host. |
| `Branding:AppName` | `Branding__AppName` | BarakoCMS | Any text. | Unset keeps the default. Blank is read as empty. |
| `Feeds:SiteUrl` | `Feeds__SiteUrl` | none | An absolute http or https URL. | Unset: feeds and the sitemap refuse to build. A relative or non-http URL throws on the request or job that reads it. |
| `Feeds:Paths:*` | `Feeds__Paths__*` | /{name}/{slug} | A path template for the feed named by the wildcard. | Unset keeps the default path. |
| `Feeds:Titles:*` | `Feeds__Titles__*` | the content type name | Any text, for the content type named by the wildcard. | Unset keeps the default title. |
| `Blueprints:Path` | `Blueprints__Path` | none | A folder path. | Unset or blank: no extra blueprints are loaded. |
| `Swagger:Enabled` | `Swagger__Enabled` | true in Development, false elsewhere | true or false. | Blank or malformed throws at start. |
| `Serilog:WriteToFile` | `Serilog__WriteToFile` | false | true or false. | Blank or malformed throws at start. |
| `HealthChecksUI:Enabled` | `HealthChecksUI__Enabled` | false | true or false. | No effect. The dashboard was removed, and a true value only logs a warning. Blank or malformed throws on the request or job that reads it. |

### Database and tenancy

| Key | Environment variable | Default | Allowed | Blank or malformed |
|---|---|---|---|---|
| `ConnectionStrings:DefaultConnection` | `ConnectionStrings__DefaultConnection` | none | A Npgsql connection string. | Unset and no DATABASE_URL: start is refused. |
| `DATABASE_URL` | `DATABASE_URL` | none | A postgres:// URL. sslmode defaults to require. | Overrides the connection string when set. An invalid sslmode throws at start. |
| `Database:AllowSuperuser` | `Database__AllowSuperuser` | false | true or false. | Blank or malformed throws at start. When false, start is refused if the database role is a superuser. |
| `Schema:ApplyLockTimeoutSeconds` | `Schema__ApplyLockTimeoutSeconds` | 300 | Greater than 0. | Blank or 0 or less keeps the default. Malformed throws at start. |
| `Leadership:RefuseBehindPooler` | `Leadership__RefuseBehindPooler` | false | true or false. | Blank or malformed throws on the request or job that reads it. |
| `Tenancy:Mode` | `Tenancy__Mode` | Single | Single or Multi. | Blank is Single. An unknown value throws at start. |
| `Tenancy:DeploymentHost` | `Tenancy__DeploymentHost` | the host in App:BaseUrl | A host name. | Unset or blank falls back to the host of App:BaseUrl. |
| `Tenancy:DatabaseEnforcement` | `Tenancy__DatabaseEnforcement` | false | true or false. | Blank or malformed throws at start. When true, start is refused if the database role can bypass row level security. |
| `Tenants:MaxImportEntries` | `Tenants__MaxImportEntries` | 5000 | Greater than 0. | Blank or 0 or less keeps the default. Malformed throws on the request or job that reads it. |
| `Metrics:TenantLabel` | `Metrics__TenantLabel` | true | true or false. | Blank or malformed throws on the request or job that reads it. When false, metrics carry the label all instead of the tenant. |
| `Multitenancy:RefuseUnknownHosts` | `Multitenancy__RefuseUnknownHosts` | false | true or false. | Blank or malformed throws at start. |
| `Multitenancy:CacheDuration` | `Multitenancy__CacheDuration` | 00:05:00 | A time span, hh:mm:ss. | Blank or malformed throws at start. |
| `CORS:AllowedOrigins` | `CORS__AllowedOrigins` | none | One comma separated string. Indexed entries (CORS__AllowedOrigins__0) are not read. | Unset or blank: in Development http://localhost:3000, http://localhost:3001 and https://localhost:7049 are allowed, elsewhere no origin is. |
| `CORS:AllowTenantDomains` | `CORS__AllowTenantDomains` | false | true or false. | Anything other than true, blank or a typo included, leaves it off. |

### Request limits and Kestrel

Every limit has a default. A value of 0 or less is refused for the keys below, so a limit cannot be switched off by leaving a key blank.

| Key | Environment variable | Default | Allowed | Blank or malformed |
|---|---|---|---|---|
| `RequestLimits:MaxBodyBytes` | `RequestLimits__MaxBodyBytes` | 10485760 (10 MiB) | Greater than 0. | Blank keeps the default. A value of 0 or less, or a malformed value, throws at start. |
| `Kestrel:Limits:MaxRequestBodySize` | `Kestrel__Limits__MaxRequestBodySize` | not supported | Do not set it. Use RequestLimits:MaxBodyBytes. | Any value throws at start and names RequestLimits:MaxBodyBytes. |
| `Kestrel:Limits:RequestHeadersTimeout` | `Kestrel__Limits__RequestHeadersTimeout` | 00:00:15 | A positive time span. | Blank is skipped and keeps the default. A zero, negative or infinite value throws at start and names the key. |
| `Kestrel:Limits:KeepAliveTimeout` | `Kestrel__Limits__KeepAliveTimeout` | 00:02:10 | A positive time span. | Blank is skipped and keeps the default. A zero, negative or infinite value throws at start and names the key. |
| `Kestrel:Limits:MaxRequestHeadersTotalSize` | `Kestrel__Limits__MaxRequestHeadersTotalSize` | 32768 | A positive number of bytes. | Blank is skipped and keeps the default. Malformed or not positive throws at start and names the key. |
| `Kestrel:Limits:MaxRequestHeaderCount` | `Kestrel__Limits__MaxRequestHeaderCount` | 100 | A positive number. | Blank is skipped and keeps the default. Malformed or not positive throws at start and names the key. |
| `Kestrel:Limits:MaxConcurrentConnections` | `Kestrel__Limits__MaxConcurrentConnections` | 10000 | A positive number. | Blank is skipped and keeps the default. Zero, negative or no limit throws at start and names the key. |
| `Kestrel:Limits:MaxConcurrentUpgradedConnections` | `Kestrel__Limits__MaxConcurrentUpgradedConnections` | 1000 | A positive number. | Blank is skipped and keeps the default. Zero, negative or no limit throws at start and names the key. |
| `Kestrel:Limits:MinRequestBodyDataRate:BytesPerSecond` | `Kestrel__Limits__MinRequestBodyDataRate__BytesPerSecond` | 240 | Greater than 0. | Blank is skipped and keeps the default. Zero or less, or malformed, throws at start. |
| `Kestrel:Limits:MinRequestBodyDataRate:GracePeriod` | `Kestrel__Limits__MinRequestBodyDataRate__GracePeriod` | 00:00:05 | A time span greater than 1 second. | Blank is skipped and keeps the default. One second or less, or malformed, throws at start. |
| `Kestrel:Limits:MinResponseDataRate:BytesPerSecond` | `Kestrel__Limits__MinResponseDataRate__BytesPerSecond` | Kestrel default | Greater than 0. | Blank is skipped and keeps the Kestrel default. Zero or less, or malformed, throws at start. |
| `Kestrel:Limits:MinResponseDataRate:GracePeriod` | `Kestrel__Limits__MinResponseDataRate__GracePeriod` | Kestrel default | A time span greater than 1 second. | Blank is skipped and keeps the Kestrel default. One second or less, or malformed, throws at start. |
| `ForwardedHeaders:Enabled` | `ForwardedHeaders__Enabled` | false | true or false. | Blank or malformed throws at start. Enabled with no proxy or network listed throws at start. |
| `ForwardedHeaders:ForwardHost` | `ForwardedHeaders__ForwardHost` | true | true or false. | Blank keeps true. Malformed throws at start. |
| `ForwardedHeaders:ForwardLimit` | `ForwardedHeaders__ForwardLimit` | 1 | 1 or more. | Blank keeps 1. 0 or less, or malformed, throws at start. |
| `ForwardedHeaders:AllowedHosts` | `ForwardedHeaders__AllowedHosts` | none | A list of host names, one indexed entry each, for example ForwardedHeaders__AllowedHosts__0. A single value is ignored. | Unset, blank or a single value: no host list, so any forwarded host is taken. |
| `ForwardedHeaders:KnownProxies` | `ForwardedHeaders__KnownProxies` | none | A list of IP addresses, one indexed entry each, for example ForwardedHeaders__KnownProxies__0. A single value is ignored. | Unset, blank or a single value: the list is empty. With ForwardedHeaders:Enabled true and both lists empty, start is refused. A bad address throws at start. |
| `ForwardedHeaders:KnownNetworks` | `ForwardedHeaders__KnownNetworks` | none | A list of CIDR ranges, one indexed entry each, for example ForwardedHeaders__KnownNetworks__0. A single value is ignored. | Unset, blank or a single value: the list is empty. With ForwardedHeaders:Enabled true and both lists empty, start is refused. A bad range throws at start. |
| `Hsts:MaxAgeDays` | `Hsts__MaxAgeDays` | 90 | Greater than 0. | Read only outside Development, where the HSTS header is sent. Blank keeps the default. 0 or less throws at start. Malformed throws at start. |
| `Hsts:IncludeSubDomains` | `Hsts__IncludeSubDomains` | false | true or false. | Read only outside Development. Blank keeps false. Malformed throws at start. |

### Rate limiting

Every window below has a safe default, and a value of 0 or less is refused at start rather than switching the limit off. A queue of 0 is allowed and means no queue. Delivery, ApiKey and each policy are opt-in: they are off until PermitLimit is set, and WindowSeconds and QueueLimit then default to 60 and 0.

| Key | Environment variable | Default | Allowed | Blank or malformed |
|---|---|---|---|---|
| `RateLimiting:Global:PermitLimit` | `RateLimiting__Global__PermitLimit` | 100 | Greater than 0. | Blank keeps the default. 0 or less throws at start, so a limit cannot be turned off. Malformed throws at start. |
| `RateLimiting:Global:WindowSeconds` | `RateLimiting__Global__WindowSeconds` | 60 | Greater than 0. | Blank keeps the default. 0 or less throws at start. Malformed throws at start. |
| `RateLimiting:Global:QueueLimit` | `RateLimiting__Global__QueueLimit` | 10 | 0 or more. 0 means no queue. | Blank keeps the default. Less than 0 throws at start. 0 is allowed and means no queue. Malformed throws at start. |
| `RateLimiting:Auth:PermitLimit` | `RateLimiting__Auth__PermitLimit` | 5 | Greater than 0. | Blank keeps the default. 0 or less throws at start, so a limit cannot be turned off. Malformed throws at start. |
| `RateLimiting:Auth:WindowSeconds` | `RateLimiting__Auth__WindowSeconds` | 900 | Greater than 0. | Blank keeps the default. 0 or less throws at start. Malformed throws at start. |
| `RateLimiting:Auth:QueueLimit` | `RateLimiting__Auth__QueueLimit` | 0 | 0 or more. 0 means no queue. | Blank keeps the default. Less than 0 throws at start. 0 is allowed and means no queue. Malformed throws at start. |
| `RateLimiting:Refresh:PermitLimit` | `RateLimiting__Refresh__PermitLimit` | 60 | Greater than 0. | Blank keeps the default. 0 or less throws at start, so a limit cannot be turned off. Malformed throws at start. |
| `RateLimiting:Refresh:WindowSeconds` | `RateLimiting__Refresh__WindowSeconds` | 900 | Greater than 0. | Blank keeps the default. 0 or less throws at start. Malformed throws at start. |
| `RateLimiting:Refresh:QueueLimit` | `RateLimiting__Refresh__QueueLimit` | 0 | 0 or more. 0 means no queue. | Blank keeps the default. Less than 0 throws at start. 0 is allowed and means no queue. Malformed throws at start. |
| `RateLimiting:Batch:PermitLimit` | `RateLimiting__Batch__PermitLimit` | 20 | Greater than 0. | Blank keeps the default. 0 or less throws at start, so a limit cannot be turned off. Malformed throws at start. |
| `RateLimiting:Batch:WindowSeconds` | `RateLimiting__Batch__WindowSeconds` | 60 | Greater than 0. | Blank keeps the default. 0 or less throws at start. Malformed throws at start. |
| `RateLimiting:Batch:QueueLimit` | `RateLimiting__Batch__QueueLimit` | 0 | 0 or more. 0 means no queue. | Blank keeps the default. Less than 0 throws at start. 0 is allowed and means no queue. Malformed throws at start. |
| `RateLimiting:Registration:PermitLimit` | `RateLimiting__Registration__PermitLimit` | 5 | Greater than 0. | Blank keeps the default. 0 or less throws at start, so a limit cannot be turned off. Malformed throws at start. |
| `RateLimiting:Registration:WindowSeconds` | `RateLimiting__Registration__WindowSeconds` | 3600 | Greater than 0. | Blank keeps the default. 0 or less throws at start. Malformed throws at start. |
| `RateLimiting:Registration:QueueLimit` | `RateLimiting__Registration__QueueLimit` | 0 | 0 or more. 0 means no queue. | Blank keeps the default. Less than 0 throws at start. 0 is allowed and means no queue. Malformed throws at start. |
| `RateLimiting:Renderer:PermitLimit` | `RateLimiting__Renderer__PermitLimit` | 1000 | Greater than 0. | Blank keeps the default. 0 or less throws at start, so a limit cannot be turned off. Malformed throws at start. |
| `RateLimiting:Renderer:WindowSeconds` | `RateLimiting__Renderer__WindowSeconds` | 60 | Greater than 0. | Blank keeps the default. 0 or less throws at start. Malformed throws at start. |
| `RateLimiting:Renderer:QueueLimit` | `RateLimiting__Renderer__QueueLimit` | 10 | 0 or more. 0 means no queue. | Blank keeps the default. Less than 0 throws at start. 0 is allowed and means no queue. Malformed throws at start. |
| `RateLimiting:SiteShare:PermitLimit` | `RateLimiting__SiteShare__PermitLimit` | 10 | Greater than 0. | Blank keeps the default. 0 or less throws at start, so a limit cannot be turned off. Malformed throws at start. |
| `RateLimiting:SiteShare:WindowSeconds` | `RateLimiting__SiteShare__WindowSeconds` | 60 | Greater than 0. | Blank keeps the default. 0 or less throws at start. Malformed throws at start. |
| `RateLimiting:SiteShare:QueueLimit` | `RateLimiting__SiteShare__QueueLimit` | 0 | 0 or more. 0 means no queue. | Blank keeps the default. Less than 0 throws at start. 0 is allowed and means no queue. Malformed throws at start. |
| `RateLimiting:Lookup:PermitLimit` | `RateLimiting__Lookup__PermitLimit` | 10 | Greater than 0. | Blank keeps the default. 0 or less throws at start, so a limit cannot be turned off. Malformed throws at start. |
| `RateLimiting:Lookup:WindowSeconds` | `RateLimiting__Lookup__WindowSeconds` | 60 | Greater than 0. | Blank keeps the default. 0 or less throws at start. Malformed throws at start. |
| `RateLimiting:Lookup:QueueLimit` | `RateLimiting__Lookup__QueueLimit` | 0 | 0 or more. 0 means no queue. | Blank keeps the default. Less than 0 throws at start. 0 is allowed and means no queue. Malformed throws at start. |
| `RateLimiting:LookupReference:PermitLimit` | `RateLimiting__LookupReference__PermitLimit` | 5 | Greater than 0. | Blank keeps the default. 0 or less throws at start, so a limit cannot be turned off. Malformed throws at start. |
| `RateLimiting:LookupReference:WindowSeconds` | `RateLimiting__LookupReference__WindowSeconds` | 900 | Greater than 0. | Blank keeps the default. 0 or less throws at start. Malformed throws at start. |
| `RateLimiting:LookupReference:QueueLimit` | `RateLimiting__LookupReference__QueueLimit` | 0 | 0 or more. 0 means no queue. | Blank keeps the default. Less than 0 throws at start. 0 is allowed and means no queue. Malformed throws at start. |
| `RateLimiting:Delivery:PermitLimit` | `RateLimiting__Delivery__PermitLimit` | none (the limit is off until this is set) | Greater than 0. | Blank is treated as unset. 0 or less throws at start. Malformed throws at start. |
| `RateLimiting:Delivery:WindowSeconds` | `RateLimiting__Delivery__WindowSeconds` | 60, once PermitLimit is set | Greater than 0. | Blank keeps 60. Set without PermitLimit throws at start. 0 or less throws at start. Malformed throws at start. |
| `RateLimiting:Delivery:QueueLimit` | `RateLimiting__Delivery__QueueLimit` | 0, once PermitLimit is set | 0 or more. 0 means no queue. | Blank keeps 0. Set without PermitLimit throws at start. Less than 0 throws at start. Malformed throws at start. |
| `RateLimiting:ApiKey:PermitLimit` | `RateLimiting__ApiKey__PermitLimit` | none (the limit is off until this is set) | Greater than 0. | Blank is treated as unset. 0 or less throws at start. Malformed throws at start. |
| `RateLimiting:ApiKey:WindowSeconds` | `RateLimiting__ApiKey__WindowSeconds` | 60, once PermitLimit is set | Greater than 0. | Blank keeps 60. Set without PermitLimit throws at start. 0 or less throws at start. Malformed throws at start. |
| `RateLimiting:ApiKey:QueueLimit` | `RateLimiting__ApiKey__QueueLimit` | 0, once PermitLimit is set | 0 or more. 0 means no queue. | Blank keeps 0. Set without PermitLimit throws at start. Less than 0 throws at start. Malformed throws at start. |
| `RateLimiting:Renderer:Key` | `RateLimiting__Renderer__Key` | none | At least 32 characters. | Secret. Set it from a secret file or the environment, never a file in git. Unset: no renderer partition. Set but shorter than 32 characters throws at start. |
| `RateLimiting:Policies:{name}:PermitLimit` | `RateLimiting__Policies__{name}__PermitLimit` | none | Greater than 0. A name is up to 64 letters, digits, dashes, underscores and dots, and cannot be a built-in policy name. | A policy a route names. Without PermitLimit the policy throws at start. 0 or less throws at start. Malformed throws at start. |
| `RateLimiting:Policies:{name}:WindowSeconds` | `RateLimiting__Policies__{name}__WindowSeconds` | 60, once PermitLimit is set | Greater than 0. | Blank keeps 60. 0 or less throws at start. Malformed throws at start. |
| `RateLimiting:Policies:{name}:QueueLimit` | `RateLimiting__Policies__{name}__QueueLimit` | 0, once PermitLimit is set | 0 or more. 0 means no queue. | Blank keeps 0. Less than 0 throws at start. Malformed throws at start. |
| `RateLimiting:Policies:{name}:Partition` | `RateLimiting__Policies__{name}__Partition` | Ip | Ip, User or ApiKey, not case sensitive. | Blank keeps Ip. Any other value throws at start. |

### Authentication and secrets

Keys marked secret have no default and must not be committed. Each key must be at least 32 characters and differ from the others.

| Key | Environment variable | Default | Allowed | Blank or malformed |
|---|---|---|---|---|
| `JWT:Key` | `JWT__Key` | none | At least 32 characters. | Secret. Set it from a secret file or the environment, never a file in git. Unset or short: start is refused. |
| `JWT:Issuer` | `JWT__Issuer` | none | Any text. | Unset: the token issuer check uses the app default. Blank is treated as unset. |
| `JWT:Audience` | `JWT__Audience` | none | Any text. | Unset: the token audience check uses the app default. Blank is treated as unset. |
| `Secrets:Key` | `Secrets__Key` | none (derived from JWT:Key) | At least 32 characters. | Secret. Set it from a secret file or the environment, never a file in git. Unset: material derived from JWT:Key is used. Short throws at start. |
| `Secrets:Keys:{id}` | `Secrets__Keys__{id}` | none | id is 1 to 32 letters, digits, dash or underscore. Key material at least 32 characters. | Secret. Set it from a secret file or the environment, never a file in git. A key ring entry, so old values still decrypt after a rotation. |
| `Secrets:CurrentKey` | `Secrets__CurrentKey` | none | An id listed under Secrets:Keys. | Unset with several keys listed throws at start. An id not in the ring throws at start. |
| `Mfa:Key` | `Mfa__Key` | none (derived from JWT:Key) | At least 32 characters, different from Secrets:Key and Connectors:Key. | Secret. Set it from a secret file or the environment, never a file in git. Short or equal to another throws at start. |
| `Mfa:Keys:{id}` | `Mfa__Keys__{id}` | none | As Secrets:Keys:{id}. | Secret. Set it from a secret file or the environment, never a file in git. A key ring entry. |
| `Mfa:CurrentKey` | `Mfa__CurrentKey` | none | An id listed under Mfa:Keys. | As Secrets:CurrentKey. |
| `Connectors:Key` | `Connectors__Key` | none (derived from JWT:Key) | At least 32 characters, different from Secrets:Key and Mfa:Key. | Secret. Set it from a secret file or the environment, never a file in git. Short or equal to another throws at start. |
| `Connectors:Keys:{id}` | `Connectors__Keys__{id}` | none | As Secrets:Keys:{id}. | Secret. Set it from a secret file or the environment, never a file in git. A key ring entry. |
| `Connectors:CurrentKey` | `Connectors__CurrentKey` | none | An id listed under Connectors:Keys. | As Secrets:CurrentKey. |
| `InitialAdmin:Username` | `InitialAdmin__Username` | none | Any text. | Unset: no first admin is created. Both username and password are needed. |
| `InitialAdmin:Password` | `InitialAdmin__Password` | none | Any text that passes the password rules. | Secret. Set it from a secret file or the environment, never a file in git. Unset: no first admin is created. |
| `Auth:RequireEmailVerification` | `Auth__RequireEmailVerification` | true | true or false. | Blank or malformed throws at start. |
| `Auth:AcknowledgeUnverifiedRegistration` | `Auth__AcknowledgeUnverifiedRegistration` | false | true or false. | Blank or malformed throws at start. Must be true when Auth:RequireEmailVerification is false, or start is refused. |
| `Auth:LegacyRoleFallback` | `Auth__LegacyRoleFallback` | false | true or false. | Blank or malformed throws on the request or job that reads it. |
| `Auth:RefreshRetryWindowSeconds` | `Auth__RefreshRetryWindowSeconds` | 30 | 0 to 300. Above 300 becomes 300. Below 0 turns the retry off. | Blank keeps 30. Malformed keeps 30 and logs a note at start. |
| `Auth:RegisterMailCooldownMinutes` | `Auth__RegisterMailCooldownMinutes` | 5 | Whole minutes. 0 or less turns the cooldown off. | Blank or malformed throws on the registration request. |
| `Auth:StepUp:Seconds` | `Auth__StepUp__Seconds` | 300 | 30 to 3600. Values outside are cut to the range. | Blank or malformed keeps 300. |
| `Roles:RefuseUnknownCapabilities` | `Roles__RefuseUnknownCapabilities` | false | true or false. | Blank or malformed throws on the request or job that reads it. |

### Modules and startup

| Key | Environment variable | Default | Allowed | Blank or malformed |
|---|---|---|---|---|
| `BarakoCMS:Modules:Enabled` | `BarakoCMS__Modules__Enabled` | all registered modules | A list, or a comma separated list. An empty value means core only. | Unset: every registered module runs. A name that matches no module is logged and ignored. |
| `BarakoCMS:Modules:Discover` | `BarakoCMS__Modules__Discover` | true | true or false. | Blank or malformed throws at start. |
| `BarakoCMS:Modules:SchemaPreflight` | `BarakoCMS__Modules__SchemaPreflight` | on when the schema is created automatically | true or false. | Blank keeps the default. Malformed throws at start. |
| `Barako:NodeId` | `Barako__NodeId` | a random id per process | A GUID. | Blank or malformed uses a random id. |
| `Seed:DemoContent` | `Seed__DemoContent` | true in Development, false elsewhere | true or false. | Blank or malformed throws at start. |
| `Seed:File` | `Seed__File` | none | A path to a seed file. | Unset or blank: no seed file is applied. |
| `EventSourcing:DocumentTypesAppend` | `EventSourcing__DocumentTypesAppend` | true | true or false. | Blank or malformed throws on the request or job that reads it. |
| `ContentTypes:MaxFields` | `ContentTypes__MaxFields` | 200 | 1 or more. | Blank keeps the default. Less than 1 is raised to 1. Malformed throws on the request or job that reads it. |
| `Content:Concurrency:Require` | `Content__Concurrency__Require` | false | true or false. | Blank or malformed throws on the request or job that reads it. |
| `PublicDelivery:RequireAcknowledgement` | `PublicDelivery__RequireAcknowledgement` | false | true or false. | Blank or malformed throws on the request or job that reads it. |
| `Erasure:Mode` | `Erasure__Mode` | Delete | Delete, None or CryptoShred. | Blank is Delete. None needs Erasure:AcknowledgeNoErasure. CryptoShred throws at start (not supported). |
| `Erasure:AcknowledgeNoErasure` | `Erasure__AcknowledgeNoErasure` | false | true or false. | Blank or malformed throws at start. |
| `Sensitivity:Mode` | `Sensitivity__Mode` | SensitiveOnly | SensitiveOnly or the other documented mode. | An unknown or blank value falls back to SensitiveOnly. |
| `CollectionSyncs:Enabled` | `CollectionSyncs__Enabled` | true | true or false. | Blank or malformed throws on the request or job that reads it. |
| `CollectionSyncs:RunLockWaitSeconds` | `CollectionSyncs__RunLockWaitSeconds` | 5 | 0 to 30. Above 30 becomes 30. | Blank keeps the default. Malformed throws at start. |
| `Lifecycle:EnforceTransitions` | `Lifecycle__EnforceTransitions` | true | true or false. | Blank or malformed throws on the request or job that reads it. |
| `Lifecycle:AllowSelfTransition:*` | `Lifecycle__AllowSelfTransition__*` | false | true or false, per content type named by the wildcard. | Unset: a self transition is refused. |
| `Lifecycle:AllowSelfTransition:*:*` | `Lifecycle__AllowSelfTransition__*__*` | false | true or false, per content type and state. | Unset: a self transition is refused. |
| `Envelope:BranchContentType` | `Envelope__BranchContentType` | Branch | A content type name. | Unset or blank keeps the default. |
| `Envelope:BranchCodeField` | `Envelope__BranchCodeField` | Code | A field name. | Unset or blank keeps the default. |
| `Idempotency:KeyHours` | `Idempotency__KeyHours` | 24 | 1 to 720. | Blank or malformed throws on the request or job that reads it. Outside the range refuses start. |
| `Idempotency:MaxStoredResponseBytes` | `Idempotency__MaxStoredResponseBytes` | 65536 | 0 to 1048576. | Blank or malformed throws on the request or job that reads it. Outside the range refuses start. |

### Jobs and outbox

| Key | Environment variable | Default | Allowed | Blank or malformed |
|---|---|---|---|---|
| `Jobs:MaxAttempts` | `Jobs__MaxAttempts` | 5 | 1 or more. | Blank or malformed throws on the request or job that reads it. Less than 1 refuses start. |
| `Jobs:BackoffBaseSeconds` | `Jobs__BackoffBaseSeconds` | 30 | 0 or more. | Blank or malformed throws on the request or job that reads it. Negative refuses start. |
| `Jobs:BackoffMaxSeconds` | `Jobs__BackoffMaxSeconds` | 3600 | At least Jobs:BackoffBaseSeconds. | Blank or malformed throws on the request or job that reads it. A value below the base refuses start. |
| `Jobs:LeaseSeconds` | `Jobs__LeaseSeconds` | 600 | 1 or more. | Blank or malformed throws on the request or job that reads it. Less than 1 refuses start. The outbound and S3 call budgets are checked against it. |
| `Jobs:StorageProbeSeconds` | `Jobs__StorageProbeSeconds` | 60 | 1 or more. | Blank or malformed throws on the request or job that reads it. Less than 1 refuses start. |
| `Jobs:DeadLetterRetentionDays` | `Jobs__DeadLetterRetentionDays` | 0 (keep forever) | Up to 3650. 0 or less keeps dead letters forever. | Blank or malformed throws on the request or job that reads it. Above 3650 refuses start. |
| `Jobs:MetricsIntervalSeconds` | `Jobs__MetricsIntervalSeconds` | 30 | 0 to 3600. 0 turns the queue count off. | Blank or malformed throws on the request or job that reads it. Outside the range refuses start. |
| `Jobs:Wolverine:Enabled` | `Jobs__Wolverine__Enabled` | false | true or false. | Blank or malformed throws at start. |
| `Outbox:MaxAttempts` | `Outbox__MaxAttempts` | 10 | 1 or more. | Blank or malformed throws on the request or job that reads it. Less than 1 is raised to 1. |
| `Outbox:PollSeconds` | `Outbox__PollSeconds` | 5 | 1 or more. | Blank or malformed throws on the request or job that reads it. Less than 1 is raised to 1. |

### Workflows

| Key | Environment variable | Default | Allowed | Blank or malformed |
|---|---|---|---|---|
| `Workflows:RunnerEnabled` | `Workflows__RunnerEnabled` | true | true or false. | Blank or malformed throws on the request or job that reads it. |
| `Workflows:RunnerConcurrency` | `Workflows__RunnerConcurrency` | 1 | 1 to 20. | Blank or malformed throws on the request or job that reads it. Outside the range refuses start. |
| `Workflows:BacklogIntervalSeconds` | `Workflows__BacklogIntervalSeconds` | 30 | 0 to 3600. 0 turns the due run count off. | Blank or malformed throws on the request or job that reads it. Outside the range refuses start. |
| `Workflows:ScheduleGraceDays` | `Workflows__ScheduleGraceDays` | 7 | Greater than 0 and at most 3650. | Blank, 0 or less, or above 3650 keeps the default. Malformed throws on the request or job that reads it. |
| `Workflows:SchedulePageSize` | `Workflows__SchedulePageSize` | 200 | 1 or more. | Blank or 0 or less keeps the default. Malformed throws on the request or job that reads it. |
| `Workflows:RunsListLimit` | `Workflows__RunsListLimit` | 100 | 1 or more. | Blank or 0 or less keeps the default. Malformed throws on the request or job that reads it. |
| `Workflows:RunsCandidateLimit` | `Workflows__RunsCandidateLimit` | 1000 | 1 or more. | Blank or 0 or less keeps the default. Malformed throws on the request or job that reads it. |
| `Workflows:Retention:Enabled` | `Workflows__Retention__Enabled` | true | true or false. | Blank or malformed throws on the request or job that reads it. |
| `Workflows:Retention:Succeeded` | `Workflows__Retention__Succeeded` | 7 | Days. 0 or less keeps these runs forever. | Blank or malformed throws on the request or job that reads it. |
| `Workflows:Retention:Failed` | `Workflows__Retention__Failed` | 90 | Days. 0 or less keeps these runs forever. | Blank or malformed throws on the request or job that reads it. |
| `Workflows:ActionLinks:Enabled` | `Workflows__ActionLinks__Enabled` | false | true or false. | Blank or malformed throws on the request or job that reads it. |
| `Workflows:ActionLinks:ExpiryHours` | `Workflows__ActionLinks__ExpiryHours` | 72 | Greater than 0 and at most 8760. | A value outside the range keeps the default. Blank or malformed throws on the request or job that reads it. |
| `Workflows:Email:Attachments:MaxCount` | `Workflows__Email__Attachments__MaxCount` | 5 | A whole number up to 2147483647. 0 turns attachments off: an email that names a file is refused. | Blank keeps the default. A malformed value refuses each email that carries attachments and names the key. Other emails are not affected. |
| `Workflows:Email:Attachments:MaxFileBytes` | `Workflows__Email__Attachments__MaxFileBytes` | 10485760 (10 MiB) | A whole number up to 2147483647. 0 turns attachments off. | Blank keeps the default. A malformed value refuses each email that carries attachments and names the key. Other emails are not affected. |
| `Workflows:Email:Attachments:MaxTotalBytes` | `Workflows__Email__Attachments__MaxTotalBytes` | 15728640 (15 MiB) | A whole number up to 2147483647. 0 turns attachments off. | Blank keeps the default. A malformed value refuses each email that carries attachments and names the key. Other emails are not affected. |
| `Workflows:Outbound:Retries` | `Workflows__Outbound__Retries` | 2 | 0 to 5. | Blank or malformed throws on the request or job that reads it. Outside the range refuses start. |
| `Workflows:Outbound:AttemptTimeoutSeconds` | `Workflows__Outbound__AttemptTimeoutSeconds` | 10 | Greater than 0. | Blank or malformed throws on the request or job that reads it. 0 or less refuses start. |
| `Workflows:Outbound:EmailSendTimeoutSeconds` | `Workflows__Outbound__EmailSendTimeoutSeconds` | 0 (no limit, stated outright) | 0 or more. 0 means no limit. | Blank or malformed throws on the request or job that reads it. Negative refuses start. |
| `Workflows:Outbound:BaseDelayMilliseconds` | `Workflows__Outbound__BaseDelayMilliseconds` | 200 | 0 up to the maximum delay. | Blank or malformed throws on the request or job that reads it. Outside the range refuses start. |
| `Workflows:Outbound:MaxDelaySeconds` | `Workflows__Outbound__MaxDelaySeconds` | 2 | Greater than 0. | Blank or malformed throws on the request or job that reads it. 0 or less refuses start. |
| `Workflows:Outbound:MaxRetryAfterSeconds` | `Workflows__Outbound__MaxRetryAfterSeconds` | 5 | 0 or more. 0 never waits for a Retry-After. | Blank or malformed throws on the request or job that reads it. Negative refuses start. |
| `Workflows:Outbound:BreakerFailures` | `Workflows__Outbound__BreakerFailures` | 5 | 0 or more. 0 turns the breaker off. | Blank or malformed throws on the request or job that reads it. Negative refuses start. |
| `Workflows:Outbound:BreakerWindow` | `Workflows__Outbound__BreakerWindow` | 10 | At least BreakerFailures. | Blank or malformed throws on the request or job that reads it. A smaller value refuses start. |
| `Workflows:Outbound:BreakerSamplingSeconds` | `Workflows__Outbound__BreakerSamplingSeconds` | 60 | Greater than 0. | Blank or malformed throws on the request or job that reads it. 0 or less refuses start. |
| `Workflows:Outbound:BreakerOpenSeconds` | `Workflows__Outbound__BreakerOpenSeconds` | 30 | Greater than 0. | Blank or malformed throws on the request or job that reads it. 0 or less refuses start. |

### Webhooks

| Key | Environment variable | Default | Allowed | Blank or malformed |
|---|---|---|---|---|
| `Webhooks:AllowProxy` | `Webhooks__AllowProxy` | false | true or false. | Blank or malformed throws at start. |
| `Webhooks:AllowInsecureSignedUrls` | `Webhooks__AllowInsecureSignedUrls` | false | true or false. | Blank or malformed throws on the request or job that reads it. |
| `Webhooks:AllowedHosts` | `Webhooks__AllowedHosts` | none | A list of host names, one indexed entry each, for example Webhooks__AllowedHosts__0. | Unset: no host is on the allow list. A single value logs a warning when the guard is first built and allows nothing. |
| `Webhooks:DeliveryLogRetentionDays` | `Webhooks__DeliveryLogRetentionDays` | 30 | Days. 0 or less keeps the log forever. | Blank or malformed throws on the request or job that reads it. |
| `Webhooks:ResponseBodyRetentionHours` | `Webhooks__ResponseBodyRetentionHours` | 24 | Hours. 0 or less keeps the bodies forever. | Blank or malformed throws on the request or job that reads it. |

### Delivery and caching

| Key | Environment variable | Default | Allowed | Blank or malformed |
|---|---|---|---|---|
| `Delivery:MaxRadiusKm` | `Delivery__MaxRadiusKm` | the built-in default radius | Greater than 0. | Blank, malformed or not positive keeps the default. |
| `Delivery:PrivateSite:CacheSeconds` | `Delivery__PrivateSite__CacheSeconds` | 5 | 0 or more. 0 turns the cache off, so every request checks. | Blank or malformed throws on the request or job that reads it. |
| `Delivery:Events:Enabled` | `Delivery__Events__Enabled` | false | true or false. | Blank or malformed throws on the request or job that reads it. |
| `Delivery:Events:MaxConnections` | `Delivery__Events__MaxConnections` | 100 | 1 or more. | Blank or malformed throws on the request or job that reads it. Less than 1 is raised to 1. |
| `Delivery:Events:MaxConnectionsPerClient` | `Delivery__Events__MaxConnectionsPerClient` | 5 | 0 or more. 0 turns the per client cap off. | Blank or malformed throws on the request or job that reads it. Less than 0 is raised to 0. |
| `Delivery:Events:KeepAliveSeconds` | `Delivery__Events__KeepAliveSeconds` | 15 | 1 or more. | Blank or malformed throws on the request or job that reads it. Less than 1 is raised to 1. |
| `Delivery:Events:GateRecheckSeconds` | `Delivery__Events__GateRecheckSeconds` | 30 | 1 or more. | Blank or malformed throws on the request or job that reads it. Less than 1 is raised to 1. |
| `Delivery:CacheTags:MaxTags` | `Delivery__CacheTags__MaxTags` | 32 | 1 to 1000. Values outside are cut to the range. | Blank keeps the default. Malformed keeps the default and logs a warning at start. |
| `Delivery:CacheTags:MaxHeaderLength` | `Delivery__CacheTags__MaxHeaderLength` | 1024 | 1024 to 16384. Values outside are cut to the range. | Blank keeps the default. Malformed keeps the default and logs a warning at start. |
| `Delivery:ResponseCache:Enabled` | `Delivery__ResponseCache__Enabled` | false | true or false. | Blank or malformed throws at start. |
| `Delivery:ResponseCache:ExpireSeconds` | `Delivery__ResponseCache__ExpireSeconds` | 60 | Whole seconds. | Blank or malformed throws at start. |
| `Delivery:ResponseCache:SizeLimitMegabytes` | `Delivery__ResponseCache__SizeLimitMegabytes` | 64 | Whole megabytes. | Blank or malformed throws at start. |
| `Delivery:ResponseCache:MaxBodyKilobytes` | `Delivery__ResponseCache__MaxBodyKilobytes` | 512 | Whole kilobytes. | Blank or malformed throws at start. |
| `Delivery:ResponseCache:MaxQueryLength` | `Delivery__ResponseCache__MaxQueryLength` | 512 | Whole characters. | Blank or malformed throws at start. |
| `Delivery:CdnPurge:Enabled` | `Delivery__CdnPurge__Enabled` | false | true or false. | Blank or malformed is read as false when the host is checked. |
| `Delivery:CdnPurge:Provider` | `Delivery__CdnPurge__Provider` | none | Cloudflare or Fastly. | Unset or any other value: purging stays off. |
| `Delivery:CdnPurge:TargetId` | `Delivery__CdnPurge__TargetId` | none | The zone id or service id. | Unset or invalid: purging stays off. |
| `Delivery:CdnPurge:ApiToken` | `Delivery__CdnPurge__ApiToken` | none | The provider API token. | Secret. Set it from a secret file or the environment, never a file in git. Unset: purging stays off. |
| `Delivery:CdnPurge:BaseUrl:*` | `Delivery__CdnPurge__BaseUrl__*` | the provider's public API address | An absolute URL, for Cloudflare or Fastly. | Unset or blank keeps the default. |
| `Delivery:CdnPurge:DebounceMs` | `Delivery__CdnPurge__DebounceMs` | 2000 | 0 or more. | Blank or malformed throws on the request or job that reads it. Less than 0 is raised to 0. |
| `Delivery:CdnPurge:TimeoutSeconds` | `Delivery__CdnPurge__TimeoutSeconds` | 10 | 1 or more. | Blank or malformed throws on the request or job that reads it. Less than 1 is raised to 1. |
| `Delivery:CdnPurge:MaxAttempts` | `Delivery__CdnPurge__MaxAttempts` | 5 | 1 or more. | Blank or malformed throws on the request or job that reads it. Less than 1 is raised to 1. |
| `Delivery:CdnPurge:MaxTagsPerFlush` | `Delivery__CdnPurge__MaxTagsPerFlush` | 500 | 1 or more. | Blank or malformed throws on the request or job that reads it. Less than 1 is raised to 1. |
| `Delivery:CdnPurge:PollSeconds` | `Delivery__CdnPurge__PollSeconds` | 15 | 1 or more. | Blank or malformed throws on the request or job that reads it. Less than 1 is raised to 1. |
| `Delivery:CdnPurge:RetryBackoffSeconds` | `Delivery__CdnPurge__RetryBackoffSeconds` | 30 | 1 or more. | Blank or malformed throws on the request or job that reads it. Less than 1 is raised to 1. |

### Email

| Key | Environment variable | Default | Allowed | Blank or malformed |
|---|---|---|---|---|
| `Email:ApiKey` | `Email__ApiKey` | none | A provider API key. | Secret. Set it from a secret file or the environment, never a file in git. Unset: EMAIL_API_KEY is tried, then the mock sender stays. |
| `Email:From` | `Email__From` | none | A mailbox address. | Unset: the provider default sender is used. |
| `Resend:ApiKey` | `Resend__ApiKey` | none | A provider API key. | Secret. Set it from a secret file or the environment, never a file in git. Only checked for a startup warning: a Resend key set with no email provider registered logs it. |

### Health, metrics and tracing

| Key | Environment variable | Default | Allowed | Blank or malformed |
|---|---|---|---|---|
| `HealthChecks:MaxPrivateMemoryMegabytes` | `HealthChecks__MaxPrivateMemoryMegabytes` | 4096 | A number of megabytes. | Blank keeps the default. Malformed throws at start. |
| `HealthChecks:MinimumFreeDiskMegabytes` | `HealthChecks__MinimumFreeDiskMegabytes` | 512 | A number of megabytes. | Blank keeps the default. Malformed throws at start. |
| `HealthChecks:MaxProjectionLagEvents` | `HealthChecks__MaxProjectionLagEvents` | 1000 | A number of events. | Blank keeps the default. Malformed throws at start. |
| `Metrics:ScrapeKey` | `Metrics__ScrapeKey` | none | Any text. | Secret. Set it from a secret file or the environment, never a file in git. Unset: the metrics endpoint serves nobody. |
| `Tracing:Otlp:Endpoint` | `Tracing__Otlp__Endpoint` | none (tracing off) | An absolute URL. | Unset or blank: tracing is off. A relative URL throws at start. |
| `Tracing:Otlp:Protocol` | `Tracing__Otlp__Protocol` | grpc | grpc or http/protobuf. | Read only when an endpoint is set. Blank keeps the default. Any other value throws at start. |
| `Tracing:Otlp:Headers` | `Tracing__Otlp__Headers` | none | name=value pairs, comma separated. | Secret. Set it from a secret file or the environment, never a file in git. Unset: no extra headers. |
| `Tracing:Otlp:TimeoutSeconds` | `Tracing__Otlp__TimeoutSeconds` | 10 | 1 to 60. | Read only when an endpoint is set. Blank or malformed throws on the request or job that reads it. Outside the range refuses start. |
| `Tracing:ServiceName` | `Tracing__ServiceName` | barakocms | Any text. | Read only when an endpoint is set. Blank keeps the default. |
| `Tracing:SampleRatio` | `Tracing__SampleRatio` | 1.0 | 0 to 1. | Read only when an endpoint is set. Blank or malformed throws on the request or job that reads it. Outside the range refuses start. |
| `Tracing:MaxQueueSize` | `Tracing__MaxQueueSize` | 2048 | 1 to 65536. | Read only when an endpoint is set. Blank or malformed throws on the request or job that reads it. Outside the range refuses start. |

## Module: AI

Settings live under Modules:AI. The older Ai section is read when Modules:AI is empty. Local models are the default and nothing leaves the host until a provider key is set.

| Key | Environment variable | Default | Allowed | Blank or malformed |
|---|---|---|---|---|
| `Modules:AI:Enabled` | `Modules__AI__Enabled` | false | true or false. | Blank or malformed throws on the request or job that reads it. |
| `Modules:AI:EmbeddingBaseUrl` | `Modules__AI__EmbeddingBaseUrl` | http://localhost:11434 | An absolute URL. | Blank is read as empty. |
| `Modules:AI:EmbeddingModel` | `Modules__AI__EmbeddingModel` | nomic-embed-text | A model name. | Blank is read as empty. |
| `Modules:AI:SemanticSearchScanLimit` | `Modules__AI__SemanticSearchScanLimit` | 1000 | 1 or more. | Blank or malformed throws on the request or job that reads it. |
| `Modules:AI:SemanticSearchPermitLimit` | `Modules__AI__SemanticSearchPermitLimit` | 60 | 1 or more. | Blank or malformed throws on the request or job that reads it. |
| `Modules:AI:SemanticSearchWindowSeconds` | `Modules__AI__SemanticSearchWindowSeconds` | 60 | 1 or more. | Blank or malformed throws on the request or job that reads it. |
| `Modules:AI:Completion:Enabled` | `Modules__AI__Completion__Enabled` | false | true or false. | Blank or malformed throws on the request or job that reads it. |
| `Modules:AI:Completion:Provider` | `Modules__AI__Completion__Provider` | Ollama | Ollama, OpenAI or Anthropic. | Blank is read as empty and the provider is refused when a completion runs. |
| `Modules:AI:Completion:OllamaModel` | `Modules__AI__Completion__OllamaModel` | llama3.2 | A model name. | Blank is read as empty. |
| `Modules:AI:Completion:OpenAIKey` | `Modules__AI__Completion__OpenAIKey` | none | A provider API key. | Secret. Set it from a secret file or the environment, never a file in git. Unset: the OpenAI provider is not used. |
| `Modules:AI:Completion:OpenAIModel` | `Modules__AI__Completion__OpenAIModel` | gpt-4o-mini | A model name. | Blank is read as empty. |
| `Modules:AI:Completion:AnthropicKey` | `Modules__AI__Completion__AnthropicKey` | none | A provider API key. | Secret. Set it from a secret file or the environment, never a file in git. Unset: the Anthropic provider is not used. |
| `Modules:AI:Completion:AnthropicModel` | `Modules__AI__Completion__AnthropicModel` | claude-sonnet-5-5 | A model name. | Blank is read as empty. |
| `Modules:AI:Completion:TimeoutSeconds` | `Modules__AI__Completion__TimeoutSeconds` | 30 | 1 to 600. | Blank or malformed throws on the request or job that reads it. |
| `Modules:AI:Completion:MaxInputChars` | `Modules__AI__Completion__MaxInputChars` | 20000 | 1 or more. | Blank or malformed throws on the request or job that reads it. |
| `Modules:AI:Completion:MaxOutputChars` | `Modules__AI__Completion__MaxOutputChars` | 8000 | 1 or more. | Blank or malformed throws on the request or job that reads it. |

## Module: Diagnostics

Settings live under Modules:Diagnostics. Forwarding is off by default.

| Key | Environment variable | Default | Allowed | Blank or malformed |
|---|---|---|---|---|
| `Modules:Diagnostics:Forwarding:Enabled` | `Modules__Diagnostics__Forwarding__Enabled` | false | true or false. | Blank or malformed throws on the request or job that reads it. |
| `Modules:Diagnostics:Forwarding:Dsn` | `Modules__Diagnostics__Forwarding__Dsn` | none | A DSN URL. | Secret. Set it from a secret file or the environment, never a file in git. Unset: nothing is forwarded. |
| `Modules:Diagnostics:Forwarding:Environment` | `Modules__Diagnostics__Forwarding__Environment` | none | Any text. | Unset or blank: no environment tag is sent. |
| `Modules:Diagnostics:Forwarding:QueueCapacity` | `Modules__Diagnostics__Forwarding__QueueCapacity` | 500 | 1 or more. | Blank or malformed throws on the request or job that reads it. |
| `Modules:Diagnostics:Forwarding:TimeoutSeconds` | `Modules__Diagnostics__Forwarding__TimeoutSeconds` | 5 | 1 to 60. Values outside are cut to the range. | Blank or malformed throws on the request or job that reads it. |

## Module: Email.Smtp

Settings live under Modules:Email.Smtp. With no Host the module registers nothing and the existing email provider stays.

| Key | Environment variable | Default | Allowed | Blank or malformed |
|---|---|---|---|---|
| `Modules:Email.Smtp:Host` | `Modules__Email.Smtp__Host` | none | A host name. | Unset or blank: the module registers nothing. |
| `Modules:Email.Smtp:Port` | `Modules__Email.Smtp__Port` | 587 | A port number. | Blank or malformed throws at start. |
| `Modules:Email.Smtp:User` | `Modules__Email.Smtp__User` | none | Any text. | Unset: no sign in. |
| `Modules:Email.Smtp:Password` | `Modules__Email.Smtp__Password` | none | Any text. | Secret. Set it from a secret file or the environment, never a file in git. Unset: no sign in. |
| `Modules:Email.Smtp:From` | `Modules__Email.Smtp__From` | none | A mailbox address. | Unset: the sender comes from the caller or the account. |
| `Modules:Email.Smtp:TenantFromDomains` | `Modules__Email.Smtp__TenantFromDomains` | none | A map from tenant to a list of sender domains, for example Modules__Email.Smtp__TenantFromDomains__acme__0. A single value binds nothing. | Empty: every tenant sends from the host sender. |
| `Modules:Email.Smtp:Security` | `Modules__Email.Smtp__Security` | SslOnConnect on port 465, StartTls otherwise | A value of the SmtpSecurity setting. | Blank is read as unset. An unknown value throws on the request or job that reads it. |
| `Modules:Email.Smtp:TimeoutSeconds` | `Modules__Email.Smtp__TimeoutSeconds` | 30 | Greater than 0. | 0 or less uses 30. Blank or malformed throws at start. |

## Module: ExternalAuth

Provider sign in. A provider is on when its client id (or app id) is set and its Enabled key is not false. ExternalAuth:Enabled set to false turns every provider off.

| Key | Environment variable | Default | Allowed | Blank or malformed |
|---|---|---|---|---|
| `ExternalAuth:Enabled` | `ExternalAuth__Enabled` | true | false turns every provider off. Any other value leaves them on. | Blank is read as on. |
| `ExternalAuth:CodeHandoff` | `ExternalAuth__CodeHandoff` | false | true or false. | Blank or malformed throws on the request or job that reads it. |
| `Facebook:AppId` | `Facebook__AppId` | none | A Facebook app id. | Unset or blank: Facebook sign in is off. |
| `Facebook:AppSecret` | `Facebook__AppSecret` | none | A Facebook app secret. | Secret. Set it from a secret file or the environment, never a file in git. |
| `Facebook:Enabled` | `Facebook__Enabled` | true when AppId is set | false turns this provider off. | Blank is read as on. |
| `Facebook:DeletionChoiceDays` | `Facebook__DeletionChoiceDays` | 30 | Greater than 0. | Blank, malformed or 0 or less keeps the default. |
| `Facebook:RequestBirthdayAndLocation` | `Facebook__RequestBirthdayAndLocation` | false | true or false. | Blank or malformed throws on the request or job that reads it. |
| `Facebook:TrustUnverifiedEmail` | `Facebook__TrustUnverifiedEmail` | false | true or false. | Blank or malformed throws on the request or job that reads it. |
| `GitHub:ClientId` | `GitHub__ClientId` | none | A GitHub OAuth client id. | Unset or blank: GitHub sign in is off. |
| `GitHub:ClientSecret` | `GitHub__ClientSecret` | none | A GitHub OAuth client secret. | Secret. Set it from a secret file or the environment, never a file in git. |
| `GitHub:Enabled` | `GitHub__Enabled` | true when ClientId is set | false turns this provider off. | Blank is read as on. |
| `Google:ClientId` | `Google__ClientId` | none | A Google OAuth client id. | Unset or blank: Google sign in is off. |
| `Google:ClientSecret` | `Google__ClientSecret` | none | A Google OAuth client secret. | Secret. Set it from a secret file or the environment, never a file in git. |
| `Google:Enabled` | `Google__Enabled` | true when ClientId is set | false turns this provider off. | Blank is read as on. |
| `LinkedIn:ClientId` | `LinkedIn__ClientId` | none | A LinkedIn OAuth client id. | Unset or blank: LinkedIn sign in is off. |
| `LinkedIn:ClientSecret` | `LinkedIn__ClientSecret` | none | A LinkedIn OAuth client secret. | Secret. Set it from a secret file or the environment, never a file in git. |
| `LinkedIn:Enabled` | `LinkedIn__Enabled` | true when ClientId is set | false turns this provider off. | Blank is read as on. |
| `Oidc:Providers:{name}:ClientId` | `Oidc__Providers__{name}__ClientId` | none | A client id. A name is 1 to 32 characters of a-z, 0-9 and hyphen. Up to 20 providers. | Unset or blank: that provider is off. |
| `Oidc:Providers:{name}:Enabled` | `Oidc__Providers__{name}__Enabled` | true when ClientId is set | false turns this provider off. | Blank is read as on. |
| `Oidc:Providers:{name}:Authority` | `Oidc__Providers__{name}__Authority` | none | An absolute https URL of the provider. | A provider without a valid authority is refused and logged. |
| `Oidc:Providers:{name}:ClientSecret` | `Oidc__Providers__{name}__ClientSecret` | none | A client secret. | Secret. Set it from a secret file or the environment, never a file in git. A provider with neither this nor SignedClientSecret is refused and logged. |
| `Oidc:Providers:{name}:SignedClientSecret:KeyId` | `Oidc__Providers__{name}__SignedClientSecret__KeyId` | none | 1 to 64 letters, digits, dot, underscore or hyphen. | Used instead of ClientSecret for a provider that wants a signed secret. A bad value refuses that provider. |
| `Oidc:Providers:{name}:SignedClientSecret:TeamId` | `Oidc__Providers__{name}__SignedClientSecret__TeamId` | none | 1 to 64 letters, digits, dot, underscore or hyphen. | As KeyId. |
| `Oidc:Providers:{name}:SignedClientSecret:PrivateKey` | `Oidc__Providers__{name}__SignedClientSecret__PrivateKey` | none | A P-256 private key in PEM. | Secret. Set it from a secret file or the environment, never a file in git. A key that is not P-256 refuses that provider. |
| `Oidc:Providers:{name}:SignedClientSecret:Audience` | `Oidc__Providers__{name}__SignedClientSecret__Audience` | the provider issuer | An absolute https URL with no query or fragment. | Blank keeps the default. A bad value refuses that provider. |
| `Oidc:Providers:{name}:ResponseMode` | `Oidc__Providers__{name}__ResponseMode` | none | A response mode the provider supports. | Blank is treated as unset. |
| `Oidc:Providers:{name}:IdTokenAudiences` | `Oidc__Providers__{name}__IdTokenAudiences` | none | A list, one indexed entry each, or one string of audiences separated by commas or spaces. At most 20 values. | Empty: only the client id is accepted. Too many or too long a value refuses that provider. |
| `Oidc:Providers:{name}:Issuer` | `Oidc__Providers__{name}__Issuer` | the authority issuer | An issuer string. | Blank is treated as unset. |
| `Oidc:Providers:{name}:Scopes` | `Oidc__Providers__{name}__Scopes` | openid email profile | A space separated list. | Unset keeps the default. |
| `Oidc:Providers:{name}:DisplayName` | `Oidc__Providers__{name}__DisplayName` | the provider name | Any text. | Blank is treated as unset. |
| `Oidc:Providers:{name}:EmailVerifiedClaim` | `Oidc__Providers__{name}__EmailVerifiedClaim` | email_verified | A claim name. | Blank is treated as unset. |
| `Oidc:Providers:{name}:EmailVerifiedMayBeText` | `Oidc__Providers__{name}__EmailVerifiedMayBeText` | false | true or false. | Anything other than true is read as false. |
| `Oidc:RateLimit:PermitLimit` | `Oidc__RateLimit__PermitLimit` | 20 | Greater than 0. | Blank, malformed or 0 or less keeps the default. |
| `Oidc:RateLimit:WindowSeconds` | `Oidc__RateLimit__WindowSeconds` | 300 | Greater than 0. | Blank, malformed or 0 or less keeps the default. |
| `Social:RateLimit:PermitLimit` | `Social__RateLimit__PermitLimit` | 10 | Greater than 0. | Blank, malformed or 0 or less keeps the default. |
| `Social:RateLimit:WindowSeconds` | `Social__RateLimit__WindowSeconds` | 60 | Greater than 0. | Blank, malformed or 0 or less keeps the default. |
| `Social:RateLimit:ExchangePermitLimit` | `Social__RateLimit__ExchangePermitLimit` | 300 | Greater than 0. | Blank, malformed or 0 or less keeps the default. |
| `Social:RateLimit:ExchangeWindowSeconds` | `Social__RateLimit__ExchangeWindowSeconds` | 60 | Greater than 0. | Blank, malformed or 0 or less keeps the default. |

## Module: FeatureFlags

This module reads no configuration keys.

## Module: Files

Uploads, images and the optional virus scanner. These keys sit at the root, not under Modules:Files.

| Key | Environment variable | Default | Allowed | Blank or malformed |
|---|---|---|---|---|
| `Files:Images:MaxWidth` | `Files__Images__MaxWidth` | 2048 | 0 or less serves the original image. | Blank or malformed throws on the request or job that reads it. |
| `Files:Images:MaxSourcePixels` | `Files__Images__MaxSourcePixels` | 50000000 | Greater than 0. | Blank or malformed throws on the request or job that reads it. |
| `Files:Scanner:Address` | `Files__Scanner__Address` | none | A host and port of a ClamAV daemon. | Unset or blank: no scanner is used. |
| `Files:Scanner:TimeoutSeconds` | `Files__Scanner__TimeoutSeconds` | 30 | Greater than 0. | Blank, malformed or 0 or less keeps the default. |

## Module: Files.S3

Settings live under Modules:Files.S3. The older Files:S3 section is read when Modules:Files.S3 is empty. With no Bucket the module stays dormant and the database storage keeps serving. A retry or timeout left unset is lowered to fit 80 percent of the shortest lease (the lower of 300 seconds and Jobs:LeaseSeconds). A value you set that cannot fit refuses start.

| Key | Environment variable | Default | Allowed | Blank or malformed |
|---|---|---|---|---|
| `Modules:Files.S3:Bucket` | `Modules__Files.S3__Bucket` | none | A bucket name. | Unset or blank: the module stays dormant. |
| `Modules:Files.S3:ServiceUrl` | `Modules__Files.S3__ServiceUrl` | none (AWS, by Region) | An absolute URL, for R2 or a self hosted store. | Unset or blank: AWS is used. |
| `Modules:Files.S3:Region` | `Modules__Files.S3__Region` | us-east-1 | An AWS region name. Used only when ServiceUrl is unset. | Blank is read as empty. |
| `Modules:Files.S3:AccessKey` | `Modules__Files.S3__AccessKey` | none | An access key. | Secret. Set it from a secret file or the environment, never a file in git. |
| `Modules:Files.S3:SecretKey` | `Modules__Files.S3__SecretKey` | none | A secret key. | Secret. Set it from a secret file or the environment, never a file in git. |
| `Modules:Files.S3:ForcePathStyle` | `Modules__Files.S3__ForcePathStyle` | true | true or false. | Blank or malformed throws at start. |
| `Modules:Files.S3:PublicBaseUrl` | `Modules__Files.S3__PublicBaseUrl` | none (public files go through the API) | The root URL of the bucket or its CDN. | Unset or blank: public files are served through the API. |
| `Modules:Files.S3:UsePublicReadAcl` | `Modules__Files.S3__UsePublicReadAcl` | false | true or false. | Blank or malformed throws at start. |
| `Modules:Files.S3:MaxErrorRetry` | `Modules__Files.S3__MaxErrorRetry` | 2, or fewer to fit the lease | 0 to 10. | Blank is treated as unset. Outside the range, or too large for the lease, refuses start. |
| `Modules:Files.S3:TimeoutSeconds` | `Modules__Files.S3__TimeoutSeconds` | 45, or less to fit the lease | Greater than 0. | Blank is treated as unset. 0 or less, or too large for the lease, refuses start. |
| `Modules:Files.S3:AllowPrivateServiceUrl` | `Modules__Files.S3__AllowPrivateServiceUrl` | false | true or false. | Blank or malformed throws at start. |

## Module: Import

Limits for spreadsheet and CSV import. These keys sit at the root. Every limit has a default, and a value of 0 or less is ignored, not applied.

| Key | Environment variable | Default | Allowed | Blank or malformed |
|---|---|---|---|---|
| `Import:MaxRecords` | `Import__MaxRecords` | 5000 | Greater than 0. | Blank or 0 or less keeps the default. Malformed throws on the request or job that reads it. |
| `Import:MaxUploadBytes` | `Import__MaxUploadBytes` | 10485760 (10 MiB) | Greater than 0. | Blank or 0 or less keeps the default. Malformed throws on the request or job that reads it. |
| `Import:MaxSeconds` | `Import__MaxSeconds` | 120 | Greater than 0. | Blank or 0 or less keeps the default. Malformed throws on the request or job that reads it. |
| `Import:MaxColumns` | `Import__MaxColumns` | 256 | Greater than 0. | Blank or 0 or less keeps the default. Malformed throws on the request or job that reads it. |
| `Import:MaxExpandedBytes` | `Import__MaxExpandedBytes` | 8388608 (8 MiB) | Greater than 0. | Blank or 0 or less keeps the default. Malformed throws on the request or job that reads it. |

## Module: Pages

Settings live under Modules:Pages.

| Key | Environment variable | Default | Allowed | Blank or malformed |
|---|---|---|---|---|
| `Modules:Pages:ContentType` | `Modules__Pages__ContentType` | page | A content type name. | Blank is read as empty. |
| `Modules:Pages:ParentField` | `Modules__Pages__ParentField` | ParentPage | A field name. | Blank is read as empty. |
| `Modules:Pages:ShowInNavigationField` | `Modules__Pages__ShowInNavigationField` | ShowInNavigation | A field name. | Blank is read as empty. |
| `Modules:Pages:OrderField` | `Modules__Pages__OrderField` | NavigationOrder | A field name. | Blank is read as empty. |
| `Modules:Pages:TitleField` | `Modules__Pages__TitleField` | Title | A field name. | Blank is read as empty. |
| `Modules:Pages:MaxDepth` | `Modules__Pages__MaxDepth` | 8 | 1 or more. | Blank or malformed throws on the request or job that reads it. |
| `Modules:Pages:ReservedSlugs` | `Modules__Pages__ReservedSlugs` | none | A list of slugs, one indexed entry each, for example Modules__Pages__ReservedSlugs__0. A single value binds nothing. | Empty or a single value: no slug is reserved. |
| `Modules:Pages:HomeSlug` | `Modules__Pages__HomeSlug` | home | A slug. | Blank is read as unset. |
| `Modules:Pages:MaxPages` | `Modules__Pages__MaxPages` | 1000 | 1 or more. | Blank or malformed throws on the request or job that reads it. |

## Module: Portability

Limit for importing a portable bundle. This key sits at the root.

| Key | Environment variable | Default | Allowed | Blank or malformed |
|---|---|---|---|---|
| `Portability:MaxImportRecords` | `Portability__MaxImportRecords` | 5000 | Greater than 0. | Blank or 0 or less keeps the default. Malformed throws on the request or job that reads it. |

## Environment variables

These are read directly from the environment, not through the configuration system. The name in both columns is the same.

| Key | Environment variable | Default | Allowed | Blank or malformed |
|---|---|---|---|---|
| `ASPNETCORE_URLS` | `ASPNETCORE_URLS` | none | A semicolon list of URLs. | Read by the health probe to find its port. Unset: the probe uses the default port. |
| `BARAKO_BUILD_SHA` | `BARAKO_BUILD_SHA` | unknown | A commit id. | Unset or blank reports unknown. |
| `BARAKO_HEALTH_PROBE_TIMEOUT_SECONDS` | `BARAKO_HEALTH_PROBE_TIMEOUT_SECONDS` | the built-in probe timeout | 1 up to the built-in maximum. | Blank, malformed or outside the range keeps the default. |
| `BARAKO_SECRETS_DIR` | `BARAKO_SECRETS_DIR` | /run/secrets | A folder. Each file in it becomes a configuration key named by the file. | Unset keeps the default. A missing folder is skipped. Files whose name starts with a dot or with ignore. are skipped. |
| `EMAIL_API_KEY` | `EMAIL_API_KEY` | none | A provider API key. | Secret. Set it from a secret file or the environment, never a file in git. Used when Email:ApiKey is unset. |
| `PGPASSWORD` | `PGPASSWORD` | none | A database password. | Secret. Set it from a secret file or the environment, never a file in git. Npgsql reads it when the connection string has no password, and the database password guard counts it. |
| `RESEND_API_KEY` | `RESEND_API_KEY` | none | A provider API key. | Secret. Set it from a secret file or the environment, never a file in git. Only checked for a startup warning: a Resend key set with no email provider registered logs it. |
| `SKIP_SEEDER` | `SKIP_SEEDER` | none | Any non empty value skips seeding in core. The Suite host skips only when it is true. | Unset or blank runs the seeder. |
