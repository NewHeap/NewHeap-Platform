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

/// <summary>
/// The application's own text assets, such as instructions it ships with its code or keeps in its
/// own store, resolved by id and version. NewHeap only reads them: a skill or an agent that names an
/// asset stores the id, the version and the content hash, never the text, and checks the hash every
/// time it loads the text. The application keeps a published version unchanged and publishes a
/// changed text as a new version.
/// </summary>
public interface INhAiTextAssetSource
{
    /// <summary>
    /// Returns the asset <paramref name="id"/> in <paramref name="version"/>, or
    /// <see langword="null"/> when the application has no such asset or version. Create it with
    /// <see cref="NhAiTextAssetFactory.Create"/> (or an equivalent loader), so its manifest carries the
    /// SHA-256 of its content. An exception is an unexpected failure of the application's source,
    /// never the answer for a missing asset.
    /// </summary>
    /// <param name="id">The asset's lowercase dash-case id.</param>
    /// <param name="version">The asset's version, 1 or higher.</param>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    ValueTask<NhAiTextAsset?> GetAsync(string id, int version, CancellationToken cancellationToken = default);

    /// <summary>
    /// The manifests of the assets an administrator may choose, in the order the application wants to
    /// show them; empty when the application lists none, so an administrator names the id and version
    /// instead. The manifests carry no content.
    /// </summary>
    /// <param name="cancellationToken">Cancels the listing.</param>
    ValueTask<IReadOnlyList<NhAiAssetManifest>> ListAsync(CancellationToken cancellationToken = default);
}

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
