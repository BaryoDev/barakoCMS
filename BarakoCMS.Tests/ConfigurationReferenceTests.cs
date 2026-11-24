using System.Text.RegularExpressions;
using barakoCMS.Infrastructure.Caching;
using barakoCMS.Infrastructure.Multitenancy;
using BarakoCMS.AI;
using BarakoCMS.Diagnostics.Forwarding;
using BarakoCMS.Email.Smtp;
using BarakoCMS.Files.S3;
using BarakoCMS.Pages;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// docs/configuration.md lists every configuration key the shipped source reads, and nothing else.
/// A key read with no row fails the first test, a row nothing reads fails the second.
/// </summary>
/// <remarks>
/// Source scans, so they need no database or host. The scan is <see cref="ConfigurationKeyScanner"/>.
/// It sees a key written as a literal, a constant, a concatenation or an interpolated string. Three
/// things it cannot see are listed here, each with its reason: options classes bound as a whole
/// (expanded by reflection, so a new property needs a row), keys built at run time (listed in
/// <see cref="RunTimeKeys"/>, and a row with an entry is checked only against the file that
/// reads it), and reads whose key is wholly a variable (<see cref="VariableKeyFiles"/>).
/// A read of a bare leaf name, such as "PermitLimit", is matched against any row that contains that
/// segment, so it cannot show which row is missing; the per-row entries in <see cref="RunTimeKeys"/>
/// are what hold those rows.
/// Keys compare case-insensitively, the way configuration does, with ":" as the separator.
/// </remarks>
public class ConfigurationReferenceTests
{
    private const string Page = "docs/configuration.md";

    // Options classes bound as a whole from a section. The test expands each to its public
    // settable properties, so adding a property without a row fails.
    private static readonly (string Section, Type Options)[] BoundOptions =
    [
        ("Delivery:ResponseCache", typeof(DeliveryResponseCacheOptions)),
        ("Multitenancy", typeof(MultitenancyOptions)),
        ("Modules:AI", typeof(AiOptions)),
        ("Modules:Pages", typeof(PagesOptions)),
        ("Modules:Email.Smtp", typeof(SmtpOptions)),
        ("Modules:Files.S3", typeof(S3StorageOptions)),
        ("Modules:Diagnostics:Forwarding", typeof(ForwardingOptions)),
    ];

