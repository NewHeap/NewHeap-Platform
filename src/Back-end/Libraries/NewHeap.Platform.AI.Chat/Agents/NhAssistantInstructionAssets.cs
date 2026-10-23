using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AI.Chat;

/// <summary>
/// The instructions of a data skill version or of an agent created in the administration that come
/// from the application's <see cref="INhAiTextAssetSource"/> instead of being stored as text: the
/// asset's id, its exact version and the SHA-256 of its content, lowercase hex. Every load checks the
/// hash; a new version of the asset is a new version of the skill or agent, saved by an administrator.
/// </summary>
/// <param name="AssetId">The asset's lowercase dash-case id, at most 128 characters.</param>
/// <param name="Version">The asset's version, 1 or higher.</param>
/// <param name="Hash">The SHA-256 of the asset's content as the administrator saw it, 64 lowercase
/// hex characters.</param>
public sealed record NhAssistantInstructionAssetReference(string AssetId, int Version, string Hash)
{
    /// <summary>
    /// The longest asset id a reference stores.
    /// </summary>
    public const int MaxAssetIdLength = 128;

    /// <summary>
    /// Whether the reference is well formed: a dash-case id of at most
    /// <see cref="MaxAssetIdLength"/> characters, a version of 1 or higher and a lowercase SHA-256 hex
    /// hash. It says nothing about whether the application has the asset.
    /// </summary>
    [JsonIgnore]
    public bool IsWellFormed => NhAssistantNames.IsSegment(AssetId)
                                && AssetId.Length <= MaxAssetIdLength
                                && Version >= 1
                                && NhAssistantInstructionAssets.IsHash(Hash);

    /// <summary>
    /// The identity the audit records for the reference: <c>&lt;asset-id&gt;@&lt;version&gt;</c>.
    /// </summary>
    [JsonIgnore]
    public string InstructionsVersion => AssetId + "@" + Version.ToString(CultureInfo.InvariantCulture);
}

/// <summary>
/// What a reference resolves to: the verified asset, or the problem that keeps it out of effect, one
/// of the asset problems of <see cref="NhAssistantRunConfigurationProblems"/>.
/// </summary>
internal sealed record NhAssistantResolvedInstructions(NhAiTextAsset? Asset, string? Problem)
{
    public static NhAssistantResolvedInstructions Of(string problem)
    {
        return new NhAssistantResolvedInstructions(null, problem);
    }
}

