using System.Text.Json.Nodes;

namespace NewHeap.Platform.AI.Chat;

/// <summary>
/// Converts the stored working memory of an agent to a new schema version before a run reads
/// it. Register it with <see cref="NhAssistantBuilder.UseAgentMemoryMigrator{TMigrator}"/> when
/// <see cref="NhAssistantAgentRunBuilder.Memory{TMemory}"/> gets a new schema version.
/// </summary>
public interface INhAssistantAgentMemoryMigrator
{
    /// <summary>
    /// Returns the memory in the shape of <paramref name="toVersion"/>, or <see langword="null"/>
    /// to start without memory. The result must deserialize into the agent's memory type.
    /// </summary>
    ValueTask<JsonNode?> MigrateAsync(
        string agentId,
        int fromVersion,
        int toVersion,
        JsonNode memory,
        CancellationToken cancellationToken = default);
}
