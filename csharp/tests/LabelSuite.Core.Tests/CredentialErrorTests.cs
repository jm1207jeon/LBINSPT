using LabelSuite.Core;
using Xunit;

namespace LabelSuite.Core.Tests;

public class CredentialErrorTests
{
    [Fact]
    public void Ec2MetadataMessageMapsToNotConfigured()
    {
        var friendly = TextractClient.FriendlyCredentialError(new Exception(
            "Unable to get IAM security credentials from EC2 Instance Metadata Service."));
        Assert.Contains("자격증명", friendly);
        Assert.Contains(".aws", friendly);
    }

    [Fact]
    public void InvalidTokenMapsToBadKey()
    {
        var friendly = TextractClient.FriendlyCredentialError(new Exception(
            "The security token included in the request is invalid. (InvalidClientTokenId)"));
        Assert.Contains("액세스 키가 잘못", friendly);
    }

    [Fact]
    public void SignatureMismatchMapsToBadSecret()
    {
        var friendly = TextractClient.FriendlyCredentialError(
            new Exception("SignatureDoesNotMatch: check your key and signing method"));
        Assert.Contains("Secret Key", friendly);
    }

    [Fact]
    public void TimeoutMapsToNetwork()
    {
        var friendly = TextractClient.FriendlyCredentialError(new TaskCanceledException());
        Assert.Contains("연결할 수 없습니다", friendly);
    }

    [Fact]
    public void ProfileNotFoundKeepsItsMessage()
    {
        var friendly = TextractClient.FriendlyCredentialError(
            new TextractClient.ProfileNotFoundException("myprofile"));
        Assert.Contains("myprofile", friendly);
        Assert.Contains("프로필 칸을 비우", friendly);
    }
}
