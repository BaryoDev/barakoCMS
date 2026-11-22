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
    private static readonly string[] PublishedImages = { "barako-cms", "barako-cms-decaf" };

    private static string Workflow(string name) =>
        File.ReadAllText(Path.Combine(RepoPaths.Root, ".github", "workflows", name));

    // The text of one top-level job, up to the next job at the same indent.
    private static string Job(string yaml, string job)
    {
        var m = Regex.Match(yaml, $@"(?ms)^  {Regex.Escape(job)}:\n(.*?)(?=^  [a-z][a-z0-9-]*:\n|\z)");
        m.Success.Should().BeTrue("the workflow has a {0} job", job);
        return m.Groups[1].Value;
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
        var top = yaml.Split("\njobs:")[0];

        top.Should().NotContain("id-token", "the file default stays read-only and each job asks for what it needs");
        Job(yaml, "build-images").Should().NotContain("id-token");
    }

    [Fact]
    public void Every_published_image_is_signed_by_digest_and_verified_against_the_release_identity()
    {
        var job = Job(Workflow("release.yml"), "publish-images");

        foreach (var img in PublishedImages)
        {
            job.Should().Contain($"for img in {string.Join(" ", PublishedImages)}",
                "{0} is one of the images the manifest and the signing loops iterate", img);
        }

        job.Should().Contain("cosign sign --yes \"ghcr.io/baryodev/$img@$DIGEST\"", "signing is by digest, never by tag");
        job.Should().Contain("cosign verify \\");
        job.Should().Contain("^https://github.com/BaryoDev/barakoCMS/.github/workflows/release.yml@");
        job.Should().Contain("--certificate-oidc-issuer");
        job.Should().Contain("https://token.actions.githubusercontent.com");
        job.Should().Contain("cosign verify-attestation --type slsaprovenance1");
        job.Should().NotMatch("*cosign sign*:$V*", "a tag can move after it is signed");
    }

    [Fact]
    public void Signing_comes_after_the_platform_gate()
    {
        var job = Job(Workflow("release.yml"), "publish-images");

        job.IndexOf("check-image-platforms.sh", StringComparison.Ordinal).Should().BeGreaterThan(0);
        job.IndexOf("cosign sign", StringComparison.Ordinal)
            .Should().BeGreaterThan(job.IndexOf("check-image-platforms.sh", StringComparison.Ordinal),
                "an image that fails the platform gate must never be signed");
    }

    [Fact]
    public void Every_image_gets_a_provenance_attestation()
    {
        var job = Job(Workflow("release.yml"), "publish-images");

        foreach (var img in PublishedImages)
        {
            job.Should().Contain($"subject-name: ghcr.io/baryodev/{img}\n");
        }
        Regex.Matches(job, "push-to-registry: true").Count.Should().Be(PublishedImages.Length);
    }

    [Fact]
    public void The_master_image_is_signed_with_its_own_workflow_identity()
    {
        var yaml = Workflow("publish-master-image.yml");

        Job(yaml, "manifest").Should().Contain("id-token: write");
        yaml.Should().Contain("cosign sign --yes");
        yaml.Should().Contain("^https://github.com/BaryoDev/barakoCMS/.github/workflows/publish-master-image.yml@");
    }

    [Fact]
    public void The_operator_script_pins_the_identity_and_refuses_other_registries()
    {
        var script = File.ReadAllText(Path.Combine(RepoPaths.Root, "scripts", "verify-image-signature.sh"));

        script.Should().Contain("set -euo pipefail");
        script.Should().Contain("https://github.com/BaryoDev/barakoCMS/.github/workflows/");
        script.Should().Contain("^ghcr\\.io/baryodev/");
        script.Should().NotContain("eval ");
    }
}