    // Keys the code builds at run time, or reads one leaf at a time from a helper, so the scan sees
    // no literal for them. One entry per row, written out in full: a "{name}" in a pattern is the
    // placeholder as the page writes it, and a "*" is not used. Each entry names the file that reads
    // the key and a token that file must contain, the leaf name it reads. A row with an entry is
    // checked against this entry only: delete the row and the third test names the entry, remove the
    // read and the token goes missing. A new key under one of these prefixes needs a row and an
    // entry by hand; the reason says where to look.
    internal static readonly (string Pattern, string File, string Token, string Reason)[] RunTimeKeys =
    [
        ("Kestrel:Limits:MaxRequestBodySize", "barakoCMS/Extensions/ServiceCollectionExtensions.cs", "\"MaxRequestBodySize\"",
            "bound onto KestrelServerLimits from the section, one child at a time"),
        ("Kestrel:Limits:RequestHeadersTimeout", "barakoCMS/Extensions/ServiceCollectionExtensions.cs", "\"RequestHeadersTimeout\"",
            "bound onto KestrelServerLimits from the section, one child at a time"),
        ("Kestrel:Limits:KeepAliveTimeout", "barakoCMS/Extensions/ServiceCollectionExtensions.cs", "\"KeepAliveTimeout\"",
            "bound onto KestrelServerLimits from the section, one child at a time"),
        ("Kestrel:Limits:MaxRequestHeadersTotalSize", "barakoCMS/Extensions/ServiceCollectionExtensions.cs", "MaxRequestHeadersTotalSize",
            "bound onto KestrelServerLimits from the section, one child at a time"),
        ("Kestrel:Limits:MaxRequestHeaderCount", "barakoCMS/Extensions/ServiceCollectionExtensions.cs", "MaxRequestHeaderCount",
            "bound onto KestrelServerLimits from the section, one child at a time"),
        ("Kestrel:Limits:MaxConcurrentConnections", "barakoCMS/Extensions/ServiceCollectionExtensions.cs", "\"MaxConcurrentConnections\"",
            "bound onto KestrelServerLimits from the section, one child at a time"),
        ("Kestrel:Limits:MaxConcurrentUpgradedConnections", "barakoCMS/Extensions/ServiceCollectionExtensions.cs", "\"MaxConcurrentUpgradedConnections\"",
            "bound onto KestrelServerLimits from the section, one child at a time"),
        ("Kestrel:Limits:MinRequestBodyDataRate:BytesPerSecond", "barakoCMS/Extensions/ServiceCollectionExtensions.cs", "\"BytesPerSecond\"",
            "bound onto KestrelServerLimits from the section, one child at a time"),
        ("Kestrel:Limits:MinRequestBodyDataRate:GracePeriod", "barakoCMS/Extensions/ServiceCollectionExtensions.cs", "\"GracePeriod\"",
            "bound onto KestrelServerLimits from the section, one child at a time"),
        ("Kestrel:Limits:MinResponseDataRate:BytesPerSecond", "barakoCMS/Extensions/ServiceCollectionExtensions.cs", "\"BytesPerSecond\"",
            "bound onto KestrelServerLimits from the section, one child at a time"),
        ("Kestrel:Limits:MinResponseDataRate:GracePeriod", "barakoCMS/Extensions/ServiceCollectionExtensions.cs", "\"GracePeriod\"",
            "bound onto KestrelServerLimits from the section, one child at a time"),
        ("RateLimiting:Global:PermitLimit", "barakoCMS/Infrastructure/Security/RateLimitSetup.cs", "\"Global\"",
            "read through a helper that takes the window name"),
        ("RateLimiting:Global:WindowSeconds", "barakoCMS/Infrastructure/Security/RateLimitSetup.cs", "\"Global\"",
            "read through a helper that takes the window name"),
        ("RateLimiting:Global:QueueLimit", "barakoCMS/Infrastructure/Security/RateLimitSetup.cs", "\"Global\"",
            "read through a helper that takes the window name"),
        ("RateLimiting:Auth:PermitLimit", "barakoCMS/Infrastructure/Security/RateLimitSetup.cs", "\"Auth\"",
            "read through a helper that takes the window name"),
        ("RateLimiting:Auth:WindowSeconds", "barakoCMS/Infrastructure/Security/RateLimitSetup.cs", "\"Auth\"",
            "read through a helper that takes the window name"),
        ("RateLimiting:Auth:QueueLimit", "barakoCMS/Infrastructure/Security/RateLimitSetup.cs", "\"Auth\"",
            "read through a helper that takes the window name"),
        ("RateLimiting:Refresh:PermitLimit", "barakoCMS/Infrastructure/Security/RateLimitSetup.cs", "\"Refresh\"",
            "read through a helper that takes the window name"),
        ("RateLimiting:Refresh:WindowSeconds", "barakoCMS/Infrastructure/Security/RateLimitSetup.cs", "\"Refresh\"",
            "read through a helper that takes the window name"),
        ("RateLimiting:Refresh:QueueLimit", "barakoCMS/Infrastructure/Security/RateLimitSetup.cs", "\"Refresh\"",
            "read through a helper that takes the window name"),
        ("RateLimiting:Batch:PermitLimit", "barakoCMS/Infrastructure/Security/RateLimitSetup.cs", "\"Batch\"",
            "read through a helper that takes the window name"),
        ("RateLimiting:Batch:WindowSeconds", "barakoCMS/Infrastructure/Security/RateLimitSetup.cs", "\"Batch\"",
            "read through a helper that takes the window name"),
        ("RateLimiting:Batch:QueueLimit", "barakoCMS/Infrastructure/Security/RateLimitSetup.cs", "\"Batch\"",
            "read through a helper that takes the window name"),
        ("RateLimiting:Registration:PermitLimit", "barakoCMS/Infrastructure/Security/RateLimitSetup.cs", "\"Registration\"",
            "read through a helper that takes the window name"),
        ("RateLimiting:Registration:WindowSeconds", "barakoCMS/Infrastructure/Security/RateLimitSetup.cs", "\"Registration\"",
            "read through a helper that takes the window name"),
        ("RateLimiting:Registration:QueueLimit", "barakoCMS/Infrastructure/Security/RateLimitSetup.cs", "\"Registration\"",
            "read through a helper that takes the window name"),
        ("RateLimiting:Renderer:PermitLimit", "barakoCMS/Infrastructure/Security/RateLimitSetup.cs", "\"Renderer\"",
            "read through a helper that takes the window name"),
        ("RateLimiting:Renderer:WindowSeconds", "barakoCMS/Infrastructure/Security/RateLimitSetup.cs", "\"Renderer\"",
            "read through a helper that takes the window name"),
        ("RateLimiting:Renderer:QueueLimit", "barakoCMS/Infrastructure/Security/RateLimitSetup.cs", "\"Renderer\"",
            "read through a helper that takes the window name"),
        ("RateLimiting:SiteShare:PermitLimit", "barakoCMS/Infrastructure/Security/RateLimitSetup.cs", "\"SiteShare\"",
            "read through a helper that takes the window name"),
        ("RateLimiting:SiteShare:WindowSeconds", "barakoCMS/Infrastructure/Security/RateLimitSetup.cs", "\"SiteShare\"",
            "read through a helper that takes the window name"),
        ("RateLimiting:SiteShare:QueueLimit", "barakoCMS/Infrastructure/Security/RateLimitSetup.cs", "\"SiteShare\"",
            "read through a helper that takes the window name"),
        ("RateLimiting:Lookup:PermitLimit", "barakoCMS/Infrastructure/Security/RateLimitSetup.cs", "\"Lookup\"",
            "read through a helper that takes the window name"),
        ("RateLimiting:Lookup:WindowSeconds", "barakoCMS/Infrastructure/Security/RateLimitSetup.cs", "\"Lookup\"",
            "read through a helper that takes the window name"),
        ("RateLimiting:Lookup:QueueLimit", "barakoCMS/Infrastructure/Security/RateLimitSetup.cs", "\"Lookup\"",
            "read through a helper that takes the window name"),
        ("RateLimiting:LookupReference:PermitLimit", "barakoCMS/Infrastructure/Security/RateLimitSetup.cs", "\"LookupReference\"",
            "read through a helper that takes the window name"),
        ("RateLimiting:LookupReference:WindowSeconds", "barakoCMS/Infrastructure/Security/RateLimitSetup.cs", "\"LookupReference\"",
            "read through a helper that takes the window name"),
        ("RateLimiting:LookupReference:QueueLimit", "barakoCMS/Infrastructure/Security/RateLimitSetup.cs", "\"LookupReference\"",
            "read through a helper that takes the window name"),
        ("RateLimiting:Delivery:PermitLimit", "barakoCMS/Infrastructure/Security/RateLimitSetup.cs", "\"Delivery\"",
            "read through a helper that takes the window name"),
        ("RateLimiting:Delivery:WindowSeconds", "barakoCMS/Infrastructure/Security/RateLimitSetup.cs", "\"Delivery\"",
            "read through a helper that takes the window name"),
        ("RateLimiting:Delivery:QueueLimit", "barakoCMS/Infrastructure/Security/RateLimitSetup.cs", "\"Delivery\"",
            "read through a helper that takes the window name"),
        ("RateLimiting:ApiKey:PermitLimit", "barakoCMS/Infrastructure/Security/RateLimitSetup.cs", "\"ApiKey\"",
            "read through a helper that takes the window name"),
        ("RateLimiting:ApiKey:WindowSeconds", "barakoCMS/Infrastructure/Security/RateLimitSetup.cs", "\"ApiKey\"",
            "read through a helper that takes the window name"),
        ("RateLimiting:ApiKey:QueueLimit", "barakoCMS/Infrastructure/Security/RateLimitSetup.cs", "\"ApiKey\"",
            "read through a helper that takes the window name"),
        ("RateLimiting:Renderer:Key", "barakoCMS/Infrastructure/Security/RateLimitSetup.cs", "\"Renderer:Key\"",
            "read through a helper that takes the window name"),
        ("RateLimiting:Policies:{name}:PermitLimit", "barakoCMS/Infrastructure/Security/RateLimitSetup.cs", "\"Policies\"",
            "read through a helper that takes the window name"),
        ("RateLimiting:Policies:{name}:WindowSeconds", "barakoCMS/Infrastructure/Security/RateLimitSetup.cs", "\"Policies\"",
            "read through a helper that takes the window name"),
        ("RateLimiting:Policies:{name}:QueueLimit", "barakoCMS/Infrastructure/Security/RateLimitSetup.cs", "\"Policies\"",
            "read through a helper that takes the window name"),
        ("RateLimiting:Policies:{name}:Partition", "barakoCMS/Infrastructure/Security/RateLimitSetup.cs", "Partition",
            "read through a helper that takes the window name"),
        ("Delivery:CdnPurge:BaseUrl:*", "barakoCMS/Infrastructure/Caching/Purge/CdnPurgeOptions.cs", "*",
            "read through a helper that takes the section name"),
        ("Delivery:CdnPurge:DebounceMs", "barakoCMS/Infrastructure/Caching/Purge/CdnPurgeOptions.cs", "DebounceMs",
            "read through a helper that takes the section name"),
        ("Delivery:CdnPurge:TimeoutSeconds", "barakoCMS/Infrastructure/Caching/Purge/CdnPurgeOptions.cs", "TimeoutSeconds",
            "read through a helper that takes the section name"),
        ("Delivery:CdnPurge:MaxAttempts", "barakoCMS/Infrastructure/Caching/Purge/CdnPurgeOptions.cs", "MaxAttempts",
            "read through a helper that takes the section name"),
        ("Delivery:CdnPurge:MaxTagsPerFlush", "barakoCMS/Infrastructure/Caching/Purge/CdnPurgeOptions.cs", "MaxTagsPerFlush",
            "read through a helper that takes the section name"),
        ("Delivery:CdnPurge:PollSeconds", "barakoCMS/Infrastructure/Caching/Purge/CdnPurgeOptions.cs", "PollSeconds",
            "read through a helper that takes the section name"),
        ("Delivery:CdnPurge:RetryBackoffSeconds", "barakoCMS/Infrastructure/Caching/Purge/CdnPurgeOptions.cs", "RetryBackoffSeconds",
            "read through a helper that takes the section name"),
        ("Delivery:CacheTags:MaxTags", "barakoCMS/Infrastructure/Caching/DeliveryTagBounds.cs", "\"MaxTags\"",
            "read through a helper that takes the key"),
        ("Delivery:CacheTags:MaxHeaderLength", "barakoCMS/Infrastructure/Caching/DeliveryTagBounds.cs", "\"MaxHeaderLength\"",
            "read through a helper that takes the key"),
        ("Workflows:Email:Attachments:MaxCount", "barakoCMS/Features/Workflows/Actions/EmailAttachments.cs", "MaxCount",
            "read through a helper that takes the key"),
        ("Workflows:Email:Attachments:MaxFileBytes", "barakoCMS/Features/Workflows/Actions/EmailAttachments.cs", "MaxFileBytes",
            "read through a helper that takes the key"),
        ("Workflows:Email:Attachments:MaxTotalBytes", "barakoCMS/Features/Workflows/Actions/EmailAttachments.cs", "MaxTotalBytes",
            "read through a helper that takes the key"),
        ("Workflows:Outbound:AttemptTimeoutSeconds", "barakoCMS/Infrastructure/Http/OutboundResilienceOptions.cs", "AttemptTimeoutSeconds",
            "read through a helper that takes the key"),
        ("Workflows:Outbound:MaxDelaySeconds", "barakoCMS/Infrastructure/Http/OutboundResilienceOptions.cs", "MaxDelaySeconds",
            "read through a helper that takes the key"),
        ("Workflows:Outbound:MaxRetryAfterSeconds", "barakoCMS/Infrastructure/Http/OutboundResilienceOptions.cs", "MaxRetryAfterSeconds",
            "read through a helper that takes the key"),
        ("Workflows:Outbound:BreakerSamplingSeconds", "barakoCMS/Infrastructure/Http/OutboundResilienceOptions.cs", "BreakerSamplingSeconds",
            "read through a helper that takes the key"),
        ("Workflows:Outbound:BreakerOpenSeconds", "barakoCMS/Infrastructure/Http/OutboundResilienceOptions.cs", "BreakerOpenSeconds",
            "read through a helper that takes the key"),
        ("ForwardedHeaders:ForwardHost", "barakoCMS/Infrastructure/Security/ForwardedHeadersSetup.cs", "\"ForwardHost\"",
            "read from the section one leaf at a time"),
        ("ForwardedHeaders:ForwardLimit", "barakoCMS/Infrastructure/Security/ForwardedHeadersSetup.cs", "\"ForwardLimit\"",
            "read from the section one leaf at a time"),
        ("ForwardedHeaders:AllowedHosts", "barakoCMS/Infrastructure/Security/ForwardedHeadersSetup.cs", "\"AllowedHosts\"",
            "read from the section one leaf at a time"),
        ("ForwardedHeaders:KnownProxies", "barakoCMS/Infrastructure/Security/ForwardedHeadersSetup.cs", "\"KnownProxies\"",
            "read from the section one leaf at a time"),
        ("ForwardedHeaders:KnownNetworks", "barakoCMS/Infrastructure/Security/ForwardedHeadersSetup.cs", "\"KnownNetworks\"",
            "read from the section one leaf at a time"),
        ("Hsts:MaxAgeDays", "barakoCMS/Infrastructure/Security/HstsPolicy.cs", "\"MaxAgeDays\"",
            "read from the section one leaf at a time"),
        ("Hsts:IncludeSubDomains", "barakoCMS/Infrastructure/Security/HstsPolicy.cs", "\"IncludeSubDomains\"",
            "read from the section one leaf at a time"),
        ("Oidc:Providers:{name}:ClientId", "BarakoCMS.ExternalAuth/OidcProviders.cs", "\"ClientId\"",
            "one section per provider, named by the operator"),
        ("Oidc:Providers:{name}:Enabled", "BarakoCMS.ExternalAuth/OidcProviders.cs", "Enabled",
            "one section per provider, named by the operator"),
        ("Oidc:Providers:{name}:Authority", "BarakoCMS.ExternalAuth/OidcProviders.cs", "\"Authority\"",
            "one section per provider, named by the operator"),
        ("Oidc:Providers:{name}:ClientSecret", "BarakoCMS.ExternalAuth/OidcProviders.cs", "\"ClientSecret\"",
            "one section per provider, named by the operator"),
        ("Oidc:Providers:{name}:SignedClientSecret:KeyId", "BarakoCMS.ExternalAuth/OidcClientSecrets.cs", "\"KeyId\"",
            "one section per provider, named by the operator"),
        ("Oidc:Providers:{name}:SignedClientSecret:TeamId", "BarakoCMS.ExternalAuth/OidcClientSecrets.cs", "\"TeamId\"",
            "one section per provider, named by the operator"),
        ("Oidc:Providers:{name}:SignedClientSecret:PrivateKey", "BarakoCMS.ExternalAuth/OidcClientSecrets.cs", "\"PrivateKey\"",
            "one section per provider, named by the operator"),
        ("Oidc:Providers:{name}:SignedClientSecret:Audience", "BarakoCMS.ExternalAuth/OidcClientSecrets.cs", "\"Audience\"",
            "one section per provider, named by the operator"),
        ("Oidc:Providers:{name}:ResponseMode", "BarakoCMS.ExternalAuth/OidcProviders.cs", "\"ResponseMode\"",
            "one section per provider, named by the operator"),
        ("Oidc:Providers:{name}:IdTokenAudiences", "BarakoCMS.ExternalAuth/OidcProviders.cs", "\"IdTokenAudiences\"",
            "one section per provider, named by the operator"),
        ("Oidc:Providers:{name}:Issuer", "BarakoCMS.ExternalAuth/OidcProviders.cs", "\"Issuer\"",
            "one section per provider, named by the operator"),
        ("Oidc:Providers:{name}:Scopes", "BarakoCMS.ExternalAuth/OidcProviders.cs", "\"Scopes\"",
            "one section per provider, named by the operator"),
        ("Oidc:Providers:{name}:DisplayName", "BarakoCMS.ExternalAuth/OidcProviders.cs", "\"DisplayName\"",
            "one section per provider, named by the operator"),
        ("Oidc:Providers:{name}:EmailVerifiedClaim", "BarakoCMS.ExternalAuth/OidcProviders.cs", "\"EmailVerifiedClaim\"",
            "one section per provider, named by the operator"),
        ("Oidc:Providers:{name}:EmailVerifiedMayBeText", "BarakoCMS.ExternalAuth/OidcProviders.cs", "\"EmailVerifiedMayBeText\"",
            "one section per provider, named by the operator"),
        ("Secrets:Keys:{id}", "barakoCMS/Infrastructure/Security/KeyRing.cs", "Keys",
            "a key ring: one entry per key id, named by the operator"),
        ("Mfa:Keys:{id}", "barakoCMS/Infrastructure/Security/KeyRing.cs", "Keys",
            "a key ring: one entry per key id, named by the operator"),
        ("Connectors:Keys:{id}", "barakoCMS/Infrastructure/Security/KeyRing.cs", "Keys",
            "a key ring: one entry per key id, named by the operator"),
        ("Oidc:RateLimit:PermitLimit", "BarakoCMS.ExternalAuth/OidcSupport.cs", "PermitLimit",
            "read as {prefix}:PermitLimit and {prefix}:WindowSeconds"),
        ("Oidc:RateLimit:WindowSeconds", "BarakoCMS.ExternalAuth/OidcSupport.cs", "WindowSeconds",
            "read as {prefix}:PermitLimit and {prefix}:WindowSeconds"),
        ("Social:RateLimit:PermitLimit", "BarakoCMS.ExternalAuth/SocialRateLimit.cs", "\"PermitLimit\"",
            "read as {prefix}:PermitLimit and the keys beside it"),
        ("Social:RateLimit:WindowSeconds", "BarakoCMS.ExternalAuth/SocialRateLimit.cs", "\"WindowSeconds\"",
            "read as {prefix}:PermitLimit and the keys beside it"),
        ("Social:RateLimit:ExchangePermitLimit", "BarakoCMS.ExternalAuth/SocialRateLimit.cs", "\"ExchangePermitLimit\"",
            "read as {prefix}:PermitLimit and the keys beside it"),
        ("Social:RateLimit:ExchangeWindowSeconds", "BarakoCMS.ExternalAuth/SocialRateLimit.cs", "\"ExchangeWindowSeconds\"",
            "read as {prefix}:PermitLimit and the keys beside it"),
        ("Secrets:CurrentKey", "barakoCMS/Infrastructure/Security/KeyRing.cs", "CurrentKey",
            "read as {section}:CurrentKey for each key ring"),
        ("Mfa:CurrentKey", "barakoCMS/Infrastructure/Security/KeyRing.cs", "CurrentKey",
            "read as {section}:CurrentKey for each key ring"),
        ("Connectors:CurrentKey", "barakoCMS/Infrastructure/Security/KeyRing.cs", "CurrentKey",
            "read as {section}:CurrentKey for each key ring"),
        ("Facebook:Enabled", "BarakoCMS.ExternalAuth/ExternalAuthSupport.cs", "Enabled",
            "read as {section}:Enabled for each provider's own section"),
        ("GitHub:Enabled", "BarakoCMS.ExternalAuth/ExternalAuthSupport.cs", "Enabled",
            "read as {section}:Enabled for each provider's own section"),
        ("Google:Enabled", "BarakoCMS.ExternalAuth/ExternalAuthSupport.cs", "Enabled",
            "read as {section}:Enabled for each provider's own section"),
        ("LinkedIn:Enabled", "BarakoCMS.ExternalAuth/ExternalAuthSupport.cs", "Enabled",
            "read as {section}:Enabled for each provider's own section"),
    ];

