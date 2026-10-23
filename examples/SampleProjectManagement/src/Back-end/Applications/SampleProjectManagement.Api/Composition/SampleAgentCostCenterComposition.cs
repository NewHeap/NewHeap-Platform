using System.Text.RegularExpressions;
using NewHeap.Platform.AI.Chat;

namespace SampleProjectManagement.Api.Composition;

/// <summary>
/// SPM-313: the cost center an agent's work is booked on, the application's own data about its
/// agents. The sample registers it as an entry of agent metadata with its JSON shape
/// (<see cref="SampleAgentCostCenter"/>), a size limit, a validator for the code and the code as its
/// searchable value; administrators may override it on agents in code. The project planner sets its
/// cost center in code; agents created in the administration get theirs as data. NewHeap stores and
/// checks the value and never interprets it: it grants no agent and no person anything.
/// </summary>
public static partial class SampleAgentCostCenterComposition
{
    /// <summary>
    /// The entry's name, as the administration's slot and the portal's editor know it.
    /// </summary>
    public const string Entry = "cost-center";

    /// <summary>
    /// The longest name of the person who owns the cost center.
    /// </summary>
    public const int MaxOwnerLength = 100;

    /// <summary>
    /// The project planner's cost center in code.
    /// </summary>
    public static readonly SampleAgentCostCenter PlannerCostCenter = new("CC-4100", "Planning office");

    /// <summary>
    /// Registers the cost-center entry: at most 512 bytes, a code like <c>CC-4100</c>
    /// (<c>value.code: unknown-cost-center</c> otherwise), an owner of at most 100 characters, found by
    /// its code and editable on agents in code. Register it once per host.
    /// </summary>
    public static NhAssistantBuilder AddSampleAgentCostCenter(this NhAssistantBuilder assistant)
    {
        ArgumentNullException.ThrowIfNull(assistant);
        return assistant.AddAgentMetadata<SampleAgentCostCenter>(Entry, entry => entry
            .MaxBytes(512)
            .Validate((value, errors) =>
            {
                if (value.Code is null || !CostCenterCode().IsMatch(value.Code))
                {
                    errors.Add("code", "unknown-cost-center");
                }
                if (value.Owner is { Length: > MaxOwnerLength })
                {
                    errors.Add("owner", NhAssistantFieldErrors.TooLong);
                }
            })
            .IndexBy(value => value.Code)
            .Editable());
    }

    [GeneratedRegex("^CC-[0-9]{4}$", RegexOptions.CultureInvariant)]
    private static partial Regex CostCenterCode();
}

/// <summary>
/// The cost center of an agent: its code, such as <c>CC-4100</c>, and optionally who owns it.
/// </summary>
public sealed record SampleAgentCostCenter(string Code, string? Owner = null);
