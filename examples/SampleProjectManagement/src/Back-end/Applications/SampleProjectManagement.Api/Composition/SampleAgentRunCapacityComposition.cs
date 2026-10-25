using NewHeap.Platform.AI.Chat.AspNet.AgentRuns;
using NewHeap.Platform.AspNet.Common.Services.BackgroundOperations;

namespace SampleProjectManagement.Api.Composition;

/// <summary>
/// SPM-326: the capacity of the sample's agent runs. The assistant's background operations (runs,
/// proposal executions, revalidations, deferred trigger fires and replays) work on a queue of their
/// own, which keeps two slots of the global limit for itself: a burst of portfolio analyses cannot take
/// every slot while an intake review waits. The queue may run four operations at the same time, so the
/// agents leave the rest of the shared slots to the other work. <c>Program.cs</c> and the sample tests
/// share this composition; call it inside <c>WithBackgroundOperations</c> after
/// <c>WithGlobalConcurrency</c>.
/// </summary>
public static class SampleAgentRunCapacityComposition
{
    public const string Queue = "agent-runs";
    public const int ReservedSlots = 2;
    public const int MaxConcurrency = 4;

    public static NhBackgroundOperationBuilder AddSampleAgentRuns(this NhBackgroundOperationBuilder operations)
    {
        return operations
            .WithReservedQueueConcurrency(Queue, ReservedSlots)
            .AddAssistantAgentRuns(runs => runs
                .Queue(Queue)
                .MaxConcurrency(MaxConcurrency));
    }
}
