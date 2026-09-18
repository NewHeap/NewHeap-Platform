using NewHeap.Platform.AI.Chat;

namespace NewHeap.Platform.AI.Chat.Tests.Infrastructure;

public sealed class ApprovalPresentationCapture
{
    public object? Arguments { get; set; }

    public NhAiInvocationContext? Context { get; set; }
}

public sealed class TestApprovalPresenter(ApprovalPresentationCapture capture) : INhAssistantToolPresenter
{
    public ValueTask<string?> GetDisplayNameAsync(
        NhAssistantToolDisplayNameContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<string?>(context.Descriptor.Id == "projects.change-status"
            ? "Change project status"
            : null);
    }

    public ValueTask<NhAssistantApprovalPresentation?> PresentApprovalAsync(
        NhAssistantApprovalPresentationContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (context.Arguments is not ProjectStatusInput input)
        {
            return ValueTask.FromResult<NhAssistantApprovalPresentation?>(null);
        }

        capture.Arguments = context.Arguments;
        capture.Context = context.InvocationContext;
        return ValueTask.FromResult<NhAssistantApprovalPresentation?>(
            new NhAssistantApprovalPresentation(
                "Change project status",
                $"Change project {input.ProjectId} to {input.Status}.",
                [
                    new NhAssistantPresentationField("Project", input.ProjectId.ToString()),
                    new NhAssistantPresentationField("New status", input.Status)
                ]));
    }
}
