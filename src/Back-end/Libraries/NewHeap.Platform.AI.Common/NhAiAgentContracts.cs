namespace NewHeap.Platform.AI;

public enum NhAiAutonomyLevel
{
    Observe = 0,
    Explain = 1,
    Propose = 2,
    Simulate = 3,
    Execute = 4
}

public sealed record NhAiAgentDescriptor(
    string Id,
    int Version,
    string Name,
    string Description,
    string ModelProfileName,
    NhAiModelCapability RequiredModelCapabilities,
    IReadOnlyList<string> AllowedToolSelectors,
    NhAiAutonomyLevel MaximumAutonomy,
    NhAiModelBudget Budget,
    string EvaluationBaselineId)
{
    public string? PromptVersion { get; init; }
    public string? PromptHash { get; init; }
    public string? ContextPolicyId { get; init; }
    public string? ApprovalPolicyId { get; init; }
}
