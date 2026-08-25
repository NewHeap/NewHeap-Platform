namespace NewHeap.Platform.AI;

public enum NhAiIdempotencyDecisionKind
{
    Acquired = 0,
    Duplicate = 1,
    Conflict = 2,
    Denied = 3
}

public sealed record NhAiIdempotencyRequest(
    Guid InvocationId,
    string ToolId,
    int ToolVersion,
    string ActorId,
    string IdempotencyKey,
    string ArgumentHash,
    string? FencingToken);

public sealed record NhAiIdempotencyLease(
    NhAiIdempotencyDecisionKind Decision,
    string Code,
    string? LeaseId = null);

public interface INhAiIdempotencyManager
{
    ValueTask<NhAiIdempotencyLease> AcquireAsync(
        NhAiIdempotencyRequest request,
        CancellationToken cancellationToken = default);

    ValueTask CompleteAsync(
        NhAiIdempotencyLease lease,
        NhAiOutcomeKind outcome,
        CancellationToken cancellationToken = default);
}

public sealed record NhAiVerificationResult(
    bool Succeeded,
    string Code,
    string? EvidenceReference = null);

public interface INhAiToolVerifier
{
    string Id { get; }

    ValueTask<NhAiVerificationResult> VerifyAsync(
        NhAiToolDescriptor descriptor,
        NhAiInvocationContext context,
        object arguments,
        object? executionResult,
        CancellationToken cancellationToken = default);
}

internal sealed class NhAiDenyIdempotencyManager : INhAiIdempotencyManager
{
    public ValueTask<NhAiIdempotencyLease> AcquireAsync(
        NhAiIdempotencyRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new NhAiIdempotencyLease(
            NhAiIdempotencyDecisionKind.Denied,
            "idempotency-manager-not-configured"));
    }

    public ValueTask CompleteAsync(
        NhAiIdempotencyLease lease,
        NhAiOutcomeKind outcome,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }
}

public static class NhAiCanonicalJson
{
    public static string ComputeHash(object value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var element = System.Text.Json.JsonSerializer.SerializeToElement(
            value,
            value.GetType(),
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        var canonical = NhAiProposalFactory.Canonicalize(element).GetRawText();
        return Convert.ToHexStringLower(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(canonical)));
    }
}
