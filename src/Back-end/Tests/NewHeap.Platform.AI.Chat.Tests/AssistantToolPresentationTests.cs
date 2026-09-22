using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using NewHeap.Platform.AI.Chat.Runtime;
using NewHeap.Platform.AI.Chat.Tests.Infrastructure;
using Xunit;

namespace NewHeap.Platform.AI.Chat.Tests;

public sealed class AssistantToolPresentationTests
{
    [Fact]
    public async Task A_presenter_receives_the_requested_culture_and_exact_governed_values()
    {
        var arguments = new ProjectStatusInput(Guid.NewGuid(), "Active");
        var invocation = Context();
        var presenter = new RecordingPresenter();
        var resolver = Resolver(presenter);
        var culture = CultureInfo.GetCultureInfo("nl-NL");

        var presentation = await resolver.ResolveApprovalAsync(
            TestProjectToolCatalog.ChangeStatus,
            arguments,
            invocation,
            culture,
            CancellationToken.None);

        Assert.NotNull(presentation);
        Assert.Same(arguments, presenter.Context?.Arguments);
        Assert.Same(invocation, presenter.Context?.InvocationContext);
        Assert.Same(culture, presenter.Context?.Culture);
    }

    [Fact]
    public async Task Failures_oversize_output_and_confidential_data_use_the_safe_fallback()
    {
        var failing = new ThrowingPresenter();
        var oversize = new OversizePresenter();
        var resolver = Resolver(failing, oversize);

        var name = await resolver.ResolveDisplayNameAsync(
            TestProjectToolCatalog.ChangeStatus,
            CultureInfo.InvariantCulture,
            CancellationToken.None);
        var presentation = await resolver.ResolveApprovalAsync(
            TestProjectToolCatalog.ChangeStatus,
            new ProjectStatusInput(Guid.NewGuid(), "Active"),
            Context(),
            CultureInfo.InvariantCulture,
            CancellationToken.None);
        var confidential = await resolver.ResolveApprovalAsync(
            TestProjectToolCatalog.ChangeStatus with { DataClassification = NhAiDataClassification.Confidential },
            new ProjectStatusInput(Guid.NewGuid(), "Active"),
            Context(),
            CultureInfo.InvariantCulture,
            CancellationToken.None);

        Assert.Equal(TestProjectToolCatalog.ChangeStatus.Id, name);
        Assert.Null(presentation);
        Assert.Null(confidential);
    }

    [Fact]
    public void Stored_presentations_are_bounded_and_old_or_corrupt_values_are_optional()
    {
        var value = new NhAssistantApprovalPresentation(
            "Change project status",
            "Change Alpha to Active.",
            [new NhAssistantPresentationField("Project", "Alpha")]);

        var json = NhAssistantToolPresentationResolver.Serialize(value);

        var restored = NhAssistantToolPresentationResolver.Deserialize(json);
        Assert.NotNull(restored);
        Assert.Equal(value.ToolDisplayName, restored.ToolDisplayName);
        Assert.Equal(value.Summary, restored.Summary);
        Assert.Equal(value.Fields, restored.Fields);
        Assert.Null(NhAssistantToolPresentationResolver.Deserialize(null));
        Assert.Null(NhAssistantToolPresentationResolver.Deserialize("{not-json"));
        Assert.Null(NhAssistantToolPresentationResolver.Serialize(
            value with { Summary = new string('x', NhAssistantToolPresentationResolver.MaxSummaryLength + 1) }));
    }

    [Fact]
    public async Task A_presenter_timeout_falls_back_without_bypassing_the_approval()
    {
        var resolver = Resolver(new HangingPresenter());

        var presentation = await resolver.ResolveApprovalAsync(
            TestProjectToolCatalog.ChangeStatus,
            new ProjectStatusInput(Guid.NewGuid(), "Active"),
            Context(),
            CultureInfo.InvariantCulture,
            CancellationToken.None);

        Assert.Null(presentation);
    }

    private static NhAssistantToolPresentationResolver Resolver(params INhAssistantToolPresenter[] presenters)
    {
        return new NhAssistantToolPresentationResolver(
            presenters,
            NullLogger<NhAssistantToolPresentationResolver>.Instance);
    }

    private static NhAiInvocationContext Context()
    {
        return new NhAiInvocationContext("agent-1", "assistant", new Dictionary<string, string>
        {
            ["division-id"] = "division-1"
        })
        {
            AccountableOwnerId = "user-1",
            RunId = Guid.NewGuid().ToString()
        };
    }

    private sealed class RecordingPresenter : INhAssistantToolPresenter
    {
        public NhAssistantApprovalPresentationContext? Context { get; private set; }

        public ValueTask<string?> GetDisplayNameAsync(
            NhAssistantToolDisplayNameContext context,
            CancellationToken cancellationToken)
        {
            return ValueTask.FromResult<string?>(null);
        }

        public ValueTask<NhAssistantApprovalPresentation?> PresentApprovalAsync(
            NhAssistantApprovalPresentationContext context,
            CancellationToken cancellationToken)
        {
            Context = context;
            return ValueTask.FromResult<NhAssistantApprovalPresentation?>(
                new NhAssistantApprovalPresentation("Status", "Summary", []));
        }
    }

    private sealed class ThrowingPresenter : INhAssistantToolPresenter
    {
        public ValueTask<string?> GetDisplayNameAsync(
            NhAssistantToolDisplayNameContext context,
            CancellationToken cancellationToken)
        {
            throw new InvalidOperationException("sensitive presenter failure");
        }

        public ValueTask<NhAssistantApprovalPresentation?> PresentApprovalAsync(
            NhAssistantApprovalPresentationContext context,
            CancellationToken cancellationToken)
        {
            throw new InvalidOperationException("sensitive presenter failure");
        }
    }

    private sealed class OversizePresenter : INhAssistantToolPresenter
    {
        public ValueTask<string?> GetDisplayNameAsync(
            NhAssistantToolDisplayNameContext context,
            CancellationToken cancellationToken)
        {
            return ValueTask.FromResult<string?>(new string('x', 161));
        }

        public ValueTask<NhAssistantApprovalPresentation?> PresentApprovalAsync(
            NhAssistantApprovalPresentationContext context,
            CancellationToken cancellationToken)
        {
            return ValueTask.FromResult<NhAssistantApprovalPresentation?>(
                new NhAssistantApprovalPresentation("Status", new string('x', 513), []));
        }
    }

    private sealed class HangingPresenter : INhAssistantToolPresenter
    {
        public async ValueTask<string?> GetDisplayNameAsync(
            NhAssistantToolDisplayNameContext context,
            CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return null;
        }

        public async ValueTask<NhAssistantApprovalPresentation?> PresentApprovalAsync(
            NhAssistantApprovalPresentationContext context,
            CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return null;
        }
    }
}
