namespace SampleProjectManagement.Api.Models;

/// <summary>
/// SPM-371: whether one agent uses compositions and the script tool in the sample's environment. The
/// agent <c>mcp</c> stands for the MCP exposure, which no agent serves.
/// </summary>
public sealed class AssistantCompositionSettingViewModel
{
    public string AgentId { get; set; } = "";

    public bool Compositions { get; set; }

    public bool Scripts { get; set; }
}

/// <summary>
/// SPM-371: the new setting of one agent. What code does not allow stays off, whatever is stored.
/// </summary>
public sealed class AssistantCompositionSettingMutateModel
{
    public bool Compositions { get; set; }

    public bool Scripts { get; set; }
}