/// <summary>
/// Resolves instruction asset references through the application's <see cref="INhAiTextAssetSource"/>
/// and keeps the texts it verified per asset id and version in memory. A save asks the source at
/// once; every load of the run configuration asks it again for every reference the data names and
/// replaces what it kept; a read of an agent uses what it kept and asks only for a reference it has
/// not resolved yet. A hash is compared on every read. A source that throws is treated as
/// unavailable: a text verified earlier stays, one never verified is out of effect until a later
/// check, at most once per <see cref="NhAssistantTiming.InstructionAssetRetryInterval"/>. Content is
/// never logged.
/// </summary>
internal sealed class NhAssistantInstructionAssets(
    IServiceProvider services,
    ILogger<NhAssistantInstructionAssets> logger)
{
    private readonly ConcurrentDictionary<(string Id, int Version), Entry> _entries = new();

    private readonly TimeSpan _retryInterval =
        (services.GetService<NhAssistantTiming>() ?? NhAssistantTiming.Default).InstructionAssetRetryInterval;

    private bool? _supported;

    /// <summary>
    /// Whether the application registered an <see cref="INhAiTextAssetSource"/> with
    /// <c>UseTextAssetSource</c>.
    /// </summary>
    public bool Supported => _supported ??= services.GetService<IServiceProviderIsService>()?.IsService(typeof(INhAiTextAssetSource)) == true;

    /// <summary>
    /// Asks the source for the reference now, as a save does, and checks the asset against it. The
    /// verified asset is kept for later reads.
    /// </summary>
    public async Task<NhAssistantResolvedInstructions> CheckAsync(
        NhAssistantInstructionAssetReference reference,
        int maxLength,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reference);
        if (!Supported)
        {
            return NhAssistantResolvedInstructions.Of(NhAssistantRunConfigurationProblems.NoAssetSource);
        }

        var key = (reference.AssetId, reference.Version);
        var entry = await AskAsync(key, cancellationToken);
        if (entry.Problem == NhAssistantRunConfigurationProblems.AssetUnavailable)
        {
            return NhAssistantResolvedInstructions.Of(NhAssistantRunConfigurationProblems.AssetUnavailable);
        }

        _entries[key] = entry;
        return Compare(entry, reference, maxLength);
    }

    /// <summary>
    /// Checks the reference a save names and adds its field errors: <c>instructionsAsset: not-allowed</c>
    /// without a source, <c>invalid</c> for a malformed reference or an asset that cannot be
    /// instructions, <c>not-found</c> for a missing asset and <c>instructionsAsset.hash: invalid</c> for
    /// another hash. Returns the failure <see cref="NhAssistantAdminErrorCodes.InstructionsAssetUnavailable"/>
    /// when the source failed, otherwise <see langword="null"/>.
    /// </summary>
    public async Task<TaskResult?> ValidateForSaveAsync(
        NhAssistantInstructionAssetReference reference,
        int maxLength,
        NhAssistantValidationErrors errors,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(errors);
        if (!Supported)
        {
            errors.Add(AssetField, NhAssistantFieldErrors.NotAllowed);
            return null;
        }
        if (!reference.IsWellFormed)
        {
            errors.Add(AssetField, NhAssistantFieldErrors.Invalid);
            return null;
        }

        var resolved = await CheckAsync(reference, maxLength, cancellationToken);
        switch (resolved.Problem)
        {
            case null:
                return null;
            case NhAssistantRunConfigurationProblems.AssetUnavailable:
                return TaskResult.Failed(
                    NhAssistantAdminErrorCodes.InstructionsAssetUnavailable,
                    "The application's text asset source failed.");
            case NhAssistantRunConfigurationProblems.AssetMissing:
                errors.Add(AssetField, NhAssistantFieldErrors.NotFound);
                return null;
            case NhAssistantRunConfigurationProblems.AssetChanged:
                errors.Add(AssetField + ".hash", NhAssistantFieldErrors.Invalid);
                return null;
            default:
                errors.Add(AssetField, NhAssistantFieldErrors.Invalid);
                return null;
        }
    }

    /// <summary>
    /// The field a save names a reference in.
    /// </summary>
    internal const string AssetField = "instructionsAsset";

    /// <summary>
    /// Asks the source again for every asset id and version in <paramref name="keys"/>, as every load
    /// of the run configuration does, and replaces what it kept. Returns whether the source failed for
    /// one of them, so the load is tried again later.
    /// </summary>
    public async Task<bool> RefreshAsync(
        IEnumerable<(string Id, int Version)> keys,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(keys);
        if (!Supported)
        {
            return false;
        }

        var unavailable = false;
        foreach (var key in keys.Distinct())
        {
            var entry = await AskAsync(key, cancellationToken);
            if (entry.Problem == NhAssistantRunConfigurationProblems.AssetUnavailable)
            {
                unavailable = true;
                // A text verified earlier stays in effect while the source fails.
                if (_entries.TryGetValue(key, out var kept) && kept.Asset is not null)
                {
                    continue;
                }
            }

            _entries[key] = entry;
        }

        return unavailable;
    }

    /// <summary>
    /// What the reference resolves to from what was kept, compared with its hash, or
    /// <see langword="null"/> when it was not resolved yet.
    /// </summary>
    public NhAssistantResolvedInstructions? Find(NhAssistantInstructionAssetReference reference, int maxLength)
    {
        ArgumentNullException.ThrowIfNull(reference);
        if (!Supported)
        {
            return NhAssistantResolvedInstructions.Of(NhAssistantRunConfigurationProblems.NoAssetSource);
        }

        return _entries.TryGetValue((reference.AssetId, reference.Version), out var entry)
            ? Compare(entry, reference, maxLength)
            : null;
    }

    /// <summary>
    /// What the reference resolves to: from what was kept, otherwise from the source, which is asked
    /// again for an unavailable asset once the retry interval passed.
    /// </summary>
    public async Task<NhAssistantResolvedInstructions> ResolveAsync(
        NhAssistantInstructionAssetReference reference,
        int maxLength,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reference);
        if (!Supported)
        {
            return NhAssistantResolvedInstructions.Of(NhAssistantRunConfigurationProblems.NoAssetSource);
        }

        var key = (reference.AssetId, reference.Version);
        if (_entries.TryGetValue(key, out var kept)
            && (kept.Problem != NhAssistantRunConfigurationProblems.AssetUnavailable
                || Environment.TickCount64 - kept.CheckedAt < (long)_retryInterval.TotalMilliseconds))
        {
            return Compare(kept, reference, maxLength);
        }

        var entry = await AskAsync(key, cancellationToken);
        _entries[key] = entry;
        return Compare(entry, reference, maxLength);
    }

    /// <summary>
    /// The manifests the application lists for administrators, or <see langword="null"/> without a
    /// source. An exception of the source propagates.
    /// </summary>
    public async Task<IReadOnlyList<NhAiAssetManifest>?> ListAsync(CancellationToken cancellationToken)
    {
        if (!Supported)
        {
            return null;
        }

        await using var scope = services.GetRequiredService<IServiceScopeFactory>().CreateAsyncScope();
        var source = scope.ServiceProvider.GetRequiredService<INhAiTextAssetSource>();
        return await source.ListAsync(cancellationToken) ?? [];
    }

    /// <summary>
    /// The asset <paramref name="id"/> in <paramref name="version"/> as the source has it now, checked
    /// like a reference without a hash, for an administrator to read before choosing it:
    /// <see langword="null"/> without a source or without such an asset. An exception of the source
    /// propagates.
    /// </summary>
    public async Task<NhAssistantResolvedInstructions?> ReadAsync(string id, int version, CancellationToken cancellationToken)
    {
        if (!Supported || !NhAssistantNames.IsSegment(id) || id.Length > NhAssistantInstructionAssetReference.MaxAssetIdLength || version < 1)
        {
            return null;
        }

        await using var scope = services.GetRequiredService<IServiceScopeFactory>().CreateAsyncScope();
        var source = scope.ServiceProvider.GetRequiredService<INhAiTextAssetSource>();
        var asset = await source.GetAsync(id, version, cancellationToken);
        if (asset is null)
        {
            return null;
        }

        var entry = Verify((id, version), asset);
        return new NhAssistantResolvedInstructions(entry.Asset ?? asset, entry.Problem);
    }

    /// <summary>
    /// The reference a data skill version stores, or <see langword="null"/> for inline instructions.
    /// </summary>
    public static NhAssistantInstructionAssetReference? ReferenceOf(AssistantAgentSkillDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return definition is { InstructionsAssetId: { } id, InstructionsAssetVersion: { } version }
            ? new NhAssistantInstructionAssetReference(id, version, definition.InstructionsHash ?? string.Empty)
            : null;
    }

    /// <summary>
    /// The reference an agent created in the administration stores, or <see langword="null"/> for an
    /// agent whose instructions are text.
    /// </summary>
    public static NhAssistantInstructionAssetReference? ReferenceOf(AssistantAgent row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return row.InstructionsFromAsset
            ? new NhAssistantInstructionAssetReference(row.InstructionsAssetId, row.InstructionsAssetVersion, row.InstructionsHash)
            : null;
    }

    /// <summary>
    /// The placeholder an agent holds while the asset it names does not resolve. Its turns are refused
    /// before a prompt is composed, and composing one from it throws.
    /// </summary>
    public static NhAiTextAsset Unavailable(NhAssistantInstructionAssetReference reference, string agentId)
    {
        ArgumentNullException.ThrowIfNull(reference);
        var id = reference.IsWellFormed ? reference.AssetId : "unavailable-instructions";
        var version = Math.Max(reference.Version, 1);
        return NhAiTextAssetFactory.Create(
            id,
            version,
            "The instructions of this agent are not available.",
            UnavailableProvenance + agentId,
            NhAiAssetRole.SystemInstructions,
            NhAiContextTrust.TrustedApplication,
            NhAiModelCapability.Chat,
            [],
            "default",
            NhAiDataClassification.Internal,
            NhAiRetentionCategory.Operational,
            id + "-v" + version.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Whether <paramref name="asset"/> is the placeholder of <see cref="Unavailable"/>.
    /// </summary>
    public static bool IsUnavailable(NhAiTextAsset asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        return asset.Manifest.SourceProvenance.StartsWith(UnavailableProvenance, StringComparison.Ordinal);
    }

    private const string UnavailableProvenance = "assistant-asset-unavailable:";

    /// <summary>
    /// The lowercase hex SHA-256 of <paramref name="content"/>, as a reference stores it.
    /// </summary>
    public static string HashOf(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
    }

    /// <summary>
    /// Whether <paramref name="value"/> is a lowercase hex SHA-256.
    /// </summary>
    public static bool IsHash(string? value)
    {
        return value is { Length: 64 } && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
    }

    private async Task<Entry> AskAsync((string Id, int Version) key, CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = services.GetRequiredService<IServiceScopeFactory>().CreateAsyncScope();
            var source = scope.ServiceProvider.GetRequiredService<INhAiTextAssetSource>();
            var asset = await source.GetAsync(key.Id, key.Version, cancellationToken);
            return asset is null
                ? new Entry(null, NhAssistantRunConfigurationProblems.AssetMissing, Environment.TickCount64)
                : Verify(key, asset);
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
        {
            logger.LogWarning(
                "The application's text asset source failed for asset '{AssetId}' version {AssetVersion} ({ExceptionType}).",
                key.Id,
                key.Version,
                exception.GetType().Name);
            return new Entry(null, NhAssistantRunConfigurationProblems.AssetUnavailable, Environment.TickCount64);
        }
    }

    /// <summary>
    /// An asset is usable as instructions when it is the asked id and version, trusted system
    /// instructions of the application, not blank, and its manifest's hash is the hash of its content.
    /// </summary>
    private static Entry Verify((string Id, int Version) key, NhAiTextAsset asset)
    {
        var usable = asset.Manifest is { } manifest
                     && string.Equals(manifest.Id, key.Id, StringComparison.Ordinal)
                     && manifest.Version == key.Version
                     && manifest.Role == NhAiAssetRole.SystemInstructions
                     && manifest.Trust == NhAiContextTrust.TrustedApplication
                     && !string.IsNullOrWhiteSpace(asset.Content)
                     && string.Equals(manifest.ContentHash, HashOf(asset.Content), StringComparison.Ordinal);
        return usable
            ? new Entry(asset, null, Environment.TickCount64)
            : new Entry(null, NhAssistantRunConfigurationProblems.AssetInvalid, Environment.TickCount64);
    }

    private static NhAssistantResolvedInstructions Compare(Entry entry, NhAssistantInstructionAssetReference reference, int maxLength)
    {
        if (entry.Asset is not { } asset)
        {
            return NhAssistantResolvedInstructions.Of(entry.Problem ?? NhAssistantRunConfigurationProblems.AssetMissing);
        }
        if (asset.Content.Length > maxLength)
        {
            return NhAssistantResolvedInstructions.Of(NhAssistantRunConfigurationProblems.AssetInvalid);
        }

        return string.Equals(asset.Manifest.ContentHash, reference.Hash, StringComparison.Ordinal)
            ? new NhAssistantResolvedInstructions(asset, null)
            : NhAssistantResolvedInstructions.Of(NhAssistantRunConfigurationProblems.AssetChanged);
    }

    /// <param name="Asset">The verified asset; <see langword="null"/> with <paramref name="Problem"/>.</param>
    /// <param name="CheckedAt">When the source was asked, in <see cref="Environment.TickCount64"/>.</param>
    private sealed record Entry(NhAiTextAsset? Asset, string? Problem, long CheckedAt);
}
