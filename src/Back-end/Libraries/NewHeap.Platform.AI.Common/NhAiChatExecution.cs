using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AI;

public sealed record NhAiChatRequest(
    NhAiInvocationContext InvocationContext,
    string ProfileName,
    NhAiModelCapability RequiredCapabilities,
    NhAiDataClassification DataClassification,
    IReadOnlyList<ChatMessage> Messages)
{
    public ChatOptions? Options { get; init; }
    public int EstimatedInputTokens { get; init; }
    public int RequestedOutputTokens { get; init; }
    public decimal? EstimatedCost { get; init; }
    public string? ExecutionRegion { get; init; }
    public string? AgentId { get; init; }
}

public sealed record NhAiChatResult(
    ChatResponse Response,
    string ProfileName,
    int ProfileVersion,
    IReadOnlyList<string> DecisionTrace);

public sealed record NhAiChatStreamCompletion(
    NhAiOutcomeKind Outcome,
    string? FinishReason);

public sealed class NhAiChatStream
{
    internal NhAiChatStream(
        IAsyncEnumerable<ChatResponseUpdate> updates,
        Task<TaskResult<NhAiChatStreamCompletion>> completion,
        string profileName,
        int profileVersion,
        IReadOnlyList<string> decisionTrace)
    {
        Updates = updates;
        Completion = completion;
        ProfileName = profileName;
        ProfileVersion = profileVersion;
        DecisionTrace = decisionTrace;
    }

    public IAsyncEnumerable<ChatResponseUpdate> Updates { get; }
    public Task<TaskResult<NhAiChatStreamCompletion>> Completion { get; }
    public string ProfileName { get; }
    public int ProfileVersion { get; }
    public IReadOnlyList<string> DecisionTrace { get; }
}

public interface INhAiChatExecutor
{
    Task<TaskResult<NhAiChatResult>> GetResponseAsync(
        NhAiChatRequest request,
        CancellationToken cancellationToken = default);

    Task<TaskResult<NhAiChatStream>> StartStreamingAsync(
        NhAiChatRequest request,
        CancellationToken cancellationToken = default);
}

