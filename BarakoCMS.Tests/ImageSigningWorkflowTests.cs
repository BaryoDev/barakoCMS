using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// The release workflow has to sign every image it publishes and fail when a signature does not
/// verify. The workflow cannot be run here, so these read the file and assert its shape.
/// </summary>
public class ImageSigningWorkflowTests
{
    private const string ReleaseIdentity =
        @"^https://github\.com/BaryoDev/barakoCMS/\.github/workflows/release\.yml@refs/heads/master$";
    private const string MasterIdentity =
        @"^https://github\.com/BaryoDev/barakoCMS/\.github/workflows/publish-master-image\.yml@refs/heads/master$";

    private static string Workflow(string name) =>
        File.ReadAllText(Path.Combine(RepoPaths.Root, ".github", "workflows", name));

    private static string[] CodeLines(string text) =>
        text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')).ToArray();

    // The text of one top-level job, up to the next job at the same indent.
    private static string Job(string yaml, string job)
    {
        var m = Regex.Match(yaml, $@"(?ms)^  {Regex.Escape(job)}:\n(.*?)(?=^  [a-z][a-z0-9-]*:\n|\z)");
        m.Success.Should().BeTrue("the workflow has a {0} job", job);
        return m.Groups[1].Value;
    }

    private static int Line(string[] lines, Func<string, bool> match, string what)
    {
        var i = Array.FindIndex(lines, l => match(l));
        i.Should().BeGreaterThanOrEqualTo(0, "a line for {0} exists", what);
        return i;
    }

    [Fact]
    public void The_publish_images_job_can_sign_and_attest()
    {
        var job = Job(Workflow("release.yml"), "publish-images");

        job.Should().Contain("id-token: write");
        job.Should().Contain("attestations: write");
    }

    [Fact]
    public void Only_the_signing_job_gets_an_oidc_token_in_the_release_workflow()
    {
        var yaml = Workflow("release.yml");
        var top = CodeLines(yaml.Split("\njobs:")[0]);

        top.Should().NotContain(l => Regex.IsMatch(l, @"^id-token:"),
            "the file default stays read-only and each job asks for what it needs");
        CodeLines(Job(yaml, "build-images")).Should().NotContain(l => l.StartsWith("id-token:"));
    }

    [Fact]
    public void Every_published_image_is_signed_by_digest_and_verified_against_the_release_identity()
    {
        var job = Job(Workflow("release.yml"), "publish-images");
        var lines = CodeLines(job);

        lines.Should().Contain("for img in barako-cms barako-cms-decaf; do");
        var signs = lines.Where(l => l.Contains("cosign sign")).ToArray();
        signs.Should().NotBeEmpty();
        foreach (var l in signs)
        {
            l.Should().Contain("@$DIGEST", "signing is by digest, never by tag: {0}", l);
            l.Should().NotMatch("*:$VERSION*");
        }

        lines.Should().Contain(l => l.StartsWith("cosign verify \\"));
        lines.Should().Contain($"IDENTITY_REGEXP: '{ReleaseIdentity}'");
        lines.Should().Contain("OIDC_ISSUER: https://token.actions.githubusercontent.com");
        lines.Should().Contain(l => l.StartsWith("cosign verify-attestation --type slsaprovenance1"));
    }

    [Fact]
    public void The_identity_is_pinned_to_master_in_every_verify_step()
    {
        var release = CodeLines(Job(Workflow("release.yml"), "publish-images"));

        release.Count(l => l == $"IDENTITY_REGEXP: '{ReleaseIdentity}'").Should().Be(2, "sign-verify and attestation-verify");
        release.Should().NotContain(l => l.StartsWith("IDENTITY_REGEXP:") && !l.EndsWith("@refs/heads/master$'"));
    }

    [Fact]
    public void The_publish_images_job_only_runs_from_master()
    {
        var job = Job(Workflow("release.yml"), "publish-images");

        job.Should().Contain("if: needs.gate.outputs.should_release == 'true' && github.ref == 'refs/heads/master'");
    }

    [Fact]
    public void Signing_comes_after_the_platform_gate()
    {
        var lines = CodeLines(Job(Workflow("release.yml"), "publish-images"));

        var gate = Line(lines, l => l.StartsWith("bash scripts/check-image-platforms.sh"), "the platform gate run");
        var sign = Line(lines, l => l.StartsWith("cosign sign --yes"), "the sign call");
        sign.Should().BeGreaterThan(gate, "an image that fails the platform gate must never be signed");
    }