    // Files where a read passes a variable as the whole key. Each reads keys this page already lists
    // under another heading, or a section named by the caller.
    internal static readonly Dictionary<string, string> VariableKeyFiles = new()
    {
        ["BarakoCMS.ExternalAuth/ExternalAuthSupport.cs"] = "reads a provider's client id and secret from the provider's own section",
        ["BarakoCMS.Files.S3/S3FilesModule.cs"] = "lists which of the S3 options are set, by property name; all are bound options listed under Files.S3",
        ["barakoCMS/Extensions/ServiceCollectionExtensions.cs"] = "builds Modules:{name} and the legacy section from a module's name; the module sections are listed per module",
        ["barakoCMS/Features/Modules/Configuration/ModuleConfigurationReader.cs"] = "reads a module's own section to show it in the console; keys are the module rows",
        ["barakoCMS/Features/Workflows/Actions/EmailAttachments.cs"] = "Workflows:Email:Attachments, one helper for three keys",
        ["barakoCMS/Infrastructure/Caching/DeliveryTagBounds.cs"] = "Delivery:CacheTags, one helper for two keys",
        ["barakoCMS/Infrastructure/Caching/Purge/CdnPurgeOptions.cs"] = "Delivery:CdnPurge, one helper over a section name",
        ["barakoCMS/Infrastructure/Connectors/ConnectorOptions.cs"] = "compares the other key material in use with Connectors:Key",
        ["barakoCMS/Infrastructure/Http/OutboundResilienceOptions.cs"] = "Workflows:Outbound, one helper for every key",
        ["barakoCMS/Infrastructure/Security/CanonicalHost.cs"] = "one helper that reads a key it is handed from this file",
        ["barakoCMS/Infrastructure/Security/ForwardedHeadersSetup.cs"] = "ForwardedHeaders, one helper for the list keys",
        ["barakoCMS/Infrastructure/Security/KeyRing.cs"] = "the Secrets, Mfa and Connectors key rings, which differ only in the section name",
        ["barakoCMS/Infrastructure/Security/RateLimitSetup.cs"] = "RateLimiting, one helper per window name",
        ["barakoCMS/Infrastructure/Services/ConfigurationService.cs"] = "the console's settings reader, which takes the key from the caller",
        ["barakoCMS/Infrastructure/Services/TenantSettingsResolver.cs"] = "reads the host default a module declares for a tenant setting; those are the module rows",
    };

