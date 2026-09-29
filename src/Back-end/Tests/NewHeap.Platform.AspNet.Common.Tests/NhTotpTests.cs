using NewHeap.Platform.AspNet.Common.Authentication.TwoFactor;
using System.Text;
using Xunit;

namespace NewHeap.Platform.AspNet.Common.Tests;

public sealed class NhTotpTests
{
    // RFC 6238 appendix B uses the ASCII secret "12345678901234567890" for SHA-1.
    private const string Rfc6238Base32Secret = "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ";

    [Theory]
    [InlineData(59L, "287082")]
    [InlineData(1111111109L, "081804")]
    [InlineData(1111111111L, "050471")]
    [InlineData(1234567890L, "005924")]
    [InlineData(2000000000L, "279037")]
    [InlineData(20000000000L, "353130")]
    public void ComputesRfc6238Sha1Vectors(long unixSeconds, string expectedCode)
    {
        var step = NhTotp.GetTimeStep(DateTimeOffset.FromUnixTimeSeconds(unixSeconds));

        var code = NhTotp.ComputeCode(Encoding.ASCII.GetBytes("12345678901234567890"), step);

        Assert.Equal(expectedCode, code);
    }

    [Fact]
    public void DecodesBase32KeysWithoutPadding()
    {
        Assert.Equal(
            Encoding.ASCII.GetBytes("12345678901234567890"),
            NhBase32.Decode(Rfc6238Base32Secret));
        Assert.Equal(
            Encoding.ASCII.GetBytes("12345678901234567890"),
            NhBase32.Decode("gezd gnbv gy3t qojq gezd gnbv gy3t qojq"));
        Assert.Empty(NhBase32.Decode("not-base32!"));
    }

    [Fact]
    public void MatchAcceptsOneStepOfClockDriftAndReturnsTheMatchedStep()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1234567890);
        var currentStep = NhTotp.GetTimeStep(now);

        Assert.Equal(currentStep, NhTotp.Match(Rfc6238Base32Secret, CodeAt(currentStep), now, null));
        Assert.Equal(currentStep - 1, NhTotp.Match(Rfc6238Base32Secret, CodeAt(currentStep - 1), now, null));
        Assert.Equal(currentStep + 1, NhTotp.Match(Rfc6238Base32Secret, CodeAt(currentStep + 1), now, null));
        Assert.Null(NhTotp.Match(Rfc6238Base32Secret, CodeAt(currentStep - 2), now, null));
        Assert.Null(NhTotp.Match(Rfc6238Base32Secret, CodeAt(currentStep + 2), now, null));
    }

    [Fact]
    public void MatchRejectsAReplayedOrOlderStep()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1234567890);
        var currentStep = NhTotp.GetTimeStep(now);

        Assert.Null(NhTotp.Match(Rfc6238Base32Secret, CodeAt(currentStep), now, lastAcceptedStep: currentStep));
        Assert.Null(NhTotp.Match(Rfc6238Base32Secret, CodeAt(currentStep - 1), now, lastAcceptedStep: currentStep - 1));
        Assert.Equal(
            currentStep,
            NhTotp.Match(Rfc6238Base32Secret, CodeAt(currentStep), now, lastAcceptedStep: currentStep - 1));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("12345")]
    [InlineData("1234567")]
    [InlineData("12a456")]
    public void MatchRejectsMalformedCodes(string? code)
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1234567890);

        Assert.Null(NhTotp.Match(Rfc6238Base32Secret, code, now, null));
    }

    [Fact]
    public void MatchAcceptsGroupedInput()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1234567890);
        var code = CodeAt(NhTotp.GetTimeStep(now));

        Assert.NotNull(NhTotp.Match(Rfc6238Base32Secret, $"{code[..3]} {code[3..]}", now, null));
        Assert.NotNull(NhTotp.Match(Rfc6238Base32Secret, $"{code[..3]}-{code[3..]}", now, null));
    }

    [Fact]
    public void FormatsTheSharedKeyAndAuthenticatorUri()
    {
        Assert.Equal("gezd gnbv gy3t qojq", NhTotp.FormatSharedKey("GEZDGNBVGY3TQOJQ"));

        var uri = NhTotp.CreateAuthenticatorUri("Sample Project", "user@example.test", "GEZDGNBVGY3TQOJQ");

        Assert.Equal(
            "otpauth://totp/Sample%20Project:user@example.test?secret=GEZDGNBVGY3TQOJQ&issuer=Sample%20Project&digits=6",
            uri);
    }

    [Fact]
    public void RecoveryCodeHashIgnoresCaseAndWhitespace()
    {
        var expected = NhRecoveryCodeHasher.Hash(NhRecoveryCodeHasher.Normalize("ABCDE-FGHIJ"));

        Assert.Equal(expected, NhRecoveryCodeHasher.Hash(NhRecoveryCodeHasher.Normalize(" abcde-fghij ")));
        Assert.StartsWith("nhv1:", expected, StringComparison.Ordinal);
        Assert.DoesNotContain(';', expected);
    }

    private static string CodeAt(long step)
    {
        return NhTotp.ComputeCode(NhBase32.Decode(Rfc6238Base32Secret), step);
    }
}
