using System.Text.Json;
using NewHeap.Platform.AI;

namespace SampleProjectManagement.Core.Services;

public sealed class ProjectAiContextSource(
    IProjectAiContextService projectContextService) : INhAiContextSource
{
    public const string SourceId = "project-documents";

    public NhAiContextSourceDescriptor Descriptor { get; } = new(
        SourceId,
        "Project names and descriptions from the authorized active division.",
        NhAiDataClassification.Internal,
        25,
        4_096);

    public async ValueTask<IReadOnlyList<NhAiContextItem>> RetrieveAsync(
        NhAiContextRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!request.InvocationContext.TryGetScopeValue(
                ProjectAiTools.DivisionScopeKey,
                out var divisionValue)
            || !Guid.TryParse(divisionValue, out var divisionId))
        {
            return [];
        }

        var documents = await projectContextService.SearchContextForAiAsync(
            divisionId,
            request.Query,
            Math.Min(request.MaxItems, Descriptor.MaxItems),
            cancellationToken);
        return documents.Select(document => new NhAiContextItem(
            $"project-{document.ProjectId:N}",
            $"project-{document.ProjectId:N}",
            SourceId,
            "application/json",
            JsonSerializer.Serialize(new
            {
                document.ProjectId,
                document.Key,
                document.Name,
                document.Description
            }),
            NhAiDataClassification.Internal,
            NhAiContextTrust.UntrustedRetrieved,
            [new NhAiExecutionScopeEntry("division", divisionId.ToString())],
            document.LastModifiedAt)
        {
            ProvenanceReferences = [$"project:{document.ProjectId:N}"]
        }).ToArray();
    }
}

public sealed class ProjectAiContextAuthorizationPolicy : INhAiContextAuthorizationPolicy
{
    public ValueTask<bool> CanAccessSourceAsync(
        NhAiContextSourceDescriptor source,
        NhAiInvocationContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var allowed = string.Equals(
                source.Id,
                ProjectAiContextSource.SourceId,
                StringComparison.Ordinal)
            && context.CapabilityGrants.Contains(ProjectAiTools.ReadCapability)
            && context.TryGetScopeValue(
                ProjectAiTools.DivisionScopeKey,
                out var divisionValue)
            && Guid.TryParse(divisionValue, out _);
        return ValueTask.FromResult(allowed);
    }
}
