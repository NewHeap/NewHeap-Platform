using System.Globalization;
using System.Reflection;
using System.Text;

namespace NewHeap.Platform.AI.Chat;

/// <summary>
/// Loads agent instructions for assistant agents.
/// </summary>
public static class NhAssistantTextAssets
{
    extension(NhAiTextAsset)
    {
        /// <summary>
        /// Loads trusted system instructions from an embedded UTF-8 resource. Line endings are
        /// normalized to <c>\n</c> before hashing, so the content hash does not depend on how the
        /// source file was checked out. The asset uses the <c>default</c> context policy and the
        /// evaluation baseline <c>&lt;id&gt;-v&lt;version&gt;</c>; use
        /// <see cref="NhAiTextAssetFactory.Create"/> for other roles or policies.
        /// </summary>
        /// <remarks>
        /// Call it as <c>NhAiTextAsset.FromEmbeddedResource(...)</c> with
        /// <c>using NewHeap.Platform.AI.Chat;</c> in scope.
        /// </remarks>
        public static NhAiTextAsset FromEmbeddedResource(
            Assembly assembly,
            string resourceName,
            string id,
            int version)
        {
            return LoadEmbeddedResource(assembly, resourceName, id, version);
        }
    }

    /// <summary>
    /// Loads trusted system instructions from an embedded UTF-8 resource. Equivalent to
    /// <c>NhAiTextAsset.FromEmbeddedResource</c>.
    /// </summary>
    public static NhAiTextAsset LoadEmbeddedResource(
        Assembly assembly,
        string resourceName,
        string id,
        int version)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceName);
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"Embedded AI asset resource '{resourceName}' was not found in '{assembly.GetName().Name}'.");
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var content = reader.ReadToEnd()
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        return NhAiTextAssetFactory.Create(
            id,
            version,
            content,
            $"embedded:{assembly.GetName().Name}/{resourceName}",
            NhAiAssetRole.SystemInstructions,
            NhAiContextTrust.TrustedApplication,
            NhAiModelCapability.Chat,
            [],
            "default",
            NhAiDataClassification.Internal,
            NhAiRetentionCategory.Operational,
            $"{id}-v{version.ToString(CultureInfo.InvariantCulture)}");
    }
}
