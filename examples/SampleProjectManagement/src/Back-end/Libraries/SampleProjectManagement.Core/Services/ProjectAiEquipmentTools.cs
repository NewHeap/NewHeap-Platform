using System.ComponentModel;
using Microsoft.AspNetCore.Authorization;
using NewHeap.Platform.AI;
using NewHeap.Platform.Common.Models;

namespace SampleProjectManagement.Core.Services;

/// <summary>
/// An order of equipment for a project: what, and how many.
/// </summary>
/// <param name="Item">What to order, such as <c>laptops</c>; at most <see cref="ProjectAiEquipmentTools.MaxItemLength"/> characters.</param>
/// <param name="Quantity">How many, from 1 to <see cref="ProjectAiEquipmentTools.MaxQuantity"/>.</param>
public sealed record ProjectAiEquipmentOrderInput(Guid ProjectId, string Item, int Quantity);

public sealed record ProjectAiEquipmentOrderReceipt(Guid ProjectId, string OrderNumber);

/// <summary>
/// The order desk of the sample: it takes equipment orders for projects and has the supplier confirm
/// them later. The same key always returns the same order.
/// </summary>
public interface IProjectEquipmentOrderDesk
{
    Task<TaskResult<string>> SubmitAsync(
        string key,
        Guid projectId,
        string item,
        int quantity,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// SPM-314: a governed tool whose approved proposals the application executes itself. The order desk
/// works asynchronously: the supplier confirms an order hours later, so an agent run cannot wait for
/// the result. The sample therefore declares the tool with <c>ExecuteInApplication</c>
/// (<c>SampleEquipmentOrderComposition</c>): an approved proposal is handed to the sample's executor,
/// which puts the order on the desk, and the desk reports how it went once the supplier confirmed.
/// The function below runs only in a host that declares the tool without <c>AlsoInline</c>, when a
/// person approves the call inline in their own turn; the sample hands those approvals to the order desk
/// as well (SPM-328).
/// </summary>
[Authorize]
[NhAiToolSet("projects")]
public sealed class ProjectAiEquipmentTools(IProjectEquipmentOrderDesk orders)
{
    public const string OrderEquipmentToolId = "projects.order-equipment";

    public const int MaxQuantity = 100;

    public const int MaxItemLength = 100;

    [NhAiTool(
        "order-equipment",
        1,
        NhAiToolEffect.Mutation,
        NhAiToolExposure.Local | NhAiToolExposure.Agent,
        Approval = NhAiApprovalRequirement.Required,
        Idempotency = NhAiIdempotencySupport.Required,
        AllowAgentGrant = true,
        RequiredCapabilities = new[] { ProjectAiTools.ManageCapability })]
    [Authorize(Policy = ProjectAiTools.ManagePolicy)]
    [Description("Order equipment, such as laptops or monitors, for one project in the authorized active division at the order desk. The supplier confirms the order later.")]
    public async Task<TaskResult<ProjectAiEquipmentOrderReceipt>> OrderEquipmentAsync(
        ProjectAiEquipmentOrderInput input,
        NhAiInvocationContext context,
        CancellationToken cancellationToken)
    {
        if (!IsValid(input))
        {
            return TaskResult<ProjectAiEquipmentOrderReceipt>.Failed(
                "order-invalid",
                $"An order names an item of at most {MaxItemLength} characters and a quantity from 1 to {MaxQuantity}.");
        }

        var submitted = await orders.SubmitAsync(
            context.IdempotencyKey ?? context.InvocationId.ToString("N"),
            input.ProjectId,
            input.Item.Trim(),
            input.Quantity,
            cancellationToken);
        return submitted.Success
            ? TaskResult<ProjectAiEquipmentOrderReceipt>.Succeeded(new ProjectAiEquipmentOrderReceipt(input.ProjectId, submitted.Data!))
            : TaskResult<ProjectAiEquipmentOrderReceipt>.Failed(submitted);
    }

    /// <summary>
    /// Whether the order names an item and a quantity the desk takes.
    /// </summary>
    public static bool IsValid(ProjectAiEquipmentOrderInput? input)
    {
        return input is not null
            && input.ProjectId != Guid.Empty
            && !string.IsNullOrWhiteSpace(input.Item)
            && input.Item.Trim().Length <= MaxItemLength
            && input.Quantity is >= 1 and <= MaxQuantity;
    }
}
