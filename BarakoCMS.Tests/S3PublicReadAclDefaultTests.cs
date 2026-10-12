using Amazon.S3;
using Amazon.S3.Model;
using BarakoCMS.Files.S3;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// <c>Files:S3:UsePublicReadAcl</c> is off unless a deployment turns it on (#789): a new AWS bucket
/// refuses ACLs, and R2, SeaweedFS and Garage ignore or refuse them.
/// </summary>
public class S3PublicReadAclDefaultTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static S3StorageOptions Bound(Dictionary<string, string?> settings)
    {
        settings["Modules:Files.S3:Bucket"] = "media";
        settings["Modules:Files.S3:AccessKey"] = "barako-test";
        settings["Modules:Files.S3:SecretKey"] = "barako-test-secret";
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        new S3FilesModule().ConfigureServices(services, configuration.GetSection("Modules:Files.S3"));
        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IOptions<S3StorageOptions>>().Value;
    }

    private static async Task<PutObjectRequest> PublicPutAsync(S3StorageOptions options)
    {
        var s3 = Substitute.For<IAmazonS3>();
        PutObjectRequest? sent = null;
        s3.PutObjectAsync(Arg.Do<PutObjectRequest>(r => sent = r), Arg.Any<CancellationToken>())
            .Returns(new PutObjectResponse());

        await new S3FileStorage(s3, Options.Create(options))
            .PutAsync(new MemoryStream([1, 2, 3]), "public/a.png", "image/png", isPublic: true, Ct);

        sent.Should().NotBeNull();
        return sent!;
    }

    [Fact]
    public void The_option_is_off_by_default()
    {
        new S3StorageOptions().UsePublicReadAcl.Should().BeFalse();
    }

    [Fact]
    public void A_deployment_that_never_set_it_binds_it_off_and_one_that_set_it_keeps_it()
    {
        Bound(new()).UsePublicReadAcl.Should().BeFalse();
        Bound(new() { ["Modules:Files.S3:UsePublicReadAcl"] = "true" }).UsePublicReadAcl.Should().BeTrue();
    }

    [Fact]
    public async Task A_public_upload_sends_no_acl_unless_it_is_turned_on()
    {
        (await PublicPutAsync(new S3StorageOptions { Bucket = "media" })).CannedACL.Should().BeNull();
        (await PublicPutAsync(new S3StorageOptions { Bucket = "media", UsePublicReadAcl = true })).CannedACL
            .Should().Be(S3CannedACL.PublicRead, "the positive control: the flag still works when set");
    }
}