    [Fact]
    public void Every_image_gets_a_provenance_attestation_for_its_own_digest()
    {
        var lines = CodeLines(Job(Workflow("release.yml"), "publish-images"));

        lines.Should().Contain("subject-name: ghcr.io/baryodev/barako-cms");
        lines.Should().Contain("subject-digest: ${{ steps.sign.outputs.barako_cms_digest }}");
        lines.Should().Contain("subject-name: ghcr.io/baryodev/barako-cms-decaf");
        lines.Should().Contain("subject-digest: ${{ steps.sign.outputs.barako_cms_decaf_digest }}");
        lines.Count(l => l == "push-to-registry: true").Should().Be(2);
        lines.Count(l => l == "create-storage-record: false").Should().Be(2);
    }

    [Fact]
    public void Latest_moves_only_after_the_last_verify_step()
    {
        var lines = CodeLines(Job(Workflow("release.yml"), "publish-images"));

        var lastVerify = Array.FindLastIndex(lines, l => l.StartsWith("cosign verify"));
        var latest = Line(lines, l => l.Contains("--tag ghcr.io/baryodev/barako-cms:latest"), "the :latest move");
        latest.Should().BeGreaterThan(lastVerify);
        lines.Take(lastVerify).Should().NotContain(l => l.Contains(":latest") || l.StartsWith("LATEST_GHCR"),
            ":latest must not be pushed with the version tag before verification");
        lines.Should().Contain("case \"$VERSION\" in *-*) echo \"prerelease, :latest not moved\"; exit 0 ;; esac");
    }

    [Fact]
    public void The_master_image_is_signed_verified_and_attested_with_its_own_identity()
    {
        var yaml = Workflow("publish-master-image.yml");
        var job = Job(yaml, "manifest");
        var lines = CodeLines(job);

        job.Should().Contain("id-token: write");
        job.Should().Contain("attestations: write");
        lines.Should().Contain(l => l.StartsWith("cosign sign --yes"));
        lines.Should().Contain(l => l.StartsWith("cosign verify \\"));
        lines.Should().Contain(l => l.StartsWith("cosign verify-attestation --type slsaprovenance1"));
        lines.Should().Contain($"IDENTITY_REGEXP: '{MasterIdentity}'");
        lines.Should().Contain("subject-digest: ${{ steps.sign.outputs.digest }}");
        lines.Should().Contain("push-to-registry: true");
        lines.Should().Contain("create-storage-record: false");
    }

    [Fact]
    public void The_operator_script_pins_the_identity_and_refuses_other_registries()
    {
        var script = File.ReadAllText(Path.Combine(RepoPaths.Root, "scripts", "verify-image-signature.sh"));

        script.Should().Contain("set -euo pipefail");
        script.Should().Contain(@"^https://github\\.com/BaryoDev/barakoCMS/\\.github/workflows/");
        script.Should().Contain("@refs/heads/master");
        script.Should().Contain("^ghcr\\.io/baryodev/");
        script.Should().Contain("cosign verify-attestation --type slsaprovenance1");
        script.Should().NotContain("eval ");
    }

    // GitHub refuses to load a workflow with two steps of the same id in one job, so no run starts.
    // A bad merge of two edits to the same block produced exactly that once.
    [Fact]
    public void Step_ids_are_unique_within_each_job_in_every_workflow()
    {
        var dir = Path.Combine(RepoPaths.Root, ".github", "workflows");
        var files = Directory.GetFiles(dir, "*.yml").Concat(Directory.GetFiles(dir, "*.yaml")).ToArray();
        files.Should().NotBeEmpty();

        foreach (var file in files)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var inSteps = false;
            foreach (var raw in File.ReadAllLines(file))
            {
                if (Regex.IsMatch(raw, @"^  [A-Za-z0-9_-]+:\s*$"))
                {
                    seen.Clear();
                    inSteps = false;
                }
                else if (Regex.IsMatch(raw, @"^    steps:\s*$"))
                {
                    inSteps = true;
                }

                // A step id sits at the step's key indent: "- id: x" or "  id: x" under "      - name".
                var m = Regex.Match(raw, @"^      (?:- )?id:\s*['""]?([A-Za-z0-9_-]+)['""]?\s*(#.*)?$");
                if (inSteps && m.Success)
                {
                    seen.Add(m.Groups[1].Value).Should().BeTrue(
                        "{0} defines step id '{1}' twice in one job", Path.GetFileName(file), m.Groups[1].Value);
                }
            }
        }
    }
}