    // Files where the receiver of every read is the module's own section, not the root.
    private static readonly HashSet<string> ModuleSectionFiles = new(StringComparer.Ordinal)
    {
        "BarakoCMS.Diagnostics/DiagnosticsModule.cs",
    };

    private sealed record Row(string Key, string Env, int Line, string[] Cells);

    [Fact]
    public void Every_key_the_source_reads_is_on_the_page()
    {
        var scan = Scan();
        var rows = PageRows();
        var keys = rows.Select(r => r.Key).ToList();

        var missing = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var read in scan.Reads)
        {
            var found = read.Kind switch
            {
                ConfigurationKeyScanner.ReadKind.Key or ConfigurationKeyScanner.ReadKind.Env when read.Key.Contains('*') =>
                    keys.Any(k => Glob(read.Key, k)),
                ConfigurationKeyScanner.ReadKind.Key or ConfigurationKeyScanner.ReadKind.Env =>
                    keys.Any(k => string.Equals(k, read.Key, StringComparison.OrdinalIgnoreCase)),
                ConfigurationKeyScanner.ReadKind.Prefix =>
                    keys.Any(k => string.Equals(k, read.Key, StringComparison.OrdinalIgnoreCase)
                        || k.StartsWith(read.Key + ":", StringComparison.OrdinalIgnoreCase)),
                _ => keys.Any(k => (":" + k + ":").Contains(":" + read.Key + ":", StringComparison.OrdinalIgnoreCase)),
            };

            if (!found)
                missing.Add($"{read.Key} ({read.Kind}, read at {read.File}:{read.Line})");
        }

