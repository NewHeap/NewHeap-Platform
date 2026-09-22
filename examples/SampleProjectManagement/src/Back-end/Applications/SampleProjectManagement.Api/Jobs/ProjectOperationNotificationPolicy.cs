using NewHeap.Platform.AspNet.Common.DAL.Entities;
using NewHeap.Platform.AspNet.Common.Services.BackgroundOperations;

namespace SampleProjectManagement.Api.Jobs;

/// <summary>
/// Keeps NewHeap's default decision about which milestones deserve a notification,
/// and threads all portfolio work of one division into a single inbox item so that
/// repeated analyses update the same notification instead of adding new ones.
/// </summary>
public sealed class ProjectOperationNotificationPolicy : INhBackgroundOperationNotificationPolicy
{
    public const string PortfolioCategory = "project-portfolio";

    private static readonly HashSet<string> PortfolioOperationTypes = new(StringComparer.Ordinal)
    {
        "sample-project-portfolio-analysis",
        "sample-project-ai-portfolio-report"
    };

    private readonly NhDefaultBackgroundOperationNotificationPolicy _defaultPolicy;

    public ProjectOperationNotificationPolicy(NhDefaultBackgroundOperationNotificationPolicy defaultPolicy)
    {
        _defaultPolicy = defaultPolicy;
    }

    public async Task<NhBackgroundOperationNotificationDecision> DecideAsync(
        NhBackgroundOperation operation,
        NhBackgroundOperationEvent milestone,
        CancellationToken cancellationToken = default)
    {
        var decision = await _defaultPolicy.DecideAsync(operation, milestone, cancellationToken);
        if (!decision.ShouldNotify
            || !operation.DivisionId.HasValue
            || !PortfolioOperationTypes.Contains(operation.OperationType))
        {
            return decision;
        }

        return decision with
        {
            GroupKey = $"project-portfolio:{operation.DivisionId.Value:N}",
            Category = PortfolioCategory
        };
    }
}
