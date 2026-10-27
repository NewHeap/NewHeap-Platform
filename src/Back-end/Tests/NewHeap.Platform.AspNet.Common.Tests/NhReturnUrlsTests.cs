using NewHeap.Platform.AspNet.Common.Authentication;
using Xunit;

namespace NewHeap.Platform.AspNet.Common.Tests;

public sealed class NhReturnUrlsTests
{
    private static readonly string[] AllowedOrigins = ["https://app.example.test", "http://localhost:4210/"];

    [Theory]
    [InlineData("/")]
    [InlineData("/projects")]
    [InlineData("/projects?view=mine#top")]
    [InlineData("https://app.example.test")]
    [InlineData("https://APP.example.test/signed-in")]
    [InlineData("https://app.example.test:443/signed-in")]
    [InlineData("http://localhost:4210/auth/microsoft/callback")]
    public void PathsAndAllowedOriginsAreReturnTargets(string returnUrl)
    {
        Assert.True(NhReturnUrls.IsAllowed(returnUrl, AllowedOrigins));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("//elsewhere.example.test")]
    [InlineData("/\\elsewhere.example.test")]
    [InlineData("\\\\elsewhere.example.test")]
    [InlineData("/projects\\..\\..\\elsewhere")]
    [InlineData("/projects\r\nLocation: https://elsewhere.example.test")]
    [InlineData("https://elsewhere.example.test/signed-in")]
    [InlineData("https://app.example.test.elsewhere.example.test/")]
    [InlineData("http://app.example.test/signed-in")]
    [InlineData("https://app.example.test:8443/signed-in")]
    [InlineData("https://user@app.example.test/signed-in")]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html,hello")]
    [InlineData("projects")]
    public void EverythingElseIsRefused(string? returnUrl)
    {
        Assert.False(NhReturnUrls.IsAllowed(returnUrl, AllowedOrigins));
    }

    [Fact]
    public void WithoutAllowedOriginsOnlyPathsAreReturnTargets()
    {
        Assert.True(NhReturnUrls.IsAllowed("/projects", null));
        Assert.False(NhReturnUrls.IsAllowed("https://app.example.test/projects", []));
        Assert.False(NhReturnUrls.IsAllowed("https://app.example.test/projects", ["not an origin"]));
    }
}
