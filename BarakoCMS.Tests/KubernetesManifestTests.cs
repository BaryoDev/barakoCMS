using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// Guardrails for the manifests in <c>k8s/</c>.
/// </summary>
/// <remarks>
/// These had never been applied. The Deployment asked for <c>memory: "128Mw"</c>, which the API
/// server rejects outright, so nothing else in the directory had been exercised either and the rest
/// of it showed that: a database password inlined in the Deployment that diverged from the secret
/// operators are told to replace, a ConfigMap no pod consumed, a Grafana dashboard sitting in the
/// apply path, and both probes pointing at the same endpoint.
///
/// Read as text rather than through a YAML parser, like ComposeDefaultsTests, because the risk being
/// guarded is a line coming back and this catches it in the unit suite without a cluster. That the
/// manifests apply was proved separately, against a real API server. See issues #280 and #281.
/// </remarks>
public class KubernetesManifestTests
{

    private static string ManifestDir() => Path.Combine(RepoPaths.Root, "k8s");

    private static string[] ManifestFiles() =>
        Directory.GetFiles(ManifestDir(), "*.yaml", SearchOption.TopDirectoryOnly)
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToArray();

    private static string[] Lines(string file) => File.ReadAllLines(file);

    private static string Deployment() => Path.Combine(ManifestDir(), "05-deployment.yaml");

    /// <summary>The quantity format the API server enforces, from its own error message.</summary>
    private static readonly Regex Quantity =
        new(@"^([+-]?[0-9.]+)([eEinumkKMGTP]*[-+]?[0-9]*)$", RegexOptions.Compiled);

    private static readonly Regex QuantityLine =
        new(@"^\s*(memory|cpu|storage|ephemeral-storage):\s*""?([^""\s#]+)""?", RegexOptions.Compiled);

    [Fact]
    public void The_quantity_matcher_accepts_a_real_quantity_and_rejects_the_one_that_shipped()
    {
        Quantity.IsMatch("128Mi").Should().BeTrue();
        Quantity.IsMatch("100m").Should().BeTrue();
        Quantity.IsMatch("10Gi").Should().BeTrue();
        Quantity.IsMatch("128Mw").Should().BeFalse(
            "this is the value that made kubectl apply fail, so a matcher that accepts it proves nothing");
    }

    [Fact]
    public void Every_resource_quantity_is_one_Kubernetes_accepts()
    {
        var offenders = ManifestFiles()
            .SelectMany(file => Lines(file).Select(line => (File: Path.GetFileName(file), Line: line)))
            .Select(x => (x.File, Match: QuantityLine.Match(x.Line)))
            .Where(x => x.Match.Success)
            .Where(x => !Quantity.IsMatch(x.Match.Groups[2].Value))
            .Select(x => $"{x.File}: {x.Match.Groups[1].Value}={x.Match.Groups[2].Value}")
            .ToArray();

        offenders.Should().BeEmpty("the API server refuses the whole manifest over one bad quantity");
    }

    [Fact]
    public void The_apply_directory_holds_only_things_kubectl_can_apply()
    {
        var strays = Directory.GetFiles(ManifestDir(), "*.json", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileName)
            .ToArray();

        strays.Should().BeEmpty(
            "kubectl apply -f k8s/ reads .json as well as .yaml, and a Grafana dashboard in here "
          + "failed the apply with 'apiVersion not set' before it reached any manifest");
    }

    [Fact]
    public void The_app_gets_its_database_password_from_the_secret_rather_than_inline()
    {
        var literals = Lines(Deployment())
            .Select(l => l.Trim())
            .Where(l => !l.StartsWith('#'))
            .Where(l => l.Contains("Password=", StringComparison.Ordinal))
            .ToArray();

        literals.Should().BeEmpty(
            "03-postgres.yaml takes POSTGRES_PASSWORD from barako-secrets, whose placeholder tells "
          + "operators to replace it. An inline connection string here means following that "
          + "instruction gives Postgres a password the app is never handed");

        string.Join('\n', Lines(Deployment())).Should().Contain(
            "key: ConnectionStrings__DefaultConnection",
            "the app reads the same secret the operator edited");
    }