internal sealed class NhAiChatExecutor(
    INhAiModelProfileResolver modelProfiles,
    INhAiBudgetManager budgetManager,
    IEnumerable<INhAiUsageSink> usageSinks) : INhAiChatExecutor
{
    internal const string ActivitySourceName = "NewHeap.Platform.AI";
    internal const string MeterName = "NewHeap.Platform.AI";

    private static readonly ActivitySource ActivitySource = new(ActivitySourceName);
    private static readonly Meter Meter = new(MeterName);
    private static readonly Counter<long> Calls = Meter.CreateCounter<long>("nh.ai.model.calls");
    private static readonly Counter<long> Tokens = Meter.CreateCounter<long>("nh.ai.model.tokens");
    private static readonly Histogram<double> Duration = Meter.CreateHistogram<double>(
        "nh.ai.model.duration",
        "ms");
    private static readonly Histogram<double> TimeToFirstToken = Meter.CreateHistogram<double>(
        "nh.ai.model.time_to_first_token",
        "ms");
    private readonly IReadOnlyList<INhAiUsageSink> _usageSinks = usageSinks.ToArray();

    public async Task<TaskResult<NhAiChatResult>> GetResponseAsync(
        NhAiChatRequest request,
        CancellationToken cancellationToken = default)
    {
        var preparation = await PrepareAsync(request, false, cancellationToken);
        if (!preparation.Success)
        {
            return TaskResult<NhAiChatResult>.Failed(preparation);
        }

        var state = preparation.Data;
        using var activity = StartActivity(request, state.Profile, false);
        var startedAt = Stopwatch.GetTimestamp();
        using var timeout = CreateTimeout(request.InvocationContext, state.Profile, cancellationToken);
        try
        {
            var response = await state.Client.GetResponseAsync(
                request.Messages,
                request.Options,
                timeout.Token);
            var elapsed = Stopwatch.GetElapsedTime(startedAt);
            await RecordCompletionAsync(
                request,
                state.Profile,
                response.Usage,
                elapsed,
                null,
                response.Text.Length,
                response.ModelId,
                response.FinishReason?.ToString(),
                NhAiOutcomeKind.Succeeded,
                activity,
                cancellationToken);
            return TaskResult<NhAiChatResult>.Succeeded(new NhAiChatResult(
                response,
                state.Profile.Name,
                state.Profile.Version,
                state.DecisionTrace));
        }
        catch (OperationCanceledException)
        {
            var elapsed = Stopwatch.GetElapsedTime(startedAt);
            await RecordCompletionAsync(
                request,
                state.Profile,
                null,
                elapsed,
                null,
                0,
                null,
                "cancelled",
                NhAiOutcomeKind.TerminalFailure,
                activity,
                CancellationToken.None);
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            var elapsed = Stopwatch.GetElapsedTime(startedAt);
            activity?.SetTag("error.type", exception.GetType().FullName);
            await RecordCompletionAsync(
                request,
                state.Profile,
                null,
                elapsed,
                null,
                0,
                null,
                "dependency-failure",
                NhAiOutcomeKind.DependencyUnavailable,
                activity,
                CancellationToken.None);
            return TaskResult<NhAiChatResult>.Failed(
                "ai-model-dependency-unavailable",
                "The selected AI model dependency is unavailable.");
        }
    }

    public async Task<TaskResult<NhAiChatStream>> StartStreamingAsync(
        NhAiChatRequest request,
        CancellationToken cancellationToken = default)
    {
        var preparation = await PrepareAsync(request, true, cancellationToken);
        if (!preparation.Success)
        {
            return TaskResult<NhAiChatStream>.Failed(preparation);
        }
        var state = preparation.Data;
        var completion = new TaskCompletionSource<TaskResult<NhAiChatStreamCompletion>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        return TaskResult<NhAiChatStream>.Succeeded(new NhAiChatStream(
            StreamAsync(request, state, completion, cancellationToken),
            completion.Task,
            state.Profile.Name,
            state.Profile.Version,
            state.DecisionTrace));
    }

    private async IAsyncEnumerable<ChatResponseUpdate> StreamAsync(
        NhAiChatRequest request,
        PreparedChat state,
        TaskCompletionSource<TaskResult<NhAiChatStreamCompletion>> completion,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var activity = StartActivity(request, state.Profile, true);
        var startedAt = Stopwatch.GetTimestamp();
        TimeSpan? firstToken = null;
        UsageDetails? usage = null;
        var outputCharacters = 0;
        string? modelId = null;
        string? finishReason = null;
        var outcome = NhAiOutcomeKind.Succeeded;
        var reachedEnd = false;
        var dependencyFailure = false;
        using var timeout = CreateTimeout(request.InvocationContext, state.Profile, cancellationToken);
        try
        {
            await using var enumerator = state.Client.GetStreamingResponseAsync(
                request.Messages,
                request.Options,
                timeout.Token).GetAsyncEnumerator(timeout.Token);
            while (true)
            {
                ChatResponseUpdate update;
                try
                {
                    if (!await enumerator.MoveNextAsync())
                    {
                        reachedEnd = true;
                        break;
                    }
                    update = enumerator.Current;
                }
                catch (OperationCanceledException)
                {
                    outcome = NhAiOutcomeKind.TerminalFailure;
                    finishReason = "cancelled";
                    completion.TrySetCanceled(timeout.Token);
                    throw;
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    outcome = NhAiOutcomeKind.DependencyUnavailable;
                    finishReason = "dependency-failure";
                    dependencyFailure = true;
                    activity?.SetTag("error.type", exception.GetType().FullName);
                    break;
                }
                modelId = update.ModelId ?? modelId;
                finishReason = update.FinishReason?.ToString() ?? finishReason;
                var textLength = update.Text.Length;
                outputCharacters += textLength;
                if (firstToken is null && textLength > 0)
                {
                    firstToken = Stopwatch.GetElapsedTime(startedAt);
                }
                foreach (var usageContent in update.Contents.OfType<UsageContent>())
                {
                    usage = usageContent.Details;
                }
                yield return update;
            }
        }
        finally
        {
            try
            {
                await RecordCompletionAsync(
                    request,
                    state.Profile,
                    usage,
                    Stopwatch.GetElapsedTime(startedAt),
                    firstToken,
                    outputCharacters,
                    modelId,
                    finishReason,
                    outcome,
                    activity,
                    CancellationToken.None);
            }
            finally
            {
                if (dependencyFailure)
                {
                    completion.TrySetResult(
                        TaskResult<NhAiChatStreamCompletion>.Failed(
                            "ai-model-dependency-unavailable",
                            "The selected AI model dependency is unavailable."));
                }
                else if (reachedEnd)
                {
                    completion.TrySetResult(
                        TaskResult<NhAiChatStreamCompletion>.Succeeded(
                            new NhAiChatStreamCompletion(outcome, SafeValue(finishReason))));
                }
                else if (!completion.Task.IsCompleted)
                {
                    completion.TrySetResult(
                        TaskResult<NhAiChatStreamCompletion>.Failed(
                            "ai-model-stream-incomplete",
                            "The AI model stream ended before completion."));
                }
            }
        }
    }

    private async Task<TaskResult<PreparedChat>> PrepareAsync(
        NhAiChatRequest request,
        bool streaming,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateRequest(request);
        if (request.InvocationContext.Deadline is { } deadline
            && deadline <= DateTimeOffset.UtcNow)
        {
            return TaskResult<PreparedChat>.Failed(
                "ai-run-deadline-expired",
                "The AI run deadline has expired.");
        }
        var requiredCapabilities = request.RequiredCapabilities | NhAiModelCapability.Chat;
        if (streaming)
        {
            requiredCapabilities |= NhAiModelCapability.Streaming;
        }
        var resolution = await modelProfiles.ResolveChatAsync(
            new NhAiModelResolutionRequest(
                request.ProfileName,
                requiredCapabilities,
                request.DataClassification,
                request.InvocationContext.Purpose,
                request.ExecutionRegion),
            cancellationToken);
        if (!resolution.Success)
        {
            return TaskResult<PreparedChat>.Failed(resolution);
        }
        var profile = resolution.Data.Profile;
        if (request.EstimatedInputTokens > profile.Budget.MaxInputTokens
            || request.RequestedOutputTokens > profile.Budget.MaxOutputTokens
            || profile.Budget.MaxCalls < 1
            || (request.EstimatedCost is { } cost
                && profile.Budget.MaxEstimatedCost is { } maximumCost
                && cost > maximumCost))
        {
            return TaskResult<PreparedChat>.Failed(
                "ai-model-profile-budget-exceeded",
                "The requested AI model call exceeds its profile budget.");
        }
        if (request.InvocationContext.RemainingBudget is { } remaining)
        {
            if (request.EstimatedInputTokens > remaining.MaxInputTokens
                || request.RequestedOutputTokens > remaining.MaxOutputTokens
                || remaining.MaxCalls < 1
                || (request.EstimatedCost is { } remainingCost
                    && remaining.MaxEstimatedCost is { } remainingMaximumCost
                    && remainingCost > remainingMaximumCost))
            {
                return TaskResult<PreparedChat>.Failed(
                    "ai-run-budget-exceeded",
                    "The requested AI model call exceeds the remaining run budget.");
            }
        }
        var reservation = await budgetManager.ReserveAsync(
            new NhAiBudgetRequest(
                request.InvocationContext.InvocationId,
                profile.Name,
                1,
                request.EstimatedInputTokens,
                request.RequestedOutputTokens,
                request.EstimatedCost),
            cancellationToken);
        if (!reservation.Success)
        {
            return TaskResult<PreparedChat>.Failed(
                "ai-run-budget-reservation-denied",
                "The requested AI model budget could not be reserved.");
        }
        return TaskResult<PreparedChat>.Succeeded(new PreparedChat(
            profile,
            resolution.Data.Client,
            resolution.Data.DecisionTrace));
    }

    private async ValueTask RecordCompletionAsync(
        NhAiChatRequest request,
        NhAiModelProfile profile,
        UsageDetails? usage,
        TimeSpan elapsed,
        TimeSpan? timeToFirstToken,
        int outputCharacters,
        string? modelId,
        string? finishReason,
        NhAiOutcomeKind outcome,
        Activity? activity,
        CancellationToken cancellationToken)
    {
        var inputTokens = ClampTokenCount(usage?.InputTokenCount);
        var outputTokens = ClampTokenCount(usage?.OutputTokenCount);
        var inputCharacters = request.Messages.Sum(message => message.Text.Length);
        var modelIdHash = string.IsNullOrWhiteSpace(modelId)
            ? null
            : NhAiCanonicalJson.ComputeHash(modelId);
        activity?.SetTag("gen_ai.usage.input_tokens", inputTokens);
        activity?.SetTag("gen_ai.usage.output_tokens", outputTokens);
        activity?.SetTag("gen_ai.response.finish_reasons", finishReason);
        activity?.SetTag("newheap.ai.model_id_hash", modelIdHash);
        activity?.SetTag("newheap.ai.input_characters", inputCharacters);
        activity?.SetTag("newheap.ai.output_characters", outputCharacters);
        activity?.SetTag("newheap.ai.outcome", outcome.ToString());
        Calls.Add(1, new("profile.name", profile.Name), new("outcome", outcome.ToString()));
        Tokens.Add(inputTokens, new("profile.name", profile.Name), new("direction", "input"));
        Tokens.Add(outputTokens, new("profile.name", profile.Name), new("direction", "output"));
        Duration.Record(
            elapsed.TotalMilliseconds,
            new KeyValuePair<string, object?>("profile.name", profile.Name));
        if (timeToFirstToken is { } ttft)
        {
            TimeToFirstToken.Record(
                ttft.TotalMilliseconds,
                new KeyValuePair<string, object?>("profile.name", profile.Name));
        }
        var record = new NhAiUsageRecord(
            request.InvocationContext.InvocationId,
            profile.Name,
            profile.Version,
            inputTokens,
            outputTokens,
            request.EstimatedCost,
            elapsed,
            DateTimeOffset.UtcNow)
        {
            RunId = request.InvocationContext.RunId,
            AgentId = request.AgentId,
            CorrelationId = request.InvocationContext.CorrelationId,
            Purpose = request.InvocationContext.Purpose,
            FinishReason = SafeValue(finishReason),
            ModelIdHash = modelIdHash,
            PromptVersion = request.InvocationContext.PromptVersion,
            PromptHash = request.InvocationContext.PromptHash,
            AgentVersion = request.InvocationContext.AgentVersion,
            CatalogVersion = request.InvocationContext.CatalogVersion,
            CatalogHash = request.InvocationContext.CatalogHash,
            ContextHash = request.InvocationContext.ContextHash,
            CachedInputTokens = usage?.CachedInputTokenCount,
            TimeToFirstToken = timeToFirstToken,
            InputCharacters = inputCharacters,
            OutputCharacters = outputCharacters,
            Outcome = outcome,
            ExecutionScopes = request.InvocationContext.ExecutionScopes
                .Take(64)
                .Select(scope => new NhAiUsageScope(scope.Type, scope.Id))
                .ToArray()
        };
        foreach (var sink in _usageSinks)
        {
            await sink.WriteAsync(record, cancellationToken);
        }
    }

    private static Activity? StartActivity(
        NhAiChatRequest request,
        NhAiModelProfile profile,
        bool streaming)
    {
        var activity = ActivitySource.StartActivity("ai.model.invoke", ActivityKind.Client);
        activity?.SetTag("gen_ai.operation.name", "chat");
        activity?.SetTag("newheap.ai.invocation_id", request.InvocationContext.InvocationId);
        activity?.SetTag("newheap.ai.run_id", SafeValue(request.InvocationContext.RunId));
        activity?.SetTag("newheap.ai.correlation_id", SafeValue(request.InvocationContext.CorrelationId));
        activity?.SetTag("newheap.ai.profile.name", profile.Name);
        activity?.SetTag("newheap.ai.profile.version", profile.Version);
        activity?.SetTag("newheap.ai.actor.kind", request.InvocationContext.ActorKind.ToString());
        activity?.SetTag("newheap.ai.streaming", streaming);
        activity?.SetTag("newheap.ai.execution_scope_count", request.InvocationContext.ExecutionScopes.Count);
        activity?.SetTag("newheap.ai.prompt_hash", request.InvocationContext.PromptHash);
        activity?.SetTag("newheap.ai.context_hash", request.InvocationContext.ContextHash);
        return activity;
    }

    private static CancellationTokenSource CreateTimeout(
        NhAiInvocationContext context,
        NhAiModelProfile profile,
        CancellationToken cancellationToken)
    {
        var timeout = profile.Timeout;
        if (context.Deadline is { } deadline)
        {
            timeout = TimeSpan.FromTicks(Math.Min(
                timeout.Ticks,
                Math.Max(0, (deadline - DateTimeOffset.UtcNow).Ticks)));
        }
        var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        source.CancelAfter(timeout <= TimeSpan.Zero ? TimeSpan.FromMilliseconds(1) : timeout);
        return source;
    }

    private static void ValidateRequest(NhAiChatRequest request)
    {
        ArgumentNullException.ThrowIfNull(request.InvocationContext);
        ArgumentNullException.ThrowIfNull(request.Messages);
        NhAiNames.ValidateSegment(request.ProfileName, nameof(request.ProfileName));
        if (request.Messages.Count is < 1 or > 256
            || request.Messages.Sum(message => message.Text.Length) > 2_000_000
            || request.EstimatedInputTokens < 0
            || request.RequestedOutputTokens < 0
            || request.EstimatedCost < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request));
        }
    }

    private static int ClampTokenCount(long? value)
    {
        return (int)Math.Clamp(value ?? 0, 0, int.MaxValue);
    }

    private static string? SafeValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        var safe = new string(value
            .Take(128)
            .Select(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.'
                ? character
                : '-')
            .ToArray());
        return safe;
    }

    private sealed record PreparedChat(
        NhAiModelProfile Profile,
        IChatClient Client,
        IReadOnlyList<string> DecisionTrace);
}
