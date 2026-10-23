using Microsoft.Extensions.DependencyInjection.Extensions;
using NewHeap.Platform.AI;
using NewHeap.Platform.AI.Chat.AspNet;
using SampleProjectManagement.Api.Services;
using SampleProjectManagement.Core.Services;

namespace SampleProjectManagement.Api.Composition;

/// <summary>
/// SPM-314: the intake reviewer may propose to order equipment for a project
/// (<c>projects.order-equipment</c>), which the sample's application executes itself instead of the
/// tool's function. The order desk works asynchronously, so the tool is declared with
/// <c>ExecuteInApplication</c> and a report deadline of eight hours: once a project approver approves
/// such a proposal, the assistant hands the approved arguments, the proposal hash, the approver and the
/// proposal id to <see cref="SampleEquipmentOrderExecutor"/>, and the proposal waits as
/// <c>awaiting-application</c> until the order desk reports how the supplier answered
/// (<see cref="SampleEquipmentOrderDesk.ConfirmAsync"/>). Without a report in time the proposal becomes
/// <c>execution-unknown</c> and an approver hands it to the order desk again or marks it resolved.
/// Call it after <c>AddSampleIntakeReviewer</c>.
/// </summary>
public static class SampleEquipmentOrderComposition
{
    /// <summary>
    /// How long the order desk may take to report after a hand-off.
    /// </summary>
    public static readonly TimeSpan ReportWithin = TimeSpan.FromHours(8);

    public static IServiceCollection AddSampleEquipmentOrders(this IServiceCollection services)
    {
        services.TryAddSingleton<SampleEquipmentOrderDesk>();
        services.TryAddSingleton<IProjectEquipmentOrderDesk>(provider => provider.GetRequiredService<SampleEquipmentOrderDesk>());
        services.TryAddScoped<ProjectAiEquipmentTools>();
        services.AddNewHeapPlatformAI(ai => ai.AddGeneratedToolCatalog<ProjectAiEquipmentToolsNhAiCatalog>());

        services.AddNewHeapAssistant(assistant => assistant
            // The application executes approved orders and reports within eight hours; every other tool
            // keeps executing in its function.
            .ExecuteInApplication(ProjectAiEquipmentTools.OrderEquipmentToolId, ReportWithin)
            .UseProposalExecutor<SampleEquipmentOrderExecutor>()
            .ConfigureAgent(SampleIntakeReviewerComposition.AgentId, agent => agent
                .Grant(ProjectAiEquipmentTools.OrderEquipmentToolId)));
        return services;
    }
}