    [Fact]
    public void The_app_pod_consumes_the_ConfigMap()
    {
        string.Join('\n', Lines(Deployment())).Should().Contain("configMapRef",
            "01-configmap.yaml sets ASPNETCORE_ENVIRONMENT=Production and nothing read it, so a "
          + "Kubernetes deployment did not run in Production mode");
    }

    [Fact]
    public void The_app_image_is_pinned_to_a_version()
    {
        var image = Lines(Deployment())
            .Select(l => l.Trim())
            .First(l => l.StartsWith("image:", StringComparison.Ordinal));

        image.Should().NotEndWith(":latest",
            "with :latest plus IfNotPresent a node keeps whatever it cached, so two replicas can run "
          + "different builds and a rollback has nothing to roll back to");
    }

    [Fact]
    public void Liveness_and_readiness_are_different_endpoints()
    {
        var paths = ProbePaths(Deployment());

        paths.Should().ContainKey("livenessProbe").And.ContainKey("readinessProbe");
        paths["livenessProbe"].Should().Be("/health/live");
        paths["readinessProbe"].Should().Be("/health/ready");
        paths["livenessProbe"].Should().NotBe(paths["readinessProbe"],
            "pointing both at one endpoint that runs the database check turns a Postgres restart "
          + "into a simultaneous restart of every replica");
    }

    [Fact]
    public void A_startup_probe_covers_the_boot_time_schema_and_seed_work()
    {
        ProbePaths(Deployment()).Should().ContainKey("startupProbe",
            "a fresh database applies the schema and seeds before it can serve, and without a "
          + "startup probe liveness counts that against the pod");
    }

    private static Dictionary<string, string> ProbePaths(string file)
    {
        var probes = new Dictionary<string, string>(StringComparer.Ordinal);
        string? current = null;

        foreach (var raw in Lines(file))
        {
            var line = raw.Trim();

            if (line is "livenessProbe:" or "readinessProbe:" or "startupProbe:")
            {
                current = line.TrimEnd(':');
                continue;
            }

            if (current is not null && line.StartsWith("path:", StringComparison.Ordinal))
            {
                probes[current] = line["path:".Length..].Trim();
                current = null;
            }
        }

        return probes;
    }

    [Fact]
    public void The_Ingress_hosts_and_every_probe_Host_header_are_in_AllowedHosts()
    {
        var allowed = AllowedHosts();
        allowed.Should().NotBeEmpty("Production refuses an empty AllowedHosts");
        allowed.Should().NotContain("*", "Production refuses a wildcard AllowedHosts");

        var ingressHosts = IngressHosts();
        ingressHosts.Should().NotBeEmpty("an Ingress with no host routes nothing");
        ingressHosts.Where(h => !allowed.Contains(h, StringComparer.OrdinalIgnoreCase))
            .Should().BeEmpty("a request for an Ingress host that is not in AllowedHosts gets a 400");

        var probes = ProbeHostHeaders(Deployment());
        probes.Keys.Should().BeEquivalentTo(new[] { "startupProbe", "livenessProbe", "readinessProbe" });

        foreach (var (probe, host) in probes)
        {
            host.Should().NotBeNull($"{probe} must send a Host header, the pod address is not in AllowedHosts");
            allowed.Should().Contain(host!, $"the {probe} Host header has to be one AllowedHosts accepts");
        }
    }

