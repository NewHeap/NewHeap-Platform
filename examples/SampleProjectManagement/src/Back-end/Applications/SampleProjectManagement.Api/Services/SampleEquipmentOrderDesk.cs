using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using NewHeap.Platform.AI.Chat;
using NewHeap.Platform.Common.Models;
using SampleProjectManagement.Core.Services;

namespace SampleProjectManagement.Api.Services;

/// <summary>
/// An order on the sample's order desk: the order number, what it orders and, for an order an agent
/// proposed and a person approved, the hand-off whose report the desk owes the assistant.
/// </summary>
public sealed record SampleEquipmentOrder(
    string OrderNumber,
    Guid ProjectId,
    string Item,
    int Quantity,
    NhAssistantProposalHandOff? HandOff);

/// <summary>
/// SPM-314: the order desk of the sample, standing in for a purchasing system whose supplier confirms
/// an order hours later. It keeps orders in memory, one per key, so a repeated hand-off of the same
/// proposal finds its order again. When the supplier confirms (<see cref="ConfirmAsync"/>, which a
/// supplier's callback calls in a real application and the sample's tests call here), the desk reports
/// the outcome of a handed-off order through <see cref="INhAssistantProposalExecutionReports"/>: with the
/// supplier's code and a short summary, in the tenant of the hand-off.
/// </summary>
public sealed class SampleEquipmentOrderDesk(IServiceScopeFactory scopes) : IProjectEquipmentOrderDesk
{
    private readonly ConcurrentDictionary<string, SampleEquipmentOrder> _orders = new(StringComparer.Ordinal);
    private int _sequence;

    /// <summary>
    /// The orders on the desk.
    /// </summary>
    public IReadOnlyCollection<SampleEquipmentOrder> Orders => _orders.Values.ToArray();

    /// <summary>
    /// Takes an order a person approved in their own turn, outside any proposal.
    /// </summary>
    public Task<TaskResult<string>> SubmitAsync(
        string key,
        Guid projectId,
        string item,
        int quantity,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        cancellationToken.ThrowIfCancellationRequested();
        var order = _orders.GetOrAdd("call:" + key, _ => new SampleEquipmentOrder(NextOrderNumber(), projectId, item, quantity, null));
        return Task.FromResult(TaskResult<string>.Succeeded(order.OrderNumber));
    }

    /// <summary>
    /// Takes the order of an approved proposal the assistant handed over. A later hand-off of the same
    /// proposal keeps its order and number and replaces the hand-off, whose report is then owed.
    /// </summary>
    public SampleEquipmentOrder Take(NhAssistantProposalHandOff handOff, ProjectAiEquipmentOrderInput input)
    {
        ArgumentNullException.ThrowIfNull(handOff);
        ArgumentNullException.ThrowIfNull(input);
        return _orders.AddOrUpdate(
            "proposal:" + handOff.ApprovalId.ToString("N"),
            _ => new SampleEquipmentOrder(NextOrderNumber(), input.ProjectId, input.Item.Trim(), input.Quantity, handOff),
            (_, existing) => existing with { HandOff = handOff });
    }

    /// <summary>
    /// The supplier confirmed, or turned down, an order: the desk reports it to the assistant, which
    /// closes the proposal, notifies its approvers and posts the summary into the agent's thread. A
    /// confirmation that arrives twice is the same report.
    /// </summary>
    public async Task<TaskResult<NhAssistantExecutionReportReceipt>> ConfirmAsync(
        string orderNumber,
        NhAssistantExecutionOutcome outcome,
        string code,
        CancellationToken cancellationToken = default)
    {
        var order = _orders.Values.FirstOrDefault(item => string.Equals(item.OrderNumber, orderNumber, StringComparison.Ordinal));
        if (order?.HandOff is not { } handOff)
        {
            return TaskResult<NhAssistantExecutionReportReceipt>.Failed(
                "order-not-handed-off",
                "The order was not handed over by the assistant, so it owes no report.");
        }

        var summary = outcome == NhAssistantExecutionOutcome.Completed
            ? $"Order {order.OrderNumber} for {order.Quantity} {order.Item} is placed with the supplier."
            : $"Order {order.OrderNumber}: the supplier answered {code}.";
        await using var scope = scopes.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<INhAssistantProposalExecutionReports>().ReportExecutionAsync(
            NhAssistantExecutionReport.For(handOff, outcome, code, summary),
            cancellationToken);
    }

    private string NextOrderNumber()
    {
        return "EQ-" + Interlocked.Increment(ref _sequence).ToString("D4", CultureInfo.InvariantCulture);
    }
}

/// <summary>
/// SPM-314: the sample's executor of approved equipment orders (<c>UseProposalExecutor</c>). It reads
/// the approved arguments exactly as people approved them, puts the order on the desk and returns at
/// once; the desk reports later. An order the desk cannot read is refused, which closes the proposal
/// as failed with the refusal's code.
/// </summary>
public sealed class SampleEquipmentOrderExecutor(SampleEquipmentOrderDesk desk) : INhAssistantProposalExecutor
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public Task<TaskResult> ExecuteAsync(NhAssistantProposalHandOff handOff, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handOff);
        cancellationToken.ThrowIfCancellationRequested();
        if (handOff.ToolId != ProjectAiEquipmentTools.OrderEquipmentToolId)
        {
            return Task.FromResult(TaskResult.Failed("tool-not-executed-here", "The order desk executes equipment orders only."));
        }

        ProjectAiEquipmentOrderInput? input;
        try
        {
            input = handOff.Proposal.Arguments.Deserialize<ProjectAiEquipmentOrderInput>(Json);
        }
        catch (JsonException)
        {
            input = null;
        }
        if (!ProjectAiEquipmentTools.IsValid(input))
        {
            return Task.FromResult(TaskResult.Failed("order-invalid", "The approved order names no item or quantity the desk takes."));
        }

        desk.Take(handOff, input!);
        return Task.FromResult(TaskResult.Succeeded());
    }
}
