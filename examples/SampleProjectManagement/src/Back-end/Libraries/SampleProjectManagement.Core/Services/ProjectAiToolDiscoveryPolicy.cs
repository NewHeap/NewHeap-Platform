using NewHeap.Platform.AI;

namespace SampleProjectManagement.Core.Services;

public sealed class ProjectAiToolDiscoveryPolicy : INhAiToolDiscoveryPolicy
{
    public ValueTask<bool> CanDiscoverAsync(
        NhAiToolDescriptor descriptor,
        NhAiInvocationContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var hasAuthorizedDivision = context.TryGetScopeValue(
                ProjectAiTools.DivisionScopeKey,
                out var divisionValue)
            && Guid.TryParse(divisionValue, out _);
        return ValueTask.FromResult(
            descriptor.Id.StartsWith("projects.", StringComparison.Ordinal)
            && descriptor.RequiresAuthorization
            && hasAuthorizedDivision);
    }
}