        foreach (var (section, options) in BoundOptions)
            foreach (var key in ConfigurationKeyScanner.ExpandBound(section, options))
                if (!keys.Contains(key, StringComparer.OrdinalIgnoreCase))
                    missing.Add($"{key} (a property of {options.Name})");

        Assert.True(missing.Count == 0,
            $"{Page} has no row for {missing.Count} key(s) the source reads. Add a row for each:\n  "
            + string.Join("\n  ", missing));
    }

    [Fact]
    public void Every_key_on_the_page_is_read_by_the_source()
    {
        var scan = Scan();
        var rows = PageRows();
        var bound = BoundOptions
            .SelectMany(b => ConfigurationKeyScanner.ExpandBound(b.Section, b.Options))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var unread = new List<string>();
        foreach (var row in rows)
        {
            var read =
                scan.Reads.Any(r => r.Kind is ConfigurationKeyScanner.ReadKind.Key or ConfigurationKeyScanner.ReadKind.Env
                    && (string.Equals(r.Key, row.Key, StringComparison.OrdinalIgnoreCase) || (r.Key.Contains('*') && Glob(r.Key, row.Key))))
                || scan.Reads.Any(r => r.Kind == ConfigurationKeyScanner.ReadKind.Prefix
                    && (string.Equals(r.Key, row.Key, StringComparison.OrdinalIgnoreCase)
                        || row.Key.StartsWith(r.Key + ":", StringComparison.OrdinalIgnoreCase)))
                || scan.Reads.Any(r => r.Kind == ConfigurationKeyScanner.ReadKind.Leaf
                    && (":" + row.Key + ":").Contains(":" + r.Key + ":", StringComparison.OrdinalIgnoreCase))
                || bound.Contains(row.Key)
                || RunTimeKeys.Any(e => Glob(e.Pattern, row.Key));

            if (!read)
                unread.Add($"{row.Key} (line {row.Line})");
        }

        Assert.True(unread.Count == 0,
            $"{Page} lists {unread.Count} key(s) that nothing in the source reads. Remove or rename each:\n  "
            + string.Join("\n  ", unread));
    }

    [Fact]
    public void Run_time_key_entries_still_match_the_code_and_a_row()
    {
        var root = RepoRoot();
        var keys = PageRows().Select(r => r.Key).ToList();
        var stale = new List<string>();
        foreach (var e in RunTimeKeys)
        {
            var path = Path.Combine(root, e.File);
            if (!File.Exists(path))
                stale.Add($"{e.Pattern}: {e.File} does not exist");
            else if (!File.ReadAllText(path).Contains(e.Token, StringComparison.Ordinal))
                stale.Add($"{e.Pattern}: {e.File} no longer contains \"{e.Token}\"");

            if (!keys.Any(k => Glob(e.Pattern, k)))
                stale.Add($"{e.Pattern}: no row on the page matches it");
        }

        Assert.True(stale.Count == 0, "RunTimeKeys has stale entries:\n  " + string.Join("\n  ", stale));
    }

    [Fact]
    public void Reads_with_a_variable_key_are_only_in_the_listed_files()
    {
        var scan = Scan();
        var unexpected = scan.Unresolved
            .Where(u => !VariableKeyFiles.ContainsKey(u.File))
            .Select(u => $"{u.File}:{u.Line}  key written as {u.Expression}")
            .ToList();

        Assert.True(unexpected.Count == 0,
            "These reads pass a key the scan cannot resolve. Write the key as a literal or a constant, "
            + "or add the file to VariableKeyFiles with the reason and give its keys a row on the page:\n  "
            + string.Join("\n  ", unexpected));

        var gone = VariableKeyFiles.Keys.Where(f => scan.Unresolved.All(u => u.File != f)).ToList();
        Assert.True(gone.Count == 0,
            "VariableKeyFiles lists files with no variable-key read any more. Remove them:\n  " + string.Join("\n  ", gone));
    }

    [Fact]
    public void Env_column_is_the_key_with_double_underscores()
    {
        var wrong = PageRows()
            .Where(r => r.Env != r.Key.Replace(":", "__", StringComparison.Ordinal))
            .Select(r => $"{r.Key} (line {r.Line}) has env var {r.Env}, expected {r.Key.Replace(":", "__", StringComparison.Ordinal)}")
            .ToList();

        Assert.True(wrong.Count == 0, string.Join("\n", wrong));
    }

    [Fact]
    public void Every_row_fills_every_column_and_keys_are_unique()
    {
        var problems = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in PageRows())
        {
            if (r.Cells.Length < 5 || r.Cells.Any(string.IsNullOrWhiteSpace))
                problems.Add($"{r.Key} (line {r.Line}) needs the five columns: key, env var, default, allowed, blank or malformed");
            if (!seen.Add(r.Key))
                problems.Add($"{r.Key} (line {r.Line}) is listed twice");
        }

        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    [Fact]
    public void Every_module_has_a_section_on_the_page()
    {
        var root = RepoRoot();
        var page = File.ReadAllText(Path.Combine(root, Page));
        var names = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var file in ConfigurationKeyScanner.ShippedFiles(root))
        {
            var text = File.ReadAllText(Path.Combine(root, file));
            if (!Regex.IsMatch(text, @"class\s+\w+\s*(?:\([^)]*\))?\s*:\s*[^{;]*\bIBarakoModule\b"))
                continue;
            foreach (Match m in Regex.Matches(text, @"\bstring\s+Name\s*=>\s*""(?<n>[^""]+)"""))
                names.Add(m.Groups["n"].Value);
        }

        Assert.NotEmpty(names);
        var absent = names.Where(n => !Regex.IsMatch(page, @"^## Module: " + Regex.Escape(n) + @"\s*$", RegexOptions.Multiline)).ToList();
        Assert.True(absent.Count == 0, $"{Page} needs a \"## Module: <name>\" heading for: " + string.Join(", ", absent));
    }

    [Fact]
    public void The_readme_links_the_page()
    {
        var readme = File.ReadAllText(Path.Combine(RepoRoot(), "README.md"));
        Assert.Contains("(docs/configuration.md)", readme);
    }

    private static ConfigurationKeyScanner.Result Scan() =>
        ConfigurationKeyScanner.Scan(RepoRoot(), ModuleSectionFiles);

    private static List<Row> PageRows()
    {
        var lines = File.ReadAllLines(Path.Combine(RepoRoot(), Page));
        var rows = new List<Row>();
        for (var i = 0; i < lines.Length; i++)
        {
            var m = Regex.Match(lines[i], @"^\|\s*`(?<k>[^`]+)`\s*\|\s*`(?<e>[^`]+)`\s*\|");
            if (!m.Success)
                continue;
            var cells = lines[i].Trim().Trim('|').Split('|').Select(c => c.Trim()).ToArray();
            rows.Add(new Row(m.Groups["k"].Value, m.Groups["e"].Value, i + 1, cells));
        }

        Assert.NotEmpty(rows);
        return rows;
    }

    private static bool Glob(string pattern, string text) =>
        Regex.IsMatch(text, "^" + Regex.Escape(pattern).Replace("\\*", ".*") + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "barakoCMS.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("barakoCMS.sln not found above " + AppContext.BaseDirectory);
    }
}
