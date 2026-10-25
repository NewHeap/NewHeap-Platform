using System.Reflection;
using System.Text.RegularExpressions;
using Xunit;

namespace NewHeap.Platform.AI.Tests;

/// <summary>
/// The media type check is a linear pattern over at most
/// <see cref="NhAiFileReference.MaxMediaTypeLength"/> characters, so it has no match timeout: a busy
/// machine decides a long valid and a long invalid media type exactly as an idle one does, and a
/// start-up check that validates media types never depends on CPU time.
/// </summary>
public sealed class NhAiMediaTypeTests
{
    [Fact]
    public void The_media_type_pattern_has_no_match_timeout()
    {
        var patterns = typeof(NhAiMediaTypes)
            .GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
            .Where(method => method.ReturnType == typeof(Regex) && method.GetParameters().Length == 0)
            .Select(method => (Regex)method.Invoke(null, null)!)
            .ToArray();

        var pattern = Assert.Single(patterns);
        Assert.Equal(Regex.InfiniteMatchTimeout, pattern.MatchTimeout);
    }

    [Fact]
    public void A_long_valid_media_type_is_accepted_and_normalized()
    {
        var subtype = "vnd." + new string('a', NhAiFileReference.MaxMediaTypeLength - "application/vnd.".Length);
        var mediaType = "Application/" + subtype.ToUpperInvariant() + "; charset=utf-8";

        var normalized = NhAiMediaTypes.Normalize(mediaType);

        Assert.Equal("application/" + subtype, normalized);
        Assert.Equal(NhAiFileReference.MaxMediaTypeLength, normalized!.Length);
        Assert.True(NhAiMediaTypes.IsValid(mediaType));
    }

    [Theory]
    [InlineData("missing-slash")]
    [InlineData("trailing-invalid")]
    [InlineData("second-slash")]
    [InlineData("over-the-limit")]
    public void A_long_invalid_media_type_is_refused(string shape)
    {
        var length = NhAiFileReference.MaxMediaTypeLength;
        var mediaType = shape switch
        {
            // No separator at all: the pattern scans the whole value before it fails.
            "missing-slash" => new string('a', length),
            // Valid up to the last character, which no media type may contain.
            "trailing-invalid" => "application/" + new string('a', length - "application/".Length - 1) + "@",
            "second-slash" => "application/" + new string('a', length - "application/".Length - 2) + "/a",
            _ => "application/" + new string('a', length)
        };

        Assert.Null(NhAiMediaTypes.Normalize(mediaType));
        Assert.False(NhAiMediaTypes.IsValid(mediaType));
    }
}
