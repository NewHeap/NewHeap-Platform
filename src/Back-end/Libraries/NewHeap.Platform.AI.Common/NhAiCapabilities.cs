namespace NewHeap.Platform.AI;

public sealed record NhAiCapabilityGrant(
    string GrantId,
    string SubjectId,
    string Capability,
    string ToolSelector,
    string Purpose,
    string Issuer,
    DateTimeOffset IssuedAt,
    DateTimeOffset ExpiresAt,
    IReadOnlyList<NhAiExecutionScopeEntry> ExecutionScopes)
{
    public NhAiActionBudget? Budget { get; init; }
    public string? RevocationReference { get; init; }
    public DateTimeOffset? RevokedAt { get; init; }
}

public sealed record NhAiCapabilityResolution(
    bool Succeeded,
    string Code,
    IReadOnlyList<NhAiCapabilityGrant> Grants);

public interface INhAiCapabilityResolver
{
    ValueTask<NhAiCapabilityResolution> ResolveAsync(
        NhAiToolDescriptor descriptor,
        NhAiInvocationContext context,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);
}

internal sealed class NhAiInvocationContextCapabilityResolver : INhAiCapabilityResolver
{
    public ValueTask<NhAiCapabilityResolution> ResolveAsync(
        NhAiToolDescriptor descriptor,
        NhAiInvocationContext context,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var expiry = context.Deadline ?? now.AddMinutes(5);
        var grants = descriptor.RequiredCapabilities
            .Where(context.CapabilityGrants.Contains)
            .Select(capability => new NhAiCapabilityGrant(
                $"invocation-{context.InvocationId:N}-{capability}",
                context.ActorId,
                capability,
                descriptor.Id,
                context.Purpose,
                "invocation-context",
                now,
                expiry,
                context.ExecutionScopes))
            .ToArray();
        return ValueTask.FromResult(CreateResolution(descriptor, context, grants, now));
    }

    internal static NhAiCapabilityResolution CreateResolution(
        NhAiToolDescriptor descriptor,
        NhAiInvocationContext context,
        IEnumerable<NhAiCapabilityGrant> candidates,
        DateTimeOffset now)
    {
        var valid = candidates
            .Where(grant => IsValid(grant, descriptor, context, now))
            .GroupBy(grant => grant.Capability, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();
        var succeeded = descriptor.RequiredCapabilities.All(required =>
            valid.Any(grant => string.Equals(
                grant.Capability,
                required,
                StringComparison.Ordinal)));
        return new NhAiCapabilityResolution(
            succeeded,
            succeeded ? "capabilities-granted" : "capability-denied",
            valid);
    }

    private static bool IsValid(
        NhAiCapabilityGrant grant,
        NhAiToolDescriptor descriptor,
        NhAiInvocationContext context,
        DateTimeOffset now)
    {
        if (!NhAiNames.IsSegment(grant.Capability)
            || string.IsNullOrWhiteSpace(grant.GrantId)
            || string.IsNullOrWhiteSpace(grant.Issuer)
            || !string.Equals(grant.SubjectId, context.ActorId, StringComparison.Ordinal)
            || !string.Equals(grant.Purpose, context.Purpose, StringComparison.Ordinal)
            || grant.IssuedAt > now
            || grant.ExpiresAt <= grant.IssuedAt
            || grant.ExpiresAt <= now
            || grant.RevokedAt <= now
            || !MatchesTool(grant.ToolSelector, descriptor.Id))
        {
            return false;
        }

        return grant.ExecutionScopes.All(required =>
            context.ExecutionScopes.Any(actual =>
                string.Equals(actual.Type, required.Type, StringComparison.Ordinal)
                && string.Equals(actual.Id, required.Id, StringComparison.Ordinal)));
    }

    private static bool MatchesTool(string selector, string toolId)
    {
        if (string.Equals(selector, toolId, StringComparison.Ordinal))
        {
            return true;
        }
        return selector.EndsWith(".*", StringComparison.Ordinal)
            && toolId.StartsWith(selector[..^1], StringComparison.Ordinal);
    }
}
