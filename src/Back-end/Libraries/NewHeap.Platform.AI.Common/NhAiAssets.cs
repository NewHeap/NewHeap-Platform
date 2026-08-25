using System.Security.Cryptography;
using System.Text;

namespace NewHeap.Platform.AI;

public enum NhAiAssetRole
{
    SystemInstructions = 0,
    DeveloperInstructions = 1,
    AgentDefinition = 2,
    EvaluationFixture = 3
}

public sealed record NhAiAssetManifest(
    string Id,
    int Version,
    string ContentHash,
    string SourceProvenance,
    NhAiAssetRole Role,
    NhAiContextTrust Trust,
    NhAiModelCapability RequiredModelCapabilities,
    IReadOnlyList<string> RequiredToolContracts,
    string ContextPolicyId,
    NhAiDataClassification DataClassification,
    NhAiRetentionCategory RetentionCategory,
    string EvaluationBaselineId)
{
    public string? ReplacedByAssetId { get; init; }
    public DateTimeOffset? DeprecatedAt { get; init; }
}

public sealed record NhAiTextAsset(
    NhAiAssetManifest Manifest,
    string Content);

public static class NhAiTextAssetFactory
{
    public static NhAiTextAsset Create(
        string id,
        int version,
        string content,
        string sourceProvenance,
        NhAiAssetRole role,
        NhAiContextTrust trust,
        NhAiModelCapability requiredModelCapabilities,
        IReadOnlyList<string> requiredToolContracts,
        string contextPolicyId,
        NhAiDataClassification dataClassification,
        NhAiRetentionCategory retentionCategory,
        string evaluationBaselineId)
    {
        NhAiNames.ValidateSegment(id, nameof(id));
        NhAiNames.ValidateSegment(contextPolicyId, nameof(contextPolicyId));
        NhAiNames.ValidateSegment(evaluationBaselineId, nameof(evaluationBaselineId));
        ArgumentException.ThrowIfNullOrWhiteSpace(content);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceProvenance);
        ArgumentNullException.ThrowIfNull(requiredToolContracts);
        if (version < 1
            || content.Length > 64 * 1024
            || sourceProvenance.Length > 512
            || requiredToolContracts.Count > 128
            || requiredToolContracts.Any(contract =>
                string.IsNullOrWhiteSpace(contract)
                || contract.Length > 256))
        {
            throw new ArgumentOutOfRangeException(nameof(version));
        }

        var hash = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(content)));
        return new NhAiTextAsset(
            new NhAiAssetManifest(
                id,
                version,
                hash,
                sourceProvenance,
                role,
                trust,
                requiredModelCapabilities,
                requiredToolContracts.Order(StringComparer.Ordinal).ToArray(),
                contextPolicyId,
                dataClassification,
                retentionCategory,
                evaluationBaselineId),
            content);
    }
}
