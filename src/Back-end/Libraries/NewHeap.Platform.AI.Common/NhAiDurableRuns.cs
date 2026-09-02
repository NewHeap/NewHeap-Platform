namespace NewHeap.Platform.AI;

public sealed record NhAiRunCheckpointReference(
    string AdapterId,
    string WorkflowId,
    int WorkflowVersion,
    string CheckpointId,
    int CheckpointSchemaVersion,
    string StateHash,
    DateTimeOffset CreatedAt,
    string? SessionId = null);

public static class NhAiRunCheckpointReferenceFactory
{
    public static NhAiRunCheckpointReference Create(
        string adapterId,
        string workflowId,
        int workflowVersion,
        string checkpointId,
        int checkpointSchemaVersion,
        string stateHash,
        DateTimeOffset createdAt,
        string? sessionId = null)
    {
        NhAiNames.ValidateSegment(adapterId, nameof(adapterId));
        NhAiNames.ValidateSegment(workflowId, nameof(workflowId));
        if (workflowVersion < 1
            || checkpointSchemaVersion < 1
            || !IsBoundedOpaqueId(checkpointId)
            || (sessionId is not null && !IsBoundedOpaqueId(sessionId))
            || stateHash.Length != 64
            || stateHash.Any(character => !char.IsAsciiHexDigit(character)))
        {
            throw new ArgumentException(
                "AI checkpoint versions must be positive and the state hash must be a SHA-256 hexadecimal value.");
        }

        return new NhAiRunCheckpointReference(
            adapterId,
            workflowId,
            workflowVersion,
            checkpointId,
            checkpointSchemaVersion,
            stateHash.ToLowerInvariant(),
            createdAt,
            sessionId);
    }

    private static bool IsBoundedOpaqueId(string value)
    {
        return !string.IsNullOrWhiteSpace(value)
            && value.Length <= 256
            && !value.Any(char.IsControl);
    }
}
