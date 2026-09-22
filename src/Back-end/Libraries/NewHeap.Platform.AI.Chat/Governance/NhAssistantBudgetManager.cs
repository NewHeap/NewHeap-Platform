using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Runtime;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AI.Chat.Governance;

/// <summary>
/// Durable per-actor, per-UTC-day budget on <c>AssistantBudgetLedger</c>. Inside an assistant turn
/// every reservation is booked atomically against the accountable human: tool calls against
/// <see cref="NhAssistantLimits.DailyToolCallBudgetPerActor"/> and model calls against the optional
/// <see cref="NhAssistantLimits.DailyModelCallBudgetPerActor"/>. Outside an assistant turn the request
/// is delegated to the budget manager that was registered before the assistant.
/// </summary>
internal sealed class NhAssistantBudgetManager(
    NhAssistantDbContextFactory contextFactory,
    [FromKeyedServices(NhAssistantFallbacks.ServiceKey)] INhAiBudgetManager fallback) : INhAiBudgetManager
{
    public const string ExhaustedCode = NhAssistantErrorCodes.BudgetExhausted;

    public async ValueTask<TaskResult<NhAiBudgetReservation>> ReserveAsync(
        NhAiBudgetRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var turn = NhAssistantExecutionScope.CurrentTurn;
        if (turn is null)
        {
            return await fallback.ReserveAsync(request, cancellationToken);
        }
        if (request.RequestedCalls < 0
            || request.RequestedInputTokens < 0
            || request.RequestedOutputTokens < 0
            || request.RequestedEstimatedCost < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request));
        }

        var isToolCall = NhAssistantExecutionScope.CurrentCall is not null;
        var limit = isToolCall
            ? turn.Limits.DailyToolCallBudgetPerActor
            : turn.Limits.DailyModelCallBudgetPerActor ?? int.MaxValue;
        var now = DateTimeOffset.UtcNow;
        var day = DateOnly.FromDateTime(now.UtcDateTime);
        var booked = await BookAsync(turn.OwnerActorId, day, isToolCall, request, limit, now, cancellationToken);
        if (booked is null)
        {
            turn.MarkBudgetExhausted();
            return TaskResult<NhAiBudgetReservation>.Failed(
                ExhaustedCode,
                "The daily assistant budget of the accountable actor is exhausted.");
        }

        var remainingCalls = limit == int.MaxValue ? int.MaxValue : Math.Max(0, limit - booked.Value);
        return TaskResult<NhAiBudgetReservation>.Succeeded(new NhAiBudgetReservation(
            Guid.NewGuid().ToString("N"),
            new NhAiModelBudget(int.MaxValue, int.MaxValue, remainingCalls, null),
            new DateTimeOffset(day.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero)));
    }

    /// <summary>
    /// Books the request and returns the new call count, or <see langword="null"/> when the limit would be exceeded.
    /// </summary>
    private async Task<int?> BookAsync(
        string actorId,
        DateOnly day,
        bool isToolCall,
        NhAiBudgetRequest request,
        int limit,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (request.RequestedCalls > limit)
        {
            return null;
        }

        for (var attempt = 0; attempt < 3; attempt++)
        {
            await using var context = contextFactory.CreateDbContext();
            var updated = await TryIncrementAsync(context, actorId, day, isToolCall, request, limit, now, cancellationToken);
            if (updated)
            {
                return await ReadCallsAsync(context, actorId, day, isToolCall, cancellationToken);
            }

            var exists = await context.BudgetLedgers
                .AnyAsync(ledger => ledger.ActorId == actorId && ledger.Day == day, cancellationToken);
            if (exists)
            {
                return null;
            }

            context.BudgetLedgers.Add(new AssistantBudgetLedger
            {
                ActorId = actorId,
                Day = day,
                ToolCalls = isToolCall ? request.RequestedCalls : 0,
                ModelCalls = isToolCall ? 0 : request.RequestedCalls,
                InputTokens = request.RequestedInputTokens,
                OutputTokens = request.RequestedOutputTokens,
                EstimatedCost = request.RequestedEstimatedCost ?? 0m,
                UpdatedAt = now
            });
            try
            {
                await context.SaveChangesAsync(cancellationToken);
                return request.RequestedCalls;
            }
            catch (DbUpdateException)
            {
                // Another reservation created today's ledger first; book against it.
            }
        }

        throw new InvalidOperationException("The assistant budget ledger could not be updated.");
    }

    private static Task<int> TryIncrementCoreAsync(
        NhAssistantDbContext context,
        string actorId,
        DateOnly day,
        bool isToolCall,
        NhAiBudgetRequest request,
        int limit,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var calls = request.RequestedCalls;
        var inputTokens = (long)request.RequestedInputTokens;
        var outputTokens = (long)request.RequestedOutputTokens;
        var cost = request.RequestedEstimatedCost ?? 0m;
        var ledgers = context.BudgetLedgers.Where(ledger => ledger.ActorId == actorId && ledger.Day == day);
        if (isToolCall)
        {
            return ledgers
                .Where(ledger => ledger.ToolCalls <= limit - calls)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(ledger => ledger.ToolCalls, ledger => ledger.ToolCalls + calls)
                        .SetProperty(ledger => ledger.InputTokens, ledger => ledger.InputTokens + inputTokens)
                        .SetProperty(ledger => ledger.OutputTokens, ledger => ledger.OutputTokens + outputTokens)
                        .SetProperty(ledger => ledger.EstimatedCost, ledger => ledger.EstimatedCost + cost)
                        .SetProperty(ledger => ledger.UpdatedAt, now),
                    cancellationToken);
        }
        return ledgers
            .Where(ledger => ledger.ModelCalls <= limit - calls)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(ledger => ledger.ModelCalls, ledger => ledger.ModelCalls + calls)
                    .SetProperty(ledger => ledger.InputTokens, ledger => ledger.InputTokens + inputTokens)
                    .SetProperty(ledger => ledger.OutputTokens, ledger => ledger.OutputTokens + outputTokens)
                    .SetProperty(ledger => ledger.EstimatedCost, ledger => ledger.EstimatedCost + cost)
                    .SetProperty(ledger => ledger.UpdatedAt, now),
                cancellationToken);
    }

    private static async Task<bool> TryIncrementAsync(
        NhAssistantDbContext context,
        string actorId,
        DateOnly day,
        bool isToolCall,
        NhAiBudgetRequest request,
        int limit,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var affected = await TryIncrementCoreAsync(
            context,
            actorId,
            day,
            isToolCall,
            request,
            limit,
            now,
            cancellationToken);
        return affected == 1;
    }

    private static Task<int> ReadCallsAsync(
        NhAssistantDbContext context,
        string actorId,
        DateOnly day,
        bool isToolCall,
        CancellationToken cancellationToken)
    {
        var ledger = context.BudgetLedgers
            .AsNoTracking()
            .Where(item => item.ActorId == actorId && item.Day == day);
        return isToolCall
            ? ledger.Select(item => item.ToolCalls).SingleAsync(cancellationToken)
            : ledger.Select(item => item.ModelCalls).SingleAsync(cancellationToken);
    }
}

internal static class NhAssistantFallbacks
{
    /// <summary>
    /// Keyed-service key of the managers that were registered before the assistant replaced them.
    /// </summary>
    public const string ServiceKey = "newheap-assistant-fallback";
}
