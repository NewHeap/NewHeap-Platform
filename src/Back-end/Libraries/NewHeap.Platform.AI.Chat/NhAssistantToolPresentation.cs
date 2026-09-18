using System.Globalization;

namespace NewHeap.Platform.AI.Chat;

/// <summary>
/// Context for the optional, user-facing name of an assistant tool.
/// </summary>
public sealed record NhAssistantToolDisplayNameContext(
    NhAiToolDescriptor Descriptor,
    CultureInfo Culture);

/// <summary>
/// Exact governed input available to an optional approval presenter. Presentation never changes
/// the proposal, approval evidence or authorization decision.
/// </summary>
public sealed record NhAssistantApprovalPresentationContext(
    NhAiToolDescriptor Descriptor,
    object Arguments,
    NhAiInvocationContext InvocationContext,
    CultureInfo Culture);

/// <summary>
/// One named, user-facing fact in an approval explanation.
/// </summary>
public sealed record NhAssistantPresentationField(string Label, string Value);

/// <summary>
/// Trusted, localized presentation stored alongside an assistant approval.
/// </summary>
public sealed record NhAssistantApprovalPresentation(
    string ToolDisplayName,
    string Summary,
    IReadOnlyList<NhAssistantPresentationField> Fields,
    string? Notice = null);

/// <summary>
/// Optionally presents governed tools and approvals in consumer terminology. Implementations must
/// derive approval text from the supplied arguments and invocation context, not from client input.
/// Return <see langword="null"/> when the presenter does not handle the tool.
/// </summary>
public interface INhAssistantToolPresenter
{
    ValueTask<string?> GetDisplayNameAsync(
        NhAssistantToolDisplayNameContext context,
        CancellationToken cancellationToken);

    ValueTask<NhAssistantApprovalPresentation?> PresentApprovalAsync(
        NhAssistantApprovalPresentationContext context,
        CancellationToken cancellationToken);
}
