using System.Text.Json;
using Xunit;

namespace NewHeap.Platform.AI.Tests;

public sealed class NhAiAssetTests
{
    [Fact]
    public void Text_asset_hash_is_deterministic_and_manifest_is_content_free()
    {
        const string protectedContent = "Never expose this full instruction in metadata.";
        var first = Create(protectedContent);
        var second = Create(protectedContent);
        var changed = Create(protectedContent + " Changed.");

        Assert.Equal(first.Manifest.ContentHash, second.Manifest.ContentHash);
        Assert.NotEqual(first.Manifest.ContentHash, changed.Manifest.ContentHash);
        Assert.Equal(64, first.Manifest.ContentHash.Length);
        Assert.DoesNotContain(
            protectedContent,
            JsonSerializer.Serialize(first.Manifest),
            StringComparison.Ordinal);
    }

    private static NhAiTextAsset Create(string content)
    {
        return NhAiTextAssetFactory.Create(
            "test-instructions",
            1,
            content,
            "embedded:test",
            NhAiAssetRole.SystemInstructions,
            NhAiContextTrust.TrustedApplication,
            NhAiModelCapability.FunctionCalling,
            ["projects.search@1"],
            "test-context",
            NhAiDataClassification.Internal,
            NhAiRetentionCategory.Evaluation,
            "test-baseline");
    }
}