    private static string[] AllowedHosts()
    {
        var line = Lines(Path.Combine(ManifestDir(), "01-configmap.yaml"))
            .Select(l => l.Trim())
            .FirstOrDefault(l => l.StartsWith("AllowedHosts:", StringComparison.Ordinal));

        line.Should().NotBeNull("01-configmap.yaml sets AllowedHosts");

        return line!["AllowedHosts:".Length..].Trim().Trim('"', '\'')
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static string[] IngressHosts()
    {
        var hosts = new List<string>();
        var inTlsHosts = false;

        foreach (var raw in Lines(Path.Combine(ManifestDir(), "08-ingress.yaml")))
        {
            var line = raw.Trim();
            if (line.StartsWith('#')) continue;

            if (line == "hosts:") { inTlsHosts = true; continue; }

            if (inTlsHosts && line.StartsWith("- ", StringComparison.Ordinal))
            {
                hosts.Add(line[2..].Trim());
                continue;
            }

            inTlsHosts = false;

            var rule = line.StartsWith("- host:", StringComparison.Ordinal) ? line[2..] : line;
            if (rule.StartsWith("host:", StringComparison.Ordinal))
                hosts.Add(rule["host:".Length..].Trim());
        }

        return hosts.ToArray();
    }

    /// <summary>The Host header value each probe sends, or null for a probe with none.</summary>
    private static Dictionary<string, string?> ProbeHostHeaders(string file)
    {
        var probes = new Dictionary<string, string?>(StringComparer.Ordinal);
        string? current = null;
        var sawHostName = false;

        foreach (var raw in Lines(file))
        {
            var line = raw.Trim();
            if (line.StartsWith('#')) continue;

            if (line is "livenessProbe:" or "readinessProbe:" or "startupProbe:")
            {
                current = line.TrimEnd(':');
                probes[current] = null;
                sawHostName = false;
                continue;
            }

            if (current is null) continue;

            if (line.EndsWith("name: Host", StringComparison.Ordinal))
                sawHostName = true;
            else if (sawHostName && line.StartsWith("value:", StringComparison.Ordinal))
            {
                probes[current] = line["value:".Length..].Trim().Trim('"', '\'');
                sawHostName = false;
            }
        }

        return probes;
    }

    /// <summary>The hardening fields the app Deployment must carry, as text a plain check can find.</summary>
    private static string[] MissingHardening(string manifest)
    {
        var text = manifest.Replace("\r", "");
        var required = new Dictionary<string, string>
        {
            ["pod runAsNonRoot"] = @"(?m)^\s*runAsNonRoot:\s*true\s*$",
            ["a numeric runAsUser"] = @"(?m)^\s*runAsUser:\s*[1-9][0-9]*\s*$",
            ["pod seccompProfile RuntimeDefault"] = @"(?ms)^\s*seccompProfile:\s*\n\s*type:\s*RuntimeDefault\s*$",
            ["readOnlyRootFilesystem"] = @"(?m)^\s*readOnlyRootFilesystem:\s*true\s*$",
            ["allowPrivilegeEscalation false"] = @"(?m)^\s*allowPrivilegeEscalation:\s*false\s*$",
            ["capabilities drop ALL"] = @"(?ms)^\s*capabilities:\s*\n\s*drop:\s*\[\s*""?ALL""?\s*\]\s*$",
            ["an emptyDir for /tmp"] = @"(?ms)mountPath:\s*/tmp\b",
        };

        return required.Where(r => !Regex.IsMatch(text, r.Value)).Select(r => r.Key).ToArray();
    }

    [Fact]
    public void The_app_Deployment_runs_non_root_read_only_with_no_capabilities()
    {
        MissingHardening(File.ReadAllText(Deployment())).Should().BeEmpty();
    }

    [Fact]
    public void The_hardening_check_rejects_the_known_bad_manifest()
    {
        var bad = File.ReadAllText(Path.Combine(RepoPaths.Root, "scripts", "testdata", "k8s-known-bad", "05-deployment.yaml"));

        MissingHardening(bad).Should().HaveCount(7, "the manifest has none of the fields, so a check that finds fewer is not looking");
    }
}
